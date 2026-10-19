# Security status

Checked 2026-10-19.

## Dependency and image review

- Rust: `cargo audit 0.22.2` loaded 1,338 RustSec advisories and scanned 114 dependencies in `Cargo.lock`; it exited successfully and reported no vulnerable packages. `cargo test -p eve-sde` passed (40 tests); `cargo fmt --check` and `cargo clippy -p eve-sde -- -D warnings` passed. CI on `1b867976` passed Rust tests, formatting, clippy, and the stable-toolchain advisory job.
- .NET: `dotnet list dotnet/EveRs.slnx package --vulnerable --include-transitive` reported no vulnerable packages across the solution projects. GitHub Actions run 122 on `a7c25a09` passed the build and all three .NET test shards.
- Python: `pip-audit 2.10.1` scanned both headless-client requirements files and reported no known vulnerabilities. The Python client job passed in GitHub Actions run 122 on `a7c25a09`.
- No tracked npm manifests or lockfiles were present. The Dockerfile uses those Rust and .NET version tags with pinned registry manifest digests. Docker Scout, Trivy, Grype, and Snyk were unavailable, so no image vulnerability database scan was completed.
- Workflow action references are pinned to full commit IDs; no action-advisory scan was available.
- GitHub Dependabot alerts are disabled for this repository. The GitHub secret-scanning MCP function was unavailable, and the REST alert endpoint was blocked by the environment proxy.

## Secret scan

`detect-secrets 1.5.0` scanned tracked files using its default detectors. It reported 20,325 heuristic matches across 63 files: 20,303 high-entropy hex matches, 7 high-entropy base64 matches, 7 secret-keyword matches, 6 basic-auth matches, and 2 JWT matches. Matched values were not displayed. The 15 matches from credential-oriented detectors have not been individually adjudicated; these results do not establish that each match is a credential. No GitHub secret-scanning result was available.

## Attack-surface review

Reviewed 2026-10-19 against the Rust, .NET, Python tool, deployment, and workflow sources listed below. This records source review and focused tests; it does not claim graphical-client or production deployment validation.

- **Wire parsing and connection isolation:** Rust `eve-marshal` applies a nesting limit and checks cursor ranges before reads; its robustness suite runs 20,000 deterministic arbitrary byte inputs. Rust `eve-net` applies a frame limit and handshake timeout, and its per-client output queue has a capacity of 1,024 messages. The Rust sim-link reads bounded frames and its slice-backed reader checks ranges; the reviewed Abyssal count helper enforces a maximum before iteration. Existing tests cover malformed handshakes, oversized frames, and decoder boundaries. .NET `Eve.Marshal` tests cover truncated streams, oversized counts, and nesting limits. `GatewayEnvelope.TakeFrames` rejects a declared gRPC frame above 16 MiB before waiting for the body; the gateway test covers that rejection. No additional length arithmetic, wire-sized allocation, or connection-teardown issue was found in these reviewed paths.
- **Authentication and authorization:** Account login uses `CryptographicOperations.FixedTimeEquals` for the stored and supplied password hashes. Existing tests cover wrong passwords, ordinary-account role bits, staff role checks, and public QA denial for non-staff callers. Representative character and item ownership checks are exercised in service and Host tests. The current `placebo` handshake does not encrypt the reusable client password hash; a network observer can replay that credential hash. This is recorded below as an unresolved protocol exposure. The review did not enumerate every RPC ownership predicate or every slash command implementation.
- **SQL and file handling:** The shared SQL command helper parameterizes values; schema, table, and SDE table names are validated at their construction boundaries. The reviewed SDE archive loader writes entry basenames into its controlled staging directory. The MITM recorder writes only to its operator-supplied output path; captured packet data does not select a file path. No injection, archive path escape, or packet-derived file path was found in these paths.
- **Secrets, containers, and network exposure:** The compose database password fallback is confined to the compose network; only the game network service publishes a host port. The production Dockerfile switches to the `eve` user. All production base images now pin registry manifest digests as well as readable version tags. No credential was printed or added during this review.
- **GitHub workflows:** Both workflow files set `contents: read` and no `pull_request_target` trigger was found. All action references are now pinned to full commit IDs. A Python regression test checks both conditions. The action commits were resolved from their upstream Git references on 2026-10-19.

