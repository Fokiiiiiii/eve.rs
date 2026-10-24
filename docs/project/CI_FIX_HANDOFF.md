# CI Fix Handoff

## Current state

- The original failures on main were the New Eden Store catalog availability assertion and the live fitting walk. The Store test now provides the settlement callback required to report offers as purchasable; its purchase path remains untouched by the catalog read. The focused Release test passed on `eve_p4` with `EVE_MARKET_SEED=skip`.
- The fitting failure was `SessionRpcBudgetExceeded` during the inventory `List` after a module move. Tracing counted 132 weighted units for setup plus six inventory/Dogma re-reads against the old 120-unit default. The default is now 160, and `DispatchGoldenTests.The_fitting_inventory_rereads_fit_the_default_session_budget` protects that sequence.
- On `eve_p4`, the skip-free live fitting regression passed both race variants. The full fast live suite passed; its eight slow cases and one skill-training-speed case were skipped by suite conditions. The focused dispatcher suite passed 147/147. After merging, `Eve.Services.Core.Tests` passed 862/862 and `Eve.Services.Tests` passed 208/208.
- Runs 519, 520, and 522 also exposed an alliance RPC budget assertion using `allianceRegistry` for a route served by `corpRegistry`; commit `f2384b0f` corrected the test target. The pre-merge Release suite passed 205/205, and the post-merge count is 208/208 above.
- The pinned SDE build 3396210 is loaded in `eve_p4`; the local JSONL copy is `.cache/sde/eve-online-static-data-3396210-jsonl`.

## CI status at this handoff

- Run 529 passed all jobs, including the required CI gate, on `37b521d0`.
- Run 531 passed all jobs, including the required CI gate and Windows job, on `e3053512`.
- Run 533 for `7db4c9ed` had passed E2E, Rust, build, and all .NET shards; its Windows job was still running at the last status check.
- Run 539 for `d5b946b4` was active at the last status check. The checked-out `origin/main` tip is now `38decf14`; no workflow for that tip was listed in the latest `ci-status.py` output.

## Continue

Check the newest `origin/main` workflow with `python3 tools/dev/ci-status.py status`. If any job turns red, inspect its step tail and retrieve the full job log through GitHub REST without forwarding the Authorization header across the log redirect. Reproduce only on `eve_p4`, through `tools/dev/db-lock.sh`, and add a focused regression for each root fix.

## Commands

Use lane `eve_p4` only and run database work through `tools/dev/db-lock.sh`. The live port is 26103. For .NET, set `MSBUILDDISABLENODEREUSE=1` and pass `-p:UseSharedCompilation=false`.


## 2026-10-23 dependency-bump gate investigation

- CI run 608 on `c2ff6220` reported RustSec `RUSTSEC-2026-0385` for locked `ctutils 0.4.2`. The advisory was issued on 2026-10-23 and its patched range is `>=0.4.4`; the locked dependency is in the RustCrypto digest chain, not Tokio.
- The Python client job runs on Python 3.12.15. Its failure was `test_selection_routes_missing_appearance_through_avatar_save_before_game_entry`: the new live scenario called `connect()` without the `EVE_LIVE_HOST` guard used by the other live tests, so the standalone job attempted `127.0.0.1:26000` and received `ConnectionRefusedError`. The Windows log also showed successful installation of grpcio 1.84.1, protobuf 7.36.2, and pycryptodome 4.1.0 before pytest began.
- Added the same `EVE_LIVE_HOST` skip condition to the paperdoll scenario and added the file to the explicit live-test list in `tools/dev/run-eve-live.sh`, so the scenario runs only with the server stack.
- Run 608 also showed independent live and .NET failures; check the newest main gate after the dependency and Python-test fixes to determine which remain.


## Push 9f8612b0

- Pushed the paperdoll live-test guard and live-harness test-list update on main as `9f8612b0`.
- Push-triggered CI run 611 (`39963051154`) was in progress at this handoff update. Run 610 on the previous head `ab356e0f` had failed its Python client and Windows Python jobs; run 611 checks the guard and server-backed scenario.
## 2026-10-23 selected-agent SHA checkout

- Runs 60 and 61 failed in `actions/checkout` when the selected-agent dispatch supplied abbreviated commit IDs. The workflow passed that prefix directly to checkout, which tried to resolve it as a remote branch or tag.
- The workflow now fetches full history from the dispatch branch, resolves a requested commit prefix or branch locally, and checks out the resolved commit in detached mode.
- Focused regression `AgentTestRefResolutionTests.test_short_commit_and_branch_refs_check_out_as_detached_commits` passed locally.
- Push-triggered CI is still running; this entry will be updated with the run result after Actions completes.

## 2026-10-23 Python and Rust dependency fixes

- Run 611 on `9f8612b0` completed with the standalone Python client job successful after the live-only paperdoll test received its `EVE_LIVE_HOST` guard. Windows package installation had already shown grpcio 1.84.1, protobuf 7.36.2, and pycryptodome 4.1.0 install on Python 3.12.15; no generated-code or API incompatibility appeared.
- Run 608 identified active RustSec advisory `RUSTSEC-2026-0385` in `ctutils 0.4.2`, a transitive RustCrypto digest dependency. The official advisory fixes it in `ctutils >=0.4.4`; `Cargo.lock` now selects 0.4.4 with the official registry checksum.
- Local `cargo +1.94.1 fmt --all -- --check` and `CARGO_TARGET_DIR=target-cx CARGO_BUILD_JOBS=2 CARGO_INCREMENTAL=0 cargo +1.94.1 check --workspace --locked` passed. Local `cargo +1.94.1 clippy --workspace --all-targets -- -D warnings` passed. `cargo audit` is not installed locally; the push-triggered advisory job must confirm the locked version.
- Runs 611 and 614 also failed independent CI paths on their older heads: run 611 reported the cargo-capacity and paperdoll-state .NET assertions; run 614 failed Host startup because `CharacterServicesComposition.CharacterServiceList` found no entry. Current main includes follow-up changes in those areas. Check the newest main push gate before treating those paths as resolved.
- The newest observed push gate was run 614 on `690991ea`; its Rust advisory job passed before the fixed lockfile was pushed, while E2E failed at Host startup. A fresh push-triggered gate is required to verify the lockfile and current main together.


## 2026-10-23 run 615 character migration expectation

- Run 615 on `1f08f9cb` failed `StoreTests.Home_station_cooldown_migration_preserves_existing_rows`: the test's expected migration list stopped at `0014_achievement_has_ever_warped`, while the current set also applies `0015_forced_paperdoll_state`.
- Updated the migration expectation. The existing regression creates a schema at migration 0003, applies all later migrations, verifies the legacy home-station values, and checks that no migration remains pending.
- Actions will verify this database-backed test. Run 615 also has a separate ship cargo-capacity failure; that root cause is being tracked independently. The E2E and Windows jobs were still active when this entry was added.
## 2026-10-23 follow-up after run 615

- Run 615 completed with three failures: the fast E2E market history RPC returned Macho error 2; `Eve.Ships.Tests.ShipTests.Cargo_capacity_modules_cannot_be_offlined_or_unfitted_past_the_hold_limit` expected `ShipRefused` but received `ShipChanged`; and the character migration expectation omitted `0015_forced_paperdoll_state`.
- Main commit `cc6067b0` now contains the ship cargo-capacity refusal correction and its live test adjustment. Run 615 predates that change; run 616 was on `731e8df2`, before `cc6067b0`.
- This push updates the character migration expectation. The fast E2E market failure still needs its own root-cause fix. Run 616 was active when checked.

## 2026-10-23 run 617 cargo-capacity test setup

- Run 617 on `912cce53` still failed the offline-capacity assertion after the server check was added. The test inserted `Rig.FixtureAsteroidOre` but constructed `Rig` without `Rig.WithShipFixtures`; that synthetic type was absent from `DogmaStatic`, so `VolumeOfAsync` ignored its volume and measured the cargo as zero.
- The rig fixture defines that ore with a volume of 1. The test now loads the fixture and derives the required stack quantity from the hold capacity and the fixture type's volume, ensuring cargo exceeds the capacity computed by dogma for the test ship.
- Run 618 is checking commit `ecf46055` and still has the previous test setup. The updated regression will run in the push-triggered gate after this fix.

## Push ecf46055

- Commit `3229ba6f` updated the locked `ctutils` release to `0.4.4` for `RUSTSEC-2026-0385`. The advisory names `ctutils` and patches releases before `0.4.4`; it does not identify Tokio. Local Rust 1.94.1 format, workspace check, and workspace Clippy checks passed. The local environment does not have `cargo-audit` installed.
- Merged current `origin/main` and pushed the advisory fix as `ecf46055`. This head also includes the current character-migration expectation and ship cargo-capacity fix.
- CI run 618 and dependency-audit run 8 are checking `ecf46055`. The standalone Python client job passed; Rust advisory, Windows, E2E, and other gate jobs were still running at this status check.

## 2026-10-23 main-gate failures after the cargo-capacity fix

- Runs 620 (`a29f636f`) and 621 (`5ba7acea`) both completed the .NET test shards successfully; run 620 therefore verifies the cargo fixture setup and the migration expectation changes. Both E2E jobs still fail `test_market_browse_and_history_refresh_fit_one_session` when `GetMarketOrderHistory` returns Macho error 2.- V24.01's `MarketQuote.GetMarketOrderHistory` directly calls `marketProxy.GetMarketOrderHistory`. EveJS v0.12.9 processes pending expiry events, reads character and corporation orders, and returns the cached owner-order rowset. eve.rs sweeps expiry, reads both owner scopes, and builds the same rowset. The Action log does not include the service exception; the failing live test now includes the ERRORRESPONSE payload in its assertion output so the next run can identify the server-side exception.
- Live-slow run 64 on `912cce53` reaches the death flow. The victim's session-change keys match the reference, including `stationid2`; the remaining failure is event delivery: hull death sends `OnItemChange` before the survivor's `AddBalls2`, and capsule death sends no filtered destiny events to the watcher. EveJS disembarks the capsule with broadcast disabled, removes it with the terminal explosion effect, then adds the corpse at that position. eve.rs sends the explosion through the simulator's observer set and later leaves the scene from the host callback; check the observer/view state and output ordering against that sequence.
- Live-slow run 65 on `ecf46055` fails the same market-history call. Runs 622 and later were still in progress at this update; check `python3 tools/dev/ci-status.py status` after the push.
- The ERRORRESPONSE payload diagnostic is included with this handoff update. This change is ready to be pushed after the required merge of the latest `origin/main` (`f9438e08` at this update); its next E2E result should identify the hidden server exception.


## 2026-10-23 follow-up: market budget and host-link fixture

- The diagnostic in run 623 (SHA `556f3ccd`) exposed the live market failure: `GetMarketOrderHistory` was refused with `SessionRpcBudgetExceeded` during the same-session three-cycle market refresh. The V24.01 callsite forwards directly to `marketProxy.GetMarketOrderHistory`; the corresponding EveJS path returns owner-order history after processing expiry. The budget limit and market cost schedule still need a behavior-backed adjustment and Actions verification.
- Rust test runs 626 and 627 failed `every_invalid_frame_is_refused_for_its_reason`: the generated “string longer than the frame” AuthRequest omitted the newly required credential-kind byte, so the first byte of the length was decoded as an unknown credential kind before the truncated text was reached. The generator now emits a valid Password credential byte and includes it in the frame length; its separate bad-credential-kind fixture remains intact. The focused Rust fixture test passed locally.
- Run 627 on `8c613b2f` also caught two SpaceContainer tests that asserted incorrect fixture and notification expectations; commit `db2f8ad9` on main adjusts those expectations. Run 628 on `e622238a` was queued after that update. Recheck the newest main-gate run and the slow live death walk before considering the earlier failures resolved.


## 2026-10-23 CI gate recovery

- CI runs 647/648 failed during Host startup because the SKINR catalog read mission-only `skinrComponentPointValues` from the default `sde` schema. Host composition now passes the mission reader (`a00e62c9`), so this was a schema selection issue rather than the SDE cache invalidation change (`86c11a0c`).
- After `6573728b` added the official mission/SKINR tables, run 650's Rust and Windows jobs exposed old mission fixtures that contained only `missions` and `epicArcs`. Commit `40bd4257` builds complete ten-table archives and checks the expanded authority list. The focused table-contract test passed locally; CI run 655 and selected Rust Agent run 119 are checking the pushed change.
- Run 650's Host test shard had incorrect nested protobuf assertions in the SKINR response test. Commit `bb6029d1` corrected the wire decoding; Agent test run 112 passed.
- The live SKINR SQL seed has been updated on main to use the mission schema and configuration shape (`5ecf9205`); Agent test run 114 passed.
- Run 650/651's SSO walk treated a returned `util.KeyVal` as an integer tuple. V24 character rows are `util.KeyVal` objects with a `characterID` field; the live test now asserts that object shape and reads `characterID` from its field dictionary. The focused live Action check is queued with this change.
- The market walk reached `GetMarketOrderHistory` and received `SessionRpcBudgetExceeded`. `PacketDispatcher` had a 460-unit fallback, but Host composition overrode it with 160; the dispatcher now gives `marketProxy` its own 460-unit per-session budget and keeps the general budget at 160. Existing dispatch tests cover a full browse and rejection before trade side effects. The changed `Eve.Services.Tests` project builds locally; the focused Action check is pending.
- Run 650 also logged a Python `SyntaxError` in the embedded `run-eve-live.sh` URL converter: shell quoting removed the nested Python key strings. The runner now uses single-quoted keys and formatting placeholders and exits if conversion returns an empty URL; `bash -n` passes, and the live Action check is pending.
- Agent run 106 selected an unrelated .NET notice test for a commit that changed the live client harness; its project build still caught an `Action<long>` delegate mismatch, already corrected by `ed01d2d8`. Agent run 108 targeted capacity tests for a documentation-only handoff commit, so its failure does not describe that commit. Future dispatches should select tests from the changed source paths; docs-only handoffs need no test dispatch.
- Recovery fixes through the SSO row assertion are pushed separately. The nine pre-existing Rust source deletions in this worktree are being preserved; they prevent the local workspace-wide fmt and Clippy commands from resolving `eve-space::targeting`. The touched `eve-sde` package check, package fmt, focused test and Clippy passed. Push-triggered Actions run the complete checks against a clean checkout.

## 2026-10-23 Windows entry and pre-push toolchain

- The required Linux and Windows CI jobs and `tools/dev/check-all.sh` now select Rust 1.94.1 by default. The Windows job checks the .NET SDK selected from `global.json`.
- The Windows job runs the UTF-8 marshal fixture test under Windows PowerShell 5.1, starts the production PowerShell server, waits for the TCP front, then performs the live login-to-character walk and stops the stack.
- Focused regression `WindowsAndPrePushCoverageTests.test_required_ci_pre_push_toolchain_and_windows_character_entry` passed locally.
- Push-triggered Actions will determine the current Windows and full-gate result; update the Windows TODO rows after that run completes.

### Public Gateway test tokens

- CI run 653's live SKINR walk received status 401 because the shared headless helper still minted an unsigned bearer token, while `GatewayTokens.TryReadIdentity` now requires a valid Host-signed token with its current key and claims.
- The gateway helper now obtains the token through the logged-in client's `userSvc.GetUserToken` RPC. SKINR, skill-plan, and title live walks use that helper, including the fresh login after Host restart. A small helper regression test verifies the RPC and authorization metadata.
- The local focused helper test passed after installing the headless-client requirements into a temporary environment. The focused live SKINR Action check will run on the pushed commit.

