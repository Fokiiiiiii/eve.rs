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
- No PostgreSQL or graphical-client checks were performed. GitHub Actions verification is pending the push.
