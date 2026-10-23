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

- PASS: Host project build (0 warnings, 0 errors) before recent upstream Host changes.
- PASS: current RPC-dispatch focused tests on Actions, run `39987623229`, ref `be113558`.
- PASS: current push CI Host shards 1–3; Host shard 4 also passed after the reconnect assertion was corrected.
- The headless suite already had `test_walk_industry.py`; a new live test `test_industry_monitor_live.py` now opens a monitor and moves a seeded material stack from ship cargo into the watched station hangar. It checks the updated material map and OnItemChange-before-OnIndustryMaterials order.
- Actions CI for `be113558` failed in an unrelated market walk: `GetMarketOrderHistory` exceeded the session RPC budget. The failure appeared on two consecutive runs. This needs a root-cause check.
- The latest local focused test build exited with code 137. A reduced-reference compile used stale Host test dependency outputs; no successful local run of the current RPC-dispatch test yet.
- No graphical-client session has been performed.
- Nine unrelated Rust deletions were present at initial inspection and remain unstaged.

## Remaining

Push the new live industry monitor test, confirm it in Actions, investigate and resolve the repeated market budget failure, then report headless and graphical-client verification separately. Persistence of the saved monitor metadata across a process restart is supported by the GameStoreCache paths but was not exercised with a restart test.