## 2026-10-23 market budget test configuration
- CI run 660 on `4f38c72f` exposed two dispatcher assertions that expected the market-specific budget to reject a second read, but their test dispatchers only lowered the general RPC budget.
- The market service has a separate `MaxMarketRpcUnitsPerMinute` limit. The two tests now set that value to 10, matching the intended per-session refusal scenario.
- `Eve.Services.Tests` builds locally, and the focused market-owner and PLEX-history tests passed 2/2 without a database. The next push gate will verify the fix.


### Dogma reload test cleanup race

- The .NET shard failure reproduced on runs 653, 657, and 658 in `ReloadServiceTests.A_gun_that_runs_dry_is_loaded_from_the_hold_when_the_reload_time_has_run_and_the_simulation_is_answered_with_the_rounds`. The test used `FakeRounds.Asked` as its completion signal, but that list is updated before the awaited store callback finishes; fixture cleanup could drop the schema during the callback's item transaction and PostgreSQL returned deadlock 40P01.
- `FakeRounds` now exposes completion after its callback returns. The live dry-gun test awaits that signal before teardown and checks the persisted 80-round module / 120-round hold result. The matching no-fit test also waits for callback completion.
- The local `Eve.Services.Dogma.Tests` project build passed with zero warnings and errors. Agent run 127 (`40005187593`) passed the focused database-backed regression on pushed commit `fbc39d63`.
- Agent run 123 on `e0cb832e` passed the two `Eve.Host.Tests` matches, then failed because the same `FullyQualifiedName~SignalTracker` filter selected zero tests in `Eve.Services.Core.Tests`. This was a test-selection mismatch; dispatch that filter to the Host project only.

### Market RPC budget regression test setup

- CI run 660 (`4f38c72f`) failed both market budget regression tests because each configured only `MaxExpensiveRpcUnitsPerMinute = 10`, while `PacketDispatcher` routes `marketProxy` through `MaxMarketRpcUnitsPerMinute`. The tests now set both limits to 10. Agent run 128 (`40005500782`) passed both focused regressions on `94e37e82`.

### SKINR unlimited component license sentinel

- Runs 661 and 666 reached the live consume request and returned HTTP 400 for a published compatible mission-SDE component whose `associatedTypeIDs.licenseUsesGranted` is `-1`. On `40110cc0`, allowing the negative value through the catalog did not resolve the failure.
- Commit `4b3cd33b` maps the SDE `-1` unlimited marker to the gateway's runtime `0` sentinel, rejects values below -1 and keeps positive finite grants. `ShipSkinrCatalogDataTests.Unlimited_license_types_use_the_runtime_unlimited_sentinel` covers those cases. Agent run 135 (`40007743442`) passed that focused `Eve.Host.Tests` unit test 1/1 with no skips; it did not run the live SKINR walk.
- CI run 666 predates that parser fix and still returned 400. Agent run 138 and CI run 671 are checking current main `287c530d`; CI 668 is checking the preceding head `4b3cd33b`.

### Contact notification compatibility checks

- CI runs 664 (`774b7a18`), 666 (`40110cc0`) and 668 (`4b3cd33b`) exposed the contact notification test's assumption that wire-decoded Python string keys are always `PyStr`; the actual keys are `PyBytes`. Main's `d2b2821d` adds key-aware notification assertions. The test helper now handles both marshal string forms.
- Agent run 138 (`40008851484`) on `287c530d` failed, but its downloaded log tail included only the test invocation, not the test result details. The persisted notification assertion now checks `level` and `message` through the same field reader rather than comparing the full dictionary's concrete key encoding. The updated Host.Tests Release build passed with zero warnings and errors; the focused database-backed check will be repeated.
- CI runs 668 (`4b3cd33b`) and 671 (`287c530d`) still returned HTTP 400 at SKINR `ConsumeRequest` after the SDE unlimited-license sentinel fix. Their assertion did not include the gateway message; the `ebf1ec87` test now prints it to identify the refusing validation branch. The catalog fix is not yet verified end to end.
- CI run 671 also failed the contact test's persisted-data assertion. Commit `ebf1ec87` changes that check to compare semantic field values through `Field`; its focused Agent test and push CI are checking the updated version.
- Commit `b5ecf895` contains these assertion changes and the live error diagnostic. Merge-result commit `b2025a73` preserves the latest main contact notification checks. The merged `Eve.Host.Tests` Release build passed with zero warnings and errors; no local PostgreSQL test was run.
- Pushed the merged assertion and live diagnostic as `ebf1ec87` after fetching/merging current main. Focused contact test run `40011081153` and selected live `test_skinr_live.py` run `40011418764` failed as described below; push CI run `40011062817` identified `INVALID_COMPONENT_LICENSE_ITEM`.

## 2026-10-23 persisted contact ID wire types

- CI run 671 on `287c530d` and Agent run 138 reported the contact notification test expected `PyLong` for persisted `senderID`, but `NotificationMgrService.Dto` emits persisted `senderID` and `receiverID` as `PyInt`.
- Updated persisted sender and receiver assertions to `PyInt`; kept the direct `OnNotificationReceived` packet assertions unchanged. The current Eve.Host.Tests Release build is still running; no local PostgreSQL test was run.
## 2026-10-23 structure market Host fixture location

CI run 672 on `1669e6dc` failed the structure market Host test because the fixture set each session's `structureid` to the Upwell structure but persisted the characters at the SDE station.
- The feature's market rules require the seller to be at the structure when the character has no Marketing skill. Main now places test characters at the structure. The new sell failure on run 675 occurs after that fixture correction; the local test now includes the wrapped user-facing refusal in its assertion output. The market feature handoff owns that separate issue.

## 2026-10-23 run 675 follow-up
- Push CI run 675 (`40011062817`) on `ebf1ec87` reported `INVALID_COMPONENT_LICENSE_ITEM`; the unlimited-license parser unit check passed earlier, but the live request payload had not yet been traced. Agent run 146 (`40011418764`) also failed; the run 154 artifact later identified the extra protobuf wrapper in the test encoder (see below).
- Run 675's Host tests found the persisted contact `senderID` type mismatch; the local expectation now matches the returned `PyInt`. The project build and focused Actions rerun are pending.
- Run 675's Windows job later completed successfully in 14.4 minutes, including the Windows PowerShell 5.1 entry point, production TCP readiness, UTF-8 fixture decode and live login-to-character walk. TODO 14 records this result; the owner's Ctrl+C/console behavior and `global.json` path remain open.
- The current worktree reports the market sell refusal in the failing test message. The Release build of `Eve.Host.Tests` passed with zero warnings and errors; no local database tests were run.


## 2026-10-23 SKINR live fixture catalog eligibility

- Run 675 and Agent run 146 reported `INVALID_COMPONENT_LICENSE_ITEM` for the unlimited license item. The live fixture query previously selected any published compatible component with a license item and binder, while `ShipSkinrCatalog.LoadAsync` only registers a component when its category and rarity have an authoritative point value.
- The SKINR live fixture now requires a matching `skinrComponentPointValues` entry, including both supported object-map and `_key`/`_value` array shapes, so it selects an item the runtime catalog can resolve. The Python file passed `python3 -m py_compile`; the live Actions check is pending.

## 2026-10-23 contact notification persisted numeric values

- Runs 675 and 676 reported the final restart-read mismatch: `data.level=-10` came back as `PyInt`, while the assertion expected `PyLong`. `NotificationMgrService.Payload` maps the persisted JSON number to `PyInt`.
- Contact notification assertions now use the observed `PyInt` values for the sender, receiver, and persisted relationship level. The local Host.Tests project build passed with zero warnings and errors; no local database test was run.
- The next focused Actions check will cover `Contact_add_persists_and_routes_the_recipient_notification` on the merged main head.
## 2026-10-23 structure market sell refusal
- Run 678 on `737ba657` still failed the first structure sell after the persisted character location was changed to the same structure as the session. That disproved the location-only diagnosis.
- `MarketHostTests` now includes the returned Macho label or CustomInfo text in the failed sell assertion. The next focused Actions run will expose the exact remaining refusal for a root fix.

## 2026-10-23 merged-head verification update

- Merged origin/main `d7f45242` after fetching; that update only added contact-notification handoff notes. The merged `Eve.Host.Tests` project build completed successfully with 0 warnings and 0 errors. No local PostgreSQL test was run.
- CI run 681 on `04a43486` is still running. Rust format/clippy, Rust tests, Python tests, fixture validation and .NET build have passed; the end-to-end, Windows and .NET test jobs are still in progress. Check its completed result before treating the SKINR live fixture change as resolved.
- The persisted contact assertions now expect `PyInt` for stored sender/receiver IDs and restart-read relationship level. The market test only adds the returned Macho refusal text to the failing assertion; the actual sell refusal is not yet identified. Both focused Actions checks must run on the next pushed head.
## 2026-10-23 SKINR consume request fixture shape

- Artifact for Agent run 154 (`40013656378`) on `04a43486` shows its test failed before reaching the Host: my temporary point-value filter compared the text SDE key column with bigint. I removed that unnecessary filter; the query returned to the prior fixture selection.
- The run 154 artifact also confirms the earlier `INVALID_COMPONENT_LICENSE_ITEM` came from the live test's extra protobuf field-1 wrapper, which made the decoded item ID zero. Main commit `8610990c` fixes `_item_consume_payload` to match V24.01's `ConsumeRequest.item` -> `ItemIdentifier.item` -> `InventoryItemIdentifier.sequential` nesting.
- Python syntax compilation passed after removing the temporary filter. A corrected live Agent run and the push gate are still in progress.

## 2026-10-23 SKINR wire fixture correction

- Commit `8610990c` corrects the headless `ConsumeRequest` fixture to the V24.01 two-message nesting. The source is the client's `ConsumeRequest.item` descriptor, `ItemIdentifier.item`, and generic inventory `Identifier.sequential` field. This fixes the test's zero item ID and leaves Host validation aligned with the client.
- Push-triggered CI run 683 (`40014207144`) and merged-parent run 682 (`40014191246`) were still running in the neighboring worktree at its last update. Current CI and Agent checks on the merged head remain pending.
- No local PostgreSQL or graphical-client verification was performed.

## 2026-10-23 CI 647/648 root-cause review- The E2E and Host logs report `42P01` for `sde.skinrComponentPointValues`. These records are in the mission SDE. The failing Host composition passed the regular SDE reader to the SKINR catalog, and the live harness loaded only the regular SDE build. Main commit `a00e62c9` now reads the catalog through `sde_mission`, verifies mission tables when reusing the loaded build, and has the live harness ensure the mission build. The cache key update in `86c11a0c` is separate from this schema mismatch.- Run 648 also reports `ShipSkinrDispatchTests.Draft_save_and_read_responses_keep_the_v24_row_shape` failing its decoded skin string assertion. Main commit `bb6029d1` corrects the test's nested response decoding.- Run 647/648 predate these changes. Run 683 on `8610990c` is checking the corrected SKINR live fixture shape; run 684 on the later head `253597a9` is now the newest push gate. Check both completed results before closing these causes.

## 2026-10-23 Agent-test selection review

- Agent run 104 on `8b3c8e04` selected `Eve.Host.Tests` with `FullyQualifiedName~IndustryMonitorNoticeTests`; the failed Release build reported CS1593 because the fixture supplied a two-argument lambda to `Action<long>`. Main commit `ed01d2d8` changes the callback fixture to the actual one-argument delegate. The selection did not match the handoff/harness files changed by the tested merge.
- Agent run 108 on `951a8744` selected `Eve.Services.Inventory.Tests,Eve.Ships.Tests` with filter `FullyQualifiedName~Capacity` plus live `test_cargo_capacity_module.py`; that commit changed only `docs/project/roller/handoff-fit-cargo-capacity.md`. Its selected-test failure does not validate the documentation-only commit. For future handoff-only commits, do not dispatch tests; source commits should use a filter from the changed source test and its owning project.

## 2026-10-23 local focused checks

- The merged `Eve.Host.Tests` project build passed with 0 warnings and 0 errors. The in-memory regression `ShipSkinrDispatchTests.Draft_save_and_read_responses_keep_the_v24_row_shape` passed 1/1 with 0 skips; `python3 -m py_compile tools/headless-client/tests/test_skinr_live.py` also passed. No local PostgreSQL test was run.
## 2026-10-23 SKINR live sequence after the wire fix

- Agent run 158 (`40014734291`) on `253597a9` passed component-item consumption, same-request retry, item deletion, unlimited license persistence and refusal of a hull item. It then received HTTP 400 at `draft.api.SaveRequest`; the test assertion did not include the gateway error string.
- The live test now captures and prints the SaveRequest gateway message on failure. Python syntax compilation passed. A new focused live run will check the precise validation branch.
- Agent run 159 (`40014735391`) is checking `Contact_add_persists_and_routes_the_recipient_notification`; run 685 (`40014722525`) is the push-triggered gate for `253597a9` and was pending at this update.
- The latest completed Windows evidence is run 675 (`ebf1ec87`), whose Windows job passed in 14.4 minutes and covered PowerShell 5.1 startup, TCP readiness, the UTF-8 fixture, and live login-to-character entry. Run 681 on older `04a43486` has unrelated market `StructureMarketDenied` and the pre-wire-fix E2E failure; run 675 also had earlier Host failures recorded above.

## 2026-10-23 structure market affiliation fixture

- Run 683 on `8610990c` moved past the sell-range issue and failed with `StructureMarketDenied`. Structure service access reads the character's `CorporationMember` relation through `RelationStore`; the Host test helper set only the character row's corporation ID and omitted the authoritative relation that `CharacterCreation` writes.
- The `MarketHostTests.CreateTraderAsync` fixture now inserts that relation in its scratch relations schema in the same transaction as the character and wallet. The denied trader receives its different corporation relation, preserving the denial case.
- The local `Eve.Host.Tests` project build passed with zero warnings and errors. Run 683 is still active; the new fixture needs its Actions check after push. No PostgreSQL test was run locally.

## 2026-10-23 SKINR draft-save live failure

## 2026-10-23 SKINR layout oneof encoding

- The focused run on `253597a9` passed consumption but got HTTP 400 when saving the design. V24.01's `component_instance.build_config_proto_from_component_instance` selects outer `SlotConfiguration.coating` or `.pattern` by category; both inner messages store their identifier in field 1. The headless fixture incorrectly used the category as the inner identifier field, making category-2 component IDs unreadable by `ShipSkinrCatalog.ReadSlot`.
- `_component_kind` now uses the category only for the outer oneof and always emits the component identifier at inner field 1. The existing SaveRequest assertion prints the gateway error if this remains invalid. Python syntax compilation passed; a focused live Actions check is pending.
## 2026-10-23 latest main follow-up

- Current origin/main is `d8f4dab4`, which includes the market trader-membership fixture update and the SKINR slot-identifier oneof correction. Run 683 on `8610990c` failed the market Host scenario with `StructureMarketDenied`; the updated fixture is in the new gate. Agent run 158 on `253597a9` passed component consumption and retry but failed at design save; run 687 added the SaveRequest refusal detail, and `d8f4dab4` corrects the inner component identifier field.
- The merged `Eve.Host.Tests` project build passed with 0 warnings and 0 errors. The in-memory SKINR response-shape test passed 1/1 with 0 skips. Python syntax compilation passed on the previous merged head; rerun it after this merge.
- CI runs 686 (`4484ec9c`) and 687 (`fe051fc3`) were still active at the latest check. Check run 688 for `d8f4dab4` after it starts; do not treat the main gate as green until the newest head completes all required jobs.

