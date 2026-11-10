# Services Core Hub Audit R15 Revalidation

Date: 2026-11-10

Audit report: `/home/user/reports/90-audit-services-core-hub-r15.md`.

## Result

All three reported findings are real. Their fixes and regression tests were already present in `origin/main` when this task began (`5e7c7c3e4` and `5f948819f`), so this revalidation adds no source changes. No finding was skipped as false. The earlier implementation record at `docs/project/audit-fixes-services-core-hub.md` describes the original failing-test-first fixes and CI follow-up.

## P1: Bound OIDs must belong to the calling session

Confirmed. `PacketDispatcher.HandleCallRequestAsync` resolves a bound call through `ServiceRegistry.Lookup(serviceName, session)`. Bind results are registered with `RegisterBoundObjectForSession`; the registry returns no handler for a session absent from that OID's holder set, so dispatch answers `UnMachoDestination` without invoking the handler. `HostServerTests.Bound_oids_are_session_owned_and_released_after_the_last_holding_session_closes` exercises two real socket sessions: a foreign call is rejected, both holders can call after binding, and the handler count does not change on rejection.

V24.01 checks `objectID` against the current `session.machoObjectsByID` before dispatch in `/home/user/eve-logic/carbon/common/script/net/ObjectCallGPCS.py:80-89`. EveJS 261108.1 passes `session` from `server/src/network/packetDispatcher.js:561-562` and rejects unowned OIDs in `server/src/services/serviceManager.js:244-250`; holder registration is in `:81-97`.

## P2: Disconnect must release that session's bound-object holders

Confirmed. `HostServer` calls `ReleaseSessionBoundObjectsAsync` in its session-finally path. The registry detaches that session's OIDs and runs release callbacks; shared OIDs stay registered until the last holder leaves. The Host socket test above checks the first close preserves the shared OID and the last close removes it and calls the release handler. `RegistryTests` also covers asynchronous cleanup retry and clearing a session's stale OID when its registry entry is already absent (the CI follow-up in `5f948819f`).

V24.01 disconnect cleanup unregisters each ID in `/home/user/eve-logic/carbon/common/script/sys/basesession.py:937-944`. EveJS wires socket close to `releaseSessionBoundObjects` in `server/src/network/tcp/index.js:271-273`; the holder-count and final-release behavior is in `server/src/services/serviceManager.js:81-129`.

## P2: A post-response callback failure must not send a second RPC response

Confirmed. `PacketDispatcher` sends `CallRsp` before running `ClientSession.RunAfterResponse`. That method catches each callback error separately and continues, while the dispatcher reports the error through its problem callback. `BoundObjectSecurityTests.An_after_response_callback_failure_does_not_change_the_answer_or_stop_later_notifications` asserts one successful response, the later notification, and error reporting.

EveJS 261108.1 likewise sends the response, then catches `afterCallResponse` failures in `/home/user/evejs-261108.1/server/src/network/packetDispatcher.js:701-727`. The regression test checks the wire packet types and notification order; no failure response is emitted after the successful response.

## Verification on this revalidation

- Release builds of `Eve.Services`, `Eve.Host`, `Eve.Services.Tests`, and `Eve.Host.Tests`: succeeded with 0 warnings and 0 errors.
- `BoundObjectSecurityTests`, `RegistryTests`, `DispatchGoldenTests`, and `StrictRpcTests`: 225 passed, 0 failed, 0 skipped.
- `HostServerTests` on `eve_p4`, with pinned SDE build 3396210 and mission build 3405148 loaded: 441 passed, 0 failed, 0 skipped.
- Main CI status check: latest listed run 1674 succeeded. Older runs 1672 and 1673 failed in the live POS unanchor test; run 1674 is newer and green.

This is headless/test and source verification. No graphical V24.01 client session, live packet trace, or running EveJS comparison was performed in this revalidation. The current tests were not replayed against a pre-fix checkout; the prior implementation record contains the original failing reproductions.
