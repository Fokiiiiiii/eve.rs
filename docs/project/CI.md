# GitHub Actions CI

`ci.yml` runs on pushes to `main` except documentation-only changes and `.github/dependabot.yml`. Push runs do not cancel the active run; GitHub retains the newest pending run for the ref. Manual `workflow_dispatch` runs can cancel older manual runs. `live-slow.yml` is dispatch-only and runs each selected headless-client test file in its own matrix job.

## Read the result for a commit

Set `GH_TOKEN` or `GITHUB_TOKEN` in the environment, then run:

```sh
python3 tools/dev/ci-status.py status --branch main
python3 tools/dev/ci-status.py status --sha <commit-sha>
python3 tools/dev/ci-status.py status --sha <commit-sha> --workflow ci.yml
```

The status command lists run URLs, per-job conclusions and durations, failing steps, and a failing-step log tail when the token can download job logs. To save output while a run is still active, add `--no-logs`.

Push concurrency keeps only one active run and the newest pending run for `main`. If an exact SHA no longer has a run because a newer push replaced its pending run, read the newest `main` run instead: it contains the current tree plus the superseded commit's changes. Check the run's `head_sha` and ensure no later push has reverted those changes before using its result as evidence.

## Dispatch

Dispatch the full CI gate or a selected subset:

```sh
python3 tools/dev/ci-status.py dispatch ci.yml --ref main --jobs all
python3 tools/dev/ci-status.py dispatch ci.yml --ref main --jobs python
```

Dispatch one slow test or the full slow matrix:

```sh
python3 tools/dev/ci-status.py dispatch live-slow.yml --ref main --test-file test_walk_industry.py
python3 tools/dev/ci-status.py dispatch live-slow.yml --ref main --test-file all
```

The token in this environment can list runs and jobs (`GET /actions/runs` and `GET /actions/runs/{run_id}/jobs`, HTTP 200), and it can dispatch workflows (`POST /actions/workflows/ci.yml/dispatches`, HTTP 204). Its job-log request (`GET /actions/jobs/{job_id}/logs`) returned HTTP 401. The helper reports that log download failure while retaining the run, job, and failed-step information. If dispatch is denied elsewhere, it prints the HTTP status and API response.

## Minutes budget

Record the sum of completed job durations for each CI run used to validate workflow changes below. Job duration is the sum of its runner jobs, so concurrent matrix jobs count separately. Keep roughly 300 Actions minutes in reserve when iterating.

| Run | Commit | Result | Sum of job durations |
|---|---|---|---|
| Baseline manual run 80 | [`235ee85a`](https://github.com/Fokiiiiiii/eve.rs/actions/runs/38888008879) | success, 2026-10-16 | 71.7 runner-minutes |
