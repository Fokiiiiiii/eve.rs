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
- Host, Host.Tests, and Identity.Tests Release builds: passed. No-database identity verifier theory: 5 passed, 0 failed, 0 skipped.
- Agent test `39975779919` for feature commit `7b8ae52a`: Host.Tests SSO cases 5/5 and Identity.Tests SSO cases 8/8 passed; zero skips. Agent test `39992399196` also passed its selected Host/Identity SSO and composition filter.
- Push-triggered CI `39975764343` found Host startup composition failing because `InventoryRpc` wraps `InvBrokerService`, while character composition searched only for the inner type. The wrapper-unwrapping fix and focused composition test were pushed in `0cc56f79`.
- Agent test `39989413962` and CI `39989400403` failed at .NET test compilation because the composition fixture supplied a two-argument lambda to `Action<long>`. The fixture correction passed a full dependency-including Host.Tests Release build with zero warnings and errors, was committed, and pushed to `main` as `872df205`.
- Focused Host/Identity Actions run 39991052345 completed successfully for the SSO and composition filter. Push CI 39991023879 passed Rust format/Clippy, Rust tests, fixture checks, Python client tests, and the full .NET build, but Host startup in the live workflow and a Host test shard failed because sde.skinrComponentPointValues is absent. The pinned lock lists three records for that table. The pinned client build 3396210 has no SKINR authority tables; official CCP mission build 3405148 has them. The lock-hash cache update pushed as 86c11a0c did not resolve this. This is a Host startup prerequisite for exercising SSO, not part of account authentication. The second CI run 39992377417 reproduced the missing-table failure despite the cache change.
- Push CI 39995121778 reproduced Host startup failure after the mission schema was loaded. The exact official build 3405148 JSONL rows show that point values and tier thresholds use nested _value arrays of _key/_value pairs, while slot configurations use config, ships, and allowAllShips; the categories table identifies ship types. The loader now reads these forms and resolves explicit priority overrides before the default all-ships configuration. A focused no-database test covers nested map parsing and ship override/exclusion behavior.
- Agent test 39995138268 passed its selected Host/Identity SSO and composition filter (zero failed jobs). The concurrent main merge also surfaced separate space-container test failures in CI 39975764343; they are outside this SSO path. No PostgreSQL database was used locally.
- Push CI 39998018028 is still running: the dedicated ShipSkinrCatalogDataTests Actions run 39998073817 passed (zero failures, zero skips). Host shard 3/4 exposed a pre-existing assertion that stopped before the SkinHex string field; the test now unwraps the full V24 protobuf nesting and its Release project build passed. The SSO headless end-to-end job is still in progress.
- Local verification: Host.Tests Release build succeeded (0 warnings/errors); ShipSkinrCatalogDataTests passed 2/2, zero skips; cargo +1.94.1 check -p eve-sde, package formatting and targeted Clippy passed. Workspace Rust fmt and Clippy were attempted; both are blocked by the pre-existing missing crates/eve-space/src/targeting/* files, which remain untouched.
- Commit a00e62c9 applies the Host SDE-schema startup fix. Commit 6573728b adds the official JSONL parser correction and focused data tests; it was pushed to main at cae36a3c. Push CI 39998018028 and the focused no-database Host test run 39998073817 are pending. The graphical V24 client was not exercised.
- No PostgreSQL database was used locally. The live SSO scenario still needs a passing Actions run; the graphical V24 client was not exercised.

## Next steps

1. Review push CI 39998018028 and focused data test run 39998073817; fix any failures at their root cause.
2. Confirm the live deterministic-issuer handshake reaches character selection; record separately that the graphical V24 client was not exercised.
