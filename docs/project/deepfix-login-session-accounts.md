# Deep fix: login, sessions and accounts (review record)

Scope: login handshake, password and SSO credentials, character selection, session change
handoff, reconnect and duplicate login, account state (alpha/omega), bans and kicks,
shutdown and restart of the Host and sim link.

Status: review only. No code behaviour was changed in this batch, because no divergence was
fully proven end to end with a failing test that could be run (no local build or test run is
available to the implementer).

## Compared sources

- eve.rs: `crates/eve-net/src/handshake.rs`, `crates/eve-net/src/hostlink.rs`,
  `crates/eve-net/src/profile.rs`.
- EveJS v0.12.9: `server/src/network/tcp/handshake.js` (`_handleAuthentication`,
  `_handleFuncResult`, `createSessionID`), `server/src/network/clientSession.js`
  (constructor, `sendSessionChange`).

## Matches (checked, no divergence seen)

- Handshake step order: version exchange, `QC` / `VK` command, `placebo` crypto request,
  `OK CC`, login challenge, `2`, server handshake, handshake result, ack. Same as EveJS.
- `challenge_responsehash` is `"55087"` in both.
- `client_id = 1000000 * user_id + node_id` with default node `0xFFAA` (EveJS
  `1000000 * account.id + PROXY_NODE_ID`).
- Session id: `now_ms * 15 + client_id % 15` (EveJS `createSessionID`), with a per-front
  collision guard in Rust (`SessionIds::next`).
- Rejected logins send a pickled `GPSTransportClosed` before close (both).
- `session_init` carries `languageID`, `countryCode`, `userid`, `userType = 30`, `role`,
  `address`, `inDetention`; the ack carries `live_updates`, `sessionID`, `client_hash`,
  `user_clientid` (both).

## Candidate divergences (NOT proven, not fixed)

1. `session_init.maxSessionTime`: EveJS sends `maxSessionTime: null` in `session_init`
   (`handshake.js`, `_handleFuncResult`). eve.rs `handshake.rs` omits this key. Impact on the
   V24.01 client is unknown; needs a check of the client read site in `Fokiiiiiii/eve-logic`.
2. SSO detection: eve.rs treats any non-empty `user_sso_token` as SSO even when the user name
   is a normal account (`handshake.rs`, `credential_kind` selection). EveJS ignores SSO fields
   and always validates the password hash. Whether V24.01 sends an SSO token alongside a
   password login is unverified; needs the client-side login flow in `eve-logic`.
3. Role in `session_init`: eve.rs sends `account role | SESSION_BASE_ROLE`; EveJS sends
   `chatRole || role` and composes the session role mask in `ClientSession`. The exact
   expected mask for V24.01 is unverified.
4. Ban handling: EveJS returns the specific reason code `ACCOUNTBANNED` for banned accounts;
   eve.rs relies on the host `AuthOutcome::Rejected { reason }` text. Host side not reviewed.
5. Password check: EveJS compares the raw hash hex (`handshake.js`, `_handleAuthentication`)
   and has a `devSkipPasswordValidation` switch; eve.rs passes the raw digest to the host. The
   host-side comparison and any dev bypass were not reviewed.

## Not checked

- `crates/eve-net/src/host.rs`, `server.rs`, `client.rs`, `frame.rs` (reconnect, shutdown and
  kick paths).
- `crates/eve-net/tests/handshake.rs` and `front.rs` (existing test coverage for the above).
- The `dotnet/` Host implementation (account state, alpha/omega, bans, duplicate login,
  character selection and creation, session change handoff).
- Sim link restart behaviour (`crates/eve-sim`, `hostlink.rs` SESSION_CLOSE / DISCONNECT).
- EveJS account store and role profiles (`server/src/services/account/accountRoleProfiles`).
- V24.01 client contracts in `Fokiiiiiii/eve-logic`.
- CI: the branch was not verified green by an implementer-side run in this record.
