#!/usr/bin/env python3
"""Inspect or dispatch this repository's GitHub Actions workflows."""
from __future__ import annotations

import argparse
from datetime import datetime
import io
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile

API = "https://api.github.com/repos/Fokiiiiiii/eve.rs"
HEADERS = {
    "Accept": "application/vnd.github+json",
    "X-GitHub-Api-Version": "2022-11-28",
}

def request(path: str, token: str, *, method: str = "GET", body: dict | None = None):
    headers = {**HEADERS, "Authorization": f"Bearer {token}"}
    data = None
    if body is not None:
        headers["Content-Type"] = "application/json"
        data = json.dumps(body).encode()
    req = urllib.request.Request(API + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=60) as response:
            return response.status, response.read(), response.headers
    except urllib.error.HTTPError as error:
        detail = error.read().decode("utf-8", "replace")
        raise RuntimeError(f"GitHub API {method} {path} failed: HTTP {error.code}: {detail[:500]}") from None

def api_json(path: str, token: str):
    _, body, _ = request(path, token)
    return json.loads(body)

def download_job_logs(job_id: int, token: str) -> tuple[bytes, str]:
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, req, fp, code, msg, headers, newurl):
            return None

    headers = {**HEADERS, "Authorization": f"Bearer {token}"}
    req = urllib.request.Request(f"{API}/actions/jobs/{job_id}/logs", headers=headers)
    opener = urllib.request.build_opener(NoRedirect)
    try:
        with opener.open(req, timeout=60) as response:
            return response.read(), response.headers.get("Content-Type", "")
    except urllib.error.HTTPError as response:
        if response.code not in (301, 302, 303, 307, 308):
            detail = response.read().decode("utf-8", "replace")
            raise RuntimeError(f"GitHub API GET /actions/jobs/{job_id}/logs failed: HTTP {response.code}: {detail[:500]}") from None
        location = response.headers.get("Location")
        if not location:
            raise RuntimeError(f"GitHub API GET /actions/jobs/{job_id}/logs returned a redirect without a destination") from None
    # GitHub returns a short-lived signed download URL. Fetch it without forwarding the API token.
    with urllib.request.urlopen(location, timeout=60) as response:
        return response.read(), response.headers.get("Content-Type", "")

def failing_step_tail(payload: bytes, content_type: str, job_id: int, step: dict) -> list[str]:
    return failing_step_text(payload, content_type, job_id, step).splitlines()[-40:]

def failing_step_text(payload: bytes, content_type: str, job_id: int, step: dict) -> str:
    if zipfile.is_zipfile(io.BytesIO(payload)):
        with zipfile.ZipFile(io.BytesIO(payload)) as archive:
            suffix = f"{step['number']}_{step['name']}.txt"
            candidates = [name for name in archive.namelist() if name.endswith(suffix)]
            if not candidates:
                candidates = [name for name in archive.namelist() if name.endswith(".txt")]
            if not candidates:
                return ""
            return archive.read(candidates[0]).decode("utf-8", "replace")

    lines = payload.decode("utf-8", "replace").splitlines()
    start, end = step.get("started_at"), step.get("completed_at")
    if start and end:

        start_time = datetime.fromisoformat(start.replace("Z", "+00:00"))
        end_time = datetime.fromisoformat(end.replace("Z", "+00:00"))
        window = []
        for line in lines:
            match = re.match(r"(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?Z) ", line)
            if match:
                timestamp = datetime.fromisoformat(match.group(1).replace("Z", "+00:00"))
                if start_time <= timestamp <= end_time:
                    window.append(line)
        if window:
            return "\n".join(window)
    return "\n".join(lines)

