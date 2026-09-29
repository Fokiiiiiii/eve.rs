# eve.rs

A rewrite of the EVE.js server (a local EVE Online server emulator, Node.js) as
a Rust compute core, a C#/.NET 10 service layer, and PostgreSQL 18. The Node
implementation stays the executable specification until cut-over: every native
component is checked against it through recorded golden fixtures.

> Localhost-only research project. Not affiliated with CCP hf. It contains no
> CCP client code and no copy of the static data export.

## Layout

| Path | What |
|---|---|
| `crates/eve-marshal` | MachoNet marshal codec, byte-identical to the JS reference |
| `crates/eve-destiny` | Carbon motion integration (bit-identical, with V8's `Math.exp`) and ball wire encoding |
| `crates/eve-oracle` | Replays `oracle/fixtures/**` against the crates |
| `crates/eve-bench` | Throughput benchmarks with ratios against the JS baseline |
| `oracle/` | Golden fixtures and the JS baseline, generated from EVE.js (see `oracle/PROVENANCE.md`) |
| `sde/` | Static data export: pinned fetch and integrity record (the data itself is never committed) |
| `dotnet/` | The .NET 10 service layer (not started) |
| `tools/headless-client` | A small Python client that speaks the wire protocol: handshake, service calls |
| `tools/spec-extract` | Scripts that extract facts (names, constants) from a local copy of the client source |
| `docs/` | Migration status (`NATIVE_MIGRATION.md`) and extracted client-spec facts (`spec/`) |

## Checks

```
cargo fmt --all --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
cargo run --release -p eve-bench -- --compare
```

## Data and the client source

- **SDE.** Fetch it with `sde/fetch.sh` into `.cache/sde/` (git-ignored). The build
  is pinned in `sde/lock.json`; the client build it matches is 24.01 (3396210).
- **Client source.** Some scripts read a local checkout of the decompiled client
  as a read-only reference (`tools/spec-extract`). Its files are CCP's property:
  they are never copied into this repository, and extracted outputs that
  reproduce its schemas are git-ignored.

## License

AGPL-3.0-only. See `LICENSE` and `NOTICE`.
