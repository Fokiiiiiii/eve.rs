# GitHub Actions CI

## Workflows

`.github/workflows/ci.yml` runs on each push to `main`, except changes limited to `docs/**`, Markdown files, and `.github/dependabot.yml`. Its `gate` job is the required status to read. The workflow checks Rust formatting and clippy, Rust tests, .NET tests in parallel database-isolated shards, Python client tests, generated fixtures, and fast live walks.

Manual CI runs accept `jobs` as a comma-separated selection: `all` (default), `rust-fmt`, `rust-test`, `dotnet`, `python`, `fixtures`, or `live`. Example: `tools/dev/ci-status.py dispatch ci.yml --jobs rust-test`.

`.github/workflows/live-slow.yml` stays manual. Each headless-client test file has its own runner and Postgres service. Set `test_file` to `all` or one exact file name, for example `test_walk_industry.py`. Dispatch with `tools/dev/ci-status.py dispatch live-slow.yml --test-file test_walk_industry.py`.

## Inspecting runs

The standard library helper needs `GH_TOKEN` or `GITHUB_TOKEN`; it never prints or stores the token. Examples:

```sh
tools/dev/ci-status.py status --branch main --limit 5
tools/dev/ci-status.py status --sha <commit-sha>
tools/dev/ci-status.py dispatch ci.yml --ref main --jobs all
tools/dev/ci-status.py dispatch live-slow.yml --ref main --test-file test_walk_industry.py
```

Status lists runs and per-job outcomes. When a job has a failed step, it identifies that step and tries to print the last 40 lines from its log. `--no-logs` skips log downloads. GitHub job-log access can require additional token scope; the initial access check returned HTTP 401 for `GET /actions/jobs/{job_id}/logs`, while run and job metadata returned HTTP 200.

## Actions minutes

Record each CI run's total elapsed job time (sum of job durations, including parallel jobs) here after the gate completes. Keep approximately 300 minutes in reserve. Cancel superseded runs when needed; per-ref concurrency already cancels older CI runs after a newer push.

| Run | Commit | Gate | Sum of job durations |
|---|---|---|---|
| Baseline before push-triggered CI | `235ee85a882f2a5a36067192874d842f1ec22828` | Success, manual run [38888008879](https://github.com/Fokiiiiiii/eve.rs/actions/runs/38888008879), 2026-10-16 | 71.7 min |