- Agent run 159 (`40014735391`) on `253597a9` completed successfully with no failed tests or jobs; the handoff maps it to the focused contact-notification check. The newer CI run 688 and Agent run 163 on `d8f4dab4` are still in progress.
- Agent run 158 on `253597a9` passed the component `ConsumeRequest` after the two-level wire fix, then received HTTP 400 for `Draft.SaveRequest`. V24's client sends a `SlotLayout` with the selected coating/pattern oneof and repeated editor configurations; the empty repeated editor list is accepted by the gateway.
- Client source encodes `SlotConfiguration.coating` or `.pattern` as the outer field selected by category, then wraps that variant's component identifier in inner field 1. The headless `_layout` helper omitted that inner wrapper, so `ShipSkinrCatalog.ReadSlot` could not resolve the component ID and `TryValidateLayout` rejected the draft. The helper now matches that structure. `_component_kind` also follows the V24 category-selected oneof.
- The fixture selector now excludes SDE `disallowedSlots`, another catalog condition it must respect, and the SaveRequest assertion includes the gateway response message. Run 686 on `4484ec9c` checks the earlier selector and market affiliation fixture; Actions for the corrected layout are pending. No PostgreSQL test was run locally.

## 2026-10-23 current main SKINR layout correction
- Origin/main is now `1a9e73e9`. The run 158 failure on `253597a9` went past component consumption but returned HTTP 400 at Draft.SaveRequest. The follow-up found two fixture errors: selected components could disallow the slot, and `_layout` omitted the inner field-1 component ID in the selected coating/pattern oneof. Main commit `67753d67` corrects both.
- Agent run 163 on `d8f4dab4` failed the focused live SKINR check before this layout correction. CI run 688 on `d8f4dab4` is still active; run 689 on `1a9e73e9` is queued for the corrected head. The latest gate is not yet green.
- The Host.Tests build on the merged market-affiliation fixture passed with 0 warnings and 0 errors. The in-memory SKINR response-shape regression passed 1/1 with 0 skips, and Python syntax compilation passed after the current layout changes.

## 2026-10-23 latest main gate follow-up

- Origin/main is `1a9e73e9`, with commit `67753d67` correcting the SKINR live draft fixture's inner component-ID field and excluding SDE components disallowed for the chosen slot. The prior focused Agent run on `d8f4dab4` failed before that fix; run 688 is still active, while the corrected full gate run 689 and focused run 165 are now active on `1a9e73e9`.
- Local Python syntax compilation passed after merging the layout changes. The latest local Host.Tests build and no-database SKINR response-shape test passed on the market affiliation fixture. No local PostgreSQL test was run.

## 2026-10-23 handoff before current push

- Origin/main `bcc231a9` adds documentation updates only and is included in the local merge `3980b75a`. The latest code gate remains run 689 on `1a9e73e9`; .NET build, Rust tests, E2E and Windows are still active. The local branch carries the persisted-contact type assertions and market refusal detail alongside the latest main fixes.

## 2026-10-23 push 5bcb27c5 verification

- Push `5bcb27c5` is on main. CI run 690 (`40016706205`) is checking that exact head; Python, fixtures, Rust formatting/clippy and advisory Rust already passed, while .NET build, Rust tests, E2E and Windows remain active. Agent run 166 (`40016728738`) is also active on the pushed head.
- Run 689 on parent `1a9e73e9` remains active. Run 688 on `d8f4dab4` has a failed Host test shard; its completed log will be inspected separately from run 690, which includes later market and SKINR fixes.

## 2026-10-23 market Host sessionInfo wire type

- CI run 686 (`40015069193`) failed `MarketHostTests.The_host_dispatches_accessible_structure_market_orders_and_recovers_partial_settlement_after_restart`: the test expected `PyStr("stationid")`, but the method result contains `PyBytes`.
- `ObjectCaching.Raw` intentionally encodes `sessionInfo` as ASCII bytes, and the V24.01 client passes that value to `getattr(session, details['sessionInfo'], None)` when invalidating cached calls. The Host wire value is correct; the test expectation now matches that Python 2 `str` representation.
- The focused Agent test and push-triggered CI run will verify the correction.

## 2026-10-23 SKINR sequencing UUID fixture decode
- Push CI run 690 (`40016706205`) on `5bcb27c5` reached the active sequencing-job walk, then the live test passed a serialized `Identifier` message to `uuid.UUID(bytes=...)` instead of its raw UUID field.
- V24 declares `GetAllActiveResponse.Job.id` and `CompletedNotice.id` as `sequencing.job.Identifier`, whose field 1 is the 16-byte UUID. The Host responses already use this wrapper; the live test now unwraps `Identifier.uuid` for the active-job and completion-notice assertions.
- Python syntax check, focused live Agent run, and push-triggered CI verification are pending.

## 2026-10-23 structure market rollback assertion

- Agent run 167 (40017357430) on a769d686 failed the focused Host market test after placing a structure sell order: the test expected the seller's wallet to be 9900 ISK, but the wallet held 9875 ISK before the deliberately rejected buy. The structure order's 40 ISK value incurs the 100 ISK minimum broker fee and 25 ISK minimum SCC surcharge; the failed buy is not the cause.
- The rollback check now captures both participants' balances immediately before the failing transaction and asserts they remain unchanged, while retaining the item, transaction-history, retry, and partial-settlement assertions. This tests transaction atomicity without assuming a station fee schedule.
- The Host.Tests project build passed with 0 warnings and 0 errors. The focused Actions check is pending. No local database test was run.

## 2026-10-23 SKINR sequence and skin data fixture fields


## 2026-10-23 partial sell-order escrow assertion

- Focused Agent run 175 (40020585589) on 0d9ecbb8 passed the new pre-failure wallet comparisons, then failed at the later partial-settlement check: the test expected two Tritanium in the seller's structure hangar, but found zero.
- `MarketTrading.PlaceWithinTransactionAsync` moves the unfilled sell stack to `EscrowBase + orderId` and links it through `orders.escrow_item_id`. A partial buy transfers only its filled quantity to the buyer and keeps the remainder in order escrow. EveJS v0.12.9 follows the same `MARKET_ESCROW_PLACE` / `MARKET_ESCROW_DELIVER` flow.
- The test now checks the two remaining units by joining the active sell order's `escrow_item_id` to inventory instead of querying the seller's hangar. The Host.Tests build passed with 0 warnings and 0 errors. No local database test was run.

## 2026-10-23 market escrow remainder correction

- Agent run 175 on 0d9ecbb8 failed the old hangar assertion (expected 2, actual 0). The replacement checks two remaining units through the active order's escrow item reference.
- CI runs 694 (0d9ecbb8), 695 and 696 (c068f252) also failed that old Host assertion. Main commit c0857c18 replaced the hangar lookup with an escrow-item quantity check; this follow-up verifies the active-order remainder state as well.
- CI runs 695 and 696 exposed a live-test `Attributes` field mismatch. Commit fa667e96 changed the assertion to match the Host response (`name` field 2, `line_name` field 3); later review of the V24.01 descriptor showed the Host encoder still had those fields shifted.

## 2026-10-23 SKINR sequence and skin data fixture fields
- CI run 692 (40018629614) on 1d96ae32 completed the sequence and published both expected notices, then failed to find the license by the test's `skin_hex` key. The live helper had decoded the nested `SavedDraft.attributes.skin.hex` message as direct text.
- Commit d9d806d1 unwraps `SavedDraft.attributes.skin.hex`; CI run 693 (40020293319) confirmed license persistence and `GetRequest` now work, then exposed the saved-skin `Attributes` field mismatch. The V24.01 descriptor defines `name=1`, `line_name=2`, `creator=3`, `layout=4`, `tier=5`, `not_sequenced=6`, and `first_sequenced=7`; the Host encoder still used its shifted field layout.
- The subsequent test-only adjustment matched the old Host response. The follow-up below corrects the Host encoder and restores assertions against the client descriptor. Python syntax compilation passed; no local database test was run.
- Current main is f2d0d09f. CI run 698 and Agent runs 178/179 are active; Agent run 176 passed the station-undock client test.

## 2026-10-23 market escrow and concurrent immediate-buy check

- Agent run 180 (`40022642650`) and CI run 699 (`40022633872`) reached the escrow remainder assertion, then failed the contender wallet sum: expected 19,980 ISK, actual 19,835 ISK.
- The existing immediate-buy compatibility entry records that unfilled volume becomes a 90-day bid. In this race one buyer fills two units for 20 ISK; the other creates a two-unit bid, reserves 20 ISK, and pays the 100 ISK broker and 25 ISK SCC minimums. The Host regression now checks both balances and the one open bid's quantity and escrow.
- Host.Tests compiled locally with 0 warnings and 0 errors; the focused Host Agent test on the corrected head is pending. No local database test was run.
- CI 699 also failed the SKINR live test because its restart helper sent SIGTERM, which did not activate the Host's Console.CancelKeyPress shutdown path. Main commit d86a5ef5 changes the helper to send SIGINT; Agent run 183 and CI 701 are checking that fix.
## 2026-10-23 Agent test selection notes

- Agent run 106 on 8b3c8e04 matched a Host composition change and failed to compile the test fixture delegate (CS1593); ed01d2d8 fixes the delegate invocation.
- Agent run 108 on 951a8744 used the unrelated Capacity filter and cargo-capacity live test even though that commit changed only the handoff note. Host startup then failed because the regular SDE lane lacked `sde.skinrComponentPointValues`; the selected test failure did not validate the handoff-only change. The Host now reads that mission table from the mission SDE lane (a00e62c9). For handoff-only commits, do not dispatch an unrelated selected-test run; for code commits, choose the test owning the changed behavior.
## 2026-10-23 follow-up: market escrow assertion and SKINR wire shape

## 2026-10-23 Host SIGTERM shutdown path

- CI run 701 (`40024345772`) on `d86a5ef5` passed the Windows job but the SKINR live test could not close the Host Unix socket after 45 seconds, even after switching the test from SIGTERM to SIGINT.
- .NET 10 removed its default SIGTERM handler; `Program.cs` only translated `Console.CancelKeyPress` (Ctrl+C) to the server cancellation token. The Host now registers POSIX SIGTERM explicitly on Unix and cancels the same server token, allowing the existing `finally` shutdown path to dispose listeners and runtime services. Windows retains its console cancellation path.
- The live restart test sends SIGTERM again, so it verifies the production shutdown path, persisted license recovery and the next request against the restarted Host. Local Host Release build passed with 0 warnings and 0 errors. Focused live Actions run and push CI are pending. No local PostgreSQL test was run.

- CI run 695 (`c068f252`) failed `MarketHostTests.The_host_dispatches_accessible_structure_market_orders_and_recovers_partial_settlement_after_restart`: the old assertion expected two seller hangar units, although partial sell-order remainder stays at the order's escrow item. The test now joins the active order to `escrow_item_id` and checks the order volume, escrow owner/location/flag/quantity, and single matching row.
- The same run failed `test_v24_skinr_design_sequences_and_applies_a_persisted_license` because the Host serialized the client `Attributes` fields at the wrong numbers. The V24.01 client defines `name=1`, `line_name=2`, `creator=3`, `layout=4`, `tier=5`, `not_sequenced=6`, and `first_sequenced=7`. `EncodeSkinDesign` now emits those fields and distinguishes saved drafts from completed licenses.
- Origin/main advanced to `1f1e23a0` and included `c0857c18`'s escrow quantity check and `fa667e96`'s test-only adjustment to the old Host field layout. This follow-up strengthens the escrow assertion and restores the live test to the V24.01 schema while correcting the Host response.
- The live SKINR test now checks the saved-draft response (`not_sequenced`) and the completed-license response (`first_sequenced`). The Host.Tests Release build passed with 0 warnings and 0 errors, and `python3 -m py_compile tools/headless-client/tests/test_skinr_live.py` passed. No local database or live-client test was run.- Focused Actions verification for the market Host test and full CI on the corrected source are pending.


## 2026-10-23 Upwell broker minimum review

