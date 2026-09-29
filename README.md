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
| `crates/eve-sde` | Reads the static data export (JSONL) and loads it into PostgreSQL 18 (`sde."<table>"(ord, key, doc jsonb)`) |
| `sde/` | Fetching the static data export: `fetch.sh`, and `lock.json` once fetched (the data itself is never committed) |
| `dotnet/` | The .NET 10 service layer (not started) |
| `tools/headless-client` | A small Python client that speaks the wire protocol (handshake, service calls); replays the same golden fixtures |
| `tools/dev` | `pg18.sh` (a local PostgreSQL 18), `run-evejs-live.sh` (live tests against the reference server) |
| `tools/spec-extract` | Scripts that extract facts (names, constants) from a local copy of the client source |
| `docs/` | Migration status (`NATIVE_MIGRATION.md`) and extracted client-spec facts (`spec/`) |

## Checks

```
cargo fmt --all --check
cargo clippy --workspace --all-targets -- -D warnings
tools/dev/pg18.sh start                                  # a local PostgreSQL 18 for the database tests
EVE_PG_URL=$(tools/dev/pg18.sh url) cargo test --workspace
(cd tools/headless-client && .venv/bin/python -m pytest -q)
cargo run --release -p eve-bench -- --compare
```

## Data and the client source

- **SDE.** `sde/fetch.sh` downloads a build of the official JSONL export into
  `.cache/sde/` (git-ignored) and records its hash in `sde/lock.json`. The pinned build
  is 3396210, the one that matches client 24.01. Load it with
  `cargo run -p eve-sde -- load .cache/sde/eve-online-static-data-3396210-jsonl --pg $(tools/dev/pg18.sh url)`.
- **Client source.** Some scripts read a local checkout of the decompiled client
  as a read-only reference (`tools/spec-extract`). Its files are CCP's property:
  they are never copied into this repository, and extracted outputs that
  reproduce its schemas are git-ignored.

## License

AGPL-3.0-only. See `LICENSE` and `NOTICE`.
