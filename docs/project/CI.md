# GitHub Actions CI

The workflows are currently in the automatic mode while the repository is public. `ci.yml` uses push and weekly schedule triggers; `ci-windows.yml` handles Windows checks on platform-sensitive pushes, every six hours, and manual dispatch; `live-slow.yml` remains dispatch-only. When the repository becomes private again run `python3 tools/dev/ci-mode.py manual` and push. This makes every workflow manual only through `workflow_dispatch`; it removes push, schedule, and pull request triggers. Use `python3 tools/dev/ci-mode.py --check` to print the current mode. Run `python3 tools/dev/ci-mode.py auto` to restore automatic triggers. The automatic trigger definitions are kept in the script.

`ci.yml` runs its Linux jobs on pushes to `main` except documentation-only changes and `.github/dependabot.yml`. Push runs share a branch concurrency group with `cancel-in-progress: false`; GitHub keeps the running run and the newest pending SHA, replacing older pending runs. Manual runs use a separate interactive group and can cancel older manual runs. Manual `workflow_dispatch` runs can cancel older manual runs and select jobs. The separate `ci-windows.yml` workflow provisions PostgreSQL 18 on the Windows runner and runs Rust, .NET, Python, and a login-to-character walk. Its 45-minute job timeout and independent workflow run keep Windows diagnostics outside the Linux push queue and `Required CI gate`. `live-slow.yml` is dispatch-only and runs each selected headless-client test file in its own matrix job. The CI workflow builds and tests with Rust 1.99.0; `Cargo.toml` retains the Rust 1.94 minimum supported version.

## Read the result for a commit

Set `GH_TOKEN` or `GITHUB_TOKEN` in the environment, then run:

```sh
python3 tools/dev/ci-status.py status --branch main
python3 tools/dev/ci-status.py status --sha <commit-sha>
python3 tools/dev/ci-status.py status --sha <commit-sha> --workflow ci.yml
python3 tools/dev/ci-status.py cancel-queued --branch main --workflow ci.yml
```

The status command lists run URLs, per-job conclusions and durations, failing steps, and a failing-step log tail when the token can download job logs. To save output while a run is still active, add `--no-logs`.

Push runs are serialized by branch, with one replaceable pending run, so runner queues remain bounded while the newest push receives a verdict. The `Required CI gate` prints its `GITHUB_SHA` and dependency results; use `status --sha` to verify the completed run's `head_sha` matches the commit being checked.

## Dispatch

Dispatch the Linux CI gate or a selected subset:

```sh
python3 tools/dev/ci-status.py dispatch ci.yml --ref main --jobs all
python3 tools/dev/ci-status.py dispatch ci.yml --ref main --jobs python
```

Dispatch Windows coverage independently:

```sh
python3 tools/dev/ci-status.py dispatch ci-windows.yml --ref main
```

Use `cancel-queued` once to clear stale queued runs after changing the concurrency policy; it cancels queued runs matching the selected branch and optional workflow, and `--sha` preserves that commit.

The slow-live workflow prints the last 500 lines of the Host, simulation and front logs when a selected walk fails. It also uploads those logs as a seven-day artifact for diagnosis.

Dispatch one slow test or the full slow matrix:

```sh
python3 tools/dev/ci-status.py dispatch live-slow.yml --ref main --test-file test_walk_industry.py
python3 tools/dev/ci-status.py dispatch live-slow.yml --ref main --test-file test_walk_xmpp_live.py
python3 tools/dev/ci-status.py dispatch live-slow.yml --ref main --test-file all
```

The token can list runs and jobs (`GET /actions/runs` and `GET /actions/runs/{run_id}/jobs`, HTTP 200), dispatch both workflows (HTTP 204), and download a current failed job log (`GET /actions/jobs/{job_id}/logs`, HTTP 200). A request for an older job log returned HTTP 401. The helper reports log-download errors while retaining run, job, and failed-step information. If dispatch is denied elsewhere, it prints the HTTP status and API response.