- EVE University defines the Upwell broker fee as the 0.5% SCC surcharge plus owner rate, with one 100 ISK minimum ([Market](https://wiki.eveuniversity.org/Market)). The eve.rs structure context now applies the floor across both components; the 40 ISK order allocates 99.80 ISK to owner fee and 0.20 ISK to SCC.
- Commit `d589748d` preserves the failed-buy rollback check by comparing pre- and post-failure balances. Commit `c0857c18` has already fixed the independent partial-sell escrow assertion; this work keeps that main change and adds the 9900 ISK placement assertion plus the fee rule test.
- Local Host.Tests and Market.Tests builds passed with 0 warnings and 0 errors; the focused fee test passed 1/1 with 0 skips. No local PostgreSQL test was run.
- On main `1f1e23a0`, full CI run 699 is still active. Focused Agent run 180 failed in its test step but its available log ends at the start of .NET tests, without a test-level failure; the full CI output will be checked before making any further change.



## 2026-10-23 immediate-buy CI root

- CI run 699 on `1f1e23a0` failed the structure-market Host test at the concurrent-buy balance check: the buyer that lost the last ask had a 90-day bid, escrow and fees debited. EveJS v0.12.9 passes `durationDays:0` from `Handle_BuyMultipleItems` and creates a resting order only when normalized duration is greater than zero. V24 `MarketQuote.BuyMulti` emits `OnClientEvent_OrderPlaced` only for returned rows. EVE University says broker fees are charged only for orders with duration other than immediate ([Market](https://wiki.eveuniversity.org/Market)).
- `MarketTrading` now uses the request duration unchanged. Single `PlaceBuyOrder` duration zero now returns EveJS's `CustomInfo` refusal when no seller matches. Focused regressions cover no match, partial match, multiple lines, failed later line rollback, and the Host-visible order list and wallets.
- CI run 699 also failed the SKINR restart check because the existing Host listener remained open after SIGTERM. Main commit `d86a5ef5` adds graceful Host shutdown; its full gate and selected tests are running. Main advanced to `eb2aa917`; CI runs 702 (`c52c10f7`) and 703 (`eb2aa917`) were still in progress. The market correction has not yet been pushed.
- The local Host.Tests build on `1f1e23a0` passed. No local PostgreSQL test was run. `Eve.Market.Tests` compilation and Actions verification are pending.

## 2026-10-23 market CI watch on cce06a2d

- CI 703 on `eb2aa917` confirmed two roots: the structure-market concurrent-buy case charged 165 ISK beyond the 20 ISK fill because the unfilled duration-zero request became a resting bid; the SKINR restart helper also left the Unix listener open after sending SIGINT. CI 704 on `0649fc34` still showed the market balance at 9,730 ISK instead of 9,980 ISK, confirming the immediate-buy path remained open.
- Main now registers POSIX SIGTERM to the Host cancellation token and the live restart helper sends SIGTERM (`d9643eb1`). CI 706/707 are checking that shutdown change; run 707 on `cce06a2d` was still in progress at this update. `ci-status.py failures cce06a2d` also reports that run as in progress.
- The current worktree fixes the separate Upwell shared 100 ISK broker minimum and preserves duration zero for immediate buys. The regressions check the 9,900 ISK seller balance, 19,980 ISK combined concurrent-buyer balance, no resting remainder, no-match refusal, partial fill and atomic multi-line rollback. Market.Tests and Host.Tests compile on the current merged source with 0 warnings and 0 errors; the focused fee rule test passed 1/1 before the latest merge. No local PostgreSQL test was run.
- Focused Actions and the push-triggered gate for these corrections remain pending.

- The live restart test sends SIGTERM again, so it verifies the production shutdown path, persisted license recovery and the next request against the restarted Host. Local Host Release build passed with 0 warnings and 0 errors.
- Focused run 186 (`40027152541`) confirmed the old Host listener closed, then the next client login failed at the front TCP port: `eve-net` exits after its Host link closes. The restart helper now restarts `eve-net` after Host readiness, using the same front port and socket.
- Focused run 189 (`40028383063`) reached the restarted gateway but the pre-restart bearer token received 401. The live walk now logs in again through the restarted front and reselects the character before checking persistence and applying the license.
- Focused run 192 (`40029216605`) passed persistence reads and license application, then found no queued front notifications because the headless client drains them while awaiting a client RPC response. A harmless `GetUserToken` call now drains the queue.
- Focused run 194 (`40030110928`) reached notification inspection and showed `Client.notifications` contains raw `Packet` values. The check now uses the `notifications_of` decoder from `evecli.observe`.
- Focused run 196 (`40030950151`) passed on `e8538586`: 1 live test passed, 0 failed, 0 skipped. Main then added a MACHO `GetServiceInfo` call to flush the client event queue after application.
- Focused run 198 (`40031794618`) passed that merged flow on `b43c0240`: 1 live test passed, 0 failed, 0 skipped. The Host Release build passed locally with 0 warnings and 0 errors. No local PostgreSQL test was run.
- CI 718 (`40031652832`) failed only the E2E `test_mission_walk.py` unanchor location assertion: its expected system did not match the legal system used by the structure fixture. Main commit `42d5ccce` corrects that expectation. Push gate `40032068845` on the corrected main head is still running.

## 2026-10-23 pre-push update on 1bf92f6f

- Main CI 713 (40029399172) is active on 1bf92f6f; CI 712 on 1bfd22e4 is also still active. The last completed relevant failure, CI 707 on cce06a2d, showed the stale initial ask quantity and a post-restart front connection refusal. Main now contains the corrections in 3cf0d3e0 and aa8030a4.
- The unpushed market implementation is commit 8944925f: duration-zero buys do not create remainder bids, direct unmatched immediate buys return the client CustomInfo refusal, and Upwell owner/SCC components share the 100 ISK minimum. Market.Tests and Host.Tests build with 0 warnings and 0 errors; no local PostgreSQL test was run.
- The current branch includes the latest origin/main merge. Focused Actions and the full push gate for the market correction are pending.

## 2026-10-23 run 719 follow-up

- Push gate run 719 (`40032068845`) completed on `42d5cccefbcdb0dac0a1f33a1f50ecf822aea9a0`. The Windows job passed, and the earlier focused Host-restart live run 198 passed 1/1 with no skips.
- The full gate failed on independent existing paths: four market/structure live walks returned MACHO error 2, `MarketHostTests.The_host_dispatches_accessible_structure_market_orders_and_recovers_partial_settlement_after_restart` expected 9,730 ISK but got 9,780 ISK, and `TradingTests.Multi_buy_rolls_back_all_lines_on_partial_failure_and_retries_without_double_charge` plus `MarketRpcTests.Buy_multi_fills_available_quantity_without_posting_the_rest` failed. Eight live cases were skipped by the configured slow-play and skill-speed guards.
- This gate head predates the current `origin/main` merge and does not exercise the successful focused SIGTERM restart check as a failure. The market/structure failures are outside the Windows character-entry and production Host-start changes; check the latest main gate for any follow-up fixes.


## 2026-10-23 multi-buy test fixture correction

- CI 719 on `42d5ccce` and CI 720 on `dc70777c` failed `TradingTests.Multi_buy_rolls_back_all_lines_on_partial_failure_and_retries_without_double_charge`: its `TradeRig.Buy` calls used the helper's `days=0` default, which exercises immediate fills and correctly returns no posted order IDs.
- The test now passes `days:90` for both failed and retry requests, keeping its intended rollback, escrow, replay and no-double-charge checks.
- The Eve.Market.Tests Release build passed locally with 0 warnings and 0 errors. The focused Actions check is pending; no local PostgreSQL test was run.


## 2026-10-23 immediate multi-buy notification assertion

- CI 719 on `42d5ccce` failed `MarketRpcTests.Buy_multi_fills_available_quantity_without_posting_the_rest` because it expected an order notification when the duration-zero request only filled matching asks and posted no remainder.
- At that revision the test expected no packets for an immediate fill. This was later superseded: EveJS v0.12.9 sends the immediate-fill refresh notification, which the 2026-10-24 follow-up restores and asserts.
- The Market.Tests Release build passed locally with 0 warnings and 0 errors. The focused Actions check is pending; no local PostgreSQL test was run.


## 2026-10-24 structure follow-up from CI 734

- CI 719 and 731 exposed the tower-scoop capacity fixture failure, corrected on main by `1472c9c3`. The later CI 734 gate on `9f7ad7a0` showed Eject refusing the original ship because it was beyond the structure boarding distance.
- This change opens unanchored Upwell hull scoop to any pilot while preserving Starbase hull authorization and the stored-corporation integrity check. It also removes only Eject's distance condition; it still validates the original ship, owner, type, location, flag, and solar-system state before restoring the session.
- The `Eve.Services.Space.Tests` Release build passed locally with 0 warnings and 0 errors. Selected Actions checks will be recorded after completion; local PostgreSQL tests were not run.

## 2026-10-24 CI run 738 follow-up

- Run 738 (40043754414, sha 35964099) finished with four failing tests across three paths: the headless market test treated the four-field GetStationAsks tuple as a KeyVal; two Space scoop tests passed invalid character names to CharacterCreation (the fixtures exceeded CharacterNames.MaxSpaces=2); and a Host structure test expected far-away EjectFromStructure to refuse.
- The market tuple assertion and EveJS-compatible immediate-fill notification are on main in 7dced4b3 (head 56e0d37d). V24.01 converts GetStationAsks tuples through ConvertTuplesToBestByOrders. EveJS v0.12.9 sends OnOwnOrdersChanged after an immediate fill, and MarketRpcTests checks the full marshalled refresh packet. Focused Agent runs 236 (Host RPC inventory) and 237 (Market immediate-fill RPC) passed.
- The Space fixture names are shortened to valid names in the current worktree. Host.Tests and Space.Tests Release builds passed locally with 0 warnings and 0 errors. Main CI run 740 on 56e0d37d remained active at this update. Run 738 skipped eight speed-gated/slow headless cases.
- No local database-backed tests were run.
## 2026-10-24 structure ejection follow-up
- V24.01 EjectFromStructure forwards the shipID as a session change; EveJS v0.12.9 restores restoreShipID without checking position or distance. The Host backend still requires the requested ship to match the stored original ship and validates its owner, type, system location, and space flag.
- The unused CanReturnToStructureShipAsync helper encoded a distance check that the eject path does not call. It is removed. The Host regression now verifies that the original ship restores from 155 km surface distance after scene positions stop being available.- Host.Tests and Space.Tests Release builds passed locally with 0 warnings and 0 errors. The focused Actions run for the far-distance Host regression, Space scoop regressions, and next push gate are pending. No local database-backed tests or graphical-client verification were performed.
## 2026-10-24 CI 737 follow-up
- Push gate run 737 (`486812c1`) confirmed the Eject live flow and all 22 selected structure scenarios passed. The Host test still asserted the old distance refusal, and the new Space tests failed before reaching scoop logic because their character names exceeded the two-space name rule; both test expectations are corrected locally, with focused Action reruns pending.
- The same gate's E2E failure was `test_market.py` expecting named `KeyVal` rows from `GetStationAsks`. Main's later `56e0d37d` merge includes the market wire-shape correction. The follow-up gate on the current head will verify the tuple correction.- The RPC inventory count discrepancy (expected 1,378, actual 1,382) was corrected on main by `35964099` and recorded by `284dfe95`; that path is outside this structure follow-up.
- The updated `Eve.Services.Space.Tests` and `Eve.Host.Tests` Release builds both passed locally with 0 warnings and 0 errors. No local PostgreSQL tests were run.


## 2026-10-24 CI run 740 and follow-up

- CI run 740 on `56e0d37d` completed with the old structure regression and invalid Space fixture names, plus two Market protocol failures. The Market cases expected one and two notifications after orders that included an immediate fill; EveJS behavior emits a fill refresh as well as resting-order create/cancel refreshes. The current worktree now expects two notifications after the fill and sell order, three after cancel, and three in the PLEX flow.
- The same run's headless job ended on `MachoError: macho error 2` for call id 19. The failure summary did not expose a pytest case name. Main's later `2a014844` commit updates the market seed and headless market walk; Actions run 745 is checking that path.
- Main commits `61e84858` and `ff287b5e` correct the distant structure ejection expectation and shorten the invalid character fixtures; run 740 predates those updates.
- Host.Tests, Space.Tests, Core.Tests, and Market.Tests Release builds passed locally with 0 warnings and 0 errors. No database-backed tests were run locally; the focused regressions will be checked on Actions.

## 2026-10-24 selected Actions rerun 238

- The Host distant-ship regression passed 1/1. Space tests passed 3/4; the remaining test failed in setup when it read an absent `session.corpid` field instead of the authoritative pilot record. The test now uses `CharacterAsync(...).CorporationId` and is queued for another focused run.

## 2026-10-24 CI run 745 market notification follow-up

- Run 745 on `fecd1a3e` failed `Native_market_rpc_reads_descriptor_rows_buys_sells_and_cancels_without_node` and `Market_client_plex_history_escrow_and_order_history_reads_are_native`. Both tests expected the old order-notification count after an immediate fill. `MarketNotifier.PublishAsync` now sends EveJS-compatible `OnOwnOrdersChanged` with reason `Filled` for an immediate fill with no resting order; the tests now include that notification in their expected counts.
- The `Eve.Market.Tests` Release build passed locally with 0 warnings and 0 errors. Focused Actions checks are pending; these database-backed tests were not run locally.
- Run 745 also failed the End-to-end job with `MachoError: macho error 2` at call ID 523. The CI log tail did not identify its pytest case; root-cause investigation is ongoing. Skips in that run included one Market test and the skill-speed plus seven slow live tests.

## 2026-10-24 headless Upwell docking setup follow-up

- The fast live E2E gate on run 747 (40046843651, 0fd2ca16) ended with MachoError: macho error 2 at call ID 525. The available CI log tail does not name the pytest case. Focused test_market.py Actions run 252 (40048337454) is still running against the earlier helper.
- V24.01 eve/client/script/ui/services/structure/structureDocking.py:64-81 routes Dock through autoPilot.NavigateSystemTo(structureID, 2500, RequestDocking). The autopilot warps to an in-range item using CmdWarpToStuff before it calls Dock. EveJS server/src/services/structure/structureDockingService.js:134-179 follows a distant target and returns the approach refusal; the headless helper was repeating that RPC from 200,000 km away without the client warp.
- The helper now issues the client-shaped beyonce.CmdWarpToStuff("item", structure_id, minRange=0), waits for the warp's Stop destiny action, then requests structureDocking.Dock and verifies completion. No production service behavior changed.
- python3 -m py_compile tools/headless-client/tests/test_market.py passed. The post-change focused Actions check and push-triggered CI are pending. No local PostgreSQL test or graphical-client run was performed.

## 2026-10-24 Upwell market docking and CI diagnostics follow-up

- Runs 746 (`2d2b11ea`) and 747 (`0fd2ca16`) failed the E2E test `test_docked_corporation_members_trade_at_an_eligible_upwell_structure`; run 747 failed at call 525 with `MachoError: macho error 2`. The Fortizar fixture is 200,000 km from the station, and the client navigates to within 2.5 km before its Dock RPC. The headless helper now follows that client movement flow; the same correction is present in the fetched main head.
- The CI log parser now extracts pytest's `FAILED path::test_name` heading when a summary table is missing. Focused regression `FailureParsingTests.test_pytest_failure_heading_without_summary_name` passed locally.
- The focused Actions rerun and push-triggered gate for the merged helper and log parser are pending. No local PostgreSQL database was used.


## 2026-10-24 CI runs 749-750 failure-output parser correction

- Run 749 on `30dcbf9d` failed `FailureParsingTests.test_pytest_failure_name_and_section_tail`: the parser returned both pytest's bare failure heading (`test_character_walk`) and its qualified summary node (`tests/test_walk.py::test_character_walk`), while the focused assertion expects the qualified node only.
- The parser drops a bare name when a matching `::`-qualified summary name is present. Run 750 (Actions run `40049835056`) on `e63b3aea` then exposed the heading-only case: `test_pytest_failure_heading_without_summary_name` returned `[]` instead of `['test_character_walk']`, because pytest's underscore-delimited failure heading was not parsed.
- `parse_test_failures` now recognizes the underscore-delimited pytest heading while retaining the qualified-summary preference. Both focused failure-parsing assertions passed locally across the two corrections; the follow-up push-triggered CI result is pending. No database tests were run.

## 2026-10-24 push e863ec63 verification

- Pushed the merged Upwell docking and CI failure-name parser changes to main as `e863ec63`.
- Push gate run 753 (`40050099878`) and focused Agent run 258 (`40050113361`) were active at this update. Run 753's Python client, Rust format/clippy, fixtures, and Rust advisory jobs had passed; the live E2E, Windows, .NET build/shards, and Rust tests were still running.
- The earlier selected run 255 on `30dcbf9d` failed its test step, but its available tail ended after the live-test command. The current focused run checks the same market protocol cases and `test_market.py` on the corrected head. No local PostgreSQL database was used.

## 2026-10-24 push 05bce6b0 handoff status

- Pushed the handoff update with current main changes as `05bce6b0`. At the first status check, no `ci.yml` workflow for this SHA was listed yet.
- CI run 753 and focused Agent run 258 for `e863ec63` were still active. Their results will be checked along with the push gate for the new head. No local PostgreSQL database was used.

## 2026-10-24 current main verification

- The checked-out main tip at this update is `9716acb1`. No push-triggered `ci.yml` run was listed for that SHA at the first check.
- CI run 753 on `e863ec63` has passed the Python client, Rust format/clippy, fixture, and Rust advisory jobs. Its E2E, Windows, .NET build/shards, and Rust test jobs were still active. Focused Agent run 258 on the same code head was also active.
- The follow-up will dispatch `ci.yml` against the latest main head so the complete gate covers the corrected parser and Upwell docking flow. No local PostgreSQL database was used.

## 2026-10-24 run 753 Upwell inventory RPC follow-up

- Run 753 on `e863ec63` failed `test_docked_corporation_members_trade_at_an_eligible_upwell_structure` at `tests/test_market.py:240`: after the trade and transaction checks, the buyer's `invbroker.ListStationItems(structure_id)` returned no Tritanium rows.
- The market settlement moves filled goods to the destination hangar. `InvBrokerService.ListStationItems` then rejected any non-SDE station ID outside the global-assets context, which excluded an Upwell structure. EveJS resolves top-level entries by the requested dockable location.
- The RPC now allows a non-SDE location only when it matches the session's docked location; the existing global-assets structure validation remains, and a remote structure query still returns an empty list. Regression `A_docked_structure_lists_its_own_station_items_but_not_a_remote_structure` checks both cases.
- The focused .NET Release build passed with 0 warnings and 0 errors. The focused Actions test and latest-main CI gate are pending. No local PostgreSQL database was used.


## 2026-10-24 selected Agent run 267 sell-order inventory expectation

- Agent run 267 (`40051462122`) on `a231dfb8` passed the focused `Eve.Services.Inventory.Tests` regression (1 passed, 0 failed, 0 skipped), then failed `tests.test_market::test_docked_corporation_members_trade_at_an_eligible_upwell_structure`. The buyer's delivered Tritanium was in its structure hangar, but the seller's remaining two units were not.
- The test expected the two remaining sell-order units to remain in the seller's hangar while also asserting a two-unit standing ask. EveJS v0.12.9 `executeSellEntryUnlocked` moves the order's unfilled remainder to its market escrow location; cancellation returns it to the original hangar. The live test now expects zero seller hangar units before cancellation and two after it. The EVE University [Market](https://wiki.eveuniversity.org/Market) page describes market orders and partial fills; its escrow detail follows the executable reference and the observed cancellation transition.
- The push gate (run 760, `40051455352`) and pre-fix run 759 (`40051408023`) were still running when this note was added. No local PostgreSQL test was run.


## 2026-10-24 push a719eee8 seller escrow expectation correction

- Merged the concurrent main updates at `cd0a6029`, retaining the new post-cancel custody check, and pushed the seller hangar expectation correction as `a719eee8`.
- Focused Agent run 268 (`40052275363`) is checking the inventory RPC regression and `tests/test_market.py` against this head. Push gate run 764 (`40052261614`) is also checking this head. Results are pending.
- The correction follows the EveJS sell-order flow: after a partial fill, unfilled order stock is in market escrow and cancellation restores it to the structure hangar. No local PostgreSQL test was run.

## 2026-10-24 selected Agent run 276 buyer escrow journal expectation

- Agent run 276 (`40052275363`) on `a719eee8` passed `Eve.Services.Inventory.Tests` (1 passed, 0 failed, 0 skipped), then passed the seller-hangar and active escrow quantity assertions in `test_docked_corporation_members_trade_at_an_eligible_upwell_structure`. It failed the buyer journal assertion: the standing bid recorded `(42, -100)` for market escrow, not the test's expected `(2, -100)` market transaction.
- EveJS v0.12.9 `executeBuyRequest` debits the standing bid's remaining reserve as `JOURNAL_ENTRY_TYPE.MARKET_ESCROW`; eve.rs likewise keeps the bid funds in escrow. The live test now checks the escrow reference because the seller's fill consumes that already-funded order, without a second purchase debit. The next focused Agent run and latest-main gate will verify this correction.
- The current main gate run 767 (`40052865670`) on `e0765b6c` was still active when this note was written. No local PostgreSQL test was run.

## 2026-10-24 push 4307511c buyer escrow journal assertion correction

- The focused rerun on `a719eee8` confirmed the new seller custody and escrow assertions, then exposed that the live walk expected a trade journal entry for a standing bid. The buyer had already funded the bid; EveJS and eve.rs record the `-100` reserve as market escrow reference type 42. The test now checks that journal entry.
- Merged the concurrent `7956ef36` change, retaining its escrow and Host RPC count checks, and pushed the combined update as `4307511c`.
- Focused Agent run `40053243687` and push gate run 769 (`40053234501`) are checking this head. Parent gate run 768 (`40053191315`) on `7956ef36` is also active. No local PostgreSQL test was run.


## 2026-10-24 run 767 Host RPC inventory count follow-up

- Run 767 on `e0765b6c` failed `Eve.Host.Tests.RpcSecurityInventoryTests.Real_host_registry_methods_are_enumerable`: the expected count was 1,382 and the Host registry returned 1,383 after a new RPC was registered.
- Commit `7956ef36` updates the expected inventory count to 1,383. The full push gate on that commit (run 768, `40053191315`) and the combined follow-up on `4307511c` (run 769, `40053234501`) are checking the corrected assertion along with the market escrow flow.


## 2026-10-24 selected Agent run 282 mission standing seed

- Agent run 282 (`40053552859`) on `e2fd3439` failed `tests.test_agent_moniker_live::test_bound_agent_moniker_courier_queries_and_offer_removal`: the walk passed an agent row dictionary to `seed_personal_standing`, whose game-master command helper accepts the agent ID, and raised `TypeError` converting the dictionary to an integer.
- The neighboring research-agent walk passes `agent["agentID"]`. The EveJS seed helper also resolves an agent name from an integer ID, so the mission walk now passes the same identifier. No production behavior changed.
- Local Python compilation and `git diff --check` passed. The focused Actions rerun and the push gate are pending.




## 2026-10-24 CORE stargate jump after warp

- Root behavior: `Flight` distinguishes alignment, active warp and post-warp coast. Stargate jump now refuses alignment/warp with `IN_WARP`, accepts coasting only after the simulation's gate surface-distance check passes, then clears the previous flight. Host clears its `_warping` marker only after the simulation accepted the jump.
- Regression coverage: Rust `scene::tests::a_warp_to_a_gate_ends_within_jump_range_of_it_and_the_jump_is_accepted` sends warp, confirms jumps are refused during both alignment and active warp, advances to coast within range, jumps, and confirms the old coast cannot resume. `Eve.Services.Space.Tests.StargateJumpWireTests.Bound_client_warp_then_stargate_jump_uses_the_wire_and_clears_warp_state` sends a bound warp packet and then a bound `CmdStargateJump` through `ServiceRegistry` and `PacketDispatcher`; it checks Notification-before-CallRsp, queue fields, bound reply tuple and warp-state cleanup without a database.
- Whole V24.01 caller check: the only `CmdStargateJump` callers are autopilot (`eve/client/script/parklife/autopilot.py:357`), manual gate jump (`stargate/client/gateJumpSvc.py:123`) and insider (`eve/devtools/script/insider.py:630`). The preceding autopilot warp records its destination and calls `CmdWarpToStuffAutopilot(destinationID)` (`autopilot.py:461-465`), matching the Host wire test's one-argument warp call. Autopilot waits for `IsJumpQueued` and `DSTBALL_WARP` to clear (`autopilot.py:303-309`), then checks surface distance against `const.maxStargateJumpingDistance` before sending the three arguments (`autopilot.py:337-357`). The UI requests gate interaction at that same max range (`eve/client/script/ui/services/menusvc.py:3029-3032`). The full client search found the following `DSTBALL_WARP` consumers: `menucheckers/sessionChecker.py:159`, `eve/client/script/ui/services/flightPredictionSvc.py:482`, `eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:131`, `eve/client/script/ui/services/menuSvcExtras/invItemFunctions.py:50`, `eve/client/script/ui/inflight/shipHud/activeShipController.py:259`, `eve/client/script/ui/camera/shipOrbitCamera.py:152,292`, `eve/client/script/ui/camera/cameraUtil.py:201`, `eve/client/script/ui/camera/spaceCamera.py:200`, `eve/client/script/environment/effects/Warp.py:454`, `eve/client/script/environment/effects/WarpFlash.py:229`, `eve/client/script/environment/spaceObject/ship.py:307`, `eve/client/script/parklife/spaceMgr.py:200,738`, `eve/client/script/parklife/autopilot.py:308,558`, and `eve/client/script/remote/michelle.py:430,434`.
- Reply and notification consumers: the sole `OnJumpQueueUpdate` listener is `eve/client/script/ui/services/jumpQueue.py:17,92-106`; it stores `(solarsystemID, space, queue, jumpKeys, keyLife)`, and `IsJumpQueued` reads the queue and key lifetime (`jumpQueue.py:80-90`, defaults at 21-22). The three direct RPC callers do not assign the result. `PerformSessionChange` returns the call result at `carbon/common/script/sys/sessions.py:476`; autopilot continues its jump flow, and the manual/insider callers likewise discard it. No client session attribute is changed by this fix; all three send `session.shipid` as the third RPC argument.
- EveJS comparison and intentional difference: see `docs/reference/compat/DEVIATIONS.md` under “Stargate jump after warp dropout”. EveJS v0.12.9's handler is at `server/src/services/ship/beyonceService.js:4196-4233`; its transition refuses during the visibility landing handoff at `server/src/space/transitions.js:1754-1760,4124-4321`. Client behavior and the EVE University [Stargates](https://wiki.eveuniversity.org/Stargates) and [Warp mechanics](https://wiki.eveuniversity.org/Warp_mechanics) pages support allowing the post-warp coast once the gate is in range. The v0.12.9 source has no focused stargate test, so the EveJS comparison was not executed end to end.
- Local verification: the .NET test project build passed with zero warnings/errors after the earlier main merge; `cargo +1.94.1 check -p eve-space`, `cargo +1.94.1 fmt --all -- --check`, workspace clippy, and the focused Rust simulation test passed. No database was used. Agent run 311 (`40072814298`) on `41c0d5e1` surfaced a warp Notification before CallRsp. A newer main change now sends `OnInvulnCancelled` only when the pilot has protection (`SpaceSessions.Ships.cs:565-578`); this fixture seeds no protection, so the current warp RPC expects CallRsp alone. Agent run 316 (`40074113564`) on `b88a601e` confirmed the bind RPC also returns CallRsp alone. Two local incremental builds ended when Roslyn `csc` exited 137 under memory pressure; the latest test adjustment awaits an Actions compile. Focused retry is pending; push gates `40072806501` (`41c0d5e1`) and `40074103640` (`b88a601e`) remain in progress. No graphical client session was used.

## 2026-10-24 CORE FIX 4: search, event log, and XMPP host calls

- After fetching and merging the current `origin/main`, the remaining missing client routes were `search.Query` and `eventLog.LogPlayerRequestedDisconnect`. `eventLog.LogClientStats` and `XmppChatMgr.Hostname` were already registered, so Host regressions now pin their existing routes and replies.
- Registered `SearchQueryService` through `CoreRpc`. It returns a `PyDict` from requested result-type IDs to `PyList` IDs, backed by live owner lookups and SDE location/type data. It handles V24 result IDs 2–10, including character, corporation, alliance, faction, constellation, solar system, region, station, and item type searches. Static and owner results use EveJS-style exact/partial matching and the 500-result cap. Requested unsupported groups retain empty lists.
- Added `eventLog.LogPlayerRequestedDisconnect` as a no-argument `None` acknowledgement. `LogClientStats` continues to validate the V24 arguments and acknowledge with `None`; the Host test checks the client's shutdown order.
- V24.01 has one `search.Query` callsite in `eve/client/script/ui/util/searchUtil.py:273-282`; result groups are defined in `eve/common/script/search/const.py:14-24`, with the 500-row limit at line 55. The complete client flow traced through `GetResultsByGroupID` callers: the scroll-list/window helpers and character/corporation helpers in `searchUtil.py:59-66,362-380`; contract station lookup in `createContractWnd.py:1101-1134`; and the universal search consumers in `mapView/colorModes/colorModeInfoBase.py:63-76`, `mapView/mapViewSearch.py:94-110`, `neocom/locations/search.py:105-117`, `addressBookWindow.py:150-168,351-363`, `infoPanels/infoPanelRoute.py:134-144`, and `infoPanels/infoPanelSearch.py:47-61`. The client primes owners/corporation tickers and removes nonexistent types before rendering (`searchUtil.py:284-349`); `GetScrollEntryData` renders owner, location, station, and type IDs into UI entries (`searchUtil.py:155-250`). Agent, wormhole, and structure result groups remain outside this fix and return empty lists.
- `clientStatsSvc.py:310-319` sends shutdown stats, then calls `LogPlayerRequestedDisconnect()`; the disconnect result is ignored, while stats success controls session-file removal. Regular stats use the same RPC at line 892. EveJS v0.12.9 returns `None` for both calls (`eventLogService.js:117-130`).
- The chat client calls `XmppChatMgr.Hostname()` in `xmppchatclient/xmppchatsvc.py:283`, reached from `Login` (line 247) and `SendGroupChat` (line 326 onward). It stores the reply as `XmppConnection.hostname` and passes it to the XMPP protocol connect call (`xmpp/xmppConnection.py:44-46,422`); the TCP address is resolved separately (`xmppchatsvc.py:284-306`). eve.rs returns the configured XMPP domain. The old EveJS v0.12.9 returned `xmppConnectHost`, while EveJS 261018.1 uses the XMPP domain; this distinction is recorded in `docs/reference/compat/DEVIATIONS.md`.
- The Host RPC inventory now expects 1,385 registered methods after adding `search.Query` and `LogPlayerRequestedDisconnect`. EveJS search reference: `searchService.js:109-156,158-195,226-298,300-323,325-403,415-429`. Host tests cover service registration and the marshalled reply shape, the client shutdown argument order, and the configured XMPP hostname when the listener is disabled.
- Local compile checks passed for `Eve.Services.Core`, `Eve.Host`, and both touched test projects with 0 warnings/errors. The one local focused test, `SearchQueryServiceTests.Query_returns_category_to_id_lists_for_the_client_search_categories`, passed (1 passed, 0 failed, 0 skipped). No PostgreSQL database or graphical client was used. Focused Actions and the push-triggered CI gate are pending.


## 2026-10-24 core view-transition RPC bootstrap regression

### Trace and client path

- In eve-rs-current.jsonl, the bound corpRegistry.GetMyApplications request at sequence 815 is answered at 816 with SessionRpcBudgetExceeded. The matching evejs-reference-client.jsonl request at sequence 246 receives CallRsp with an empty $dict at 247. The adjacent GetRedeemTokens exchange also matches between captures (current 813-814, reference 244-245), so it is not the rejected call.
- The whole V24.01 client search found the bound GetMyApplications call in bco_applications.py:69-79 (the service call is line 71); the result is cached and consumed as a corporation-keyed mapping in bco_applications.py:80-92. The corporation service wrapper is base_corporation.py:558-562; GetActiveApplication consumes the corporation list at :579-582; the applications UI consumes the status-filtered result at corp_ui_applications.py:122.
- NeocomSvc.CreateNeocom constructs the main button tree before the fixed, menu, skills and character roots (neocomSvc.py:256-279), and _ConstructMainBtnDataRoot stores mainBtnDataRoot before constructing its children (:304-308). BtnDataNode.__init__ calls OnBadgeCountChanged after construction (btnDataNode.py:35-73); blinking defaults on (neocomSettings.py:9); OnBadgeCountChanged calls HasUnseenItems (btnDataNode.py:97-102), which calls GetItemCount (btnDataNode.py:530-533). The corporation node's GetItemCount calls GetMyOpenInvitations (btnDataCorporation.py:21-22).
- Whole-client search found GetMyOpenInvitations consumers only in btnDataCorporation.py:21 and eveCommands.py:1758. base_corporation.py:564-568 returns an empty list when there is no session corporation, otherwise it status-filters the cached GetMyApplications result. This makes the rejected bound call on a corporation session interrupt corporation-button construction.
- The client assigns mainBtnDataRoot before that construction can fail; a later CreateNeocom call then skips rebuilding the remaining roots, while NeocomContainer.ApplyAttributes stores the passed roots (neocomContainer.py:86-127) and _ConstructCharCont dereferences charSheetBtnDataRoot.children[0] and skillsBtnDataRoot.children[0] (:220-225). This accounts for the missing children data after the failed RPC. The later view transition and teardown sites named in the report are ViewStateSvc.ActivateView (:242-362), ChangePrimaryView (:487-502), CloseSecondaryView (:589-600), UpdateOverlays (:635-658), and MapViewPanel.Close (mapViewPanel.py:69-72).
- The logs and code identify a failed bootstrap reply as the initiating break; no notification, session attribute, or map-view payload is changed by this fix.

### Server fix and regression coverage

- Latest main contains the server-side budget isolation from af9cd402: PacketDispatcher.cs:28 creates a per-session corporation budget, and :192 routes corpRegistry/corporation through it rather than the shared expensive-RPC budget. GetMyApplications remains costed; this keeps the guard while allowing unrelated general RPC bursts.
- HostServerTests.Corporation_application_bootstrap_bound_call_survives_an_exhausted_shared_budget (HostServerTests.cs:618) starts the real Host over TCP, registers the production CorpRegistryService and its bound object, exhausts the general budget through registered calls, confirms the next call is refused, then sends the client-shaped bound GetMyApplications call with machoVersion=1 and asserts CallRsp plus an empty PyDict. The fixture constructs a data source but opens no database connection; the handler returns before storage because no character is selected.
### Verification

- The selected client-source searches covered every GetMyApplications and GetMyOpenInvitations occurrence in /home/user/eve-logic; the call and result consumers are listed above.
- The first full build after the final main sync reached Host.Tests, then its C# compiler exited with code 137 after 43m34s while multiple workspace builds were active. Retrying Host.Tests alone with `--no-dependencies -m:1` passed with 0 warnings and 0 errors (8m27s). The focused `Corporation_application_bootstrap_bound_call_survives_an_exhausted_shared_budget` test passed (1 passed, 0 failed, 0 skipped; 1s).
- Latest main also included the eve-sim tick-aligned jump hold. Workspace clippy flagged identical held-message branches in hold_tick_aligned (crates/eve-sim/src/lib.rs:61-78); their predicates were joined without changing which messages are held, emitted payloads, or order. Whole-client searches found DoDestinyUpdate consumers in eve/client/script/remote/michelle.py:481-525 and eve/client/script/parklife/recorder.py:13-14,68-79; all OnInvulnCancelled consumers are crimewatchTimers.py:493-497, baseActivationController.py:136-141, pvpFilamentController.py:250-255, randomJumpController.py:180-185 and void_space_activation_controller.py:190-194. The sole OnJumpQueueUpdate listener is jumpQueue.py:17,92-106; IsJumpQueued reads the stored queue and key lifetime at :80-90.
- After that correction, cargo +1.94.1 fmt --all -- --check passed and cargo +1.94.1 clippy --workspace --all-targets -- -D warnings passed (16m08s; target-cx). The existing tick_aligned_updates_preserve_notification_order_and_keep_replies_immediate unit test was not executed locally; push CI will run Rust tests. The only local focused test executed was the Host RPC regression above.
- Pushed as 9153efef. Focused Agent run 328 (`40079840357`) succeeded (1 passed, 0 failed, 0 skipped). Push-triggered ci.yml run `40079827817` completed with three failures: `StargateJumpWireTests` expected `OnJumpQueueUpdate` before `CallRsp`; the trade test expected its previous refusal text for a foreign-owned item; and the live anchor test received the persisted structure and `OnStructureStateChanged` but no structure slim in the ballpark. Later main commits `6cc09a2a` and `17748f9a` changed the jump and trade flows. On run 795 (`85ce03fd`), the E2E job and the affected space and trade test shards passed, so the anchor case passed on rerun without a placement-code change from this task.

### CI follow-up

- Run 795's Host shard 4/4 failed in `AccountWalletHostRpcTests` with insufficient wallet funds; that failure is outside CORE FIX 2. At the last status check, remaining shards were still running. The failed run 791 separately skipped the skill-speed check and seven slow-live checks; these were not counted as passes. No graphical-client validation was performed, and no local PostgreSQL database was accessed.


## 2026-10-24 CORE FIX 4 Actions run 306 follow-up
- Focused Agent run 306 (`40071132573`) on `2f177fc2` passed `Eve.Services.Core.Tests` (1 passed, 0 failed, 0 skipped) and failed only `SearchQueryHostTests.Host_registers_search_and_returns_the_client_query_wire_shape`: the no-database Host registry had no character services, so `CoreRpc` was not composed.
- The persistent Host path already registers `search.Query` through `CoreRpc`. The in-memory/no-character Host now registers the same query service over empty lookup sources, preserving the route and returning empty groups without inventing data. The Host regression exercises this real registry path.
- The RPC inventory assertion is 1,385 for the two new registered methods. The updated `Eve.Host.Tests` Debug build passed with 0 warnings and 0 errors. Focused Actions rerun and push-gate run `40071118705` are pending.

## 2026-10-24 CORE FIX 5 — loaded scenes for NPC waves and live space travel

Baseline checked after fetching `origin/main`: `aeb7a0f8fb3248774475bc3f0137c06b52b88131`; the owner log described repeated `NO_SCENE` refusals in empty systems. The Rust simulation already creates a solar-system scene on `Sim::enter` (`crates/eve-space/src/sim/spawn.rs:728-739`) and on jump arrival before `Jumped` (`spawn.rs:1034-1066`), then removes empty system scenes (`spawn.rs:1021-1030`). `Sim::spawn` constructs a missing public system scene on demand from authoritative universe data (`spawn.rs:825-835`); unknown systems and private-room misses still refuse. The Host’s active-pilot set is the scene-presence signal (`SpaceSessions.Npc.cs:621`), and the wave director waits for that signal before spawning system-owned waves.

The Host checks this signal before each NPC request and stops a batch once no pilot remains in the system (`SpaceSessions.Npc.cs:488-492`). Only `NO_SCENE` refusals stop producing the repeated ship-refused log (`SpaceSessions.Npc.cs:118-132`). System-owned waves wait until any pilot loads the scene, retry after 15 seconds, and clean up any partial ships from a scene race without persisting a spawn row (`DungeonWaveDirector.cs:328-373`, `SpaceSessions.DungeonWaves.cs:13-40`). Other refusal errors continue through the existing failure path. The intentional timing difference from EveJS is documented in `docs/reference/compat/DEVIATIONS.md`.

The real Host registers a `ShipService` from `AgentRpc` (`dotnet/src/Eve.Host/Rpc/AgentRpc.cs:109`) and `HostComposition` adds it to the service registry (`dotnet/src/Eve.Host/Hosting/HostComposition.cs:258-262`). The focused Host-space test binds with the `ship.MachoBindObject` signature through `Rig.BindShipAsync`, which registers the returned bound OID; it then resolves that OID from `ServiceRegistry` for `Undock` and asserts the client tuple and station/system transition before the wave spawns (`PacketDispatcher.cs:587-592`, `Rig.cs:440-453,489-498`, `DungeonWaveSpaceTests.cs:28-66`). The test uses the real Host service and typed Marshal values; the Actions live walk will exercise the serialized Macho call sequence and client kwargs.

Whole-tree V24.01 client cross-check (`/home/user/eve-logic`):

- Warp RPC wrapper/callers: `eve/client/script/remote/michelle.py:732-740`; `profiling.py:317`; `addressBookEntries.py:155`; `neocom/journal.py:452`; `infoPanels/listSurroundingsBtn.py:381`; `agencyNew/contentPieces/escalationContentPiece.py:74`; `menusvc.py:1427,2737,2747,2757`; `movementFunctions.py:418,432,451,462,488`; `transmissionMgr.py:142`; `objectives/client/objective_task/dungeon.py:341,369`; `jobboard/client/features/dungeons/escalation_sites/job.py:57`. The separate autopilot warp is `autopilot.py:464`. `movementFunctions.py:437-458` sends the item target and `minRange`, then sets the UI destination/alignment. Warp Destiny updates enter through `michelle.py:481-522`; the warp effect emits `OnWarpFinished(itemID,typeID)` at `Warp.py:522`, consumed by `tacticalNavigationService.py:481` and dungeon objectives at `objective_task/dungeon.py:85,94,201,218,252,264`.
- Stargate jump callers: `eve/client/script/parklife/autopilot.py:357`, `eve/devtools/script/insider.py:630`, and `stargate/client/gateJumpSvc.py:123`; each sends gate, destination beacon and ship ID. The live jump scenario warps to the gate, calls `CmdStargateJump`, then binds the destination ballpark (`tools/headless-client/tests/test_jump.py`).
- Dock entry is selected through `eveCommands.py:2986-2989`, mapped by `actionConst.py:80-87` and `selectedItemConst.py:200-213`, and sends `CmdDock(itemID, session.shipid)` in `movementFunctions.py:505-516`. Undocking UI calls bound `ship.Undock(shipID, ignoreContraband, onlineModules=...)` in `station/base.py:484-510`; structure undock instead routes through `undockingSvc.py:120-136` and `structureDocking.py:34-35`. That service emits progress at `undockingSvc.py:101,109,112,115`; all progress consumers are `modeButton.py:49,105`, `undockButton.py:40,88`, `controlButton.py:46,97`, and `statefulButtonController.py:61`. Completion is `OnUndockingCompleted(locationID)` at `undockingSvc.py:134`, consumed by `dockingHeroNotificationService.py:126,165` and `tacticalNavigationService.py:501`.
- The live movement coverage is `test_movement.py` (align/orbit/follow/stop and dock from range), `test_undock.py` (undock, movement and dock), `test_warp.py` (warp to a celestial), and `test_jump.py` (warp to gate and jump). Warp and jump are slow-marked; the selected Actions dispatch must enable slow live checks to avoid skips.

Regression tests added: `DungeonWaveTests.A_system_owned_wave_waits_until_a_pilot_loads_its_scene`, `DungeonWaveTests.A_scene_disappearing_during_a_wave_retries_and_removes_any_partial_spawn`, and the registered Host undock/wave test above. Local check passed: `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Services.Space.Tests/Eve.Services.Space.Tests.csproj -p:UseSharedCompilation=false` (0 warnings, 0 errors). The database-backed focused test and slow live walk will be dispatched on Actions after push, with results added in a follow-up entry. No PostgreSQL database was used locally.


## 2026-10-24 CORE FIX 4 CI 782 follow-up

- Focused Agent run 313 (`40073236363`) on `aeb7a0f8` passed the Search, EventLog, XMPP, and RPC inventory selections (no failures).
- Push gate 782 (`40073226581`) exposed an upstream Space wire regression: `StargateJumpWireTests.cs:52-57` seeds a pilot with `InvulnerableUntil=0`, but `EndInvulnerability` emitted `OnInvulnCancelled` for that pilot because its one-shot flag began unset. The client receivers delete the invulnerability timer (`eve/client/script/ui/crimewatch/crimewatchTimers.py:493-496`) and clear activation validators (`eve/client/script/ui/shared/abyss/baseActivationController.py:136-140`, `randomJump/client/randomJumpController.py:180-184`, `pvpFilaments/client/activation_window/pvpFilamentController.py:250-254`, `voidspace/client/void_space_activation_controller.py:189-193`); the jump-cloak timer receiver is `crimewatchTimers.py:475-477`. The server now suppresses the one-shot notification when there is no future protection, jump cloak, or protection timer. Actual timed protections still cancel through `ArmProtection` at `SpaceSessions.Ships.cs:128` (undock) and `SpaceSessions.Movement.cs:493` (gate arrival).
- The same gate's live run failed `tests/test_structures.py:455-482`: after waiting for warp, `structureDeployment.Anchor` received the configured “Space is not available” response. The Host's anchor path then asks `PositionOfAsync` for the ship location. The intended `.live-logs` artifact was absent because the upload action excluded hidden paths; the workflow now includes hidden log files so the next Actions run retains host, front, and simulation output. The simulator failure still needs confirmation from that rerun.
- The E2E summary also recorded 8 skipped cases: the skill-speed check and seven slow-live checks. They did not count as passed. Local Space compilation initially ended with Roslyn exit 137 under contention; a single-node retry is running. No database was used locally.

## 2026-10-24 CORE FIX 1 local and CI follow-up
- The Host fixture now supplies DogmaStatic with the test ship type and an empty StationCatalog. This lets the registered Host jump request pass the gate-rule checks that previously failed on a null static source, then exercise the expected simulation notification and wire reply.
- CI run 788 on 84b49946 predates the fixture repair and the next main merge. Its clippy error came from identical message-holding branches in hold_tick_aligned and is now one combined condition. The same run's structure-control and Trade errors have follow-up changes in main commit a40423f1 (Fix remaining CI trade and jump regressions), so run 788 is historical evidence rather than a result for the merged head.
- Local verification: after the merge, Rust 1.94.1 format check, cargo check -p eve-space -p eve-sim, and workspace all-target clippy passed with private target-cx; the focused simulation test passed earlier. Before merging main's added ship TypeDef fixture, Eve.Services.Space.Tests compiled with prebuilt references (0 warnings, 0 errors). The full dependency build and the post-merge test-project rebuild both ended when Roslyn exited 137 under shared memory pressure. No database was used.- The focused Host wire test and updated push gate are pending on the next pushed head. Run 788 skipped the skill-speed case and seven slow-live cases; these were not counted as passing. No graphical client or EveJS end-to-end run was used.

## 2026-10-24 CORE FIX 1 latest-main gate before push
- The worktree now includes origin/main through ab5acba6, merged before the planned push. The preceding main gate, run 807, is still active at this check: the Python client and fixture-generator jobs passed, while .NET build, Rust format/clippy, Windows, selected tests, and the server-stack E2E are active or queued.
- After that merge, Rust 1.94.1 format, eve-space/eve-sim check, and workspace all-target clippy passed. The updated Space test-project rebuild exited 137 because the shared host had less than 1 GiB available memory; the earlier pre-merge build of that project passed. The focused Host test will be checked on Actions after push. No local database or graphical client was used.
## 2026-10-24 push 1629f575 stargate jump

- Pushed the merged stargate jump work as 1629f575858c after fetching and merging origin/main through f9d453be. This head includes the coast-only jump acceptance, accepted-jump warp-state cleanup, simulation regression, Host wire regression, the required DogmaStatic ship fixture, and the clippy cleanup.
- Focused Agent run 40085311194 is checking Eve.Services.Space.Tests with FullyQualifiedName~StargateJumpWireTests; push-triggered CI run 809 is Actions run 40085296182. Both were queued at this note update.
- Rust 1.94.1 format, eve-space/eve-sim check, and workspace all-target clippy passed locally. The post-merge C# test-project compile ended with Roslyn exit 137 while under shared memory pressure; the earlier build before the main fixture update passed. The focused simulation regression had passed in the prior local run. No database or graphical client was used.

## 2026-10-24 CORE FIX 5 docking arrival follow-up

- The slow-live run `40087865729` had one pass and one failure: `test_movement::test_a_dock_from_beyond_the_range_sends_the_ship_to_the_station_and_a_second_asks_docks_it` received `MachoError: macho error 2` while polling the ballpark. In `Scene::poll`, arrival at docking range called `dock()`, which sent `OnDockingAccepted` and began the station transition before the client’s second `CmdDock`.
- The change now clears the approach marker on arrival and leaves the ship in space. The next `CmdDock` accepts docking and emits the existing notification and session transition. This matches EveJS v0.12.9 `Handle_CmdDock` (`server/src/services/ship/beyonceService.js:4133-4194`): out of range it starts `followBall` and returns `DockingApproach`; it calls `acceptDocking` only when already in range. The reference movement command is `followBall` (`server/src/space/runtime/scene/pilotCommands.js:282-302`).
- Whole-tree V24.01 cross-check: the only `CmdDock` RPC caller is `RealDock`, which sends `itemID` and `session.shipid` in `movementFunctions.py:505-516`; selected-item actions route through `selectedItemConst.py:200-213`. All `OnDockingAccepted` consumers found are `spaceMgr.py:510-514` (HUD travel state and client event), `tacticalNavigationService.py:486-489` (travel state), and `dockingHeroNotificationService.py:22,33-34` (hero notification). The argument remains the station ID; reply, notification and session shapes are unchanged.
- Updated the existing `scene::tests::a_dock_request_from_out_of_range_approaches_and_completes_docking_on_arrival` regression to match the client sequence: the approach stops in range, the first poll emits no acceptance, and the client's second `CmdDock` accepts it. The focused test passed. On push SHA `8c299486`, Rust CI caught the stale arrival-acceptance assertion on Linux and Windows; this update fixes that root cause. Corrected live run `40090893730` is checking movement, undock, celestial warp and gate-jump walks.

## 2026-10-24 CORE FIX 5 docking and scene-refusal correction

- A system with no simulation scene can refuse a transient NPC spawn with `NO_SCENE`. `SpawnAsync` now suppresses that expected refusal even for spawn paths without a callback; dungeon-wave callers still receive their existing scene-unavailable signal and can retry. The host checks for pilots before each ship in a batch, so a wave stops if its system scene closes while the batch is being made.
- The Rust scene now stops a docking approach inside docking range and waits for the client's second `CmdDock` before accepting the dock. This matches the V24.01 caller in `eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:505-516`, which sends `CmdDock` again after the approach, and the headless sequence in `tools/headless-client/tests/test_movement.py:100-159`.
- The complete client search found the `OnDockingAccepted` consumers in `eve/client/script/parklife/spaceMgr.py:510-514`, `eve/client/script/ui/inflight/tacticalNavigation/tacticalNavigationService.py:486-489`, and `eve/client/script/ui/view/dockingHeroNotificationService.py:22,33-34`. They consume the notification after docking acceptance; no reply or notification payload was changed.
- Local checks: the focused Rust station-approach test passed (1 passed); `cargo +1.94.1 check -p eve-space`, workspace clippy with warnings denied, and `cargo +1.94.1 fmt --all -- --check` passed. The touched .NET test project built with 0 warnings and 0 errors. Database-backed tests were not run locally.
- Selected Actions verification for the slow movement, undock, celestial warp and gate-jump walks is pending for the next pushed revision.

## 2026-10-24 CORE FIX 1 verification after latest main sync

- Re-fetched and merged `origin/main` through `507d3890` after the stargate code push. The post-warp coast handling, Host cleanup, and both regression tests remain present on main.
- Focused Agent run 399 (`40089540739`) on `ad55c2de` completed successfully with the `Eve.Services.Space.Tests` filter `FullyQualifiedName~StargateJumpWireTests` and Rust tests enabled; the Actions report found no failed tests or jobs. This directly checks the Host wire sequence and the simulation regression on the merged code.
- Push-gate run 827 on `72a87fb9` has passed Rust tests, Rust format/clippy, .NET build, fixture checks, and the Rust advisory job. End-to-end, Windows, and the remaining .NET shards were still running when this note was written; the current `origin/main` is `0b512602`; the intervening main changes did not touch stargate files.
- The original push gate 809 (`1629f575`) failed the structure-anchor walk while warping to the anchor point: the client received `MachoError: macho error 2`, and the simulation log reported `frame of 2294286943 bytes exceeds 1048576`. The same gate failed `BeltRatsHostTests.A_refused_belt_spawn_is_reported_as_a_failure` because its expected failure collection was empty. Neither failure came from the stargate jump request. Main later changed the anchor walk to wait for ship placement (`532287ea`); run 827 is the pending CI check for that test change.
- The standard gate skips `tests/test_jump.py` because it is slow-live. I canceled manual nightly dispatch `40089713766` before test steps; its temporary GitHub PostgreSQL service had initialized, but no test step or application query used it. I did not use a local or owner database. No graphical client or EveJS end-to-end session was used. EveJS v0.12.9 comparison remains source-traced at `server/src/services/ship/beyonceService.js:4196-4233` and `server/src/space/transitions.js:4124-4321`.
## 2026-10-24 CORE FIX 5 push 8c299486

- Pushed the NPC scene-refusal and docking-approach fixes as `8c2994862f3de21450615c3b032fe5b473e916ab`, after fetching and merging `origin/main` through `014e0b85`.
- Selected Actions run `40090624287` is checking `BeltRatsHostTests.A_scene_unavailable_belt_spawn_is_reported_without_log_spam` and the slow live movement, undock, celestial warp and gate-jump walks. Results will be added after the run completes.
- Local checks on the changed projects: the focused Rust station-approach test passed (1 passed); `cargo +1.94.1 check -p eve-space`, workspace clippy with warnings denied, and `cargo +1.94.1 fmt --all -- --check` passed. The .NET Space test project built with 0 warnings and 0 errors. No local PostgreSQL database was used.
## 2026-10-24 CORE FIX 5 selected-run argument correction

- Agent run `40090624287` ended before tests started: the live selection repeated the `slow:` prefix for each comma-separated filename, while `agent-test.py` accepts it once for the whole list. No test result was produced by that run.
- Corrected dispatch `40090893730` uses `slow:test_movement.py,test_undock.py,test_warp.py,test_jump.py` and is in progress. Do not count the earlier run as a pass.
## 2026-10-24 CORE FIX 5 updated simulation regression push

- Pushed `bd6ab6ef00c1c5667aef7c1ead3fba408f0281e0` after fetching and merging origin/main. It updates the stale auto-accept simulation test to assert the V24.01 second-`CmdDock` sequence, removes the duplicate test, and records the intentional difference from EveJS 261018.1 in favor of V24.01 and EveJS v0.12.9.
- The focused Rust regression passed locally, as did Rust 1.94.1 formatting and workspace clippy. Push-triggered CI for this revision is being checked. Selected live run `40090893730` is still checking the production code for movement, undock, celestial warp and stargate jump.


## 2026-10-24 CORE FIX 1 latest-main verification follow-up

- Fetched and fast-forwarded to `origin/main` at `8799c2aa`; stargate simulation and Host code remain present. Current-head focused Agent run 412 (`40091793046`) passed `Eve.Services.Space.Tests` with `FullyQualifiedName~StargateJumpWireTests` (no failed tests or jobs). Rust run 415 (`40092063319`) is checking the current workspace and is pending at this note update.
- On the task code, push gate 829 (`11c7a4f3`) passed the full Rust workspace tests, including the warp-arrival/jump simulation regression. It failed the E2E structure-anchor scenario at `test_structures.py:490` after a coordinate warp, with “You must be in space to deploy a structure”; that scenario does not call `CmdStargateJump`. The .NET Space tests 2/2 job was cancelled after its test step ran for 30 minutes; no test result was reported. The standard E2E job skipped the skill-speed check and seven slow-live tests, including `tests/test_jump.py`; skips are not passes.
- Older gate 831 (`05f054a3`) failed Rust test `scene::tests::a_dock_request_from_out_of_range_approaches_and_completes_docking_on_arrival`: it still expected docking acceptance on arrival. The later main update replaced that expectation with the client's second `CmdDock` sequence. Current-head Rust run 415 will establish the result on the updated test.
- The V24.01 caller evidence and EveJS v0.12.9 comparison above remain current. No graphical-client or EveJS live-session verification was done.

## 2026-10-24 CORE FIX 5 docking approach update ordering

- Selected live run `40090893730` completed with 5 passed, 1 failed, 0 skipped. The failing Host/client walk was `test_a_dock_from_beyond_the_range_sends_the_ship_to_the_station_and_a_second_asks_docks_it[race-1]`: the expected `DoDestinyUpdate` was absent in the one-second window before the `DockingApproach` user error. The sim had queued the course update for the next whole-second flush while the approach error was returned immediately.
- `hold_tick_aligned` now flushes any queued updates and the current approach batch together, preserving order so `DoDestinyUpdate` reaches Host before `Approaching` becomes the RPC error. No client wire fields or later in-range second-`CmdDock` behavior changed.
- Whole-client source checks: `RealDock` invokes bound `CmdDock` at `eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:503-518`; Michelle applies `DoDestinyUpdate` at `eve/client/script/remote/michelle.py:481-512` and forwards batches at `:513-522`; the recording consumer is `eve/client/script/parklife/recorder.py:68-78`. The complete search found no other code consumers. The end-to-end walk asserts the approach error, movement events, rest inside docking range, and the second dock at `tools/headless-client/tests/test_movement.py:100-159`.
- Local verification on this fix: `cargo +1.94.1 check -p eve-sim`, the focused Rust ordering test (1 passed), `cargo +1.94.1 fmt --all -- --check`, and workspace clippy with warnings denied passed. Post-fix Actions live verification is pending.


## 2026-10-24 CORE FIX 1 handoff after push d3990b89

- Pushed the handoff merge commit `d3990b89cef3dec4d454fcb49d9e763823cfb141` after merging `origin/main` through `2005c521`. The subsequent main head `425cec8e` adds character-contact/notepad Host tests and handoff text only; it does not change the stargate or space-simulation files.
- Current-head focused Host wire run 412 (`40091793046`, SHA `8799c2aa`) passed with no failed tests or jobs. Rust workspace run 415 (`40092063319`, SHA `8799c2aa`) is still running its requested-tests step; its result is pending. The task-code push gate results and skipped slow-live cases are in the preceding CORE FIX 1 section.
- `ci-status.py failures d3990b89` found no push-triggered `ci.yml` run for this docs/merge commit. The code push itself was already checked by run 809, focused runs 399 and 412, and the Rust workspace result in run 829.


## 2026-10-24 CORE FIX 1 handoff after push 2919b0e1

- The latest push is `2919b0e11bdbfbd5e71044e730e2b9761a99b0e3`; HEAD was clean and matched `origin/main` at that SHA. `ci-status.py failures 2919b0e1` found no push-triggered `ci.yml` run for the documentation/merge update.
- The current-main Host wire check is run 412 (`40091793046`, SHA `8799c2aa`): success, no failed tests or jobs. Full Rust workspace tests on the task code passed in run 829 (`11c7a4f3`), including the stargate warp-arrival/jump regression. The additional current-main Rust run 415 (`40092063319`, SHA `8799c2aa`) remained active in its requested-tests step after more than 35 minutes; it is not counted as a pass.
- The source-level V24.01 caller and consumer checks and EveJS v0.12.9 comparison are recorded above. Slow-live `tests/test_jump.py` was skipped by the standard gate; no graphical-client or EveJS live-session check was performed. No local or owner PostgreSQL database was used.

## 2026-10-24 CORE FIX 1 handoff after push 187f1035

- Pushed `187f10358954d536602479d7cf31e5f51789ac50` after fetching and merging `origin/main` through `9d6f0054`. That merge added character-phase Host coverage and handoff files; it does not change the stargate or space-simulation code. `ci-status.py failures 187f1035` found no `ci.yml` run.
- Host wire run 412 (`40091793046`) is successful. Rust run 415 (`40092063319`, SHA `8799c2aa`) remains active in its requested-tests step after more than 35 minutes. Count the Rust workspace success in run 829 for the stargate simulation regression; do not count run 415 as a pass while it is active.


## 2026-10-24 CORE FIX 5 latest-main verification

- Fetched and fast-forwarded the worktree to `origin/main` at `3c7e46e6`; the scene, NPC-spawn and docking-order fixes are already in its history.
- Post-fix selected live run `40093124130` at `56bcae9a` passed: Host 1/1 and live walk 6/6, with no skips. The walk in `tools/headless-client/tests/test_movement.py:100-159` covers docking approach and the second `CmdDock`, undock, celestial warp and stargate jump. The focused Space spawn-log regression in run `40094490427` passed 1/1.
- Push gate `40093110881` still fails the separate Astrahus anchor live test at `tools/headless-client/tests/test_structures.py:490` with `CustomNotify: You must be in space to deploy a structure`; its matching focused run `40094490427` had 21 passed, 1 failed, 0 skipped. The same anchor test failed on the earlier gate `11c7a4f3`, before the docking-order fix. The .NET Space 2/2 shard was cancelled after 30 minutes, so it produced no result. The push gate skipped eight unrelated E2E checks; skips are not passes.
- Client evidence: `RealDock` calls bound `CmdDock` at `/home/user/eve-logic/eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:503-518`. The full client search found `DoDestinyUpdate` consumers in Michelle (`eve/client/script/remote/michelle.py:481-522`) and the recorder (`eve/client/script/parklife/recorder.py:68-78`); docking-acceptance consumers are SpaceMgr (`eve/client/script/parklife/spaceMgr.py:510-518`), tactical navigation (`tacticalNavigation/tacticalNavigationService.py:486-490`) and the docking hero notification (`eve/client/script/ui/view/dockingHeroNotificationService.py:22-36`). The five `OnInvulunOnUndockingUpdated` consumers use it for invulnerability/timer handling; none changes `session.shipid`.
- Verification was through live headless Actions. No graphical-client session or local PostgreSQL database was used.


## 2026-10-24 push gate recovery (runs 841-842)

- Run 841 (`9aff921f`) failed `test_a_director_anchors_an_astrahus_from_its_ship_in_a_legal_system_and_the_structure_is_placed_and_told` at `tools/headless-client/tests/test_structures.py:490` with `CustomNotify: You must be in space to deploy a structure.` It also failed `Bound_inventory_add_accepts_client_quantity_and_transport_kwargs_and_moves_the_item` with `Expected an integer, got PyNone`. Space shard 2/2 was cancelled at its 30-minute job limit.
- Run 842 (`92b41deb`) repeated the Astrahus refusal and reported the inventory item remained at the station: expected ship ID `1990000000`, actual station ID `60000004`. Space shard 2/2 was also cancelled at 30 minutes.
- The inventory fixture had a ship item and a session `shipid` but did not persist `characters.ship_id`. `CharacterShips.MoveAsync` loads the authoritative active-ship snapshot before it can move an item; with no snapshot it returns `NothingHappened`. Main now sets the ship through `PgCharacterStore.SetShipAsync` in the same item transaction (`d407fe4e`) and creates it as a singleton (`af2df06f`). The Host regression now binds the persisted active ship and moves a stack from the station hangar into its cargo, exercising the destination that run 842 failed to reach. The V24.01 active-ship fitting caller uses that bound target in `eve/client/script/dogma/clientDogmaLocation.py:669-671`; the docked unfit route uses station-hangar Add in `eve/client/script/ui/shared/fitting/fittingSlotController.py:238-241`. `invCacheContainer.Add` forwards the bound call and its result at `eve/client/script/environment/invCache.py:998-1025`. EveJS v0.12.9 `server/src/services/inventory/invBrokerService.js:12087-12673` returns the moved item ID on successful Add.
- The Astrahus headless test waited only for `stationid` to clear, then called the remote service directly. V24.01's `InShipInSpace()` requires a nonzero `solarsystemid` and `shipid`, and no `structureid` (`eve/common/script/sys/eveCfg.py:1522-1523`); the Deploy UI also checks the inflight view and warp state before confirming (`eve/client/script/ui/services/structure/structureDeployment.py:85-95`). ConfirmDeployment calls Anchor, then cleans up and reloads the structure directory (`structureDeployment.py:172-175`). The test now waits for `stationid=None`, the expected `shipid` and `solarsystemid`, and no `structureid` before exercising Anchor. The server also verifies the ship's resolved position in that system (`dotnet/src/Eve.Host/Persistence/Structures/StructureAnchorAuthority.cs:85-100, 136-144`). The focused live Actions check for this wait is pending.
- The existing 2-way Space shard assignment has repeatedly left shard 2/2 running to the 30-minute job limit without a TRX result. A 4-way split is prepared as a separate workflow change so every Space test remains assigned and each hash partition is smaller; Actions will establish whether that removes the timeout.
- The selected skill-wire test passed in Agent run `40097684911` on `49c8a4bc` after `98ebdc31` taught its wire reader to consume notifications before the CallRsp. Agent run `40098866056` failed before tests because its dispatch set `TEST_REF=HEAD`, which the resolver correctly rejected; it is not a test result. The recovery dispatch must use the pushed commit SHA.
- Later full gates `40097819764` (`1e3675a8`) and `40097855460` (`c70b013c`) passed the Astrahus E2E job. Their remaining .NET shards were still active at this note update; neither is counted as a green full gate yet.


## 2026-10-24 push 71c1f19a follow-up

- Pushed `71c1f19a` after fetching and merging `origin/main` at `ffcd7393`. The Astrahus live test now waits for the same in-space session attributes the V24.01 UI checks before confirming deployment.
- Push-triggered CI run `40099182876` is queued. Focused Agent runs `40099200679` and `40099201050` were dispatched against `71c1f19a` for `test_structures.py` and `Bound_inventory_add_accepts_client_quantity_and_transport_kwargs_and_moves_the_item`; both are queued and no test result is available yet.
- The latest known full gates `40097819764` (`1e3675a8`) and `40097855460` (`c70b013c`) passed E2E, including the Astrahus scenario, but their Space 2/2 job was still active at the prior status check. Run 841 and 842 each cancelled Space 2/2 at the 30-minute limit.
- This push still uses the 2-way Space matrix. The pending workflow commit changes the Space test assignment to four hash shards while preserving every test. Its push-triggered run will verify the split and the complete gate.


## 2026-10-24 active-ship inventory regression alignment

- The Host regression now binds the active ship and moves a station-hangar stack into the ship's cargo, matching the destination asserted in run 842. It persists `characters.ship_id` in the same transaction as the ship and stack, and sets `session.shipid` to that ship.
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -c Release -p:UseSharedCompilation=false` passed with 0 warnings and 0 errors. No PostgreSQL test was run locally. A focused Actions check for this updated case will be dispatched against its pushed SHA.
- Agent run `40099201050` was still queued and targets the earlier Host test on `71c1f19a`; it does not cover this updated station-to-ship scenario.


## 2026-10-24 push 91e0c9bf follow-up

- Pushed `91e0c9bf` after fetching and merging `origin/main` through `5104407a`. The push contains the 4-way Space test matrix and the inventory regression that binds the active ship and moves a station-hangar stack into cargo.
- The changed .NET test project compiled locally with `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -c Release -p:UseSharedCompilation=false`: 0 warnings and 0 errors. No local PostgreSQL test was run.
- Push-triggered CI run `40099675510` is queued. Focused Host run `40099687884` was dispatched for the inventory test on `91e0c9bf` and is queued. The focused Astrahus module and the earlier Host test runs `40099200679` and `40099201050` on `71c1f19a` are in progress; the latter does not include the revised station-to-ship scenario.
- Run `40099182876` for `71c1f19a` is still in progress. The full-gate result for `91e0c9bf` and its four Space shard results are pending.


## 2026-10-24 latest CI checks

- Focused Host inventory test run `40099687884` passed for `91e0c9bf`. Focused Agent run `40100082351` passed the skill RPC notification-order test for `463d402d`; the failure in run `40097320500` was the test reading `CallRsp` before the queued notifications (`Expected: 7, Actual: 12`).
- Run `40097320500` also failed the live case `test_a_director_anchors_an_astrahus_from_its_ship_in_a_legal_system_and_the_structure_is_placed_and_told` at `tools/headless-client/tests/test_structures.py:490` with `CustomNotify: You must be in space to deploy a structure.` The updated wait for `stationid`, `shipid`, `solarsystemid`, and no `structureid` is part of the later pushes; the full live result is pending.
- The latest status check has push gates `40099182876` (71c1f19a), `40099675510` (91e0c9bf), `40099745688` (23db167f), `40099954010` (12cde77b), and `40100343293` (d89e97b2) still active or queued. No three-consecutive-green sequence has been recorded.
- The previously failing 2-way Space shard was canceled at 30 minutes without a TRX result. The 4-way assignment is now in run `40099675510`; all four shard results and its complete gate remain pending.
- The MachoError call IDs from runs 745 and 747 are confirmed as `test_docked_corporation_members_trade_at_an_eligible_upwell_structure` in `tools/headless-client/tests/test_market.py`: each failed at the direct `structureDocking.Dock` call (`test_market.py:116`, test at `:172`) while the Fortizar was 200,000 km away. The V24.01 client path at `eve/client/script/ui/services/structure/structureDocking.py:64-81` warps to docking range before requesting Dock; the headless helper now follows that transition.

## 2026-10-24 documentation push 32368c7d

- Push `32368c7d` contains handoff updates and the merge through `fabf4845`. No CI run was created for this push because `.github/workflows/ci.yml` excludes `docs/**` and Markdown files from push triggers.
- Agent test run 432 (`40095571275`) failed `Eve.Host.Tests.CharacterContactNotepadHostTests.Bound_inventory_add_accepts_client_quantity_and_transport_kwargs_and_moves_the_item`: it expected destination ship `1990000000` but observed station `60000004`. The fixture set session `shipid` without persisting the authoritative `characters.ship_id`, so the active-ship move did not happen. The regression now persists the active ship and exercises moving a station-hangar stack into its cargo; focused run 454 (`40099687884`) passed.
- The latest code-bearing push gates remain under observation: run `40099675510` for `91e0c9bf` has a failed live job while its .NET shards continue; run `40099182876` for `71c1f19a` also has a failed live job and failed .NET shards. Their complete logs and the newer queued gates must determine whether these failures are already resolved by later commits.


## 2026-10-24 CI recovery continuation

- Completed run 852 (`40098905958`, `755a38a2`) failed `test_a_corporation_unanchors_and_scoops_its_control_tower` at `tools/headless-client/tests/test_structures.py:633`, before the tower action, when the shared `_undock` helper's `ship.Undock` returned MachoError 2 (call 15). The pytest case and full failure tail are recorded from the Actions log; no user error payload was included.
- Run 852 also failed `Eve.Host.Tests.RpcSecurityInventoryTests.Real_host_registry_methods_are_enumerable`: expected 1385, actual 1393. The current test still asserts 1385 at `dotnet/tests/Eve.Host.Tests/RpcSecurityInventoryTests.cs:91`. The currently queued gates must establish the inventory count on their SHAs before changing this assertion.
- Run 852 repeated `Bound_inventory_add_accepts_client_quantity_and_transport_kwargs_and_moves_the_item` (expected an integer, got PyNone) and `Batched_config_lookups_refuse_more_than_500_raw_ids` (NullReferenceException in `ConfigService.GetMultiOwnersEx` before the requested limit error). These failures predate later inventory and config fixes. Run 853 (`40098945511`, `e114b7c5`) repeated the config failure and RPC inventory count failure.
- Agent run 464 (`40101697373`) passed `Batched_config_lookups_refuse_more_than_500_raw_ids` on `1ca1c591`.
- The V24.01 owner lookup flow is `eve/common/script/sys/eveCfg.py:549` (`EveOwners` binds `GetMultiOwnersEx`) through `carbon/common/script/sys/cfg.py:537-604` (`Recordset._Prime` batches pending keys, decodes the five-field rowset, and caches rows). `eve/client/script/parklife/ownerprimer.py:35-42` passes distinct owners from a `DoBallsAdded` event without a local count cap. EveJS v0.12.9 `server/src/services/config/configService.js:378-478` returns the five-column tuple rowset and has no 500-ID cap. The server-side limit is already recorded in `docs/reference/compat/DEVIATIONS.md:5531, 5587`; an over-500 client batch was not observed in these CI logs.
- Run 40099954010's live artifact records `eve-sim: the host connection ended: frame of 1107305565 bytes exceeds 1048576` immediately after NPC/site generation in system 30000903. The host then logs the simulation link reset and the site generation failure. The whole workflow has not yet published its pytest failure summary, so the exact case remains outstanding.
- At this update, no three consecutive green push gates have been recorded.

## 2026-10-24 follow-up: current-main owner and Astrahus results

- CI run 854 (`40099182876`, `71c1f19a`) failed `test_a_director_anchors_an_astrahus_from_its_ship_in_a_legal_system_and_the_structure_is_placed_and_told` at `tools/headless-client/tests/test_structures.py:510` after the updated wait confirmed `stationid=None`, the expected `shipid` and `solarsystemid`, and no `structureid`. The server refused `Anchor` with `CustomNotify: You must be in space to deploy a structure.` `StructureAnchorAuthority.AnchorAsync` first checks session system/ship (`dotnet/src/Eve.Host/Persistence/Structures/StructureAnchorAuthority.cs:85-100`), then checks the simulation ship position and matching system (`:136-144`) with the same refusal. The client-side session assertion passed, so the remaining branch is consistent with missing or mismatched simulation position; the run does not log the actual position result. The client deployment flow and its preconditions are documented above. Newer gates include `48d0dbf8` (active ship assignment preserving the undock wire shape); check its E2E outcome before changing this path.
- CI run 855 (`40099954010`, `12cde77b`) artifacts show `ConfigServiceTests.Multi_owner_prime_accepts_501_unbatched_client_ids` failed with `MachoWrappedException` from `RequestedIds`, and `HostServerTests.Config_recordset_routes_return_client_rows_through_host_with_transport_kwargs` expected CallRsp opcode 7 but received ErrorResponse opcode 15 on the 501-agent `GetMultiOwnersEx` call. These are one root: the raw-ID limit counted cache-backed agent owners that need no database query. V24.01 `Recordset._Prime` passes all pending keys to its multi-fetcher (`carbon/common/script/sys/cfg.py:537-604`), and `ownerprimer` passes distinct event owners in one Prime (`eve/client/script/parklife/ownerprimer.py:35-42`); EveJS v0.12.9 returns the five-column owner rowset without a 500-ID cap. Main commit `7f05600b` now permits cache-backed owners while retaining the database limit. Selected Agent tests on `2856a206` are checking the service and Host regressions; results are pending.
- Run 855's `RpcSecurityInventoryTests.Real_host_registry_methods_are_enumerable` still expected 1385 but discovered 1393. Main commit `0bea06f7` updates the snapshot to 1393; current-main selected and push-gate checks are pending.
- Run 855's simulator artifact records a frame length of 1,107,305,565 bytes after NPC/site generation in system 30000903. The completed-workflow pytest summary is still unavailable, so the exact live case remains pending. No three consecutive green push gates have been recorded.

## 2026-10-24 historical CI boundary check

- Run 583 (`39935089151`, `6ba36886`) was the last successful full gate before the first red gate in this interval. Run 584 (`39943204376`, `2d422e84`) was the first failure; its sole test failure was `RpcSecurityInventoryTests.Real_host_registry_methods_are_enumerable`, expected 1374 and found 1378. Main commit `097de488` later refreshed that inventory baseline. This direct assertion identifies the first red cause; no bisection was needed for run 584.
- Run 647 (`39991023879`, `872df205`) failed before the headless client walk because `HostComposition` queried missing relation `sde.skinrComponentPointValues` while loading `ShipSkinrCatalog`. The same startup failure appears in the Windows live job and Host tests, so run 647 has no pytest case to identify. Commit `a00e62c9` moved the mission-only table read to the mission SDE lane; this historical startup defect is not the current run 855 E2E failure.


## 2026-10-24 RPC inventory baseline follow-up

- Selected Agent run 470 (`40102696909`, SHA `2856a206`) failed `Eve.Host.Tests.RpcSecurityInventoryTests.Real_host_registry_methods_are_enumerable`: expected 1394 registered methods and found 1396 at `dotnet/tests/Eve.Host.Tests/RpcSecurityInventoryTests.cs:90`. The expected inventory is now 1396.
- The changed Host test project compiles locally with zero warnings and errors. The focused Agent regression check will run against the pushed change. The current push gates are still in progress; no three-green sequence has been reached.


## 2026-10-24 RPC inventory push status

- Commit `187b7c0e` is on main with the Host RPC inventory expectation set to 1396. Its first push attempt raced another main update; the branch was fetched and merged again, then the push succeeded.
- Focused Agent run 477 (`40105045416`) passed `RpcSecurityInventoryTests.Real_host_registry_methods_are_enumerable` against the pushed change.
- Push gate run 872 (`40104524389`, SHA `1686b316`) and newer run 873 (`40104855348`, SHA `e7ba3843`) remain in progress. Run 873 includes commit `187b7c0e`; its E2E result is still pending. There is no three-green sequence yet.
- The changed contract read-model test now expects the real issuer corporation, assignee, and contract type values. The V24.01 client reads the login reply at `eve/client/script/ui/shared/neocom/contracts/contracts.py:190-198`, and the contract panel consumes issuer, assignee and type at `eve/client/script/ui/shared/neocom/contracts/contractPanels.py:90-98`; the detail window checks personal assignee at `contractsDetailsWnd.py:94`.


## 2026-10-24 simulation frame cancellation root

- Completed CI run 870 (`40104165507`, SHA `f76fc833`) and run 872 (`40104524389`, SHA `1686b316`) both failed the live test `test_a_director_anchors_an_astrahus_from_its_ship_in_a_legal_system_and_the_structure_is_placed_and_told` at `tools/headless-client/tests/test_structures.py:513`. The pilot's session was already undocked in the expected ship and system; the client bound Beyonce, sent `UpdateStateRequest`, called bound `CmdWarpToStuff("position", ...)`, then the test's direct `structureDeployment.Anchor` call received `CustomNotify: You must be in space to deploy a structure.` The run 872 simulation artifact recorded `frame of 874185566 bytes exceeds 1048576`; related repeated artifacts were `874841694` bytes (run 873), `4076556294` (run 854), and `1107305565` (run 855).
- Root cause: `eve-sim` read each length-prefixed frame with `AsyncReadExt::read_exact` from a `tokio::select!` loop that also polls and flushes simulation ticks. Tokio documents that `select!` cancels losing futures and that `read_exact` is not cancellation-safe, so bytes consumed into the dropped header or payload future were lost; the next frame then began at the wrong byte and appeared to have a huge length ([Tokio select cancellation-safety documentation](https://docs.rs/tokio/latest/tokio/macro.select.html)). The existing `eve-net::frame::FrameReader` retains partial bytes in its `BytesMut` and fills it with `read_buf` (`crates/eve-net/src/frame.rs:13-54`); `eve-sim` now reuses it for the hello and select loop, preserving the 1 MiB limit. The new focused test cancels reads during both a partial header and a partial payload, then confirms the complete frame is returned.
- The simulation connection is internal Host-to-Rust IPC; the EVE client contract did not change. Whole-client review found the only `UpdateStateRequest` call path in `eve/client/script/remote/michelle.py:847-855`. The full `CmdWarpToStuff` caller search covered `eve/client/script/util/profiling.py:317`, `eve/client/script/ui/shared/addressBookEntries.py:155`, `eve/client/script/ui/shared/neocom/journal.py:452`, `eve/client/script/ui/shared/infoPanels/listSurroundingsBtn.py:381`, `eve/client/script/ui/shared/agencyNew/contentPieces/escalationContentPiece.py:74`, `eve/client/script/ui/services/menusvc.py:1427,2737,2747,2757`, `eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:418,432,451,462,488`, `eve/client/script/parklife/transmissionMgr.py:142`, `eve/client/script/parklife/autopilot.py:464` (autopilot variant), `objectives/client/objective_task/dungeon.py:341,369`, and `jobboard/client/features/dungeons/escalation_sites/job.py:57`. Michelle forwards the call to the bound park and returns no payload (`michelle.py:732-738`). The structure UI checks inflight view, `InShipInSpace()`, and warp state before deployment (`structureDeployment.py:85-95`); its confirmation sends `Anchor`, cleans up, and reloads the directory (`eve/client/script/ui/services/structure/structureDeployment.py:166-176`). It consumes no return value. This server-internal fix changes none of those wire arguments, response shapes, session attributes, or notifications.
- Other failures in run 870 were stale test baselines already corrected on main: `Login_and_escrow_reads_use_persisted_contract_rows_and_owner_scopes` expected zero issuer-corporation, assignee, and type fields while the stored rows returned `(200,100,1)` and `(200,300,2)`; the V24.01 login reply and contract panel consume those fields (`eve/client/script/ui/shared/neocom/contracts/contracts.py:190-198`, `eve/client/script/ui/shared/neocom/contracts/contractPanels.py:90-98`; personal assignment is checked in `eve/client/script/ui/shared/neocom/contracts/contractsDetailsWnd.py:94`). The RPC inventory snapshot expected 1394 while the registry contained 1396. Run 873's Host 4/4 and Host 2/4 shards passed, and selected Agent run 477 passed the 1396 inventory assertion.
- Local verification: `cargo +1.94.1 check -p eve-sim`, the focused `a_partial_sim_frame_survives_select_cancellation` test (1 passed), `cargo +1.94.1 fmt --all -- --check`, and `cargo +1.94.1 clippy --workspace --all-targets -- -D warnings` all passed. No local database was used. Run 873 (`40104855348`, SHA `e7ba3843`) has the same E2E failure; its .NET space 4/4 shard is still active at this note, so its complete gate result is pending. Its fast-live summary skips skill-speed and seven slow-live cases; those skips are not passes. No graphical-client session was run, and no three-green push-gate sequence has been recorded.
