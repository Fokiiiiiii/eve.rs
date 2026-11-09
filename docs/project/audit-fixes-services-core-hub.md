# Services core and hub audit fixes

Date: 2026-11-09

Audit report: `/home/user/reports/90-audit-services-core-hub-r15.md`

## Findings and verification

All three findings in the audit report were confirmed against the current implementation, the V24.01 client source at `/home/user/eve-logic`, and EveJS 261028.1 at `/home/user/evejs-261028.1`. No finding was skipped as false.

### P1: bound OIDs were not scoped to the session that received them

The dispatcher previously resolved OIDs through a process-wide registry without passing the caller session. It now records the session when a bind result registers an OID and resolves bound calls only for a recorded holder. The real Host regression test binds an OID in one connection, verifies a second connection receives `UnMachoDestination` without running the handler, then verifies both sessions can call after each binds the same OID.

Evidence: V24.01's `ObjectCallGPCS.py:80-89` rejects OIDs absent from `session.machoObjectsByID`; EveJS passes the session at `packetDispatcher.js:559-562` and checks it in `serviceManager.js:244-250`. Its holder tracking is at `serviceManager.js:81-97`.

### P2: disconnect did not release the session's bound-object registrations

Host session teardown now detaches all OIDs held by that session, awaits asynchronous release handlers, and leaves failed cleanup in the existing retry queue. A shared OID remains registered while another session still holds it; the Host socket regression test verifies it is removed and released after the last holder disconnects. Existing registry retry coverage verifies failed asynchronous cleanup is retained and retried.

Evidence: V24.01 unregisters each OID in `basesession.py:937-944`; EveJS releases the session's OIDs in `tcp/index.js:265-267` and retains a shared OID until the final holder leaves in `serviceManager.js:108-129`.

### P2: a post-response callback failure could send a second RPC response

`ClientSession.RunAfterResponse` now catches and reports failures per callback, then continues through the remaining callbacks. The dispatcher cannot route those failures through the RPC error path after sending `CallRsp`. The regression test verifies one successful response, the following notification, and error reporting.

Evidence: EveJS sends the response before invoking the hook and catches hook exceptions at `packetDispatcher.js:701-727`. The client call path and session OID release path were also checked across the whole `/home/user/eve-logic` source tree using searches for `machoObjectsByID`, `UnregisterMachoObject`, `ClientHasReleasedTheseObjects`, `afterCallResponse`, and `afterCallFailure`; relevant implementations are in `ObjectCallGPCS.py:80-89, 184-193` and `basesession.py:742-768, 937-944`.

## Regression coverage

- `BoundObjectSecurityTests`: non-owner rejection and post-response failure behavior.
- `HostServerTests.Bound_oids_are_session_owned_and_released_after_the_last_holding_session_closes`: real Host socket bind, foreign-session rejection, shared-holder calls, and disconnect cleanup.
- `RegistryTests`: release ownership, shared holders, refIDs, and retained asynchronous cleanup retries.
- Focused dispatch and Host tests also cover existing bound call, RPC, corporation bootstrap, and skill purchase behavior affected by session-scoped registration.

The new regression tests were run before the fixes and reproduced the failures: a foreign session received `CallRsp`, and a post-response callback failure produced `ErrorResponse` instead of the queued notification. The Host socket test also initially failed on foreign access and then, after that fix, on disconnect cleanup.

## Local verification

- Release builds of `Eve.Services`, `Eve.Host`, `Eve.Services.Tests`, and `Eve.Host.Tests`: all succeeded with 0 warnings and 0 errors.
- `Eve.Services.Tests` focused slice (`BoundObjectSecurityTests`, `RegistryTests`, `DispatchGoldenTests`, `StrictRpcTests`): 224 passed, 0 failed, 0 skipped.
- `Eve.Host.Tests` `HostServerTests` class on `eve_p15`: 433 passed, 0 failed, 0 skipped. The first run used only SDE build 3396210 and failed 12 SDE-backed compositions because `sde_mission.skinrComponentPointValues` was not loaded; after loading the pinned mission build 3405148, the full class passed.
- Focused Host ownership, corporation bootstrap, skill bound route, and character composition tests: 4 passed, 0 failed, 0 skipped.
- `Eve.Corporations.Tests` `CorpStationMgrTests` on `eve_p15`: 8 passed, 0 failed, 0 skipped.
- `git diff --check`: passed.

## Push CI follow-up

Push CI run 1646 reported a regression in `SpaceServiceTests.Undocking_tells_the_client_in_the_reference_order_and_answers_none`: when the registry had no service entry for an OID, `UnregisterBoundObject` returned before removing that OID from the releasing session's local list. A new `RegistryTests` case reproduced the failure before the correction. The registry now clears that session bookkeeping while still returning `false` for the absent service entry.

After the correction, the Release builds of `Eve.Services` and `Eve.Services.Tests` succeeded with 0 warnings and errors; the Services registry/dispatch slice passed 225 tests, and the complete `SpaceServiceTests` class passed 168 tests on `eve_p15`, all with 0 skipped.

## Verification limits

The EveJS implementation was source-compared but not started or exercised as a running server. No graphical V24.01 client session or live packet trace was run. Verification results for local builds and tests are recorded in the job summary; no skipped tests are counted as passing.
