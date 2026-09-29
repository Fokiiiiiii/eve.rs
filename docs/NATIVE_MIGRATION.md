# Moving EVE.js to Rust, C# and PostgreSQL

Status of the migration from the Node.js server to Rust (compute core), C#/.NET 10
(service layer) and PostgreSQL 18 (persistence). The plan is a full rewrite with
the Node server kept, until cut-over, as the executable specification: every
native component is checked against it through recorded golden fixtures.

## What the current server looks like

About 865k lines of JavaScript under `server/src` (205 services, 181k lines of
space simulation, 128k of Abyssal content logic), one world process holding all
state and a 10 Hz scene tick, and 114 runtime tables in SQLite as JSON blobs
behind a synchronous cache-first API (about 415 call sites). There was no test
coverage of marshal, Destiny, dogma or inventory, so the first job was to create
an oracle.

## Done

| Milestone | What | Where |
|---|---|---|
| M0 | Golden fixtures recorded from the JS implementation (marshal: 543 cases; destiny: 4758 cases, exact IEEE-754 bits) and a JS baseline benchmark | `oracle/`, `tools/bench`, `server/tests/oracle_*.test.js` |
| M1a | Rust marshal codec, byte-identical to `marshal.js` on every fixture | `crates/eve-marshal` |
| M1b | Rust carbon integration and ball wire encoding, bit/byte-identical on every fixture | `crates/eve-destiny` |
| M2 | PostgreSQL 18 persistence in C#: table store, owner leases with epoch fencing, write-ahead outbox, cache-first store (dirty rows, transient paths, background flush), SQLite importer. Pinned by golden fixtures for the row codec, table sets and roles, by a `gamestore.sqlite` written by the real JS store, and by tests against a real PostgreSQL 18 | `dotnet/src/Eve.GameStore`, `dotnet/src/Eve.GameStore.Import` |
| M3a | The marshal codec in C#, byte-identical on all 543 marshal fixtures in both directions; corrupt and hostile input only ever fails with a `MarshalException` | `dotnet/src/Eve.Marshal` |
| M3b | The C# service layer: MachoNet packets, a packet dispatcher, service and bound-object registry, `[Service]`/`[Method]` with a source-generated dispatch. Checked against 21 scenarios (76 requests) recorded from the real JS dispatcher: request bytes in, response bytes and bound-object state out | `dotnet/src/Eve.Macho`, `dotnet/src/Eve.Services`, `dotnet/src/Eve.Services.Generator` |
| M3c | First service wave: `pingService`, `browserLockdownSvc`, `subscriptionMgr` (the last reads Omega licences from the store). 50 scenarios (197 requests) recorded from the real JS services across environment, config, stored-licence, clock and session variants | `dotnet/src/Eve.Services.Core` |

Run the checks:

```
node tools/run-isolated-tests.js server/tests/oracle_marshal.test.js server/tests/oracle_destiny.test.js
cd rust && cargo test --workspace && cargo clippy --workspace --all-targets -- -D warnings
```

## Measured (same machine, Rust release build vs. Node 22)

| Workload | JS | Rust | Ratio |
|---|---|---|---|
| marshal encode, small call | 127k/s | 3.9M/s | ~29x |
| marshal encode, 200-entry dict | 4.8k/s | 135k/s | ~28x |
| marshal encode, 1000-row rowset | 42/s | 3,000/s | ~60x |
| marshal decode, 1000-row rowset | 132/s | 2,370/s | ~20x |
| marshal decode, small call | 734k/s | 1.2M/s | ~1.8x |
| marshal decode, 200-entry dict | 34k/s | 50k/s | ~1.5x |
| carbon integrate | 1.27M/s | 12.6M/s | ~10x |
| ball encode (500 ships) | 205k ships/s | 12.2M ships/s | ~60x |

Small-message decode is allocation-bound and only modestly faster; at about a
microsecond per packet it is not what limits the server. The large wins are on
rowsets (shared row descriptors), encoding, and numeric kernels.

### The C# codec (same machine, .NET 10 release build vs. the recorded Node 22 baseline)