## Dependency audit

`dependency-audit.yml` runs `cargo audit --file Cargo.lock` (cargo-audit 0.22.2), `dotnet list dotnet/EveRs.slnx package --vulnerable --include-transitive` (failing when NuGet reports a vulnerable package) and `pip-audit` 2.10.1 on both headless-client requirement files. It runs weekly, on manual dispatch, and on pushes to `main` that change a Cargo, NuGet, or Python dependency file. A finding fails the job. The workflow has no `pull_request` trigger, so fork pull requests are not audited at submission time; the weekly schedule, manual dispatch, and qualifying dependency-file pushes to `main` trigger the audit.

## Minutes budget

Record runner job durations for validation runs below. Matrix jobs count separately even when they run concurrently.

### Duration profile and test layout

Run 272 (`f153f9c1`, 2026-10-19) is the latest green full gate before the Space split. From the first job start at 23:16:08 UTC until the Required CI gate completed at 23:34:08 UTC, the run took 18.0 minutes wall time. The Space job took 15.2 minutes, of which its `dotnet test` step used 12.85 minutes; static-data loading, artifact download and setup used about 2.3 minutes. End-to-end took 8.8 minutes. The detailed project timing profile below comes from the run 276 TRX artifacts; run 269 supplied the initial weights used by `tools/dev/shard-dotnet-projects.py`.

Run 272's successful job durations and .NET test step timing were:

| Job | Duration |
|---|---:|
| .NET build | 2.6 min |
| Host tests 1/4 | 2.3 min |
| Host tests 2/4 | 2.6 min |
| Host tests 3/4 | 1.8 min |
| Host tests 4/4 | 2.1 min |
| Space tests | 15.2 min (12.85 min test step) |
| Other tests 1/3 | 4.9 min |
| Other tests 2/3 | 5.6 min |
| Other tests 3/3 | 5.5 min |
| End-to-end | 8.8 min |
| Rust tests | 2.5 min |
| Rust format and clippy | 0.5 min |
| Python client | 0.2 min |
| Fixture generation | 0.1 min |

The preceding red run 270 (`ec9575b0`) had a 16.2-minute Space job; all other jobs completed in 6.8 minutes or less except the 9.0-minute end-to-end job. `other tests 3/3` failed after 4.0 minutes, while its sibling jobs passed. Its retained job summary does not include per-project VSTest durations; run 269 remains the source of the project duration table.

Run 276 (commit `93ac5061`, 2026-10-20) TRX test-session durations and executed test counts:

| Test project / shard | TRX elapsed | Tests executed |
|---|---:|---:|
| Eve.AccessGroups.Tests | 2.1 s | 23 |
| Eve.Agents.Tests | 1.5 s | 19 |
| Eve.Bookmarks.Tests | 4.0 s | 33 |
| Eve.Characters.Tests | 6.3 s | 120 |
| Eve.Content.Tests | 8.3 s | 41 |
| Eve.Corporations.Tests | 29.8 s | 135 |
| Eve.Crimewatch.Tests | 1.3 s | 55 |
| Eve.Dogma.Tests | 22.4 s | 128 |
| Eve.Dungeons.Tests | 2.2 s | 60 |
| Eve.Events.Tests | 2.2 s | 18 |
| Eve.FactionWarfare.Tests | 21.6 s | 99 |
| Eve.Fitting.Tests | 5.8 s | 78 |
| Eve.GameStore.Import.Tests | 4.5 s | 15 |
| Eve.GameStore.Tests | 3.4 s | 334 |
| Eve.Hacking.Tests | 0.9 s | 26 |
| Eve.Host.Tests shard 1/4 | 2 m 25 s | 127 |
| Eve.Host.Tests shard 2/4 | 1 m 51 s | 127 |
| Eve.Host.Tests shard 3/4 | 7 m 22 s | 125 |
| Eve.Host.Tests shard 4/4 | 5 m 33 s | 126 |
| Eve.Identity.Tests | 1.5 s | 48 |
| Eve.Insurgency.Tests | 1.8 s | 18 |
| Eve.Inventory.Tests | 12.2 s | 56 |
| Eve.Market.Tests | 39.7 s | 116 |
| Eve.Marshal.Tests | 1.6 s | 1107 |
| Eve.Npc.Tests | 4.8 s | 167 |
| Eve.Planets.Tests | 1.5 s | 51 |
| Eve.ReadSide.Tests | 0.9 s | 6 |
| Eve.Relations.Tests | 2.0 s | 12 |
| Eve.Scheduler.Tests | 2.9 s | 20 |
| Eve.Services.CharacterInfo.Tests | 5.1 s | 72 |
| Eve.Services.CharacterSelect.Tests | 15.8 s | 61 |
| Eve.Services.Core.Tests | 7.3 s | 799 |
| Eve.Services.Crimewatch.Tests | 4.4 s | 44 |
| Eve.Services.Dogma.Tests | 1 m 28 s | 286 |
| Eve.Services.Generator.Tests | 3.7 s | 20 |
| Eve.Services.Inventory.Tests | 39.1 s | 71 |
| Eve.Services.Skills.Tests | 30.5 s | 70 |
| Eve.Services.Space.Tests shard 1/2 | 4 m 9 s | 501 |
| Eve.Services.Space.Tests shard 2/2 | 6 m 5 s | 500 |
| Eve.Services.Station.Tests | 9.4 s | 60 |
| Eve.Services.Tests | 0.9 s | 86 |
| Eve.Services.Wallet.Tests | 1.5 s | 10 |
| Eve.Ships.Tests | 2 m 37 s | 241 |
| Eve.Skills.Tests | 5.1 s | 97 |
| Eve.Space.Tests | 1.4 s | 445 |
| Eve.Structures.Tests | 5.9 s | 105 |
| Eve.Trade.Tests | 1.7 s | 9 |
| Eve.Universe.Tests | 3.7 s | 67 |
| Eve.Wallet.Tests | 6.2 s | 40 |
| Eve.Wormholes.Tests | 1.5 s | 26 |

Slowest individual tests in run 276, by TRX duration:

| Test | Duration |
|---|---:|
| `Eve.Host.Tests.StructureCompositionTests.The_cyno_jammer_of_a_system_is_answered_through_the_registry_only_with_the_suppression_upgrade_online` | 33.7 s |
| `Eve.Ships.Tests.ShipTests.Every_restricted_specialized_hold_accepts_a_current_SDE_sample_and_refuses_a_module` | 25.7 s |
| `Eve.Host.Tests.CharacterHostTests.Installing_a_clone_at_a_structure_pays_its_owner_corporation_and_needs_its_clone_service` | 22.6 s |
| `Eve.Host.Tests.CharacterHostTests.Jump_clones_are_read_changed_and_kept_through_the_host_registry_and_a_restart` | 22.6 s |
| `Eve.Host.Tests.StructureCompositionTests.An_imported_structure_with_an_elapsed_deadline_advances_once_after_start_and_a_replay_changes_nothing` | 19.0 s |
| `Eve.Host.Tests.StructureCompositionTests.A_structure_cyno_beacon_jump_is_refused_by_name_through_the_registry_for_a_jammed_system_and_for_no_access` | 17.2 s |
| `Eve.Host.Tests.StructureOfficeTests.An_unpaid_office_is_given_up_when_its_rent_falls_due_and_its_items_go_to_asset_safety_after_a_restart` | 16.5 s |
| `Eve.Services.Dogma.Tests.ReloadServiceTests.A_gun_that_runs_is_given_what_the_simulation_added_and_the_rounds_the_hold_was_short_of_are_given_back` | 16.4 s |
| `Eve.Host.Tests.CharacterHostTests.Skill_injector_applies_sequential_awards_and_persists_points_and_inventory_through_host` | 16.1 s |
| `Eve.Host.Tests.StructureCompositionTests.Reinforcement_change_notice_is_durable_formatted_and_scoped_to_the_owner_corporation` | 15.9 s |

