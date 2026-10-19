#!/usr/bin/env python3
"""Inspect or dispatch this repository's GitHub Actions workflows."""
from __future__ import annotations

import argparse
import io
import json
import os
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
                    _, payload, _ = request(f"/actions/jobs/{job['id']}/logs", token)
                    with zipfile.ZipFile(io.BytesIO(payload)) as archive:
                        candidates = [n for n in archive.namelist() if n.endswith(f"{step['number']}_{step['name']}.txt")]
                        if not candidates:
                            candidates = [n for n in archive.namelist() if n.endswith(".txt")]
                        for name in candidates[:1]:
                            lines = archive.read(name).decode("utf-8", "replace").splitlines()
                            print("      " + "\n      ".join(lines[-40:]))
                except (RuntimeError, zipfile.BadZipFile) as error:
                    print(f"    log tail unavailable: {error}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    status = sub.add_parser("status", help="list runs, jobs, and failing step log tails")
    status.add_argument("--branch")
    status.add_argument("--sha")
    status.add_argument("--workflow")
    status.add_argument("--limit", type=int, default=10)
    status.add_argument("--no-logs", action="store_true")
    send = sub.add_parser("dispatch", help="dispatch a workflow")
    send.add_argument("workflow", choices=["ci.yml", "live-slow.yml"])
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
