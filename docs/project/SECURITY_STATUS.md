# Security status

Checked 2026-10-19 on the Rust 1.99.0 and .NET 10 toolchains.

## Dependency and image review

- Rust: `cargo +1.99.0 fmt --all -- --check` and `cargo +1.99.0 clippy --workspace --all-targets -- -D warnings` passed. `cargo +1.99.0 test --workspace` passed, including doctests. `cargo-audit` was not installed, so the Rust advisory database was not checked.
- .NET: `dotnet list dotnet/EveRs.slnx package --vulnerable --include-transitive` reported no vulnerable packages for the projects in the solution. `dotnet build dotnet/EveRs.slnx -nodeReuse:false` completed with zero warnings and errors. The serial solution test run had failures in Host and Inventory tests and had not completed when this record was written.
- Python: `pip-audit 2.10.1 -r tools/headless-client/requirements.txt` reported no known vulnerabilities. Headless-client unit tests passed with pytest; live-client walk was not completed.
- No tracked npm manifests were present. Docker Scout and the Trivy, Grype, and Snyk commands were unavailable. The Docker base image is `rust:1.99.0-bookworm`; no image vulnerability database scan was completed.
- Workflow action references were reviewed locally. They use version tags, including `actions/checkout@v7`, `actions/cache@v6`, and `actions/setup-dotnet@v6`; no advisory check of those action versions was completed.

## Secret scan

`detect-secrets 1.5.0` scanned tracked files without network verification. It reported 20,325 heuristic matches across 63 files: 20,303 high-entropy hex matches, 7 high-entropy base64 matches, 7 secret-keyword matches, 6 basic-auth matches, and 2 JWT matches. The scanner output was reduced to paths and detector names; matched values were not displayed. The high-entropy matches include large static-data and fixture files. Each remaining credential-like match needs review before a clean secret-scan result can be stated. GitHub secret-scanning MCP was not available in this session.

## Attack-surface review

The Rust and .NET marshal decoders use checked bounds and bounded collection allocation. RPC envelope decoders validate tuple arity before indexed access. SQL value inputs use parameters; dynamic schema and table identifiers are constrained at their construction boundary, and read-side queries are registered from static definitions. Reviewed production static-data loading paths use fixed relative names rather than client-supplied paths.

The public gateway gRPC frame parser previously accepted an arbitrary advertised frame size while buffering the request. It now rejects a declared frame above the Host codec's 16 MiB limit, with a regression test for rejection before the body arrives. The targeted .NET build and regression test passed (1/1).

## Outstanding checks

The first serial .NET solution test run reported two Host failures and one Inventory assertion failure. Both Host cases passed when rerun individually under the assigned database lock. The Inventory assertion expected a list, while the EveJS ListStations contract returns a rowset; the test now checks the rowset type and one station row. The full Inventory test project then passed (65 tests). A full Host test-project rerun was stopped after 20 minutes without a test summary; the two Host cases passed individually. Rust advisory scanning, Docker image scanning, action-version advisory review, a complete secret review, and the headless-client live walk remain outstanding.