| Workload | JS | C# | Ratio |
|---|---|---|---|
| marshal encode, small call | 137k/s | 948k/s | ~6.9x |
| marshal decode, small call | 663k/s | 1.06M/s | ~1.6x |
| marshal encode, 200-entry dict | 4.8k/s | 32k/s | ~6.7x |
| marshal decode, 200-entry dict | 33k/s | 59k/s | ~1.8x |
| marshal encode, 1000-row rowset | 48/s | 1,155/s | ~24x |
| marshal decode, 1000-row rowset | 117/s | 1,767/s | ~15x |
| marshal decode, 64 x 4 KiB buffers | 139k/s | 313k/s | ~2.3x |
| marshal encode, 64 x 4 KiB buffers | 13.7k/s | 8.2k/s | **~0.6x** |

The Rust codec stays the one for the network front (about 2.6x the C# rowset encode rate and 1.3x the decode rate). The C# codec is
slower than Node on buffer-heavy encoding (a memory copy the JS `Buffer.concat` path avoids); an
`IBufferWriter` overload that writes straight into the socket buffer is the fix, not done yet.

## Findings worth knowing

- **`Math.exp` is not the platform `exp`.** V8 uses its own fdlibm-derived
  routine. Using Rust's `f64::exp` changes the last bit in about 2% of carbon
  steps. `eve-destiny` ports V8's routine and is bit-identical on 4,000+
  recorded arguments, including subnormals and thresholds. Any port of a
  Destiny kernel that calls `exp`, `pow` or trigonometry must be checked the
  same way.
- **`decode(encode(x))` is not `x`.** The JS decoder returns every string
  opcode as a `Buffer` and every 64-bit integer as a `BigInt`, and it is
  deliberately lenient (a string longer than the input yields the bytes that
  are there). The Rust decoder reproduces both, and adds bounds the JS one does
  not have: nesting depth, and allocation proportional to the input.
- **The JS reference throws lazily.** A malformed identity only fails an encode
  whose mode needs it. The Rust port keeps that (`IdRef::Invalid`) so recorded
  failures match by class (`TypeError`, `RangeError`, ...).

- **jsonb does not keep key order.** PostgreSQL orders object keys by length and then bytes, and the JS server
  iterates integer-like keys first and the rest in insertion order. Nothing in the store depends on the order of
  keys inside a document, and services ported from JS must not either (the row codec fixtures compare documents
  order-insensitively for this reason). A JSON string containing the escape `\u0000` cannot be stored in jsonb; the
  store reports `PERSISTENCE_UNSUPPORTED_JSON` naming the table instead of failing obscurely.
- **A fence is checked in the transaction that publishes.** Each mutation takes a row share lock on its owner row
  and re-validates (role, epoch, instance, lease) in the same transaction, so a successor that takes over cannot
  be raced. The lease clock is the database's, so hosts with skewed clocks agree.
- **The reference server re-types what it echoes.** Its decoder turns every Python `str` into a `Buffer`, every
  integral real into a number and every 64-bit integer into a BigInt, so a service that returns received values
  writes different opcodes than it read (`str` becomes `buffer`). `JsSemantics.AsReceived` reproduces that where a
  port must stay byte-compatible; the packet fields the dispatcher passes through use it.
- **A failing handler is answered with None, not an error.** Any exception other than a wrapped exception makes
  the reference send a `None` result. The C# dispatcher keeps that (with an `OnProblem` callback for logging) so
  behaviour is identical; changing it is a deliberate decision for later, not an accident of the port.
- **`||` on decoded values.** `x || null` in the packet code treats a decoded `str` (a `Buffer`, even an empty one)
  as true and a decoded string-table item as true only when non-empty. `JsSemantics.Truthy` gives the same
  answers for the typed values.

## Not done

- **Not wired into the running server.** There is no N-API/FFI layer. Marshal
  alone would gain little across N-API (the cost moves to building JS objects);
  the real gain is a Rust network front (M4).
- **movement / warp / nativeSubwarp** (about 4k lines) are not ported: they are
  factories whose ~30 helpers are wired in `space/runtime/destinyMovementEngine.js`,
  which needs the full service graph to load.
- **The scene tick benchmark** needs the generated SDE tables (not in the
  repository). The login-path latency benchmark needs a running world.
- **M3 is a foundation plus three services, not the service layer.** The reference has 77 service directories
  (205 services); three are ported. Waves that need the SDE (account/character/skills, inventory/dogma/fitting,
  ship/station, market, then the long tail) can now start: the SDE is loaded (below). Not yet ported from the dispatcher: the object-cache
  (`CacheOK`) short-circuit for cached method calls, `afterCallResponse` hooks, packet types other than
  call/ping/release-notification (session change notifications are only acknowledged), and the tracing hooks.
  Method names are matched exactly and only the `[Method]` names are reachable, unlike the reference where a
  call can reach any method of the class by name.
- **The cache-first store's API is synchronous on an in-memory copy** (`Read` returns a copy; tables must be
  preloaded), which keeps the ~415 call sites' shape but not their aliasing: code that mutated a value returned
  by `read` and never called `write` (which the JS store tolerates) must be found and fixed during the port.
  Static (SDE and hand-curated) tables are not part of this store yet.
