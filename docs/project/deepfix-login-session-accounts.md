# Deep fix: login, sessions and accounts (review record)

Scope: login handshake, password and SSO credentials, character selection, session change
handoff, reconnect and duplicate login, account state (alpha/omega), bans and kicks,
shutdown and restart of the Host and sim link.

Status: review only. No Rust or C# behaviour was changed. None of the candidates below is
proven against the V24.01 client, so none was turned into a code fix.

## Compared sources

- eve.rs: `crates/eve-net/src/handshake.rs`, `crates/eve-net/src/hostlink.rs`,
  `crates/eve-net/src/profile.rs`.
- EveJS v0.12.9: `server/src/network/tcp/handshake.js` (`_handleAuthentication`,
  `_handleFuncResult`, `createSessionID`), `server/src/network/clientSession.js`.
- V24.01 client, `Fokiiiiiii/eve-logic` (decompiled, commit 1dbb5711):
  `carbon/common/script/net/GPS.py`, `session.py`, `loginCharacterselection/`,
  `eve/client/script/ui/login/`, `carbon/common/script/sys/`.

## Matches (checked, no divergence seen)

- Handshake step order, `QC` / `VK`, `placebo`, `OK CC`, server challenge, handshake result,
  ack. Client side in `GPS.py` (`HandleClientAuthentication`, `Authenticate`) follows the same
  order.
- `challenge_responsehash` is `"55087"` in both (the client checks it as
  `CryptoHash(clientChallenge)`, `GPS.py` Authenticate).
- `client_id = 1000000 * user_id + node_id` with default node `0xFFAA` (EveJS
  `1000000 * account.id + PROXY_NODE_ID`).
- Session id formula `now_ms * 15 + client_id % 15` (EveJS `createSessionID`).
- Rejected logins send a pickled `GPSTransportClosed` before close (both).

## V24.01 client check of the candidates

What `GPS.py` shows (commit 1dbb5711):
- `Authenticate(username, password, token)` writes the secure client handshake dict with
  `'user_sso_token': token` for every login, and `'user_password_hash'` from
  `macho.PasswordHash`. The value of `token` for a plain password login is set by the caller,
  which was not found in the files read. So whether a password login sends `None` or an empty
  string cannot be stated yet.
- After login the client does `response.update(crAck)`, stores `self.handShake = response`,
  and runs `live_updates` through `OnLiveClientUpdate`. It does not read `session_init`
  itself. `session_init` must be consumed by the machoNet or session layer, which was not
  readable (see not-checked list).

Candidate status:
1. `session_init.maxSessionTime` (eve.rs omits it, EveJS sends `null`): NOT RESOLVED. The read
   site of `session_init` was not found in the readable files, so whether the client needs
   the key, null, or tolerates a missing key is unknown. Dropped from the fix list for now.
2. SSO detection (eve.rs treats any non-empty `user_sso_token` as SSO): PARTLY CHECKED. The
   client always sends the key. If the caller passes `None` for password logins, eve.rs
   already classifies them correctly (`handshake.rs` treats `Value::None` as absent). If it
   passes an empty string, eve.rs would wrongly classify a password login as SSO. Not proven
   either way, so no change.
3. `session_init.role` derivation: NOT RESOLVED, the client role mask consumer was not found.

## Not checked (remaining)

- Where `session_init` and `sessionID` are consumed on the client:
  `carbon/common/script/net/machoNet.py` (about 242 KB, too large for the GitHub MCP read
  path used here), and the callers of `GPS.Authenticate` that pass `token`.
- `carbon/common/script/sys/basesession.py` (81 KB, same limit).
- `crates/eve-net/src/host.rs`, `server.rs`, `client.rs`, `frame.rs`.
- `crates/eve-net/tests/handshake.rs` and `front.rs`.
- The `dotnet/` Host: accounts, alpha/omega, bans, duplicate login, character selection and
  creation, session change handoff.
- Sim link restart behaviour.
- EveJS account store and role profiles.
- CI: the run dispatched for this branch was not confirmed green in this record.