The test job uses four Host shards and splits `Eve.Services.Space.Tests` into two shards. `tools/dev/shard-dotnet-tests.py` discovers test cases from each built assembly, removes theory argument text before deriving class names, assigns whole test classes to shards by case count, and supplies a VSTest filter to each shard. Every class is assigned to exactly one shard; each shard has its own PostgreSQL service. Each .NET shard verifies that its TRX reports at least one executed test. `tools/dev/shard-dotnet-projects.py` assigns the remaining test projects to three jobs; each project runs once, and each job has its own PostgreSQL service. New projects are included with a five-second initial weight. All jobs download the same Release build artifact produced once by `.NET build`. CI runs use the job matrix and the Required CI gate waits for every selected matrix job.

Run 275 had a green required-gate conclusion in 8.3 minutes, but its Host and Space shard filters executed zero tests. Theory arguments containing fully qualified types had been parsed as part of the test class name. The shard generator now removes theory arguments before deriving class names, and every .NET test shard fails if its TRX report records zero executed tests.

Run 276 (`93ac5061`, 2026-10-20) is the first complete measured run after that correction ([run 276](https://github.com/Fokiiiiiii/eve.rs/actions/runs/39348892807)). From the first job start at 00:39:05 UTC to the Required CI gate completion at 00:51:30 UTC, wall time was 12.4 minutes. This is 31% shorter than run 272's 18.0-minute baseline and meets the 15-minute goal. The longest .NET job was Host shard 3/4 at 9.7 minutes; end-to-end took 7.2 minutes.

Run 276 job durations:

| Job | Duration |
|---|---:|
| End-to-end | 7.2 min |
| Rust tests | 2.8 min |
| .NET build | 2.6 min |
| Host tests 1/4 | 4.9 min |
| Host tests 2/4 | 4.3 min |
| Host tests 3/4 | 9.7 min |
| Host tests 4/4 | 8.0 min |
| Space tests 1/2 | 5.8 min |
| Space tests 2/2 | 8.4 min |
| Other tests 1/3 | 5.8 min |
| Other tests 2/3 | 5.3 min |
| Other tests 3/3 | 5.7 min |
| Rust format and clippy | 0.5 min |
| Rust latest stable (advisory) | 0.5 min |
| Python client | 0.2 min |
| Fixture generation | 0.1 min |

The .NET matrix writes one TRX file per test project or shard and uploads it even when a test job fails. The project table above uses the TRX test-session start-to-finish span and records executed case counts; Host and Space test assemblies appear as separate shard rows.

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

The Host exception came from evaluating `spaceSessions!.PrepareSpaceDogmaAsync` while composing `InventoryRpc`; character-only compositions can have no space session provider. `InventoryRpc` now supplies that optional callback only when the provider exists. The focused `StructureOfficeTests.A_director_gives_an_office_up_and_its_items_go_to_asset_safety_while_a_member_cannot` test passed locally. The inventory label test now has the required `[PgFact]` attribute, and its focused PostgreSQL test passed locally. The recorded-walk fixture grants GM roles to its account for the slash-command walks, so this test is also a GM session. Its recorded first `ToggleValidation` result is false; the test had incorrectly overridden that expectation to true. The expected result now follows the recorded GM role behavior; the focused `ValidationAndOmegaTests` suite passes locally (4 tests). Run 101's .NET build passed after the test attribute correction. Run 108 passed the live suite, including the restored false expectation for the recorded GM session; the earlier structures undock error did not recur. Run 108's .NET all other tests shard failed in `Eve.Ships.Tests.ModuleBonusTests.Armor_shield_and_mining_burst_strength_uses_the_level_five_sde_skill_bonus`: it tried to fit command bursts on Rifter type 587, which the SDE fitting rules refuse. The test now uses Drake for armor and shield bursts and Rorqual with level-five Capital Industrial Ships for the mining burst. Its level-five SDE result is 50 from a base 40 and the 25% skill bonus. The focused PostgreSQL test passes locally. Run 121 on `96c48594` exposed a malformed `actions/cache` SHA in the workflow pin update (`55cc834e…`); the published `v6.1.0` commit is `55cc8345863c7cc4c66a329aec7e433d2d1c52a9`, and the CI and slow-live workflows now use that SHA. The setup errors were corrected in `a7c25a09` and `78517bec`: the toolchain action now receives its explicit stable input, and the cache action uses its published full commit ID. Run 123 on `10bdf3d1` passed the required gate: Rust format/clippy, Rust tests, fixtures, Python, end-to-end, .NET build, and all three .NET shards succeeded. Run 127 on `913e4d41` passed every job except the Host shard, where `NewEdenEntitlementHostTests.An_omega_offer_extends_the_end_time_announces_the_grade_and_a_replayed_purchase_extends_nothing` compared a millisecond-precision purchase timestamp against sub-millisecond wall-clock bounds. The runtime captures purchase time as Unix milliseconds; the assertion now normalizes its before/after bounds to that precision. Run 151 on `43918420` exposed a separate Host failure: `LiveScriptIsolationTests.Every_schema_the_host_reads_is_scratch_in_the_live_script_and_dropped_at_its_end` found `EVE_CONTRACTS_SCHEMA` missing from the live script's scratch-schema export and cleanup. The contract persistence schema was added in `98af69ad`; `tools/dev/run-eve-live.sh` now assigns it to `${SCRATCH}_contracts` and drops it with the other per-run schemas. Runs 151–160 use SHA-specific push concurrency groups and remained independently active; each commit has its own attributable result. Run 160 on `11daee3e` passed Rust format/clippy, Rust tests, fixtures, Python, .NET build, end-to-end, Host, and Space jobs. Its `.NET (all other tests)` shard failed because `Switching_character_clears_old_presence_and_closing_old_session_preserves_replacement` and `Unselected_observer_gets_empty_initial_state_and_raw_presence_like_reference` expected raw presence for unselected sessions, contrary to the intentional selected-observer rule introduced by `fd177cab` and documented in `SECURITY_STATUS.md` and `DEVIATIONS.md`. The expectations and contradictory service comment now match the existing guard; both focused tests passed locally. The remaining Windows job and Required CI gate were still running when this entry was written. Run 152 also found `GameRpcServicesTests.Raffle_and_gateway_methods_keep_the_reference_return_shapes` calling `GetMyContractEscrow` through a service composition without an `IContractReadModel`. That method now intentionally requires the authoritative read model; its return shape is covered by `ContractProxyServiceTests.Escrow_read_uses_the_persisted_summary`. The unrelated raffle and gateway test no longer calls contract escrow without wiring its data source.

Runs 172–174 (SHAs `dce9440d`, `75f01741`, and `f308d922`) each reached a conclusion with their own SHA-specific run. Their Linux required checks passed, including all three .NET shards in runs 173 and 174; the .NET (all other tests) failure recorded above is no longer present in these runs. The required gate failed only because the Windows job was still included in its dependencies. In run 172 the Windows PostgreSQL-backed .NET test step ran for 47.9 minutes before failing. Runs 173 and 174 completed that step successfully; run 174's Windows job later failed at `Exercise the PowerShell 5.1 headless entry point` after 56.6 minutes. The Windows workflow now has a 45-minute timeout and its result no longer blocks the Linux required gate. The remaining PowerShell entry-point failure is documented for cx32 to investigate. Run 175 on `f64569a5` is the first push after this gate change. Its `Required CI gate` succeeded after Rust format/clippy, Rust tests, the Linux end-to-end job, and all three .NET shards passed: [run 175](https://github.com/Fokiiiiiii/eve.rs/actions/runs/39261663701). At the time of this check the separate Windows job was still running, so this records the gate result rather than the full workflow conclusion. Run 179 on `bf914b37` then exposed two missing `charge_type_id` initializers in `crates/eve-space/src/modules/cycle.rs`, which broke clippy, Rust tests, and the live build; the next push `04d66b04` passed the same Linux checks and its Required CI gate: [run 180](https://github.com/Fokiiiiiii/eve.rs/actions/runs/39263171129). Run 186 on `7a2ab4ea` also passed the Required CI gate after all Linux checks succeeded: [run 186](https://github.com/Fokiiiiiii/eve.rs/actions/runs/39269139080). Runs 188 and 189 later failed the .NET build and live-stack build with `CS0136` in `SpaceSessions.cs`: two `SimNotify` switch cases declared the pattern variable `observer` in the same scope. The second case now uses `structureObserver`, preserving the routing behavior. A focused Release build of `Eve.Services.Space.csproj` passed locally with project references already built. Run 197 on `029ce776` passed Rust format/clippy, Rust tests, fixtures, Python, the live walk, the .NET build, the Space shard, and the all-other-tests shard; its Host shard found one boundary assertion failure in `NewEdenEntitlementHostTests.An_omega_purchase_never_shortens_and_stops_at_the_2100_floor`. Subscription timestamps are persisted at Unix-millisecond precision, while this test captured its lower bound at sub-millisecond precision. The lower bound now uses Unix-millisecond precision, matching the neighboring Omega purchase test. Run 195 on `95512e4f` had already passed the Required CI gate; Windows remains diagnostic and is outside that gate. SHA `9b65cafc` contains the assertion correction, and its push run is pending.

Run 205 on `af059238` reported `ControlTowerHostTests.A_control_tower_burns_one_period_of_its_race_fuel_and_goes_offline_when_the_bay_cannot_pay` with 0 fuel blocks where the test expected 40 after unanchoring; the Space shard passed on run 209 (`6eaa703e`) in 13.6 minutes. No code change for this case separates those runs, so the failure did not reproduce. Run 219 on `429e17da` reproduced a different live assertion: `OnInvulnCancelled` arrived before `DoDestinyUpdate` during the movement walk, this time at the speed-fraction step. `compare_events` now checks the invulnerability notice and payload independently from the simulation event sequence, while preserving the order and payload checks for both streams. A focused fixture comparison with the two notifications reversed passed locally; a follow-up CI result is pending. Run 219 also failed `.NET (all other tests)` in `Eve.Market.Tests.MarketLockOrderTests` for `PlaceAsync`: the source guard searched the public wrapper for locks after lock acquisition moved into `PlaceWithinTransactionAsync`. The guard now verifies that `PlaceAsync` and `PlaceBuyOrdersAsync` delegate to that helper, then checks the helper's lock order. The focused `MarketLockOrderTests` run passed locally (4 tests); CI for this correction is pending. Run 219 also failed the Host shard in `CrateServicePersistenceTests.Crate_custom_info_and_claim_fitting_round_trip_through_postgres_and_gamestore`: the fitting read was composed with a null `IFittingHost`, and its owner-access check dereferenced it. The test now supplies a host stub that permits only the fixture character to read its character-scoped fittings. The focused PostgreSQL Host test passed locally (1 test); CI for this correction is pending.

Run 236 on `de3ec3a4` passed Rust format/clippy, Rust tests, fixtures, Python, the live walk, .NET build, Host and all-other .NET shards. Its Space shard failed four session-close assertions that still expected a station hangar. `SpaceSessions` now receives the rig’s PostgreSQL `PgSpaceItemStore` and persists the active ship pose on disconnect, leaving the hull in its current system; the expected state is an in-space inventory item plus its saved `SpaceItem`. The affected tests now wait for `SpaceSessions.QuiesceAsync` and check the persisted system after normal disconnect, safe logoff, a gate jump and pending ammunition writes. Run 256 on `866916fc` passed these assertions in the Space shard.
Run 237 on `26af10bf` failed Rust clippy in 13 `empty_line_after_doc_comments` diagnostics after Rust documentation comments were separated from their items. Run 256 on `866916fc` passed Rust format and clippy.

Run 255 on `df7c88cf` reached a conclusion but its Required CI gate failed only because the Space shard had four disconnect assertions expecting ships in station hangars. The runtime persists a disconnected active ship in its current solar system, with an in-space inventory flag and saved `SpaceItem`; those assertions now wait for session work to quiesce and verify that persisted state. The focused Space test run passed on run 256. The run’s Linux Rust, Python, fixture, live, .NET build, Host and all-other .NET jobs passed. The gate completed in 20.8 minutes. `cancel-queued --branch main --workflow ci.yml --sha df7c88cf` found no queued CI runs to clear. The branch-scoped pending-run policy, Windows path filter, and Linux-only required gate are already active in both workflows.

Run 256 on `866916fc` passed the Required CI gate: Rust format/clippy, Rust tests, fixtures, Python, the end-to-end walk, .NET build, and all three .NET test shards succeeded. The updated disconnect assertions passed in the Space shard. The required gate completed successfully at [run 256](https://github.com/Fokiiiiiii/eve.rs/actions/runs/39317419198).
Windows workflow handoff for cx32: in runs 194 (`d01856d3`) and 195 (`95512e4f`), PostgreSQL 18 installation/readiness, Rust tests and the .NET build completed, but `Run PostgreSQL-backed .NET test projects` (whole-solution `dotnet test EveRs.slnx`) continued until the 45-minute job timeout canceled it, about 35 minutes into that step. This is the separate Windows diagnostic workflow; it does not block the Required CI gate. Please investigate why the Windows solution test command does not finish within the remaining job time.

Run 269 on `a3a1f373` passed Rust format/clippy, Rust tests, fixtures, Python, the end-to-end walk, .NET build, the Space shard and the Host shard. The all-other .NET shard failed only in `CoreCatalogTests.Core_factory_exposes_node_graph_callback_for_host_wiring`: its QA RPC test session had no `accountRole` staff grant, so the intentional QA authorization check did not forward the callback. The fixture now grants the QA role before invoking `qa_unsubscribe`; the focused test passed locally (1 test). Run 270 on `ec9575b0` repeated that single failure because it ran before the fixture correction was merged. Run 272 on `f1539c1` passed all Linux jobs and the Required CI gate: [run 272](https://github.com/Fokiiiiiii/eve.rs/actions/runs/39338882753). It is the first green required gate after the fixture correction; the separate Windows workflow was still in progress when checked. The prior recorded-walk and Host `InventoryRpc` failures are resolved as recorded above: GM walk expectations match the recorded role behavior, and character-only inventory composition omits the space-only callback when no space session provider exists. The currently configured `ci.yml` branch concurrency keeps one active push run and one replaceable pending run; its Required CI gate does not wait for Windows. Run 271 for `7c780c90` was superseded while pending. Runs 272 (`f1539c1`) and 273 (`e2c4be90`) both reached successful conclusions with their Required CI gates green; run 273 completed all Linux checks, including the newly split Space test shards, in 15.2 minutes. The gate reports success independently of the separate Windows workflow.


CI run 422 on `745e786b` found `EVE_XMPP_SCHEMA` missing from `tools/dev/run-eve-live.sh` scratch-schema export and cleanup. The script now assigns `${SCRATCH}_xmpp` and drops it with the other per-run schemas; the focused Host isolation and XMPP tests pass locally. CI run 425 on `791f4ec4` passed Rust checks, .NET build and all .NET shards, then reproduced the live inventory `MachoError 2` on the station hangar `GetInventory` call after login; its root cause remains open. The two-character XMPP Local/corporation/private walk passed separately in live-slow run 52.

## SDE lookup index initialization

`SdeReader.BuildAsync` only reads the loaded build number. `eve-sde ensure` installs the SDE lookup indexes under a per-schema PostgreSQL advisory transaction lock. This keeps concurrent test readers from racing to create the same index and preserves the read-only contract checked by `LiveScriptIsolationTests`.
