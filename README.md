# eve.rs

A new server for the EVE Online client, in Rust (network front and simulation), C#/.NET 10 (service layer) and
PostgreSQL 18. The aim is a server that the same, unmodified client can play on, that is faster and easier to
maintain, and that does not depend on EVE.js. EVE.js is not ported line by line: its external behaviour (what the
client can observe) is kept, and everything behind that is designed again. It stays as a behavioural reference to
run the same scenarios against. Start with `CLAUDE.md`, `docs/PLAN.md` and `docs/adr/`.

> Localhost-only research project. Not affiliated with CCP hf. It contains no
> CCP client code and no copy of the static data export.

## Layout

| Path | What |
|---|---|
| `crates/eve-marshal` | MachoNet marshal codec, checked byte-for-byte against the reference's recorded output |
| `crates/eve-destiny` | Carbon motion integration (bit-identical, with V8's `Math.exp`) and ball wire encoding |
| `crates/eve-oracle` | Replays `oracle/fixtures/**` against the crates |
| `crates/eve-bench` | Throughput benchmarks with ratios against the JS baseline |
| `oracle/` | Golden fixtures and the JS baseline, generated from EVE.js (see `oracle/PROVENANCE.md`); `oracle/fixtures/hostlink` pins the front-host link for both languages |
| `crates/eve-net` | The network front: the client's TCP connection, the login handshake, and the link to the service host. `eve-net` is the server binary; `eve-loadgen` measures any server from the client side |
| `dotnet/src/Eve.Host` | The service host (`eve-host`): sessions, the packet dispatcher, `machoNet`, development accounts |
| `crates/eve-sde` | Reads the static data export (JSONL) and loads it into PostgreSQL 18 (`sde."<table>"(ord, key, doc jsonb)`) |
| `sde/` | Fetching the static data export: `fetch.sh`, and `lock.json` once fetched (the data itself is never committed) |
| `dotnet/src/Eve.GameStore` | The PostgreSQL 18 persistence layer: per-table `(key, doc jsonb, rev)`, owner leases with epoch fencing, a write-ahead outbox, and the cache-first store the services use |
| `dotnet/src/Eve.GameStore.Import` | `eve-gamestore-import`: copies a reference-server `gamestore.sqlite` into PostgreSQL and verifies it |
| `dotnet/src/Eve.Marshal` | The marshal codec in C#, checked against the same golden fixtures as the Rust one |
| `dotnet/src/Eve.Macho` | MachoNet packets: addresses, call requests, call/error/ping responses |
| `dotnet/src/Eve.Services` | The service layer: sessions, wrapped-exception errors, service and bound-object registry, packet dispatcher, `[Service]`/`[Method]` |
| `dotnet/src/Eve.Services.Generator` | A Roslyn source generator that writes each service's dispatch (no reflection on the call path) |
| `dotnet/src/Eve.Services.Core` | The services ported so far (`pingService`, `browserLockdownSvc`, `subscriptionMgr`) |
| `tools/headless-client` | A small Python client that speaks the wire protocol (handshake, service calls); replays the same golden fixtures |
| `tools/dev` | `pg18.sh` (a local PostgreSQL 18), `run-eve-live.sh` (start eve.rs and run the live tests, or any command, against it), `run-evejs-live.sh` (live tests against the reference server) |
| `tools/spec-extract` | Scripts that extract facts (names, constants) from a local copy of the client source |
| `docs/` | The plan (`PLAN.md`), decisions (`adr/`), registered deviations from the reference (`compat/`), status (`NATIVE_MIGRATION.md`) and extracted client-spec facts (`spec/`) |

## Checks

```
cargo fmt --all --check
cargo clippy --workspace --all-targets -- -D warnings
tools/dev/pg18.sh start                                  # a local PostgreSQL 18 for the database tests
EVE_PG_URL=$(tools/dev/pg18.sh url) cargo test --workspace
(cd tools/headless-client && .venv/bin/python -m pytest -q)
(cd dotnet && EVE_PG_URL=$(../tools/dev/pg18.sh url) dotnet test EveRs.slnx)      # 1,902 tests; the database ones are skipped without EVE_PG_URL
cargo run --release -p eve-bench -- --compare
```

## Data and the client source

- **SDE.** Fetched on the first start, never committed (`sde/data/` and `.cache/` are git-ignored).
  `cargo run -p eve-sde -- ensure --pg $(tools/dev/pg18.sh url)` does the whole job and is what a server
  calls at start-up (`eve_sde::ensure::ensure_loaded`): if the database already holds the wanted build it
  does nothing; otherwise it downloads the archive (CCP's host first, then the mirror listed in
  `sde/lock.json`), checks its SHA-256 against the pin, extracts it under `.cache/sde/`, loads it, and
  deletes the archive. The pinned build is 3396210, the one that matches client 24.01. Downloads use `curl`.
  Without `--pg` it only fetches and extracts, and prints the directory. `--archive FILE` uses a local zip
  instead of downloading (for a host that cannot reach either source), `--build latest` follows CCP's
  current export, `--sha256 skip` accepts an archive that is not the pinned one. `sde/fetch.sh` is the
  shell equivalent of the fetch half.
- **Client source.** Some scripts read a local checkout of the decompiled client
  as a read-only reference (`tools/spec-extract`). Its files are CCP's property:
  they are never copied into this repository, and extracted outputs that
  reproduce its schemas are git-ignored.

## License

AGPL-3.0-only. See `LICENSE` and `NOTICE`.
