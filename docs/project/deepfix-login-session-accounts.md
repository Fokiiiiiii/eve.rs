# Deep fix: login, sessions and accounts (review record)

Scope: login handshake, password and SSO credentials, character selection, session change
handoff, reconnect and duplicate login, account state (alpha/omega), bans and kicks,
shutdown and restart of the Host and sim link.

Status: review only. No code change. All three handshake candidates were resolved against the
V24.01 client and dropped (the client does not depend on the difference).

## Compared sources

- eve.rs: `crates/eve-net/src/handshake.rs`, `hostlink.rs`, `profile.rs`.
- EveJS v0.12.9: `server/src/network/tcp/handshake.js`, `server/src/network/clientSession.js`.
- V24.01 client, `Fokiiiiiii/eve-logic`, read through the local codebase-memory index
  (`home-user-eve-logic`), not whole-file reads.

## Matches (checked, no divergence seen)

- Handshake step order, `QC`/`VK`, `placebo`, `OK CC`, server challenge, handshake result, ack.
- `challenge_responsehash` `"55087"`, client_id `1000000 * user_id + node_id` (node `0xFFAA`),
  session id `now_ms * 15 + client_id % 15`, rejected logins get `GPSTransportClosed`.

## Client evidence and candidate outcomes

- Client login reader of the ack: `carbon/common/script/net/machoNet.py` `ConnectToServer`
  (lines 1555-1714). The only field it reads from `session_init` is `userid`
  (`response['session_init']['userid']`, around line 1610). `MachoNetService.Authenticate`
  (line 3440-3453, server side) builds `session_init` from the auth service `loginInfo`.
- `maxSessionTime`: the only client-side references are the timer
  `__ClientSessionMaxTimePoll` (machoNet.py 2456-2474, reads an attribute `s.maxSessionTime`
  of a session object) and `GameUI.__SessionTimeoutTimer`
  (`eve/client/script/parklife/gameui.py` 294-305). Nothing reads the dict key
  `session_init['maxSessionTime']`. The client does not need null and tolerates a missing key.
  DROPPED.
- `role`: `session_init['role']` is never read by the client (only `userid` is). The role is
  applied server-side and through session attributes (`machoNetTransport.py`
  `_AssociateWithSession`, 681-740, which only sets `userid` from the packet). DROPPED.
- SSO detection: `ConnectionService.Connect` (`carbon/client/script/remote/connectionService.py`
  66-79) calls `ConnectToServer(address, userid, password, ct)` without an SSO token, so
  `ssoToken` keeps its default `None`, which `Authenticate` sends as `user_sso_token: None`.
  eve.rs maps `Value::None` to absent, so a normal password login stays `Password`
  (`handshake.rs`, `credential_kind`). An empty string is never sent by this path. DROPPED.
  Side note (not a server divergence): `Connect` passes `ct` into the `sockettype` position of
  `ConnectToServer`, a client quirk that does not change the server handshake.

## Not checked (remaining)

- `crates/eve-net/src/host.rs`, `server.rs`, `client.rs`, `frame.rs`, and the eve-net tests.
- The `dotnet/` Host: accounts, alpha/omega, bans, duplicate login, character selection and
  creation, session change handoff.
- Sim link restart behaviour, and EveJS account store and role profiles.
- CI: see the run list for this branch; this record does not claim it is green.
