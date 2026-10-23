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
- PASS: previous direct-service focused test (1 passed, 0 skipped); it was replaced because it did not exercise the packet dispatcher.
- PASS on Actions for prior source at run `39975595679`, ref `d573f9e9`; this was before the packet-dispatch test correction.
- Latest RPC packet dispatch test uses `PacketDispatcher.DispatchAsync` with Macho CallReqs for ConnectJob, DisconnectJob and inventory mutation. It checks notification order, changed and unchanged material quantities, target, monitor ID, payload, and session isolation.
- The latest local test build attempt exited with code 137. A reduced-reference build then exposed stale Host test dependency outputs and test analyzer findings; the analyzer findings in the new test were corrected. A fresh successful focused run for the corrected test is pending.
- `ci-status.py failures` found no push-triggered `ci.yml` run for the previous main SHA.
- No existing headless end-to-end industry job walk was found. No live industry walk or graphical-client session has been performed.
- Nine unrelated Rust deletions were present at initial inspection and remain unstaged.

## Remaining

Commit and push the packet-dispatch test correction, confirm it on Actions, and report headless and graphical-client verification separately.
