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

- PASS: `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/src/Eve.Host/Eve.Host.csproj --no-restore -m:1 -p:UseSharedCompilation=false` (0 warnings, 0 errors; about 54 minutes under shared machine load).
- PASS: local no-DB focused test `Host_inventory_dispatch_publishes_changed_materials_once_after_item_change_and_isolates_sessions` (1 passed, 0 skipped).
- PASS on Actions for commit `9cec6507`: selected `Eve.Host.Tests` (2 passed, 0 failed, 0 skipped).
- Earlier Actions live tests failed at Host startup because the initial decorator replaced the concrete `InvBrokerService` before `RepackagingService`'s lookup. The current composition unwraps the decorated broker for that lookup.
- Current composition was pushed to `main` at `885429d7`. Selected Actions tests and push-triggered CI are still pending.
- The headless test index has no existing end-to-end industry job walk. No live industry walk or graphical-client session has been performed.
- Nine unrelated Rust deletions were present at initial inspection and remain unstaged.

## Remaining

Run selected Host tests on Actions, inspect push-triggered CI and fix any regression. Report headless and graphical-client verification separately.
