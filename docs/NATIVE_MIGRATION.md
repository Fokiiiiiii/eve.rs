# Moving EVE.js to Rust, C# and PostgreSQL

Status log of the work that replaces the Node.js server with Rust (network front, simulation), C#/.NET 10
(service layer) and PostgreSQL 18. **The direction and the order of work are in `PLAN.md`**: the goal is a new,
faster, more maintainable server that the same client can play on, keeping only the external compatibility of
EVE.js and redesigning the internals (`adr/0001-external-compatibility-only.md`). The Node server is the
behavioural reference, checked through recorded golden fixtures and live scenarios, not a design to reproduce.
The milestones M0 to M3 below are the foundations (slice S0 in the plan); M4 to M6 are replaced by the plan's
slices S1 to S6.

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
| S1 | **The walking skeleton.** A Rust network front (`eve-net`: TCP, framing, the Placebo handshake, forwarding raw packets) and a C# service host (`Eve.Host`: sessions, the dispatcher, `machoNet`, a development account directory) joined by the link of ADR 0002. The headless client's live tests, the same file that passes against the EVE.js reference, pass against it | `crates/eve-net`, `dotnet/src/Eve.Host`, `oracle/fixtures/hostlink` |
| S2 | **Login to a character.** Accounts in PostgreSQL (`Eve.Identity`), characters (`Eve.Characters`), items (`Eve.Inventory`), skills (`Eve.Skills`) and the static data they need (`Eve.Sde`), each module with its own schema and migrations (`Eve.Data`, `adr/0003`); `charUnboundMgr` (`Eve.Services.CharacterSelect`) with the screen's calls, character creation with its rookie ship and its race's starting skills in one transaction, three slots, name rules, and choosing a character, which sets the session, sends the session change and the skills notification that makes the client load its skills; the skill handler (`Eve.Services.Skills`) answers what the client asks about them. The scenarios recorded from the reference (`oracle/fixtures/scenarios`) are replayed against a running server and compared by shape and value: they pass against the reference itself (38 tests) and against eve.rs with the deviations of `compat/DEVIATIONS.md` (43 tests with the S1 live tests). The starting skill points computed from the SDE equal the reference's for all twelve bloodlines, and the skills notification is byte-identical to the reference's for the same skill | `dotnet/src/Eve.{Identity,Characters,Inventory,Skills,Sde,Data,Services.CharacterSelect,Services.Skills}`, `tools/headless-client/tests/test_login_to_character.py` |
| S3a | **Skills, wallet, the character's record and the stations' names** (the first stretch of S3). `Eve.Skills` (above), `Eve.Wallet` (ISK and PLEX, opened in the creation transaction), `Eve.Universe` (the stations of the static data with the names the client shows: system, planet number, moon, owner and operation; all 62 stations the reference reported come out with the same name, type, owner, operation, system, constellation, region and security), `charMgr` (the public record, the home station row, creation date, contact list and the fixed answers) and `account.GetCashBalance`. `stationSvc` (also `station`) answers `GetStation` as a cached method result whose marshalled bytes, and so its checksum, equal the reference's for all 62 recorded stations, plus the station item bits, the guests (the characters in the game at the station), the solar system and the empty alliance lists. The inventory broker hands out bound inventories for the hangar, the station and a ship, and answers the reads on them with the row forms of the reference (packed rows over the item descriptor, `util.Row`, `Rowset`, the empty `set` for an empty flag). `character_info.json`, `stations.json` and `inventory.json` are recorded from the reference; the replay passes against the reference (52 tests) and eve.rs (53 with the live tests) | `dotnet/src/Eve.{Wallet,Universe,Services.CharacterInfo,Services.Wallet,Services.Station,Services.Inventory}`, `oracle/fixtures/scenarios/{character_info,stations,inventory}.json` |
| S3d | **Dogma and a fitted ship.** `Eve.Dogma` (`adr/0004`) works out every attribute of an item from the client's rules and the static data: operation order, the stacking penalty, caps and floors, domains and functions, the effects the game implements in code (`BuiltInEffects`) and the brain of a character (`BrainBuilder`, what each trained skill does, in the reference's order). A new character's ship now comes with its starter modules (`StarterKits`, an afterburner, the race's civilian weapon and a miner in their slots), made in the creation transaction and listed by the ship's inventory. `dogmaIM` (`Eve.Services.Dogma`) binds for the station and answers `GetAllInfo`: the ship, its modules with their running effects, the character (two attribute lists and the brain), the state rows and the heat. Checked against `dogma.json`, recorded from the reference for the four rookie ships: the engine's values for the ship, each module and the character equal the reference's (two documented deviations, `DEVIATIONS.md` #11 and #12), the brain is identical for all four races, and the whole live answer replays against eve.rs (57 live tests) and the reference itself (56). 1,993 .NET tests; mutations of the engine's rules and of the brain's ordering are caught | `dotnet/src/Eve.{Dogma,Services.Dogma}`, `dotnet/src/Eve.Characters/StarterKits.cs`, `oracle/fixtures/scenarios/dogma.json` |
| S3e | **Moving items and fitting.** `Eve.Fitting` (`adr/0005`) holds the fitting rules (`FittingRules.Check`: the slot exists and is free, the skills, the ship group or type, how many of a type or group, rig size and calibration, turret and launcher hard points, the first free slot) and the state of each module (`PgModuleStateStore`, in its own schema; a starter kit is created online). `Eve.Ships` (`CharacterShips`) changes a character's ship in one transaction under the character's lock: an item goes between the hangar and the ship (a slot, the first free slot, the cargo hold), a module goes on or off, and the outcome carries the item moves and the dogma events (attributes changed from-to, an effect started or stopped) that come from working the ship out before and after. `Eve.Services.Ships` says that in the reference's notifications and user errors. `dogmaIM` now answers `SetModuleOnline`, `TakeModuleOffline` and the three notifications after `GetAllInfo` (sent after its answer: `ClientSession.AfterResponse`); the inventory broker answers `Add`. Recorded from the reference (`fitting.json`) and replayed against both servers: unfit, online and offline, and the events they send, with a ship of each race. Fitting a module and the cargo hold cannot be recorded (the reference answers `None` to the headless client): they follow the client's contract and the reference's validator and are covered by unit tests (`DEVIATIONS.md`, "Not yet matched"). 2,078 .NET tests; mutations of the rules, the ship changes and the adapters are caught | `dotnet/src/Eve.{Fitting,Ships,Services.Ships}`, `oracle/fixtures/scenarios/fitting.json` |
| S3f | **Skill training.** `Eve.Skills` (`adr/0006`) works a character's queue out from the clock: `Training` (points a level costs, points a minute, when each line starts and ends, which queues are refused, what has finished, the points a stopped skill keeps) is pure, `CharacterTraining` keeps the queue, the settled levels and the history in PostgreSQL in one transaction under the character's lock and settles whatever the clock finished before anything reads skills (the ship for dogma included). `skillHandler` answers `SaveNewQueue`, `CharStartTrainingSkillByTypeID`, `CharStartTrainingSkill`, `AbortTraining`, `CharStopTrainingSkill`, the queue and history reads, and shows the running skill with the points it has gained; `TrainingAnnouncer` tells the client in the reference's order (`OnNewSkillQueueSaved`, `OnMultipleCharactersTrainingUpdated`, `OnSkillQueuePausedServer`, and for a level that finishes `OnServerSkillsChanged`, `OnSkillLevelsTrained`, `OnSkillsChanged`, the ship's attributes that changed, the character's and the brain) with a timer per running queue; a character that logs in later finds its levels trained, told nothing about. Recorded from the reference at its own speed (`training.json`: Caldari and Amarr, every call and error of the queue) and at a speed 60,000 times faster so levels finish while the client waits (`training_fast.json`, which also caught that Spaceship Command IV changes a ship's agility) and replayed against both servers; two documented deviations (`DEVIATIONS.md` #13, #14). 2,158 .NET tests; mutations of the training rules, the queue changes and the adapters are caught | `dotnet/src/Eve.{Skills,Services.Skills}`, `Eve.Services.Dogma/CharacterDogmaSync.cs`, `oracle/fixtures/scenarios/training*.json` |
| S4 | **Space (first slice).** A ship undocks, is sent the system, flies and docks, in a simulation of its own. `crates/eve-space` builds a solar system from the static data (stations, planets, moons, belts, the star and stargates, named and ordered as the reference does; a stargate's facing worked out with the reference's single-precision arithmetic and equal to the recorded gates to the last bit), the slim items and the `AddBalls2` / `SetState` payloads (equal to what the reference sent for the recorded undock, apart from its customs offices and props and the low bits of a far point), and a scene: a ship is put by its station at top speed, told the time and given the ball and then the state at the reference's stamps, and answers `CmdSetSpeedFraction`, `CmdGotoDirection`, `CmdStop`, `CmdAlignTo`, `CmdOrbit`, `CmdFollowBall` and `CmdDock` with the destiny actions the reference sent (a repeat of an order the ship already follows says nothing, a speed of nothing is a `Stop`, a ship that stands is given its speed back by the order that sends it somewhere, and a dock from beyond the range is a `FollowBall` to the station plus the user error `DockingApproach`, as recorded in `movement.json` and `approach.json`; `motion.rs` steers what follows or orbits); motion is worked out from the time each call carries (the carbon integrator), a docking is accepted within 2.5 km of the station's surface and done four seconds later. `crates/eve-sim` is the process; the link (`adr/0007`, golden frames in `oracle/fixtures/simlink`, written by a Python generator and replayed by Rust and C#) carries `Enter`, `Bind`, `UpdateState`, `Command`, `ShipSpec`, `Leave` and back `EnterReply`, `Notify`, `Accepted` (after every accepted command), `Refused`, `Approaching`, `Docked`. In the host `Eve.Space` holds the codec and `SimClient` (reconnects, awaits an enter and a dock), `CharacterShips` moves the ship item and the character (one transaction, the character's lock), and `Eve.Services.Space` sends a ship out and brings it home in the reference's order (the ship's row, the objects bound to the station let go, the session change, the undocking protection; at a docking the protection ends, objects let go, the ship's row, the session change, `OnDockingFinished`), forwards the simulation's notifications to the client, and docks every ship at its last station when the simulation is lost or a session ends. `ship` (`Undock`) and `beyonce` (bind, `UpdateStateRequest`, `GetFormations`, the commands) are thin over it. Checked by `tests/test_undock.py`, which replays the recorded scenario for two races on the reference itself and on eve.rs; deviations #15 to #19 (`docs/compat/DEVIATIONS.md`); 131 Rust and 2,229 .NET tests, mutation checks of the scene, the link client, the ship moves and the hub. Not yet: approaching a station to dock, align, orbit, follow, warp, jumps, other ships in the system, damage |

Run the checks (the server itself: `tools/dev/run-eve-live.sh`):

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


### The front and the host (S1), against the EVE.js reference

Same machine (4 cores, shared with the load generator), same client (`eve-loadgen`, which speaks the wire
protocol and works against either server), `machoNet.GetTime` as the call, release builds. A run against a fresh
server includes account creation, so each was run twice; the ranges are those runs.

| Workload | EVE.js reference | eve.rs (front + host) | Ratio |
|---|---|---|---|
| 100 clients log in at once | 116-187/s, p50 281-450 ms | 3,400-4,100/s, p50 20-26 ms | ~20-35x |
| 100 clients x 2,000 calls, one in flight each | 2,613-2,675 calls/s, p50 36 ms, p99 60 ms | 29,300-41,100 calls/s, p50 2.1-2.6 ms, p99 6-12 ms | ~11-15x |
| 10 clients x 20,000 calls, one in flight each | 2,413 calls/s, p50 3.4 ms, p99 14 ms | 25,300 calls/s, p50 0.36 ms, p99 1.1 ms | ~10x, latency ~9x lower |
| 10 clients x 20,000 calls, 32 in flight each | not measured | 107,000 calls/s, p50 2.7 ms | |

The reference is one JavaScript thread that also runs its world; here the front (Rust, threads), the host (C#)
and the load generator share the four cores. Numbers move between runs by about a third on this machine.
Nothing was tuned yet: a frame is copied several times and the host allocates per packet.

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

- **The reference's session id can repeat.** `sessionID = ms * 15 + clientID % 15`, so two logins in the same
  millisecond whose account ids differ by a multiple of three get the same id. In the reference that is a latent
  bug; in the front it replaced a live session's queue, which a concurrent-login test caught. Ids are still
  formed that way (the client only sees an opaque long) but are moved up past the last one issued.
- **`challenge_responsehash` (`"55087"`) is a constant, not the CRC of the challenge.** The Placebo answer the
  client sends back is `crc_hqx` of the marshalled challenge, but the value the server sends is fixed and no
  headless client checks it. Recorded so nobody derives it again.
- **Everything the client sees at login is small.** The handshake is nine messages, one Python function to run
  on the client (the time-dilation handler), and a session-initial-state packet the client blocks on. That is
  why the front can be small: the game starts after it.
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
- **S1 and S2 reach the character screen, not the world.** The host serves `machoNet` (`GetTime`, `GetInitVals`,
  `GetServiceInfo`, `GetGlobalConfig`, `GetGlobalConfigValue`, `GetServerStatus`, `GetNodeID`), `pingService`,
  `browserLockdownSvc`, `subscriptionMgr` and, with PostgreSQL and the SDE, `charUnboundMgr`. After a character is
  chosen the session is in a station with a fitted ship; `dogmaIM.GetAllInfo` and the inventory reads answer, and nothing else does yet (S3). Not yet
  matched: see "Not yet matched" in `compat/DEVIATIONS.md`.
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
- **Slices S1 to S6 of `PLAN.md`** (the Rust network front, the walking skeleton, login to a character, station,
  space, the long tail, cut-over): not started beyond the pieces below. The
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
