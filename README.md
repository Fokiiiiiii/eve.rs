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
| `crates/eve-space` | Space: a solar system's stations, celestials and stargates from the static data, slim items and the destiny payloads (`AddBalls2`, `SetState`), and a scene of ships that undock, fly and dock; checked against the recorded undock |
| `crates/eve-sim` | `eve-sim`: the space simulation process. The service host connects to its Unix socket (`oracle/fixtures/simlink` pins the link for both languages) |
| `crates/eve-oracle` | Replays `oracle/fixtures/**` against the crates |
| `crates/eve-bench` | Throughput benchmarks with ratios against the JS baseline |
| `oracle/` | Golden fixtures and the JS baseline, generated from EVE.js (see `oracle/PROVENANCE.md`); `oracle/fixtures/hostlink` pins the front-host link for both languages |
| `crates/eve-net` | The network front: the client's TCP connection, the login handshake, and the link to the service host. `eve-net` is the server binary; `eve-loadgen` measures any server from the client side |
| `dotnet/src/Eve.Host` | The service host (`eve-host`): sessions, the packet dispatcher, `machoNet`, development accounts |
| `dotnet/src/Eve.Space`, `Eve.Services.Space` | The link to the space simulation (codec, client) and the `ship` and `beyonce` services: undock, the destiny stream, movement commands, docking |
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
(cd dotnet && EVE_PG_URL=$(../tools/dev/pg18.sh url) dotnet test EveRs.slnx)      # 2,229 tests; the database ones are skipped without EVE_PG_URL
cargo run --release -p eve-bench -- --compare
```

## Shared foundations

Features do not keep timers, event hooks or tables of running things of their own: the server has a durable scheduler (`Eve.Scheduler`), an
event bus with a transactional outbox (`Eve.Events`), a relation store for standings, wars, security status and affiliation (`Eve.Relations`),
a content runtime for missions, anomalies and incursions (`Eve.Content`), an effect registry for what modules do (`Eve.Dogma`), and a rowset
read side (`Eve.ReadSide`). Read [`docs/FOUNDATIONS.md`](docs/FOUNDATIONS.md) before adding a feature.

## Development environment and CI

- **GitHub Codespaces / Dev Container** (`.devcontainer/`): the Rust toolchain, the .NET 10 SDK, Python and a PostgreSQL 18
  service (`docker-compose.yml` beside it). `post-create.sh` sets up the Python client, restores the packages and loads the
  static data into PostgreSQL; `EVE_PG_URL` is set, so `cargo test --workspace` and `dotnet test` run the database tests.
- **GitHub Actions** (`.github/workflows/`): `ci.yml` runs on every push and pull request: Rust (fmt, clippy, tests), .NET
  (tests against a `postgres:18` service with the static data loaded), the Python client's own tests, a check that the golden
  fixtures are what their generators write, and the fast end-to-end tests (the whole server stack and the headless client,
  `tools/dev/run-eve-live.sh`; its logs are kept as an artifact). `live-slow.yml` runs every test file of the client on its
  own against the stack, nightly and on request (`tools/dev/run-live-each.sh`). The static data is cached by build; Dependabot
  keeps Cargo, NuGet, pip, Actions and Docker up to date.
- **Docker Compose** (`docker-compose.yml`, `Dockerfile`): the server itself for an EVE client, `docker compose up --build`:
  PostgreSQL 18, a one-shot static-data load, `eve-sim`, `eve-host` and `eve-net` (port 26000).
- `EVE_MARKET_SEED=skip` stops the host from fetching the NPC market's first orders from CCP's ESI on its first start (CI,
  the dev container and the compose file set it).

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
