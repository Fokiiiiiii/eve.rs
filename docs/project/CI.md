# GitHub Actions CI

`ci.yml` runs its Linux jobs on pushes to `main` except documentation-only changes and `.github/dependabot.yml`. Push runs do not cancel the active run; GitHub retains the newest pending run for the ref. Manual `workflow_dispatch` runs can cancel older manual runs and select jobs. Windows coverage is selected for platform-sensitive paths, a manual `windows` selection, and the weekly schedule. It provisions PostgreSQL 18 on the Windows runner and runs Rust, .NET, Python, and a login-to-character walk. `live-slow.yml` is dispatch-only and runs each selected headless-client test file in its own matrix job. The CI workflow builds and tests with Rust 1.99.0; `Cargo.toml` retains the Rust 1.94 minimum supported version.

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
python3 tools/dev/ci-status.py dispatch ci.yml --ref main --jobs windows
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

Runs 92, 93, 99, and 101 failed in the fast end-to-end job at `tools/headless-client/tests/test_login_to_character.py::test_the_recorded_walk_replays`: step 5 `ToggleValidation[true]` returned false where the walk expected true. Run 93 also failed during a structures undock call with Macho error 2; Run 101 did not reproduce that undock failure. Runs 92 and 93 reported 69 failures out of 492 tests in each .NET Host shard. The shared exception was `System.ArgumentException: Delegate to an instance method cannot have null 'this'` while composing `InventoryRpc`. Run 98 also found `xUnit1013` on the PostgreSQL-backed `Station_inventory_rpcs_list_owned_items_and_set_a_ship_label` test added in merged main changes.

The Host exception came from evaluating `spaceSessions!.PrepareSpaceDogmaAsync` while composing `InventoryRpc`; character-only compositions can have no space session provider. `InventoryRpc` now supplies that optional callback only when the provider exists. The focused `StructureOfficeTests.A_director_gives_an_office_up_and_its_items_go_to_asset_safety_while_a_member_cannot` test passed locally. The inventory label test now has the required `[PgFact]` attribute, and its focused PostgreSQL test passed locally. The recorded-walk fixture grants GM roles to its account for the slash-command walks, so this test is also a GM session. Its recorded first `ToggleValidation` result is false; the test had incorrectly overridden that expectation to true. The expected result now follows the recorded GM role behavior; the focused `ValidationAndOmegaTests` suite passes locally (4 tests). Run 101's .NET build passed after the test attribute correction. Run 108 passed the live suite, including the restored false expectation for the recorded GM session; the earlier structures undock error did not recur. Run 108's .NET all other tests shard failed in `Eve.Ships.Tests.ModuleBonusTests.Armor_shield_and_mining_burst_strength_uses_the_level_five_sde_skill_bonus`: it tried to fit command bursts on Rifter type 587, which the SDE fitting rules refuse. The test now uses Drake for armor and shield bursts and Rorqual with level-five Capital Industrial Ships for the mining burst. Its level-five SDE result is 50 from a base 40 and the 25% skill bonus. The focused PostgreSQL test passes locally. Run 121 on `96c48594` exposed a malformed `actions/cache` SHA in the workflow pin update (`55cc834e…`); the published `v6.1.0` commit is `55cc8345863c7cc4c66a329aec7e433d2d1c52a9`, and the CI and slow-live workflows now use that SHA. The setup errors were corrected in `a7c25a09` and `78517bec`: the toolchain action now receives its explicit stable input, and the cache action uses its published full commit ID. Run 123 on `10bdf3d1` passed the required gate: Rust format/clippy, Rust tests, fixtures, Python, end-to-end, .NET build, and all three .NET shards succeeded. Run 127 on `913e4d41` passed every job except the Host shard, where `NewEdenEntitlementHostTests.An_omega_offer_extends_the_end_time_announces_the_grade_and_a_replayed_purchase_extends_nothing` compared a millisecond-precision purchase timestamp against sub-millisecond wall-clock bounds. The runtime captures purchase time as Unix milliseconds; the assertion now normalizes its before/after bounds to that precision. The next full-gate run will check this correction.
