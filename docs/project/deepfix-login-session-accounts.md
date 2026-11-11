# Deep fix: login, sessions and accounts (review record)

Scope: login handshake, password and SSO credentials, character selection, session change
handoff, reconnect and duplicate login, account state (alpha/omega), bans and kicks,
shutdown and restart of the Host and sim link.

Status: review only. No code change in this record.

## Compared sources

- eve.rs: `dotnet/src/Eve.Identity/Accounts.cs`, `dotnet/src/Eve.Host/HostServer.cs`,
  `dotnet/src/Eve.Host/ConnectedSessions.cs`, `dotnet/src/Eve.Host/Hosting/HostRuntime.cs`,
  `dotnet/src/Eve.Services/ClientSession.cs`, `crates/eve-net/src/host.rs`,
  `crates/eve-net/src/handshake.rs`.
- V24.01 client, `Fokiiiiiii/eve-logic`, read through the local codebase index.
- EveJS v0.12.9 reference: the `home-user-evejs-261108.1` index was not present when this
  record was written, so the EveJS side of the duplicate-login and shutdown comparison is
  still open.

## Reason-code table (earlier)

| Case | eve.rs sends | Client | Result |
|---|---|---|---|
| Wrong password | `LoginAuthFailed` (`Accounts.cs` LoginRefused.BadCredentials) | `loginII.py` `Login._Connect` 416-498 focuses password | match |
| Banned account | `ACCOUNTBANNED` (`Accounts.cs` LoginRefused.Banned) | `ExceptionMappingGPCS.py` MESSAGES 11-48; `machoNet.py` 2523-2539 | match |
| Host error | `Unavailable` | not in MESSAGES, shown as raw text | cosmetic |

## Duplicate login: implemented in eve.rs (not a gap)

- `dotnet/src/Eve.Host/ConnectedSessions.cs` `AddExclusive` (28-46): a new session for an
  account replaces the existing one and returns the old one.
- `dotnet/tests/Eve.Host.Tests/ConnectedSessionsTests.cs` 7-25 and 27-39: the old session is
  told "Another client logged in to this account." and a concurrent login leaves one session.
- `dotnet/tests/Eve.Host.Tests/HostServerTests.cs` 2159-2180: a second front login disconnects
  the previous session of the same account.
- The reason text is a plain string passed to `RequestDisconnect` (`ClientSession.cs` 252-260).
  The front turns a `Disconnect` reason into a GPSTransportClosed pickle
  (`crates/eve-net/tests/front.rs` 247-264 proves that a reason such as `ACCOUNTBANNED` reaches
  the client as a pickled close). The client shows unknown reason text raw.
- Open point: the V24.01 client has no dedicated duplicate-login code. It was not decided
  whether the plain-text duplicate reason should become a client-known code.

## Shutdown notice: real gap, not fixed

- The client expects `ACL_SHUTTINGDOWN` (ExceptionMappingGPCS.py MESSAGES, line 11-48).
- eve.rs has no emitter: `dotnet/src/Eve.Host/Hosting/HostRuntime.cs` `ShutdownAsync` (67-102)
  stops the XMPP listener, runs cleanup, and disposes services without notifying sessions.
  The client sees a closed socket.
- Smallest safe first step (not done, needs a run to validate timing): in `ShutdownAsync`,
  before disposing services, call `RequestDisconnect("ACL_SHUTTINGDOWN")` on each connected
  session and wait for the front to flush. Test with the `fake_host` pattern in
  `crates/eve-net/tests/front.rs` (the existing disconnect test at 247-264 is the template).
  Risk: `ShutdownAsync` has about 85 callers, so the change must not break tests that count
  disconnects. It was not made because the flush order could not be verified without a run.

## Not checked (remaining)

- EveJS shutdown and second-login behaviour (index not ready).
- `crates/eve-net/src/host.rs` write-side flush, `server.rs`, `client.rs`, `frame.rs`.
- The dotnet alpha/omega, character selection and creation flows, and sim-link restart.
- CI: see the run list for this branch; this record does not claim it is green.
