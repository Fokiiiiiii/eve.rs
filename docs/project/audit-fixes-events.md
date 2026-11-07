# Event bus audit findings (r14)

## Verified findings and changes

### P2: SKINR completion notices could be lost after the event cursor committed

The general claim that `AfterCommit` must provide at-least-once delivery is false. `EventBus` commits a subscriber's database work and cursor before running an in-memory callback. `EventContext.AfterCommit` is documented as post-commit best-effort, and the existing `EventBusTests.AfterCommit_failure_does_not_redeliver_the_committed_event` test encodes that contract. PostgreSQL-only handler effects are atomic with the cursor; external effects before commit may repeat; an `AfterCommit` callback is outside that transaction and is not retried.

The concrete SKINR use was a real client-visible defect: its two V24 notices were sent only from `AfterCommit`, so a process stop or callback failure after the cursor commit permanently skipped them. The V24.01 listeners are `shipSkinSequencingNoticeMessenger.py:16-31` and `shipSkinLicenseNoticeMessenger.py:17-29`; the license service consumes the update in `shipSkinLicensesSvc.py:153-184`. EveJS 261028.1 emits `CompletedNotice` followed by `UpdatedNotice` in `server/src/_secondary/express/gatewayServices/shipSkinSequencingGatewayService.js:28-38`.

The Host subscriber now writes a durable scheduler job in the same transaction as its EventBus cursor. The job preserves the two notices' payloads and order, retries while the character has no live `Notices.Consume` stream, and is retained for up to seven days. It stays pending after queuing: the authenticated HTTP/2 stream writer acknowledges each frame only after `BodyWriter.WriteAsync` flushes it, and a later scheduler pass finishes the job after all frames are acknowledged. The Host integration regression completes an actual SKINR job, verifies its persisted license, leaves the recipient disconnected, restarts Host, then queues frames to a stream that disconnects before reading them. A second restart and reconnect must receive both notices. The test asserts their exact names, order, character, job UUID, skin identifier and license values.

V24's notice stream has no application-level acknowledgement in the inspected client flow, so the delivery boundary is a successful Host HTTP/2 stream flush, not proof that the client processed the frame. If the Host stops after a flush but before the scheduler marks the job complete, it may send the same notice again after restart. A real graphical-client check has not been performed. EveJS source was inspected but the EveJS server was not run. The seven-day retention window is an intentional eve.rs reliability extension over EveJS's active-stream broadcast behavior.

### P2: invalid EventBus options could stall or prematurely dead-letter events

Confirmed. `BatchPerPump = 0` prevents the pump loop from handling events; `MaxAttempts = 0` dead-letters the first handler failure; and `PollMs < 5` makes the retry-wait `Math.Clamp` bounds invalid. The constructor now validates positive retry/backoff/retention bounds, `PollMs >= 5`, `BatchPerPump >= 1`, and `HardRetentionMs >= RetentionMs` before registering its scheduler handler. Retry backoff multiplication is bounded to avoid overflow.

`EventBusOptionsTests` covers each invalid boundary and confirms the scheduler handler was not registered. The tests were first run against the unmodified constructor and failed because no exception was thrown; after the change all nine cases passed.

## Verification record

- Source evidence: V24.01 client under `/home/user/eve-logic`; EveJS 261028.1 under `/home/user/evejs-261028.1`; Host and Events code in this repository.
- Search tooling: codebase-memory MCP and Serena were unavailable in this session; repository and source tracing used `rg`, `sed`, and `nl`.
- `Eve.Events.Tests` full slice: 30 passed, 0 failed, 0 skipped on `eve_p5`; this includes `EventBusOptionsTests` and `EventBusTests`.
- Host classes using the changed notice/event behavior (`ShipSkinrNoticeDeliveryTests`, `ShipSkinrDispatchTests`, `PublicGatewayTests`, `LocalChatGatewayTests`, `ControlTowerHostTests`, `StructureCompositionTests`, `WarEligibilityOwnerSourcesTests`): 52 passed, 0 failed, 0 skipped on `eve_p5` before the final multi-stream acknowledgement refinement.
- Final focused Host regressions after that refinement: 4 passed, 0 failed, 0 skipped on `eve_p5`, including the queued-before-restart case, the actual HTTP/2 flush acknowledgement, the multiple-stream acknowledgement, and the existing title-notice HTTP/2 flow.
- Release `-warnaserror` builds of `Eve.Events`, `Eve.Events.Tests`, `Eve.Host`, and `Eve.Host.Tests` succeeded with 0 warnings. The Host source and test projects were rebuilt after the final acknowledgement change.
- Baseline evidence: `EventBusOptionsTests` initially had 9 expected missing-validation failures; the queued-before-stream-restart Host regression initially timed out because the old implementation had already completed its in-memory delivery job. All pass after their fixes.
- Database-backed tests use the isolated local database `eve_p5`.
- Not checked: graphical V24 client session, real packet trace, or an executable EveJS comparison run.
