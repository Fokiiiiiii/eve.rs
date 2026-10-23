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

- Runs 620 (`a29f636f`) and 621 (`5ba7acea`) both completed the .NET test shards successfully; run 620 therefore verifies the cargo fixture setup and the migration expectation changes. Both E2E jobs still fail `test_market_browse_and_history_refresh_fit_one_session` when `GetMarketOrderHistory` returns Macho error 2.
- V24.01's `MarketQuote.GetMarketOrderHistory` directly calls `marketProxy.GetMarketOrderHistory`. EveJS v0.12.9 processes pending expiry events, reads character and corporation orders, and returns the cached owner-order rowset. eve.rs sweeps expiry, reads both owner scopes, and builds the same rowset. The Action log does not include the service exception; the failing live test now includes the ERRORRESPONSE payload in its assertion output so the next run can identify the server-side exception.
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


## 2026-10-23 CI 647/648 root-cause review

- The E2E and Host logs report `42P01` for `sde.skinrComponentPointValues`. These records are in the mission SDE. The failing Host composition passed the regular SDE reader to the SKINR catalog, and the live harness loaded only the regular SDE build. Main commit `a00e62c9` now reads the catalog through `sde_mission`, verifies mission tables when reusing the loaded build, and has the live harness ensure the mission build. The cache key update in `86c11a0c` is separate from this schema mismatch.
- Run 648 also reports `ShipSkinrDispatchTests.Draft_save_and_read_responses_keep_the_v24_row_shape` failing its decoded skin string assertion. Main commit `bb6029d1` corrects the test's nested response decoding.
- Run 647/648 predate these changes. Run 683 on `8610990c` is checking the corrected SKINR live fixture shape; run 684 on the later head `253597a9` is now the newest push gate. Check both completed results before closing these causes.

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