- **M4 Rust network front, M5 Rust tick and visibility, M6 cut-over**: not started beyond the pieces below. The
  dispatcher fixtures are the conformance suite the Rust front will be checked against (they include the
  response packet bytes the front must produce).

## Added since the first import

- **`eve-sde`**: loads the JSONL static-data export into a PostgreSQL 18 `sde`
  schema (`ord`, `key`, `doc jsonb`) and verifies row counts. Tested against a
  local PostgreSQL 18.4. `sde/fetch.sh` downloads and pins an SDE build
  (default 3396210, the version `DatabaseCreator` targets) into `.cache/`; the
  export itself is not committed.
- **`tools/headless-client` (`evecli`)**: a Python client with its own marshal
  codec (a third implementation of the golden fixtures), the login handshake and
  service calls. It passes against a scripted mock server. The same tests run
  against the real Node server with `tools/dev/run-evejs-live.sh`: login, server
  time and ping, `GetInitVals`, and the answer to an unknown service pass (4 tests; the wrong-password test is
  skipped because the development server does not validate passwords).
- **`docs/spec`, `tools/spec-extract`**: facts (packet layout, handshake,
  service and method names, dogma operators) taken from a local copy of the
  client source. No client code is copied.
- **SDE build 3396210 loaded, and fetched on the first start from now on.** The development environment cannot
  reach `developers.eveonline.com` (or `data.everef.net`), so the archive
  (`eve-online-static-data-3396210-jsonl.zip`, sha256 `17ae0c7f...279b3e`, 84,190,400 bytes) was downloaded by a
  one-off GitHub Actions workflow from the EVE Ref mirror and committed, to get it into the environment. It has
  since been removed from the tree (it stays in the history of `main`), `sde/data/` is git-ignored, and
  `eve-sde ensure` (`crates/eve-sde/src/{fetch,ensure}.rs`) replaces it: on start-up it skips everything if
  `sde._build` already names the build, else downloads from the sources in `sde/lock.json` (CCP's host, then the
  mirror), refuses an archive whose SHA-256 is not the pinned one or whose `_sde.jsonl` is another build,
  extracts flat, loads in one transaction and deletes the archive. Whether CCP's own copy has the same bytes as
  the mirror's has not been compared, since neither host is reachable from here; if it does not, that source
  is skipped on the hash and the mirror serves. The download half was exercised here only against `file://`
  and loopback HTTP sources and against the real archive passed with `--archive`, never against either host.
  Loaded, the archive gave 61 tables, 668,618 records. Against the tables the reference's `DatabaseCreator`
  builds from the same archive: `solarSystems` 8,490 and `stations` 5,210 match exactly, `typeDogma` 26,724
  matches, and `itemTypes` has one row fewer than `types` (typeID 0, `#System`, which `DatabaseCreator` leaves
  out). Only build 3396210 has been used; a `latest` build has not been fetched.
- **Reference server start-up.** Booting it on that data takes about five minutes and 3-4 GB before it listens on
  26000. Its static tables are read from `EVEJS_GAMESTORE_DATA_DIR` (default `<repo>/_local/gameStore/data`, else
  the source tree), not from `EVEJS_DATA_ROOT`, so a relocated data root also needs that variable.
- **A call the reference cannot dispatch gets a `None` result**, for an unknown service as much as an unknown method
  (checked live); the client test expects that, as the dispatcher fixtures record.
- **Still blocked on the environment**: `apt.postgresql.org` and `www.postgresql.org` are denied, so PostgreSQL 18
  comes from the npm-packaged binaries; .NET 10 comes from Ubuntu's packages.

## Policy

`server/src/space/destiny/native/README.md` requires native Destiny ports to be
independently derived from the pinned Carbon source and not to be relied on by
live gameplay without a separate reviewed change. `eve-destiny` therefore is
not loaded by anything. New Rust code is AGPL-3.0-only like the rest of the
repository; the Carbon-derived integration keeps its MIT notice.
