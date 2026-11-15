# Space / combat audit landing

## Scope and landing

This branch was created from `origin/main` at `4e402f7e5` and contains the two combat-audit commits from the rescue worktree, cherry-picked without rewriting history:

- `0202c82d1 Fix low-chance wrecking hit resolution` (source commit `ea4ba5eb9`).
- `ba52f12f7 Retry refused CONCORD response spawns` (source commit `3456409be`).
- `e02c2f40a Report smartbomb hits and fix docking handoff` adds the protocol/codec path and bound-session transition check.
- `0a80de3aa Persist combat-logoff ships through timer expiry` adds durable restore and post-restart death settlement.
- `e83ff4d4e Record offline combat aggression for smartbomb and ECM` connects offline player targets to Crimewatch.
- `da8cd3b61 Generate smartbomb hit SimLink fixtures` adds the missing valid and truncated SmartbombHit cases to the Python fixture generator so the committed oracle is reproducible.

The remaining changes in this landing add the missing Host and persistence paths for combat logoff, report actual smartbomb player hits to Crimewatch, and fix the docking handoff for unbound staging sessions. They complete the existing findings in [audit-space-combat.md](audit-space-combat.md); that audit remains the detailed record of the two cherry-picked fixes and the prior CONCORD evidence.

## Behavior and evidence

### Low-chance turret resolution and CONCORD retry

The two cherry-picked fixes preserve the client-visible hit result and keep a refused CONCORD spawn pending for retry while its criminal flag is active. The V24.01 client consumes server `hitQuality` in `eve/client/script/environment/godma.py:502-533` and `eve/client/script/ui/shared/uilog.py:210-220,278-305`; it does not re-run the server's hit roll. EveJS `server/src/space/combat/laserTurrets.js:151-180` rejects `roll >= hitChance` before it applies wrecking quality. V24.01 `eve/client/script/ui/services/crimewatchSvc.py:89-105,222-253` restores and displays Crimewatch timers and flags, but a timer notification does not report whether a CONCORD response ship spawned. EveJS retains the response record and retries an empty responder spawn while the flag remains active (`server/src/services/security/crimewatchState.js:5066-5108,5169-5185`). The original retry finding and Host/Runtime test evidence are recorded in `audit-space-combat.md`.

### Combat logoff ships and offline combat

CCP's [Weapon and Logoff Timers](https://support.eveonline.com/hc/en-us/articles/203208932-Weapon-and-Logoff-Timers), updated August 29, 2024, documents the 15-minute capsuleer timer, its activation by offensive modules (including smartbombs and ECM bursts), and extension by attacks while the pilot is offline; it also documents the separate five-minute NPC timer. The V24.01 action path is generic: `shipmodulebutton.py:1333-1354` calls the active effect, then `godma.py:976-985,2049-2067` dispatches Dogma activation. `crimewatchSvc.py:94-105,222-232,327-329` restores and stores the timer state and expiry, and `crimewatchTimers.py:347-393` renders it. `gameui.py:942-985` handles the separate safe-logoff countdown and cancellation.

On disconnect, Host now retains a ship as `CombatLogoffShip` through the authoritative Crimewatch expiry and leaves a targetable unpiloted simulation entity. System restoration, expiry, re-entry, and offline ship destruction re-check persisted ownership and current timer state. Death settlement removes the lost hull and writes the replacement capsule, inventory outcome, and killmail event in the same database transaction. A Host restart can restore the retained ship and settle its later destruction. The 0031 migration adds the durable item kind; `origin/main` had migrations through 0030, so 0031 is next in sequence.

The Host records offensive module attacks, ECM burst hits, and smartbomb hits against online or retained offline player ships. The simulator sends SmartbombHit only for player ships actually in the blast that are not protected or tethered; the Host validates victim identity before recording module aggression. The event uses SimLink kind 164 / protocol 98 and the matching Rust, C# codec, and oracle fixture shape. V24.01 `crimewatchSvc.py:228-232,327-329` receives the timer state and `crimewatchTimers.py:384-393` displays it; the server owns the target list and timer calculation. EveJS `smartbombRuntime.js:211-338` checks the full blast set, records each offensive aggression, then applies damage; `moduleRuntimeCallbacks.js:410-478` wires those callbacks to Crimewatch, and `runtime/scene/tick.js:1641-1653` runs the cycle. A direct instrumented `executeSmartbombCycle` invocation with one owner and one victim returned only the victim and called the aggression recorder before damage. CCP's timer article is the authoritative gameplay source for the offline extension rule. EveJS has no server test script for this flow; its full network/database server flow was not run end to end.

### Docking handoff