### Findings fixed

- **Unbounded pending handshakes — denial of service.** `crates/eve-net/src/server.rs` spawned a task for every accepted socket; the handshake timeout limited each task’s lifetime but did not limit concurrent unauthenticated tasks. The listener now admits at most 256 concurrent handshakes and closes excess sockets. A slot is released once authentication completes or the handshake ends. Regression coverage: `pending_handshakes_are_capped_until_a_slot_is_released` verifies rejection at capacity and recovery after a slot is freed.
- **Mutable GitHub Actions references — supply-chain exposure.** `.github/workflows/ci.yml` and `.github/workflows/live-slow.yml` previously referenced version tags and `dtolnay/rust-toolchain@master`, allowing the action code to change without a repository change. The workflows now use full commit IDs with the release/channel recorded in comments. Regression coverage: `tools/headless-client/tests/test_workflow_security.py` checks that every workflow action uses a full commit ID and rejects `pull_request_target`.
- **Mutable Docker base tags — supply-chain exposure.** `Dockerfile` previously selected Rust and .NET base images by version tags alone. Each `FROM` now pins the image manifest digest alongside its version tag. Regression coverage: `tools/headless-client/tests/test_workflow_security.py` requires a SHA-256 digest for every base image.

## Outstanding checks

- GitHub Actions run 121 for commit `96c48594` failed during setup because the cache action ID was malformed; the pinned toolchain step also exposed a missing explicit `stable` input. The issues were corrected in `a7c25a09` and `78517bec`. Run 122 on `a7c25a09` passed every required job, including the end-to-end stack and complete test suites. Local focused checks also passed for the changed Rust listener and workflow policy.
- The authorization review sampled role and ownership tests but did not enumerate every RPC that accepts a client-provided character, item, or location identifier, or every slash command that requires a staff role.
- **Reusable login credential hash over an unencrypted transport.** `crates/eve-net/src/handshake.rs` negotiates `placebo` and accepts the client password hash in the login dictionary. Since that hash is reused and compared as the credential, a network observer can replay it to authenticate as the account. Adding challenge binding or transport encryption at this layer would change the unmodified V24.01 handshake. The server currently has no code-level mitigation; the listener must be carried over a protected network tunnel or a future client-compatible authentication protocol must replace this flow. Existing handshake tests cover hash acceptance and refusal, not replay resistance.
- Per-account throttling for expensive authenticated RPCs needs a dedicated pass; no complete inventory of costly calls and limits is recorded here.
- The review sampled marshal and gateway frame parsing. JSON/protobuf payload handling and malformed-input exception isolation across every Host/RPC entry remain to be traced.
- The container image CVE scan and external action-advisory review remain open. The image base references and workflow actions are now pinned, but this review did not evaluate advisories for the pinned revisions.
- The earlier heuristic scan produced credential-oriented candidates. The direct token-shaped matches are classified below; the remaining keyword and entropy matches are heuristic candidates without enough recorded context to establish that they are credentials.
- No live graphical-client security walk was performed for this review.

## Public exposure review

Reviewed 2026-10-19 across all 1,962 reachable commits and current tracked files. A local pattern scan covered added history lines for private-key blocks, common GitHub/OpenAI/AWS/Slack token forms, JWT-shaped strings, credential-bearing database URLs, password/secret assignments, and Windows user paths. It emitted only masked prefixes and paths; no scan output included full candidate values.

### Findings

