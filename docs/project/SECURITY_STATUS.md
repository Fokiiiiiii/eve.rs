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

Source review found parameterized SQL values in the shared SQL command helper, bounded Rust marshal reads, existing malformed-input decoder tests, and SDE archive extraction that writes basenames into its controlled staging directory. No additional exploitable issue was identified in the reviewed paths. The Rust robustness test exercises 20,000 deterministic arbitrary byte inputs; the .NET marshal suite covers truncated streams, oversized counts, and nesting limits.

The public gateway gRPC frame parser already rejects a declared frame above the Host codec's 16 MiB limit before the full body arrives; its regression test records this behavior.

## Outstanding checks

The full CI run for `1f24d8c7` is pending. The replay uses a GM account, as in the recorded trace, so its first validation toggle should return false. The existing GML role test confirms that transition; CI will confirm the live walk. The container-image CVE scan, external action-advisory review, and individual review of the credential-oriented secret-scan matches remain open.
