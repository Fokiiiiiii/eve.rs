# SSO handshake and account-link handoff

## Objective and implementation

Authenticate V24.01 `ConnectionService.LoginSso` using inbound `user_sso_token`, then resolve a stable eve.rs account only through a persisted issuer/subject link.

- Rust classifies the V24 SSO handshake and forwards the token as the HostLink SSO credential. Debug formatting redacts the token.
- Host verifies the JWT signature with issuer JWKS, RS256, issuer, configured audiences, expiration/not-before, and the character subject before resolving the link. Invalid credentials, missing verifier, unknown subject, or missing link refuse with `LoginAuthFailed`; refused logins do not open a session or send initial state.
- `identity.external_identity_links` persists unique issuer/subject-to-account links. Link lookup is explicit; the `EVE-SSO-CONNECTION` sentinel is not used as an account identity.
- The CCP token is not forwarded as a local gateway token. The game-session acknowledgement retains `access_token` and `computer_hash` fields with their existing separate-provider semantics.
- Account setup requires `EVE_SSO_AUDIENCES` to contain `EVE Online` and the actual application client ID. The inspected V24 callsites do not expose that ID, so deployment configuration must provide it.

## Verified

- Host dispatch contract coverage checks the SSO credential, linked account, session userid/role/language, and refusal before session-open.
- Identity coverage checks signature, issuer, audience, expiry, subject, repeated login, cross-account isolation, and persistent link resolution.
- Deterministic-issuer headless scenario logs in, requests character selection, creates a character, selects it, and checks the linked account session id.
- Actions run [40004676875](https://github.com/Fokiiiiiii/eve.rs/actions/runs/40004676875) on commit 607cf277: Host SSO 5 passed/0 failed/0 skipped; Identity SSO 8 passed/0 failed/0 skipped; live SSO 1 passed/0 failed/0 skipped. The first live run, 40001119508, exposed a KeyVal parsing issue after successful login; main commit e0cb832e fixed the parser and the rerun passed.
- Earlier Host contract run 40000836917 and Identity run 40002097933 also passed.
- Local Rust package check, formatting and targeted Clippy passed. Host.Tests Release build passed with zero warnings/errors. No PostgreSQL instance was used locally; Actions exercised its isolated test database.
- The graphical V24 client was not exercised.

## Remaining scope notes

- Production SSO needs the deployment's real application client ID in `EVE_SSO_AUDIENCES`.
- Older main CI run 40002451993 failed in the SKINR live test because its helper sent an unsigned fake gateway bearer (401), and in two unrelated market dispatch golden tests. Gateway token verification is owned by the separate 07-gateway-security task; those failures were outside this SSO change.