The simulation now creates a widow and preserves the client ballpark during boarded/ejected handoff only for an already-bound pilot. A staging session that has not completed binding still leaves its unpiloted ship in space without acquiring a client ballpark transition. This matches V24.01 `eve/client/script/remote/michelle.py:482-485` (Destiny updates are ignored without a ballpark) and `:968-987` (a cleared ballpark is rebuilt by `SetState`).

## Changes brought over and exclusions

Included changes were checked against the source worktree's per-file diffs and status. `crates/eve-space/src/scene/mod.rs` is also included although it was outside the explicit path list: its `SmartbombHit` output/session routing and bound-pilot query are required by the listed weapon and docking changes. `SpaceSessions.Logoff.cs`, migration 0031, and the two new Host test classes are the new untracked files required by those flows. The `SpaceServiceTests.cs` changes were taken from the unstaged diff only.

The staged `SpaceServiceTests.cs` changes in the source worktree for FleetLoginHook behavior and a pre-bind docking wait were excluded because they are unrelated to this audit. I also left out the staged TODO / compatibility-sweep edits, mission, fitting, skills, FleetLoginHook, RPC and character-composition changes and tests, and the `.serena` plus unrelated project notes; they do not implement or verify a combat-audit finding. All unrelated staged, unstaged, and untracked work in `/home/user/wt-cx5` was left untouched. No README or SDE data was changed.

## Verification

All builds and tests used Release configuration. The earlier audit's PostgreSQL-backed .NET tests used the dedicated `eve_p4` database; the landing-fix checks below used `eve_p3`.

- Rust: `cargo fmt --package eve-space`; `cargo build --package eve-space --release`; `cargo check --package eve-space --release`; and `cargo clippy --package eve-space --all-targets --release -- -D warnings` all succeeded without warnings. The full `cargo test --package eve-space --release` run passed 1,027 unit tests and 40 integration/reference tests, with 0 failures and 0 ignored tests.
- A Release `cargo build --package eve-sim --release` also succeeded without warnings to run the Host test that requires a real headless simulation process.
- .NET Release builds of `Eve.Services.Crimewatch`, `Eve.Host`, `Eve.Services.Space`, `Eve.Ships`, and `Eve.Space` completed with 0 warnings and 0 errors.
- `Eve.Services.Space.Tests`: 1,243 passed, 0 failed, 0 skipped.
- `Eve.Services.Crimewatch.Tests`: 64 passed, 0 failed, 0 skipped.
- `Eve.Space.Tests`: 563 passed, 0 failed, 0 skipped.
- `Eve.Ships.Tests`: 257 passed, 0 failed, 0 skipped.
- Full `Eve.Host.Tests`: 1,380 passed, 0 failed, 25 skipped. These existing skips require `EVE_ABYSSAL_SIM_BINARY` and `EVE_ABYSSAL_SIM_SDE`; they were not counted as passes or changed. The combat-related `CharacterHostTests.A_player_ship_death_creates_a_persistent_victim_kill_report_notification` was rerun with the Release `eve-sim`, pinned build 3396210 SDE, and `eve_p4`: 1 passed, 0 failed, 0 skipped.

These are headless test and build checks. The unmodified graphical V24.01 client was not launched, and the complete EveJS network/database server flows were not executed end to end. The client callsites and EveJS reference paths above were inspected in the earlier audit record; a focused EveJS smartbomb-cycle invocation is recorded there. No behavior is claimed as graphically verified.

## SimLink fixture generator gap found while landing

The CI job `Fixtures match their generators` in `.github/workflows/ci.yml:331-346` runs `tools/dev/gen-simlink-fixtures.py` and `tools/dev/gen-hostlink-fixtures.py`, then requires `git diff --exit-code -- oracle/`. On the combat-audit branch, the committed `SmartbombHit` valid and truncated fixtures were present, but the SimLink generator had no kind mapping, encoder branch, or matching cases. Before the fix it regenerated 308 valid and 139 invalid cases instead of the committed 309 and 140; the only missing names were `a smartbomb reports each player ship it hit` and `smartbomb hit promises a target whose owner record is truncated`.

The wire shape is grounded in ADR 0007's SimLink frame (`u32 length | u8 kind | body`, little endian) and the checked-in implementations: Rust kind 164 is `SMARTBOMB_HIT` (`crates/eve-space/src/link/kind.rs:23-24`), and its payload is `sessionId u64, shipId i64, moduleId i64, count u32`, followed by each target's `shipId i64, characterId i64, typeId i32` (`crates/eve-space/src/link/payload.rs:1820-1829`). Rust decodes the same 20-byte target record and refuses a count that cannot fit (`decode_message.rs:2571-2581`). C# writes those same fields (`dotnet/src/Eve.Space/SimCodec.Encode.cs:2266-2277`) and applies the same 20-byte truncation bound (`SimCodec.cs:988-1000`). The generator now emits the two fixture scenarios from field values and `struct.pack`; the truncated scenario contains three of the four type-id bytes for its single promised target, so both decoders classify it as `truncated`.