- **JWT-shaped test fixtures, not live credentials.** Commit `ecc26420ba0e`, `dotnet/tests/Eve.Host.Tests/PublicGatewayTests.cs:25-26`, added two strings with the masked prefix `eyJh…`. They remain in HEAD. The file documents them as account-identity test tokens; the gateway test path does not validate their signatures. They are synthetic test inputs, not live account/session credentials. No rotation or revocation is indicated by this finding.
- **Historical Windows path.** Commit `f5d8678eab3a`, `docs/IMPLEMENTATION_AUDIT.md:102`, contains a path beginning `C:\Us…`. That document is absent from HEAD. The username is not reproduced here. This is a historical path disclosure, not a credential; no credential rotation is indicated.
- **Credential-like database strings in current configuration are development examples.** `docker-compose.yml:15,30`, `.devcontainer/devcontainer.json:31`, `.github/workflows/ci.yml`, `.github/workflows/live-slow.yml`, and `server.env.example` use local compose/CI/example database credentials. These are scoped to disposable local or CI databases and are not production credentials. No production credential was identified in these matches.
- No private-key block or direct match for the scanned GitHub, OpenAI, AWS access-key, or Slack token formats was found in the reachable history additions. No capture dump, pcap, auth.json, or cookie file path was found in current or historical tracked paths. `tools/mitm/` contains recorder source and tests, not recorded traffic.
- Commit metadata across reachable history uses the required owner noreply identity except for 17 commits authored as Claude and 15 by Dependabot. The owner’s real address was not found in commit metadata. The tracked-content scan for embedded contact addresses did not complete, so that part of the review remains open.

### Public repository configuration

- `.github/workflows/ci.yml` and `.github/workflows/live-slow.yml` declare `contents: read`; action references are pinned to full commit IDs. Neither uses `pull_request_target`. The reviewed workflow files contain no repository secret references. CI database values are test-only.
- `.github/dependabot.yml` configures weekly updates for Cargo, NuGet, pip, GitHub Actions, and Docker. No issue or pull-request templates are tracked.
- `LICENSE` is AGPL-3.0-only and `NOTICE` contains notices for the third-party code reviewed there. `NOTICE` says the repository contains no CCP client code or SDE copy; the embedded `client-*` data below needs an owner licensing decision before treating that statement as settled.

### Game-data and large-file review

No image, executable, client script, or binary archive was found among current tracked files. No tracked file exceeds 1 MiB except JSON fixtures/data. The largest are `dotnet/src/Eve.Dungeons/Data/catalog/v1/dungeons-01.json` (6,173,489 bytes), `oracle/fixtures/destiny/cases.json` (3,260,598 bytes), `dotnet/src/Eve.Npc/Data/npc_authority.json` (3,205,598 bytes), and `oracle/fixtures/npc/hull_weapons.json` (2,533,366 bytes). These are JSON catalogs/fixtures, not client binaries; keep them only with their source/provenance documented.

The following compressed records are embedded and look client-derived. They contain EVE gameplay data and, in the mission file, mission templates/messages. The owner should confirm redistribution rights or replace them with data fetched/generated from a permitted source before continuing public distribution:

| Tracked file | Compressed size | Contents | Recommendation |
| --- | ---: | --- | --- |
| `dotnet/src/Eve.Services.Core/Data/client-missions.jsonl.gz` | 72,072 bytes | 2,893 records with mission template/message fields | Review rights; remove or replace if redistribution is not authorized |
| `dotnet/src/Eve.Universe/Data/client-systems.jsonl.gz` | 21,379 bytes | 8,114 solar-system records | Review rights; prefer the permitted official SDE source |
| `dotnet/src/Eve.Universe/Data/client-spacecomponentsbytype.jsonl.gz` | 474 bytes | 10 space-component records | Review rights; prefer the permitted official SDE source |
| `dotnet/src/Eve.Universe/Data/client-solarsystemcontent.jsonl.gz` | 384 bytes | 6 solar-system content records | Review rights; prefer the permitted official SDE source |
