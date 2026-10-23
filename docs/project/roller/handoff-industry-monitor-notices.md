# Industry monitor materials notices handoff

## Implemented

`industryMonitor.ConnectJob` retains persisted monitor ID metadata and the available-material resolver. Active watches are process-local, one per `ClientSession`, replaced on reconnection and removed on disconnect/session close. `DisconnectJob` only removes the watch and saved row when the ID matches that session. Restart retains metadata and the next monitor ID, but does not revive a previous connection's watch.

`IndustryInventoryChangePublisher` wraps the inventory broker only in the final Host dispatch service list, after `RepackagingService` has received the concrete `InvBrokerService`. This preserves Host initialization that looks up the concrete broker type. After successful broker mutations, it rereads authoritative `PgItemStore` contents after the broker's inventory notification and emits `OnIndustryMaterials(monitorID, materials)` only for a changed material map. Session identity, `clientID` notification type, monitor ID and PyDict payload follow EveJS 261018.1 and the V24.01 consumer.

## Evidence

- EveJS 261018.1: `industryRuntimeState.connectMonitor` persists metadata and establishes a session watch; `industryMonitorNotices.tellMonitorAfterItemChange` compares material maps before sending.
- EveJS v0.12.9: `services/industry/industryMonitorService.js` returns `(monitorID, availableMaterials)` from ConnectJob.
- V24.01: `eve/client/script/industry/industrySvc.py` applies materials only to the open job matching the monitor ID.
- [EVE University Manufacturing](https://wiki.eveuniversity.org/Manufacturing): input/output locations can be selected for industry jobs.
- Existing resolver still checks blueprint owner, input location, flag and blueprint category.

## Checks

- PASS before final Host-list wrapping adjustment: Host build succeeded with 0 warnings/errors; the local no-DB focused test passed (1 passed, 0 skipped).
- PASS on Actions for commit `9cec6507`: `Eve.Host.Tests` selected tests (2 passed, 0 failed, 0 skipped).
- Actions live tests for `9cec6507` failed at Host startup because the initial decorator replaced the concrete `InvBrokerService` before `RepackagingService`'s type lookup. The registration order is corrected locally; rerun Actions after commit.
- A local focused test rerun after that correction was terminated when Roslyn `csc` exited with code 137 under shared machine memory/CPU load. No database was used locally.
- The headless test index has no existing end-to-end industry job walk. No live industry walk or graphical-client session has been performed.
- Nine unrelated Rust deletions were present at initial inspection and remain unstaged.

## Remaining

Commit and push the Host dispatch ordering fix, then run selected Host tests on Actions and inspect push-triggered CI. Report headless and graphical-client gaps separately.