This is a Host-to-simulation internal message, not a new EVE client RPC. The V24.01 client call path is the generic offensive-module path: `eve/client/script/ui/inflight/shipModuleButton/shipmodulebutton.py:1333-1354` performs the offensive confirmation then activates the effect, and `eve/client/script/environment/godma.py:976-985` dispatches that activation. The client restores Crimewatch timer state on session changes and stores timer updates (`crimewatchSvc.py:94-105,222-250`); `crimewatch/crimewatchTimers.py:347-370` displays the weapons timer. A source-wide scan of the client found no `SmartbombHit` protocol consumer. EveJS v0.12.9 evaluates the whole blast set and records each offense before applying damage (`server/src/space/modules/smartbombRuntime.js:211-338`); the callback delegates to Crimewatch (`server/src/space/runtime/moduleRuntimeCallbacks.js:462-477`), with the initial and later cycles wired from `scene/modules.js:3265-3286` and `scene/tick.js:1643-1654`. This matches the server-owned target list and timer consequences recorded above.

The Rust `simlink_fixtures` replay and .NET `Eve.Space.Tests.SimCodecTests` replay the same generated valid frame, so each encoder must match the committed bytes; both suites also check invalid fixture error reasons. Rust's SmartbombHit wire model, encoder/decoder and fixture replay were already committed with `e02c2f40a`; this landing fix needed only the Python generator and oracle fixture update.

### Landing-fix verification

The CI fixture job in `.github/workflows/ci.yml:331-346` was reproduced locally:

- `python3 tools/dev/gen-simlink-fixtures.py`: 309 valid and 140 invalid fixtures.
- `python3 tools/dev/gen-hostlink-fixtures.py`: 14 valid and 10 invalid fixtures.
- `git diff --exit-code -- oracle/`: passed after both generators.

The committed Rust and C# codecs agree with the shared SmartbombHit fixture bytes. Rust `simlink_fixtures` verifies every valid fixture's encoded bytes and every invalid fixture's error reason; .NET `SimCodecTests` replays the same fixture file.

Rust, using toolchain 1.94.1:

- `cargo fmt --all` and `cargo check -p eve-space --release` passed.
- `cargo clippy --workspace --all-targets -- -D warnings` passed.
- `cargo test -p eve-space --release`: 1,027 unit tests and 40 integration/reference tests passed; 0 failures and 0 ignored tests. The `simlink_fixtures` target passed 13/13.
- `cargo build -p eve-sim --release` passed for the Host test that launches the headless simulation.

.NET Release builds passed with 0 warnings and 0 errors for `Eve.Space`, `Eve.Services.Crimewatch`, `Eve.Services.Space`, `Eve.Ships`, and `Eve.Host`. The test projects were built with warnings treated as errors. Focused test results, all on `eve_p3`, were:

- `Eve.Space.Tests.SimCodecTests`: 498 passed, 0 failed, 0 skipped.
- Combat-focused `Eve.Services.Space.Tests` filters (`SmartbombHitHostTests`, `EcmBurstHostTests`, `CombatLogoffRestoreTests`, `SmartbombSafetyHostTests`, `WeaponHostTests`, and three relevant `SpaceServiceTests`): 56 passed, 0 failed, 0 skipped.
- `Eve.Services.Crimewatch.Tests.RuntimeTests`: 42 passed, 0 failed, 0 skipped.
- `Eve.Ships.Tests.DeathTests`: 17 passed, 0 failed, 0 skipped.
- `Eve.Host.Tests.ConcordResponseHostTests`: 1 passed, 0 failed, 0 skipped.
- `Eve.Host.Tests.CharacterHostTests.A_player_ship_death_creates_a_persistent_victim_kill_report_notification`: 1 passed, 0 failed, 0 skipped, using the Release `eve-sim` and pinned client SDE.

The first Space test attempt exposed that `eve_p3` had no SDE data; after loading client SDE build 3396210, the same 56 selected tests passed. The Host killmail test initially skipped without its simulation environment and then failed at startup because `sde_mission` was absent. Loading the pinned mission build 3405148 and rerunning with `EVE_ABYSSAL_SIM_BINARY` and `EVE_ABYSSAL_SIM_SDE` produced the passing result above. These setup attempts are not counted as passes; the final selected runs had no skips.

The source-wide search of `/home/user/eve-logic` found no `SmartbombHit` client consumer. The checked client behavior remains the generic module activation path (`shipmodulebutton.py:1333-1354`, `godma.py:976-985,2049-2067`) and Crimewatch timer display/update (`crimewatchSvc.py:222-253,327-329`, `crimewatchTimers.py:347-393`). The real graphical V24.01 client and the complete EveJS network/database server flow were not run for this landing fix.
