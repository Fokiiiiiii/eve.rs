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

The token can list runs and jobs (`GET /actions/runs` and `GET /actions/runs/{run_id}/jobs`, HTTP 200), dispatch both workflows (HTTP 204), and download a current failed job log (`GET /actions/jobs/{job_id}/logs`, HTTP 200). A request for an older job log returned HTTP 401. The helper reports log-download errors while retaining run, job, and failed-step information. If dispatch is denied elsewhere, it prints the HTTP status and API response.

## Minutes budget

Record the sum of runner job durations for validation runs below. Matrix jobs count separately even when they run concurrently. Keep roughly 300 Actions minutes in reserve.

| Run | Commit | Result | Sum of job durations |
|---|---|---|---|
| Baseline manual run 80 | [`235ee85a`](https://github.com/Fokiiiiiii/eve.rs/actions/runs/38888008879) | success, 2026-10-16 | 71.7 runner-minutes |
| CI manual Python run 89 | [`f3d1bbca`](https://github.com/Fokiiiiiii/eve.rs/actions/runs/39208510614) | success | 0.3 runner-minutes |
| Live slow push run 12 | [`f3d1bbca`](https://github.com/Fokiiiiiii/eve.rs/actions/runs/39207623961) | workflow validation failure | 0.0 runner-minutes |
| CI push run 90 | [`53939261`](https://github.com/Fokiiiiiii/eve.rs/actions/runs/39208812104) | superseded while pending | 0.0 runner-minutes |
| Live slow push run 13 | [`53939261`](https://github.com/Fokiiiiiii/eve.rs/actions/runs/39208810738) | workflow validation failure | 0.0 runner-minutes |
| CI push run 91 | [`89b026a4`](https://github.com/Fokiiiiiii/eve.rs/actions/runs/39209115523) | superseded while pending | 0.0 runner-minutes |
| Live slow manual run 14 | [`89b026a4`](https://github.com/Fokiiiiiii/eve.rs/actions/runs/39209122955) | selected `test_marshal_golden.py` passed | 6.9 runner-minutes |
| CI push run 92 | [`a46d9d31`](https://github.com/Fokiiiiiii/eve.rs/actions/runs/39209512962) | failed tests; details below | 65.5 runner-minutes |
| CI push run 93 | [`a1c2820c`](https://github.com/Fokiiiiiii/eve.rs/actions/runs/39211003632) | repeated live and Host test failures | 71.5 runner-minutes |

Runs 92 and 93 both failed in the fast end-to-end job at `tools/headless-client/tests/test_login_to_character.py::test_the_recorded_walk_replays`: step 5 `ToggleValidation[true]` returned false where the walk expected true. Run 93 also reported `TypeError` failures in the undock walk. Both .NET Host shards reported 69 failures out of 492 tests; the first was `Eve.Host.Tests.StructureOfficeTests.A_director_gives_an_office_up_and_its_items_go_to_asset_safety_while_a_member_cannot`. The Host failures share `System.ArgumentException: Delegate to an instance method cannot have null 'this'`, thrown while composing `InventoryRpc`. Rust format/clippy, Rust tests, Python tests, fixtures, the .NET Space shard, and the remaining .NET test shard passed in both runs. The Required CI gate recorded failure from these test results.
