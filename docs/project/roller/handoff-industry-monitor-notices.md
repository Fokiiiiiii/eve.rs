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
- PASS: direct-service focused test (1 passed, 0 skipped); replaced because it did not exercise packet dispatch.
- PASS on Actions at run `39975595679`, ref `d573f9e9`; this was before packet-dispatch coverage.
- The current tests use `PacketDispatcher.DispatchAsync` with Macho CallReqs for ConnectJob, DisconnectJob and inventory mutation. They verify changed/unchanged quantities, notice order/name/target/monitor ID/material payload, reconnect replacement and session isolation.
- Actions run `39986027701` found the reconnect test counting both `OnItemChange` and `OnIndustryMaterials` as a single expected notification. The assertion now selects only `OnIndustryMaterials`; a rerun is pending.
- The latest local focused test attempt exited with code 137. The reduced-reference compile used stale Host test dependencies and reported unrelated API mismatches; no successful local run of the packet-dispatch revision yet.
- Push-triggered CI run `39986003895` was still in progress at last check; Host test shard 4/4 failed at the same reconnect assertion. Other Host shards and the .NET build passed.
- No existing headless end-to-end industry job walk was found. No live industry walk or graphical-client session has been performed.
- Nine unrelated Rust deletions were present at initial inspection and remain unstaged.

## Remaining

Push the assertion correction, confirm on Actions, inspect push-triggered CI, and report headless and graphical-client verification separately.
