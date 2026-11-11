# Deep fix: login, sessions and accounts (review record)

Scope: login handshake, password and SSO credentials, character selection, session change
handoff, reconnect and duplicate login, account state (alpha/omega), bans and kicks,
shutdown and restart of the Host and sim link.

Status: one code change (shutdown notice). Handshake candidates were dropped earlier because
the V24.01 client does not depend on them. Reason codes match for wrong password and ban.

## Compared sources

- eve.rs: `dotnet/src/Eve.Identity/Accounts.cs`, `dotnet/src/Eve.Host/HostServer.cs`,
  `dotnet/src/Eve.Host/ConnectedSessions.cs`, `dotnet/src/Eve.Host/Hosting/HostRuntime.cs`,
  `dotnet/src/Eve.Host/Hosting/HostComposition.cs`, `dotnet/src/Eve.Services/ClientSession.cs`,
  `crates/eve-net/src/host.rs`, `crates/eve-net/src/handshake.rs`.
- V24.01 client, `Fokiiiiiii/eve-logic`, read through the local codebase index.
- EveJS reference: not indexed in this session, so no EveJS file:line for duplicate login or
  shutdown.

## Reason-code table

| Case | eve.rs sends | Client | Result |
|---|---|---|---|
| Wrong password | `LoginAuthFailed` (`Accounts.cs` LoginRefused.BadCredentials) | `loginII.py` `Login._Connect` 416-498 focuses password | match |
| Banned account | `ACCOUNTBANNED` (`Accounts.cs` LoginRefused.Banned) | `ExceptionMappingGPCS.py` MESSAGES 11-48; `machoNet.py` 2523-2539 | match |
| Host error | `Unavailable` | not in MESSAGES, shown raw | cosmetic |
| Duplicate login | `AddExclusive` replaces the old session (`ConnectedSessions.cs` 28-46), old session told "Another client logged in to this account." | no dedicated client code | implemented; not a gap |
| Server shutdown | `ACL_SHUTTINGDOWN` sent first (new) | `ExceptionMappingGPCS.py` MESSAGES has `ACL_SHUTTINGDOWN` | implemented in this change |

## Shutdown notice (this change)

- `ConnectedSessions.DisconnectAll(reason)` asks each connected session to close with the
  reason through the existing `ClientSession.RequestDisconnect` path. A session that cannot be
  reached is skipped inside try/catch. Returns the number asked.
- `HostRuntime.ShutdownAsync` calls `DisconnectAll("ACL_SHUTTINGDOWN")` first. If any session
  was asked, it waits a fixed 500 ms so the front can write the notices, then disposes
  services as before. The wait is bounded and only happens when a session was asked, so the
  about 85 test callers without sessions are not slowed.
- `HostRuntime` takes an optional trailing `ConnectedSessions? connectedSessions = null`.
  `HostComposition.BuildAsync` passes its `connected` instance (one-line change).
- Test: `dotnet/tests/Eve.Host.Tests/ConnectedSessionsTests.cs`
  `The_shutdown_notice_reaches_every_connected_session`.
- Not covered by a test: the `ShutdownAsync` wiring itself and the real socket flush. The
  500 ms pause is a heuristic, not a proven flush. Needs a CI run to confirm.

## Not checked (remaining)

- EveJS shutdown and second-login behaviour.
- `crates/eve-net/src/host.rs` write flush, `server.rs`, `client.rs`, `frame.rs`.
- The dotnet alpha/omega, character selection and creation flows, and sim-link restart.
- CI: see the run list for this branch; this record does not claim it is green.
