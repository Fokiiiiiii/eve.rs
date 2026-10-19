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
    if zipfile.is_zipfile(io.BytesIO(payload)):
        with zipfile.ZipFile(io.BytesIO(payload)) as archive:
            suffix = f"{step['number']}_{step['name']}.txt"
            candidates = [name for name in archive.namelist() if name.endswith(suffix)]
            if not candidates:
                candidates = [name for name in archive.namelist() if name.endswith(".txt")]
            if not candidates:
                return []
            return archive.read(candidates[0]).decode("utf-8", "replace").splitlines()[-40:]

    lines = payload.decode("utf-8", "replace").splitlines()
    start, end = step.get("started_at"), step.get("completed_at")
    if start and end:
        # Job logs prefix every line with an RFC3339 timestamp. Isolate the failing step's time window.
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
            return window[-40:]
    return lines[-40:]


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
    args = parser.parse_args()
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if not token:
        parser.error("set GH_TOKEN or GITHUB_TOKEN in the environment")
    try:
        if args.command == "dispatch":
            dispatch(args, token)
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
