# Host security and dependency audit handoff

## Change
- The Host now rejects password login hashes unless they are 20 bytes, before calling the account directory. This prevents malformed client input from being persisted when account auto-creation is enabled.
- Added a Host regression test for empty, short, long, and 1 MiB hashes. Existing Host login fixtures now use 20-byte digests.
- Eve-logic evidence: `/home/user/eve-logic/carbon/common/script/net/machobase.py:52-61` computes the client password hash with SHA-1; `/home/user/eve-logic/carbon/common/script/net/GPS.py:564-580` puts that hash in the secure login request; `/home/user/eve-logic/eve/client/script/ui/login/loginII.py:475` passes credentials to the connection service.

## Security review
- `AuthRequest.ToString` redacts password hashes and SSO tokens (`dotnet/src/Eve.Host/HostCodec.cs:33`). Host authentication logs suppress provider exceptions and do not include credentials (`dotnet/src/Eve.Host/HostServer.cs:422`).
- Marshal decoding is bounded to 16 MiB frames, 64 MiB inflated input, depth 256, and one million decoded allocation units (`dotnet/src/Eve.Host/HostCodec.cs:47`, `dotnet/src/Eve.Marshal/MarshalCodec.cs:15-20, 52`). Regression cases cover deep streams and oversized collection allocation (`dotnet/tests/Eve.Marshal.Tests/CodecTests.cs:114-157, 300-378`).
- The account SQL validates schema identifiers and passes username/hash values as SQL parameters (`dotnet/src/Eve.Identity/PgAccountDirectory.cs:13-29, 232-270`). No SQL injection finding or justified dependency ignore was identified.

## Verification
- Dependency audit Actions run 40372992028: Rust, .NET, and Python jobs succeeded.
- `dotnet build dotnet/src/Eve.Host/Eve.Host.csproj -p:UseSharedCompilation=false --no-restore`: passed, 0 warnings/errors.
- Focused Host regression: 1 passed, 0 failed, 0 skipped.
- Full `Eve.Host.Tests` without PostgreSQL: 478 passed, 102 failed because the assigned `EVE_PG_URL` lane was unavailable, 227 skipped, 807 total. No PostgreSQL database was used.
- The change is local and unpushed. The required full-project gate did not pass in this no-database environment, so no push was made.
