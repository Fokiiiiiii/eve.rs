# R14 identity audit fixes

## Disposition

Both findings in `/home/user/reports/90-audit-eve-identity-r14.md` were confirmed. No finding was rejected as false.

## P2 — preserve the account's country code on password login

`identity.accounts.country_code` is stored at account creation (`dotnet/src/Eve.Identity/Migrations/0001_accounts.sql:8`; `PgAccountDirectory.cs:34-35,253-258`). Before this fix, PostgreSQL password login selected only `id, password_hash, role, banned`, and the shared login decision returned the current `AccountOptions.CountryCode` (`PgAccountDirectory.cs:32-35,273`; `Accounts.cs:276-283`). The in-memory account also had no country field. PostgreSQL SSO login already selected and returned the stored country (`PgAccountDirectory.cs:327-342,363`), exposing a password/SSO mismatch.

The V24.01 client passes the SSO token through `gameui.py:752-759` and `connectionService.py:128-141`. The MachoNet path carries `session_init` into the session (`carbon/common/script/net/machoNet.py:4021-4037`); initial-state notifications are applied by `SessionChangeGPCS.py:68-74`, and `basesession.py:1184-1191` treats `countryCode` as a string attribute. The Host maps `LoggedIn.CountryCode` to the accepted authentication response (`dotnet/src/Eve.Host/HostServer.cs:393-420`) and passes it into initial session state (`:448-469`). EveJS reads `account.countryCode` with the configured default as fallback (`/home/user/evejs-261028.1/server/src/network/tcp/handshake.js:722-725`) and sends that value in `session_init` (`:811-822`).

The in-memory account now keeps its creation country, and both password and SSO login return that value (`Accounts.cs:169-182,252-288`). PostgreSQL login reads `country_code` along with the other account fields and returns it through the same decision function (`PgAccountDirectory.cs:32-35,59-66,233-279`). The country is still selected from configuration when a new account is created.

Regression coverage changes a previously misleading in-memory SSO expectation, verifies password and SSO logins after opening a directory with different options, checks both database login routes on a new directory, and exercises Host authentication plus the wire session initial state (`SsoIdentityTests.cs:85-102`; `PgAccountTests.cs:125-144`; `HostServerTests.cs:418-474`). The tests failed before the fix with `JP`/`GB` mismatches and passed after it.

## P3 — reject noncanonical SSO character subjects before persistence

`SsoTokenVerifier` constructs a `VerifiedSsoIdentity` from the signed JWT `sub` and accepts it only through `SsoIdentityRules` (`dotnet/src/Eve.Identity/SsoTokenVerifier.cs:127-150`). Before this fix, `SsoIdentityRules` used `ulong.TryParse`, which accepted `CHARACTER:EVE:00123`. The PostgreSQL constraint accepts only `^[1-9][0-9]{0,19}$` (`dotnet/src/Eve.Identity/Migrations/0005_sso_identity_links.sql:1-3`), so PostgreSQL linking raised a check-constraint exception while the in-memory linker accepted the same subject (`PgAccountDirectory.cs:282-313`; `Accounts.cs:38-52,241-249`).

CCP's [EVE SSO documentation](https://developers.eveonline.com/docs/services/sso/) defines the JWT `sub` as `CHARACTER:EVE:<character-id>`. V24.01 forwards the SSO token unchanged through `gameui.py:665-669,752-759`, `connectionService.py:128-141`, and `machoNet.py:1555-1600,3442-3444`; the client does not parse or normalize the subject. EveJS's `authenticationService.js:268-292` records whether an SSO token was supplied but has no matching issuer/subject-to-local-account linker in the inspected login path, so it provides no equivalent persistence rule. The existing PostgreSQL constraint is the local storage contract; the shared validator now enforces its positive, ASCII decimal form and the `ulong` range before either store is called.

Coverage signs tokens with leading-zero, zero, empty, nondigit, and overflow subjects; it also runs each through both account-link stores and SSO login. The PostgreSQL test asserts invalid links return `false` and leave no link row (`SsoIdentityTests.cs:46-82`; `PgAccountTests.cs:146-170`). Before the fix, the leading-zero token was accepted, the in-memory linker returned `true`, and PostgreSQL raised SQLSTATE `23514`. After the fix all seven selected cases passed.

## Verification

- On PostgreSQL database `eve_p4`, the relevant Identity test classes (`InMemoryAccountTests`, `PgAccountTests`, `SsoIdentityTests`, `RoleGrantTests`, `EntitlementRulesTests`) passed: 58 passed, 0 failed, 0 skipped.
- The full `HostServerTests` class, including its real socket authentication and initial-state paths, passed after merging current main: 423 passed, 0 failed, 0 skipped.
- Release builds with warnings treated as errors succeeded with 0 warnings and 0 errors for `Eve.Identity`, `Eve.Identity.Tests`, `Eve.Host`, and `Eve.Host.Tests`.
- The source audit used shell search/read commands because codebase-memory-mcp and Serena were not available in this environment. EveJS was compared by source; no EveJS runtime login scenario was executed. The graphical V24.01 client was not run.
- One intermediate post-fix P3 run caught an assertion-helper type mismatch (`count(*)` returned `Int64`); the assertion was corrected to `long`, then the focused P3 tests passed. No product assertion was weakened.
