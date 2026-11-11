# Deep fix: login, sessions and accounts (review record)

Scope: login handshake, password and SSO credentials, character selection, session change
handoff, reconnect and duplicate login, account state (alpha/omega), bans and kicks,
shutdown and restart of the Host and sim link.

Status: review only. No code change. Handshake candidates (maxSessionTime, role, SSO token)
were dropped earlier because the V24.01 client does not depend on them. The reason-code
table below found no proven mismatch, so no fix and no new test.

## Compared sources

- eve.rs: `dotnet/src/Eve.Identity/Accounts.cs` (`LoginRefused`, `Decide`),
  `dotnet/src/Eve.Host/HostServer.cs` (`FrontConnection.AuthenticateAsync`),
  `crates/eve-net/src/handshake.rs` (`transport_closed`), `crates/eve-net/tests/front.rs`
  and `handshake.rs` (existing refused-login tests).
- V24.01 client, `Fokiiiiiii/eve-logic`, read through the local codebase index
  (`home-user-eve-logic`).

## Reason-code table

| Case | eve.rs sends | Client expects / handles | Result |
|---|---|---|---|
| Wrong password | `LoginAuthFailed` (`Accounts.cs` `LoginRefused.BadCredentials`) | `loginII.py` `Login._Connect` (lines 416-498): on `UserError` with msg starting `LoginAuthFailed` it focuses the password field | match |
| Banned account | `ACCOUNTBANNED` (`Accounts.cs` `LoginRefused.Banned`) | `ExceptionMappingGPCS.py` MESSAGES (line 11-48) maps `ACCOUNTBANNED` to a localized text; `machoNet.py` `OnPeopleWhoShouldntBeLoggedInNotification` (2523-2539) also closes with `ACCOUNTBANNED` | match |
| Host error during login | `Unavailable` (`Accounts.cs` `LoginRefused.Unavailable`) | not in MESSAGES; `ConnectToServer` (machoNet.py 1555-1714) shows the raw reason text | cosmetic gap, not a code the client expects |
| Duplicate login | nothing emitted by eve.rs | client has no dedicated duplicate code; the server notification path uses status 5/12/13 with `ACCOUNTBANNED` | no emitter in eve.rs, not a mismatch in a sent code |
| Server shutdown | no shutdown reason found in eve.rs search | client expects `ACL_SHUTTINGDOWN` (in MESSAGES) | not verified; eve.rs emitter not found |
| Handshake timeouts, version mismatch | `HANDSHAKE_*` strings not emitted by eve.rs (they are not needed for accepted clients) | in MESSAGES | not exercised |

Uncertainty: whether `LoginAuthFailed` is also in the client `cfg.messages` table (which decides
between the `LoginAuthFailed` path and the generic `OnConnectionFailed` path in
`ConnectToServer`) was not verifiable from the repo. EveJS sends the same string, so no change.

## Not checked (remaining)

- `dotnet/src/Eve.Host` shutdown and kick paths (grep found no `ACL_SHUTTINGDOWN` emitter).
- `crates/eve-net/src/host.rs`, `server.rs`, `client.rs`, `frame.rs`.
- The dotnet Host for alpha/omega, duplicate login state, character selection and creation.
- Sim link restart behaviour.
- CI: see the run list for this branch; this record does not claim it is green.
