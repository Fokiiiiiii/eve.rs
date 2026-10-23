# SSO handshake and account-link handoff

## Objective

Authenticate the V24.01 `ConnectionService.LoginSso` path from the inbound `user_sso_token`, resolve it only through a persisted CCP issuer/subject account link, and keep the client acknowledgement's local gateway `access_token` separate.

## Source findings

- V24.01 sends `user_name=EVE-SSO-CONNECTION`, `user_password=fakePW`, and `user_sso_token`; the Rust handshake classifies that path as SSO and forwards the token in HostLink v2.
- CCP SSO documentation requires RS256/JWKS signature validation, issuer validation, both `EVE Online` and the application's client ID in `aud`, and a live `exp`; CCP `sub` identifies the chosen character.
- The inspected V24 callsites do not expose the application client ID. Production SSO requires `EVE_SSO_AUDIENCES` with the actual client ID and `EVE Online`; the implementation does not guess it.
- Account binding is explicit: `identity.external_identity_links` stores the issuer, character subject, and stable account id. EveJS v0.12.9 only records the SSO token and does not define an account mapping.
- The CCP token is used only as the inbound SSO credential. `access_token=None` and `computer_hash=None` remain in the game-session acknowledgement; the separate multi-login computer-hash gate is outside this change.

## Implementation and coverage

- Rust parses and forwards the V24 token with redacted debug output; HostLink protocol version 2 carries credential kind and token.
- The Host verifies CCP JWT signature, issuer, configured audiences, expiration/not-before, and character subject before resolving the explicit link.
- Missing verifier, invalid token, unknown subject, or absent link refuses with `LoginAuthFailed`. Session opening remains after accepted authentication.
- PostgreSQL migration `0005_sso_identity_links.sql` persists a unique issuer/subject link. Identity coverage includes signature, issuer, audience, expiry, subject, repeated login, and cross-account isolation. PostgreSQL coverage checks resolution through a newly created directory.
- Host socket coverage checks the SSO credential/token, verified account id, role, country, and resulting `userid`/role/language initial state; it also checks an unlinked refusal before session-open.
- `tools/headless-client/tests/test_sso_login_live.py` uses a local RSA test issuer, provisions a persisted link in the live-run scratch schema, and reaches character selection and `SelectCharacterID`. The runner configures the matching local issuer and audience and retains the merged paperdoll live test.

## Checkpoint

- Rust `cargo +1.94.1 check -p eve-net`, package fmt, and targeted all-target Clippy: passed.
- Python compile checks and HostLink fixture generation (14 valid, 10 invalid cases): passed.
- Host Release, Host.Tests Release, and Identity.Tests Release builds: passed before the main merge. No-database identity verifier theory: 5 passed, 0 failed, 0 skipped.
- Agent test for feature merge `fafa7948`: Identity.Tests SSO cases passed (8/8); Host.Tests found one fixture setup error because the linked account used default `AutoCreate=false`. The fixture now enables account creation; a follow-up validation is pending.
- Workspace Rust fmt and Clippy cannot complete in this worktree because it already lacks `crates/eve-space/src/targeting/*`; those pre-existing deletions remain untouched. Targeted eve-net checks pass.
- No PostgreSQL database was used locally. Push-triggered `ci.yml` and the follow-up Actions run are pending. The graphical V24 client exercise remains outstanding.

## Next steps

1. Commit the Host fixture correction and this checkpoint, fetch and merge `origin/main`, then push `HEAD:main`.
2. Dispatch Host/Identity SSO tests for the new feature SHA and inspect the push-triggered CI run, including the live headless scenario; update this note with results.
3. Keep the TODO row open until Actions verification is recorded.