def normalize_log_line(line: str) -> str:
    line = re.sub(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z\s+", "", line, count=1)
    return re.sub(r"\x1b\[[0-?]*[ -/]*[@-~]", "", line)


def parse_test_failures(text: str) -> list[str]:
    patterns = (
        re.compile(r"^\[agent-test failure\]\s+(.+?)\s*$"),
        re.compile(r"^\s*FAILED\s+(.+?)(?:\s+-\s+.*)?$"),
        re.compile(r"^_{3,}\s*(.+?)\s*_{3,}\s*$"),
        re.compile(r"^\s*Failed\s+(.+?)(?:\s+\[[^\]]+\])?\s*$"),
        re.compile(r"^\s*test\s+(.+?)\s+\.\.\.\s+FAILED\s*$"),
        re.compile(r"^----\s+(.+?)\s+stdout ----\s*$"),
        re.compile(r"^\s*FAIL:\s*(.+?)\s*$"),
    )
    names = []
    headings = []
    for raw_line in text.splitlines():
        line = normalize_log_line(raw_line)
        for index, pattern in enumerate(patterns):
            match = pattern.match(line)
            if match:
                name = match.group(1).strip()
                target = headings if index == 2 else names
                if name and name not in target:
                    target.append(name)
                break
    return names or headings


def failure_log_tail(text: str, test_name: str) -> list[str]:
    lines = text.splitlines()
    normalized = [normalize_log_line(line) for line in lines]
    custom = f"[agent-test failure] {test_name}"
    start = next((index for index, line in enumerate(normalized) if line.strip() == custom), None)
    if start is not None:
        end = next((index for index in range(start + 1, len(lines)) if normalized[index].startswith("[agent-test failure] ")), len(lines))
        return lines[start + 1:end][-40:]

    rust = f"---- {test_name} stdout ----"
    start = next((index for index, line in enumerate(normalized) if line.strip() == rust), None)
    if start is not None:
        end = next((index for index in range(start + 1, len(lines)) if normalized[index].startswith("---- ") or normalized[index].startswith("failures:")), len(lines))
        return lines[start:end][-40:]

    short_name = test_name.rsplit("::", 1)[-1]
    headings = [index for index, line in enumerate(normalized)
                if re.match(r"^_{3,}.*_{3,}\s*$", line) and short_name in line]
    if headings:
        start = headings[0]
        end = next((index for index in range(start + 1, len(lines))
                    if re.match(r"^_{3,}.*_{3,}\s*$", normalized[index]) or "short test summary" in normalized[index]), len(lines))
        return lines[start:end][-40:]

    starts = [index for index, line in enumerate(normalized)
              if re.match(r"^\s*Failed\s+", line) and test_name in line]
    if starts:
        start = starts[0]
        end = next((index for index in range(start + 1, len(lines))
                    if re.match(r"^\s*Failed\s+", normalized[index]) or "Test Run Failed" in normalized[index]), len(lines))
        return lines[start:end][-40:]
    return lines[-40:]


def print_compact_summary(run: dict, token: str) -> None:
    print(f"Run {run['run_number']} {run['name']} {run['conclusion'] or run['status']} sha={run['head_sha'][:12]}")
    print(f"  {run['html_url']}")
    jobs = api_json(f"/actions/runs/{run['id']}/jobs?per_page=100", token).get("jobs", [])
    failed_jobs = [job for job in jobs if job.get("conclusion") in {"failure", "cancelled", "timed_out"}]
    if not failed_jobs:
        print("No failed tests or jobs.")
        return
    for job in failed_jobs:
        print(f"  failed job: {job['name']}")
        steps = [step for step in job.get("steps", []) if step.get("conclusion") in {"failure", "cancelled", "timed_out"}]
        if not steps:
            print(f"    job conclusion: {job.get('conclusion')}")
            continue
        try:
            payload, content_type = download_job_logs(job["id"], token)
        except (RuntimeError, urllib.error.URLError, zipfile.BadZipFile) as error:
            print(f"    log download unavailable: {error}")
            continue
        for step in steps:
            log = failing_step_text(payload, content_type, job["id"], step)
            names = parse_test_failures(log)
            print(f"    failing step: {step['name']}")
            if names:
                for name in names:
                    print(f"      failing test: {name}")
                    tail = failure_log_tail(log, name)
                    if tail:
                        print("        " + "\n        ".join(tail))
            else:
                tail = log.splitlines()[-40:]
                if tail:
                    print("      " + "\n      ".join(tail))

def dispatch(args, token: str) -> None:
    inputs = {}
    if args.workflow == "ci.yml":
        inputs["jobs"] = args.jobs
    elif args.workflow == "live-slow.yml":
        inputs["test_file"] = args.test_file
    status, _, _ = request(
        f"/actions/workflows/{urllib.parse.quote(args.workflow)}/dispatches",
        token,
        method="POST",
        body={"ref": args.ref, "inputs": inputs},
    )
    print(f"Dispatched {args.workflow} on {args.ref} (HTTP {status}).")

def dispatch_agent_test(args, token: str) -> None:
    workflow = api_json("/actions/workflows/agent-test.yml", token)
    endpoint = f"/actions/workflows/{workflow['id']}/runs?" + urllib.parse.urlencode(
        {"per_page": 100, "event": "workflow_dispatch", "branch": "main"}
    )
    previous = {run["id"] for run in api_json(endpoint, token).get("workflow_runs", [])}
    started = datetime.now().astimezone()
    inputs = {
        "ref": args.ref,
        "dotnet_projects": args.projects,
        "dotnet_filter": args.filter,
        "live_tests": args.live,
        "rust": args.rust,
    }
    status, _, _ = request(
        "/actions/workflows/agent-test.yml/dispatches",
        token,
        method="POST",
        body={"ref": "main", "inputs": inputs},
    )
    print(f"Dispatched agent-test.yml for test ref {args.ref} (HTTP {status}); waiting for the run id.", flush=True)
    started_text = started.isoformat()
    delay = 1
    for _ in range(9):
        runs = api_json(endpoint, token).get("workflow_runs", [])
        candidates = []
        for run in runs:
            if run["id"] in previous:
                continue
            created = datetime.fromisoformat(run["created_at"].replace("Z", "+00:00"))
            if created.timestamp() < started.timestamp() - 5:
                continue
            if run.get("display_title") not in (None, f"Agent test {args.ref}"):
                continue
            candidates.append(run)
        if candidates:
            selected = max(candidates, key=lambda run: (run.get("created_at", ""), run["id"]))
            print(f"Run id: {selected['id']} ({selected['html_url']})")
            return
        print(f"Run id is not listed yet; checking again in {delay}s (dispatch started {started_text}).", flush=True)
        time.sleep(delay)
        delay = min(delay * 2, 15)
    raise RuntimeError("GitHub accepted the dispatch but did not list its run id within 90 seconds.")

def wait_for_run(run_id: int, token: str) -> int:
    delay = 5
    last_status = None
    while True:
        run = api_json(f"/actions/runs/{run_id}", token)
        state = run.get("status", "unknown")
        if state != last_status:
            print(f"Run {run_id}: {state}{' (' + str(run.get('conclusion')) + ')' if run.get('conclusion') else ''}", flush=True)
            last_status = state
        if state == "completed":
            print_compact_summary(run, token)
            return 0 if run.get("conclusion") == "success" else 1
        print(f"Checking again in {delay}s.", flush=True)
        time.sleep(delay)
        delay = min(delay * 2, 60)

def print_ci_failures(sha: str, token: str) -> None:
    workflow = api_json("/actions/workflows/ci.yml", token)
    query_args = {"per_page": 100}
    if len(sha) == 40:
        query_args["head_sha"] = sha
    query = urllib.parse.urlencode(query_args)
    runs = api_json(f"/actions/workflows/{workflow['id']}/runs?{query}", token).get("workflow_runs", [])
    runs = [run for run in runs if run.get("head_sha", "").startswith(sha)]
    if not runs:
        print(f"No ci.yml run found for {sha}.")
        return
    run = max(runs, key=lambda item: (item.get("created_at", ""), item["id"]))
    if run.get("status") != "completed":
        print(f"ci.yml run {run['id']} for {sha} is {run['status']}: {run['html_url']}")
        return
    print_compact_summary(run, token)

def cancel_queued(args, token: str) -> None:
    query = {"per_page": 100, "status": "queued"}
    runs = api_json("/actions/runs?" + urllib.parse.urlencode(query), token).get("workflow_runs", [])
    selected = [
        run for run in runs
        if run.get("head_branch") == args.branch
        and (not args.workflow or run.get("path", "").endswith(args.workflow))
        and (not args.sha or not run.get("head_sha", "").startswith(args.sha))
    ]
    for run in selected:
        try:
            status, _, _ = request(f"/actions/runs/{run['id']}/cancel", token, method="POST")
        except RuntimeError as error:
            if "HTTP 409" in str(error):
                print(f"Skipped run {run['run_number']} sha={run['head_sha'][:12]} because it was no longer queued.")
                continue
            raise
        print(f"Cancelled queued run {run['run_number']} sha={run['head_sha'][:12]} (HTTP {status}): {run['html_url']}")
    if not selected:
        print(f"No queued runs to cancel on {args.branch}.")

def print_run(run, token: str, show_logs: bool) -> None:
    print(f"Run {run['run_number']} {run['name']} {run['conclusion'] or run['status']} sha={run['head_sha'][:12]}")
    print(f"  {run['html_url']}")
    jobs = api_json(f"/actions/runs/{run['id']}/jobs?per_page=100", token).get("jobs", [])
    for job in jobs:
        duration = ""
        if job.get("started_at") and job.get("completed_at"):
            from datetime import datetime
            start = datetime.fromisoformat(job["started_at"].replace("Z", "+00:00"))
            end = datetime.fromisoformat(job["completed_at"].replace("Z", "+00:00"))
            duration = f" ({(end-start).total_seconds()/60:.1f} min)"
        print(f"  job {job['name']}: {job['conclusion'] or job['status']}{duration}")
        failed = [step for step in job.get("steps", []) if step.get("conclusion") == "failure"]
        for step in failed:
            print(f"    failing step: {step['name']}")
            if show_logs:
                try:
                    payload, content_type = download_job_logs(job["id"], token)
                    lines = failing_step_tail(payload, content_type, job["id"], step)
                    if lines:
                        print("      " + "\n      ".join(lines))
                except RuntimeError as error:
                    print(f"    log tail unavailable: {error}")
                except (urllib.error.URLError, zipfile.BadZipFile):
                    print("    log tail unavailable: the log download could not be read")

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    status = sub.add_parser("status", help="list runs, jobs, and failing step log tails")
    status.add_argument("--branch")
    status.add_argument("--sha")
    status.add_argument("--workflow")
    status.add_argument("--limit", type=int, default=10)
    status.add_argument("--no-logs", action="store_true")
    cancel = sub.add_parser("cancel-queued", help="cancel queued runs for a branch")
    cancel.add_argument("--branch", default="main")
    cancel.add_argument("--workflow")
    cancel.add_argument("--sha", help="preserve runs for this commit SHA")
    send = sub.add_parser("dispatch", help="dispatch a workflow")
    send.add_argument("workflow", choices=["ci.yml", "ci-windows.yml", "live-slow.yml"])
    send.add_argument("--ref", default="main")
    send.add_argument("--jobs", default="all", help="ci.yml job selection")
    send.add_argument("--test-file", default="all", help="live-slow.yml filename or all")
    agent = sub.add_parser("dispatch-agent-test", help="dispatch a selected agent test run")
    agent.add_argument("--ref", required=True, help="source branch or commit SHA to check out")
    agent.add_argument("--projects", required=True, help="comma-separated test project names, all, or empty to skip")
    agent.add_argument("--filter", default="", help="dotnet test filter")
    agent.add_argument("--live", required=True, help="comma-separated headless test files, fast, slow, all, or empty to skip")
    agent.add_argument("--rust", required=True, choices=["none", "tests", "clippy", "all"])
    wait = sub.add_parser("wait", help="poll a workflow run and print its failure summary")
    wait.add_argument("run_id", type=int)
    failures = sub.add_parser("failures", help="print failing tests from the ci.yml run for a commit")
    failures.add_argument("sha")
    args = parser.parse_args()
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if not token:
        parser.error("set GH_TOKEN or GITHUB_TOKEN in the environment")
    try:
        if args.command == "dispatch":
            dispatch(args, token)
            return 0
        if args.command == "dispatch-agent-test":
            dispatch_agent_test(args, token)
            return 0
        if args.command == "wait":
            return wait_for_run(args.run_id, token)
        if args.command == "failures":
            print_ci_failures(args.sha, token)
            return 0
        if args.command == "cancel-queued":
            cancel_queued(args, token)
            return 0
        query = {"per_page": 100}
        if args.workflow:
            workflow = api_json(f"/actions/workflows/{urllib.parse.quote(args.workflow)}", token)
        runs = api_json("/actions/runs?" + urllib.parse.urlencode(query), token).get("workflow_runs", [])
        if args.workflow:
            runs = [run for run in runs if run.get("path") == workflow.get("path")]
        if args.branch:
            runs = [run for run in runs if run.get("head_branch") == args.branch]
        if args.sha:
            runs = [run for run in runs if run.get("head_sha", "").startswith(args.sha)]
        runs = runs[: max(1, min(args.limit, 100))]
        for run in runs:
            print_run(run, token, not args.no_logs)
        return 0
    except RuntimeError as error:
        print(error, file=sys.stderr)
        return 1

if __name__ == "__main__":
    raise SystemExit(main())
