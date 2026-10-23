# Industry monitor materials notices handoff

## Implemented

`industryMonitor.ConnectJob` retains the existing persisted monitor ID metadata and available-material resolver. It now records one process-local active watch per `ClientSession`, replacing that session's watch when a job connects again. `DisconnectJob` removes only the matching session's active watch and saved monitor row. Closing a session drops its active watch; saved monitor rows remain metadata and do not resume a notification watch after process restart.

`IndustryInventoryChangePublisher` decorates the Host inventory broker. For successful inventory mutation RPCs, it calls the monitor service after the inner operation returns. The broker commits and sends its existing inventory notifications before the publisher rereads authoritative `PgItemStore` contents. The monitor compares type quantities and sends `OnIndustryMaterials(monitorID, materials)` only when the material map changed. The notification uses the same session, `clientID` notification address and PyDict shape as the V24.01 client flow.

## Evidence

- EveJS 261018.1: `industryRuntimeState.connectMonitor` persists monitor metadata and sets one session watch; `industryMonitorNotices.tellMonitorAfterItemChange` compares material maps and sends the named notice only on change.
- EveJS v0.12.9: `services/industry/industryMonitorService.js` returns `(monitorID, availableMaterials)` from ConnectJob.
- V24.01 client: `eve/client/script/industry/industrySvc.py`, `OnIndustryMaterials` updates the open job only when its monitor ID matches.
- [EVE University Manufacturing](https://wiki.eveuniversity.org/Manufacturing): input and output locations are selectable; the input location determines which inventory supplies manufacturing materials.
- The existing resolver continues to enforce the blueprint owner, input location, flag and blueprint-category filtering.

## Checks

- PASS: `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/src/Eve.Host/Eve.Host.csproj --no-restore -p:UseSharedCompilation=false`.
- PASS: one local, non-DB focused test, `Host_inventory_dispatch_publishes_changed_materials_once_after_item_change_and_isolates_sessions` (1 passed, 0 skipped). It checks notification order, name, user/session target, monitor ID, material payload, unchanged quantity suppression and second-session isolation.
- The disconnect/reconnect regression is included in `IndustryMonitorNoticeTests`; it was not run locally and must be selected in Actions.
- No PostgreSQL database was used. No live headless industry walk or graphical-client session was run. The headless test index has no existing end-to-end industry job walk.
- Initial Host compilation found one local-name collision; it was corrected. The succeeding Host build had 0 warnings and 0 errors.

## Remaining

Run the focused Host tests on Actions, including `Disconnect_and_reconnect_replace_the_session_monitor`, and inspect the push-triggered CI result. The live headless and graphical client flows remain to be checked. The nine unrelated Rust deletions present at initial inspection were preserved and remain unstaged.
