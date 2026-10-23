# Character phase gateway handoff (CX)

Worktree: `/home/user/wt-cx4`  
Database lane: `eve_p3` (`127.0.0.1:5432`), live port `26102`

## Current progress

- Gateway tokens are signed and verified as HS256. The Host-issued token includes account and optional character claims, issuer, audience, issued-at, expiry, and a random token id. Requests check signature, expiry, selected character claim when present, and persisted account ownership. Unknown handlers are checked only after authorization.
- Request logging records neither authorization headers nor protobuf payloads; refusal and error coverage has been added.
- The listener serves HTTP/2 TLS from optional PKCS#12 configuration and h2c without a certificate. Client source confirms the local address override uses h2c while tier discovery uses TLS. Local certificate and token-key setup is documented in `public-gateway-local-dev.md`.
- Character title grant/revoke now persists state, removes a revoked title from the worn slot, and emits `GrantedNotice` / `RevokedNotice` through the authenticated `Notices/Consume` stream. Host composition registers the reward service.
- The title reward claim request/source is still not implemented: the current Host does not yet have authoritative unclaimed achievement reward sources. No reward claim payload or eligibility is fabricated.

## Verification status

- `dotnet build dotnet/src/Eve.Host/Eve.Host.csproj --no-restore -p:UseSharedCompilation=false -p:BuildProjectReferences=false -m:1` passed with 0 warnings and 0 errors.
- Focused `PublicGatewayTests` passed: 11 passed, 0 skipped, on the `eve_p3` lane. The test checks signature/expiry/character refusal, no token logging, h2c/TLS transport, persistent title changes, notice payloads and order.
- The TLS test now pins its individual `HttpRequestMessage` to HTTP/2 exact. The earlier client test used the default HTTP/1.1 request version, so Kestrel correctly refused it on the HTTP/2-only TLS endpoint.
- The local death hull run stopped during `cargo build` before starting services: this checkout has deleted all five `crates/eve-space/src/targeting/` source files that exist in `origin/main`, causing the missing-module and follow-on compile errors. No live scenario or database operation started. Run the walk on Actions' clean checkout after pushing.
- The serialized full .NET Actions suite was dispatched for `269a91ea` (run `39946389313`). A preceding CI run on `9cec6507` exposed a stale RPC registry inventory baseline (expected 1374, actual 1378); the exact count assertion is updated to 1378. Run the focused inventory test and full suite against the follow-up commit.
- S8/S9 handoffs and repository instructions were reviewed. CBM was used before Serena reads/edits; the Serena manual tool was not available in the deferred tool set.

## Existing worktree state

The worktree initially contained nine deletions under `crates/eve-net/src/bin`, `crates/eve-sde/src/bin`, `crates/eve-sim/src/bin`, and `crates/eve-space/src/targeting`. They were not staged or committed. The two Rust entrypoints were temporarily restored from CBM for the local runner attempt and deleted again; their original deletion state remains.

Update this note after every push with commits, checks, and remaining work.
