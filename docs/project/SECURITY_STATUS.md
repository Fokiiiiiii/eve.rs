# Security status

Checked 2026-10-19.

## Dependency and image review

- Rust: `cargo audit 0.22.2` loaded 1,338 RustSec advisories and scanned 114 dependencies in `Cargo.lock`; it exited successfully and reported no vulnerable packages. `cargo test -p eve-sde` passed (40 tests); `cargo fmt --check` and `cargo clippy -p eve-sde -- -D warnings` passed. CI on `1b867976` passed Rust tests, formatting, clippy, and the stable-toolchain advisory job.
- .NET: `dotnet list dotnet/EveRs.slnx package --vulnerable --include-transitive` reported no vulnerable packages across the solution projects. The CI build passed with zero warnings on `1b867976`; the current full CI run is pending.
- Python: `pip-audit 2.10.1` scanned both headless-client requirements files and reported no known vulnerabilities. The CI Python client job passed on `1b867976`; the current CI run is pending.
- No tracked npm manifests or lockfiles were present. The Dockerfile uses `rust:1.99.0-bookworm`, `mcr.microsoft.com/dotnet/sdk:10.0`, and `mcr.microsoft.com/dotnet/runtime:10.0`. Docker Scout, Trivy, Grype, and Snyk were unavailable, so no image vulnerability database scan was completed.
- Workflow action references were reviewed locally. They use version tags, including `actions/checkout@v7`, `actions/cache@v6`, and `actions/setup-dotnet@v6`; no action-advisory scan was available.
- GitHub Dependabot alerts are disabled for this repository. The GitHub secret-scanning MCP function was unavailable, and the REST alert endpoint was blocked by the environment proxy.

## Secret scan

`detect-secrets 1.5.0` scanned tracked files using its default detectors. It reported 20,325 heuristic matches across 63 files: 20,303 high-entropy hex matches, 7 high-entropy base64 matches, 7 secret-keyword matches, 6 basic-auth matches, and 2 JWT matches. Matched values were not displayed. The 15 matches from credential-oriented detectors have not been individually adjudicated; these results do not establish that each match is a credential. No GitHub secret-scanning result was available.

## Attack-surface review

Reviewed 2026-10-19 against the Rust, .NET, Python tool, deployment, and workflow sources listed below. This records source review and focused tests; it does not claim graphical-client or production deployment validation.

- **Wire parsing and connection isolation:** Rust `eve-marshal` applies a nesting limit and checks cursor ranges before reads; its robustness suite runs 20,000 deterministic arbitrary byte inputs. Rust `eve-net` applies a frame limit and handshake timeout, and its per-client output queue has a capacity of 1,024 messages. Existing tests cover malformed handshakes, oversized frames, and decoder boundaries. .NET `Eve.Marshal` tests cover truncated streams, oversized counts, and nesting limits. `GatewayEnvelope.TakeFrames` rejects a declared gRPC frame above 16 MiB before waiting for the body; the gateway test covers that rejection. No additional length arithmetic, wire-sized allocation, or connection-teardown issue was found in these reviewed paths.
- **Authentication and authorization:** Account login uses `CryptographicOperations.FixedTimeEquals` for the stored and supplied password hashes. Existing tests cover wrong passwords, ordinary-account role bits, staff role checks, and public QA denial for non-staff callers. Representative character and item ownership checks are exercised in service and Host tests. No issue was found in these sampled paths; this review did not enumerate every RPC ownership predicate or every slash command implementation.
- **SQL and file handling:** The shared SQL command helper parameterizes values; schema, table, and SDE table names are validated at their construction boundaries. The reviewed SDE archive loader writes entry basenames into its controlled staging directory. No injection or archive path escape was found in these paths.
- **Secrets, containers, and network exposure:** The compose database password fallback is confined to the compose network; only the game network service publishes a host port. The production Dockerfile switches to the `eve` user. All production base images now pin registry manifest digests as well as readable version tags. No credential was printed or added during this review.
- **GitHub workflows:** Both workflow files set `contents: read` and no `pull_request_target` trigger was found. All action references are now pinned to full commit IDs. A Python regression test checks both conditions. The action commits were resolved from their upstream Git references on 2026-10-19.

### Findings fixed

- **Unbounded pending handshakes — denial of service.** `crates/eve-net/src/server.rs` spawned a task for every accepted socket; the handshake timeout limited each task’s lifetime but did not limit concurrent unauthenticated tasks. The listener now admits at most 256 concurrent handshakes and closes excess sockets. A slot is released once authentication completes or the handshake ends. Regression coverage: `pending_handshakes_are_capped_until_a_slot_is_released` verifies rejection at capacity and recovery after a slot is freed.
- **Mutable GitHub Actions references — supply-chain exposure.** `.github/workflows/ci.yml` and `.github/workflows/live-slow.yml` previously referenced version tags and `dtolnay/rust-toolchain@master`, allowing the action code to change without a repository change. The workflows now use full commit IDs with the release/channel recorded in comments. Regression coverage: `tools/headless-client/tests/test_workflow_security.py` checks that every workflow action uses a full commit ID and rejects `pull_request_target`.
- **Mutable Docker base tags — supply-chain exposure.** `Dockerfile` previously selected Rust and .NET base images by version tags alone. Each `FROM` now pins the image manifest digest alongside its version tag. Regression coverage: `tools/headless-client/tests/test_workflow_security.py` requires a SHA-256 digest for every base image.

## Outstanding checks

- The full regression suites will run in GitHub Actions after the security commits are pushed. Local checks here cover the changed Rust listener and workflow policy only.
- The authorization review sampled role and ownership tests but did not enumerate every RPC that accepts a client-provided character, item, or location identifier, or every slash command that requires a staff role.
- Per-account throttling for expensive authenticated RPCs needs a dedicated pass; no complete inventory of costly calls and limits is recorded here.
- The review sampled marshal and gateway frame parsing. JSON/protobuf payload handling and malformed-input exception isolation across every Host/RPC entry remain to be traced.
- The container image CVE scan and external action-advisory review remain open. The image base references and workflow actions are now pinned, but this review did not evaluate advisories for the pinned revisions.
- The 15 credential-oriented matches from the earlier heuristic secret scan still need individual adjudication. The matched values were not displayed in this review.
- No live graphical-client security walk was performed for this review.
