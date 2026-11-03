# Industry monitor materials notices handoff

## Implemented

`industryMonitor.ConnectJob` retains persisted monitor ID metadata and the available-material resolver. Active watches are process-local, one per `ClientSession`, replaced on reconnection and removed on disconnect/session close. `DisconnectJob` only removes the watch and saved row when the ID matches that session. Restart retains metadata and the next monitor ID, but does not revive a previous connection's watch.

`InventoryRpc` registers `IndustryInventoryChangePublisher` around the inventory broker. `CharacterServiceList` unwraps its concrete broker for `RepackagingService` while Host dispatch retains the decorated broker. After successful broker mutations, it rereads authoritative `PgItemStore` contents after the broker's inventory notification and emits `OnIndustryMaterials(monitorID, materials)` only for a changed material map. Session identity, `clientID` notification type, monitor ID and PyDict payload follow EveJS 261018.1 and the V24.01 consumer.

## Evidence

- EveJS 261018.1: `industryRuntimeState.connectMonitor` persists metadata and establishes a session watch; `industryMonitorNotices.tellMonitorAfterItemChange` compares material maps before sending.
- EveJS v0.12.9: `services/industry/industryMonitorService.js` returns `(monitorID, availableMaterials)` from ConnectJob.
- V24.01: `eve/client/script/industry/industrySvc.py` applies materials only to the open job matching the monitor ID.
- [EVE University Manufacturing](https://wiki.eveuniversity.org/Manufacturing): input/output locations can be selected for industry jobs.
- Existing resolver still checks blueprint owner, input location, flag and blueprint category.

## Checks

- PASS: Host project build (0 warnings, 0 errors) before the latest upstream Host changes.
- PASS: focused Host dispatch tests on Actions, run `39987623229`, ref `be113558`.
- PASS: Host CI shards 1–4 passed on the prior push after correcting the reconnect assertion.
- PUSHED: live headless industry monitor walk in commit `8b3c8e04`; it seeds materials at two locations and checks monitor payload 8 plus OnItemChange-before-OnIndustryMaterials ordering.
- FAIL: push CI run `39990672521` on `8b3c8e04`; Host startup reads missing `sde.skinrComponentPointValues` in `ShipSkinrCatalog.LoadAsync`, preventing live walks from reaching their calls. The market walk also exceeds the current session RPC budget.
- FAIL: focused agent-test runs `39990690564` and `39991587566` both failed during the one-time .NET solution build before test output. The second run was dispatched after the first failure; neither returned a test result.
- No graphical-client session has been performed.
- Monitor metadata persistence across process restart has not been exercised; no PostgreSQL database was used locally.
- Nine unrelated Rust deletions were present at initial inspection and remain unstaged.

## Remaining

Resolve the CI failures, confirm the Host dispatch and live industry walk on Actions, and exercise saved monitor metadata across restart if a no-database test path is available. Report headless and graphical-client verification separately.


## Facility manager read-path update (2026-10-23)

Added Host reads for `facilityManager.GetMaxActivityModifiers` from the shared cost-index source and `GetFacilityTaxes` from the pinned NPC corporation SDE data or deployed structure state. `FacilityManagerService` now returns a client notification error for `SetFacilityTaxes` when no authoritative writer is available, instead of returning a false success. The TODO still records the broader provider row as partial: structure locations/discovery and tax mutation with role checks, persistence and notifications remain open.

- `dotnet build dotnet/src/Eve.Services.Core/Eve.Services.Core.csproj -p:UseSharedCompilation=false --no-restore`: passed, 0 warnings/errors.
- `dotnet build dotnet/src/Eve.Host/Eve.Host.csproj -p:UseSharedCompilation=false --no-restore`: passed, 0 warnings/errors.
- Focused Host test `IndustryFacilityManagerTests.Max_activity_modifiers_include_each_client_activity_with_its_cost_index`: passed, 1/1, 0 skipped.
- No PostgreSQL or graphical-client checks were performed. GitHub Actions focused tests completed after the push: Host run `40001886886` and Core run `40002916849` passed with no failed tests or jobs. Push CI run `40001866034` failed; details follow.


## CI follow-up for `da6d90a8`

Focused Host and Core checks passed on Actions. Push CI's .NET build, all .NET test shards, and Rust format/clippy passed. The overall run failed in three jobs: Windows SDE/Rust workspace, Linux Rust workspace, and the fast live client at `tests/test_skinr_live.py:389` (`ConsumeRequest` returned HTTP 401 instead of 200). The run also reported eight skipped headless tests (skill training speed and slow movement/warp/jump/others/target/module/health cases); these are not passes. The failures concern Rust and SKINR paths untouched by this industry change; run `40001866034` remains red.


## Material authorization update (2026-11-03)

The current EveJS reference is 261028.1. Its `industryRuntimeState.resolveMonitorAvailableMaterials` resolves the blueprint owner and facility locations before aggregating (`industryRuntimeState.js:5621-5672`), while its raw-request fallback at `5653-5666` is intentionally not copied. The V24 caller sends `job.dump()` (`industrySvc.py:304-307`); that dump includes client-provided character/corporation identifiers and input-location fields (`job.py:624-640`). The client-side `canTake` check (`job.py:577-578`) is not trusted by Host. The returned `(monitorID, materials)` tuple and `OnIndustryMaterials(jobID, materials)` map remain unchanged (`industrySvc.py:304-307,323-327`).

Production `IndustryMonitorRpcService` now requires a policy callback backed by persisted blueprint ownership, the authenticated character session, corporation membership, the current office and its folder, Factory Manager role, and effective per-division Query plus Take access. It canonicalizes personal hangar/corporate office roots and allows direct industry-container children only for the supported container groups; it does not accept client owner IDs or raw location claims. The same authorization check runs before notification material reads.

- Local Host build: passed, 0 warnings and 0 errors.
- Focused PostgreSQL-backed `IndustryMonitorNoticeTests`: passed, 5/5, 0 skipped.
- Relevant Host regression classes: passed, 50/50, 0 skipped.
- `HostServerTests.Inventory_registration_injects_the_drone_bay_port_for_capacity_and_return`: passed, 1/1, 0 skipped.
- Tests used the pinned SDE copied into the task database `eve_p3` and ran through `tools/dev/db-lock.sh`.
- No graphical-client session was performed.
- GitHub Actions verification for this update is pending.
