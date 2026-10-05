# Ship Operations and Module Coverage Handoff

**Updated:** 2026-10-05

**Scope:** EVE University Wiki module families, player ship operations, current eve.rs Host/C#/Rust paths, and the next bounded implementation slices.

This is a coverage inventory, not a completion checklist. Every family in the [Fitting Modules and Rigs Guide](https://wiki.eveuniversity.org/Fitting_Modules_and_Rigs_Guide) is retained below. “Handler missing” means no matching active effect/action path was found in the current code. It does **not** mean the item type is absent from the SDE. Mill supplied the SDE cache location and a PG18 import report below; per-family type/effect/attribute and fitting restriction joins remain to be checked before implementation. Belfast is not an inspection target.

## Existing execution path

The client sends module operations through the existing `dogmaIM` service. Host `ModulePackages.Of` resolves the requested SDE effect, validates the fitted module and online state, and creates the simulation package. An effect with no registered handler is refused as `UNSUPPORTED_EFFECT`. The registered package families are propulsion, turret, missile, repair, projected, smartbomb, ECM burst, and cloak ([ModuleEffects.cs](../dotnet/src/Eve.Services.Space/Effects/ModuleEffects.cs), [ModulePackages.cs](../dotnet/src/Eve.Services.Space/ModulePackages.cs)).

The Rust `Action` union currently carries Repair, Remote, Smartbomb, ECM Burst, Cloak, and Charged behavior. Turret shots use the separate `SimTurret` path. DogmaIM also routes module activation/deactivation, online/offline, and rack overload; the simulation supports module cycles and heat ([modules.rs](../crates/eve-space/src/modules.rs), [SimMessages.cs](../dotnet/src/Eve.Space/SimMessages.cs), [DogmaIMService.Modules.cs](../dotnet/src/Eve.Services.Dogma/DogmaIMService.Modules.cs)).

Current working paths confirmed in code:

- **Implemented active families:** afterburner and microwarpdrive; ordinary energy/projectile/hybrid turret firing; ordinary missile launchers; local armor/shield/hull and ancillary repair; remote repair, capacitor transfer, neutralizer, nosferatu, web, painter, dampener, scrambler/disruptor, tracking/guidance disruption, ECM; capacitor boosters; smartbomb; ECM burst; generic cloak.
- **Safety distinction:** Host classifies the listed hostile Remote effects and turret shots for weapon safety and calls the existing Crimewatch aggression callback after an accepted hostile activation ([SpaceSessions.cs](../dotnet/src/Eve.Services.Space/SpaceSessions.cs)).
- **Fitting:** `FittingRules` checks slots, hardpoints, CPU/power, skill requirements, and SDE-provided ship-group/type restriction attributes ([FittingRules.cs](../dotnet/src/Eve.Fitting/FittingRules.cs)). The numeric restriction attributes have not been joined to the current SDE in this handoff.
- **Dogma/charge:** `DogmaWorld` supports item modifiers and resolves a loaded charge/module pair through `otherID`; generic ammo loading uses the DogmaIM inventory path ([DogmaWorld.cs](../dotnet/src/Eve.Dogma/DogmaWorld.cs), [DogmaIMService.Ammo.cs](../dotnet/src/Eve.Services.Dogma/DogmaIMService.Ammo.cs)). This is a foundation, not proof that every scripted module works in space.

## Complete Wiki family inventory

| Slot/category | Wiki families retained | Current eve.rs status |
|---|---|---|
| **High — Damage** | Bomb Launchers; Doomsday Devices; Hybrid Turrets (Railguns, Blasters); Energy Turrets (Pulse Lasers, Beam Lasers); Projectile Turrets (Autocannons, Artillery); Missile Launchers (short-range, long-range, rapid); Smartbombs | Ordinary turrets, ordinary missiles, and Smartbombs have active paths. Bombs, Doomsday, Vorton/disintegrator/barrage-specific effects, and structure launchers have no confirmed active path. `TurretEffect` explicitly defers mining lasers, disintegrators, and barrage; `MissileEffect` defers defender missiles, guided bombs, and structure launchers. |
| **High — Drone Upgrades** | Drone Control Units; Drone Link Augmentors | Drone launch, engage, return-bay, abandon, scoop, and combat paths exist. Additional control capacity/range from fitted upgrade modules is not verified. |
| **High — Logistics** | Remote Capacitor Transmitters; Remote Armor Repairers; Remote Hull Repairers; Remote Shield Boosters | Active Remote transfer and repair actions exist. Per-type restrictions and all assistance/Crimewatch interactions still need contract tests. |
| **High — Tackling** | Interdiction Sphere Launchers; Warp Disruption Field Generators | Targeted warp scramble/disrupt is implemented. Sphere/bubble entities, deployment, expiry, and warp-volume intersection are not represented by the current targeted action. |
| **High — Mining** | Mining Lasers; Strip Miners; Ice Harvesters; Gas Cloud Harvesters | No mining-laser module effect path was found. Mining target entities, extraction/depletion, charge consumption, and ledger production remain unverified. |
| **High — Siege** | Bastion; Siege; Triage; Industrial Core | No active mode handler was found. Mode timing, hull restrictions, mobility/warp lock, combat/repair changes, assistance blocking, and fuel requirements need SDE-backed behavior. |
| **High — Misc** | Auto Targeting; Cynosural Field Generators; Cloaking Devices; Clone Vat Bay; Fleet Assistance/Command Bursts; Jump Portal Generators; Salvager; Scan Probe Launchers; Tractor Beams | Cloak has a generic active path, but normal/prototype/covert differences are not modeled. The other families have no confirmed complete Host-to-simulation path. |
| **Mid — Capacitor** | Capacitor Batteries; Capacitor Boosters; Capacitor Rechargers | Capacitor Booster has a C# charge path. Passive battery/recharger behavior has not been verified by type. |
| **Mid — Damage Supplements** | Tracking Computers; Missile Guidance Computers | No complete active/scripted operation path was verified. |
| **Mid — Drone Upgrades** | Omnidirectional Tracking Links; Drone Navigation Computers | Fitted-module bonuses to drone range/tracking/navigation have not been verified. |
| **Mid — EWAR** | ECM; ECM Bursts; Guidance Disruptors; Remote Sensor Boosters; Remote Sensor Dampeners; Sensor Boosters; Target Painters; Tracking Disruptors | ECM/ECM Burst, guidance/tracking disruption, dampening, painter, web, and tackle paths exist. Remote Sensor Booster and local Sensor Booster behavior are not complete. |
| **Mid — Propulsion** | Afterburners; Microwarpdrives; Micro Jump Drives | Afterburner/MWD are implemented. MJD spool, jump, interruption, and cooldown are not. |
| **Mid — Shield Tanking** | Shield Boosters; Ancillary Shield Boosters; Shield Boost Amplifiers; Shield Extenders; Shield Hardeners; Shield Rechargers; Shield Resistance Amplifiers | Shield repair and ancillary repair package paths exist. Remaining active/passive attributes are not verified category-wide. |
| **Mid — Tackling** | Stasis Webifiers; Warp Scramblers; Warp Disruptors | Targeted web/scramble/disrupt paths exist. |
| **Mid — Misc** | Hull Repair Systems; Passive Targeting Systems; Scanners; Data Analyzers; Relic Analyzers | Hull repair has a local repair path. Passive targeting, scan/probe, analyzer, and hacking interactions need their own entity/client contracts. |
| **Low — Armor Tank** | Armor Hardeners; Armor Plates; Armor Repair Systems; Energized Armor Resistance Membranes; Resistance Plating | Armor repair is implemented. Armor buffer/resistance families are not verified exhaustively. |
| **Low — Capacitor** | Capacitor Flux Coils; Capacitor Power Relays | Passive Dogma stat effects are not verified per family. |
| **Low — Damage Supplements** | Ballistic Control Systems; Gyrostabilizers; Heat Sinks; Magnetic Field Stabilizers; Tracking Enhancers | Dogma and stacking effects need current-SDE-backed per-family checks. |
| **Low — EWAR** | Signal Amplifiers; Sensor Backup Arrays | Passive targeting/sensor attributes are not verified per family. |
| **Low — Fitting** | Auxiliary Power Controls; CPU Upgrades; Power Diagnostic Systems; Reactor Controls | Fitting resource validation exists; all live Dogma attribute effects are not verified. |
| **Low — Propulsion** | Inertial Stabilizers; Nanofiber Internal Structures; Overdrive Injectors; Hyperspatial Accelerators | Agility/speed/warp-speed attribute effects need per-family verification. |
| **Low — Shield Tanking** | Shield Flux Coils; Shield Power Relays | Passive Dogma stat effects are not verified per family. |
| **Low — Misc** | Cargo Expanders; Damage Controls; Ice Harvester Upgrades; Mining Upgrades; Reinforced Bulkheads; Warp Core Stabilizers | Cargo, resist, mining-yield, hull, and warp-core attribute effects need per-family verification. |
| **Rigs** | All rig-slot families, attribute bonuses, calibration/size rules, and penalties | Fitting infrastructure exists; the complete SDE effect/penalty catalog and live behavior are unverified. |
| **Subsystems** | Strategic Cruiser Core, Defensive, Offensive, and Propulsion subsystems | Slot/fitting and post-fit attribute behavior are not verified against the current SDE. |

Passive rows are not automatically “unsupported”: Dogma has a general modifier model. Their per-item and runtime coverage is simply not established here. The Wiki family remains listed until current-SDE data and tests establish otherwise.

## Special-operation contracts and required entities

### Remote Sensor Booster

The existing `Remote.Damp` mathematics can apply positive percentages to targeting range and scan resolution: the Rust stacking function separates positive and negative modifiers, and `Incoming.worked` multiplies both attributes by their factors. However, the current Host marks every `Remote.Damp` as hostile. Reusing it unchanged would route a positive boost through weapon-safety and `AggressionRequest`/Crimewatch. The existing Rust `ewar::Base` also has no four-way sensor-strength fields, while EVEJS Remote Sensor Boosters apply range, scan resolution, and all four sensor-strength bonuses.

Contract: existing `dogmaIM.Activate` RPC; SDE effect name `remoteSensorBoostFalloff`; `EffectDef.Id` and `EffectDef.Guid` from SDE; client `jammingType` `remoteSensorBoost`; source attributes for max target range, scan resolution, and gravimetric/ladar/magnetometric/radar sensor-strength percentages; cycle/range/falloff values from the effect/module. Assistance needs a separate Host classification, ship-target validation, active `disallowAssistance` rules, and cycle refresh/expiry. Do not report two range/scan bonuses as full parity.

Small first C# boundary: `ProjectedEffect.cs`, `ModuleAttributes.cs`, `SpaceSessions.cs`, focused Host/Space tests. Full sensor-strength parity additionally needs the EWAR/dogma/ship payload path, after Kepler finishes the current Rust module work. Wiki references: [Electronic Warfare](https://wiki.eveuniversity.org/Electronic_Warfare), [Scripts](https://wiki.eveuniversity.org/Scripts); pinned [EVEJS assistance runtime](https://github.com/Fokiiiiiii/evejs-v0.12.9/blob/828823a75f211fdecb945b087a26c225f111491f/server/src/space/modules/assistanceModuleRuntime.js).

Client-source check: [`eve-logic` at `1dbb571`](https://github.com/Fokiiiiiii/eve-logic/tree/1dbb5711a072a146e1d228365668ac05655eefe1) maps effect `6427` to `buffBarConst.Slot_SensorBooster` in [`dogma/effects/__init__.py`](https://github.com/Fokiiiiiii/eve-logic/blob/1dbb5711a072a146e1d228365668ac05655eefe1/dogma/effects/__init__.py); [`dogma/const.py`](https://github.com/Fokiiiiiii/eve-logic/blob/1dbb5711a072a146e1d228365668ac05655eefe1/dogma/const.py) defines `disallowAssistance=854`, `maxTargetRangeBonus=309`, and `scanResolutionBonus=566`. `dogmaim.py` in this snapshot only exposes the service start/accessor, so it does not confirm the RPC implementation details.

### Cloak, MJD/MJFG, bombs, Defender and Point Defense

- **Cloak:** C# recognizes `cloaking`, `cloakingWarpSafe`, and `cloakingPrototype`, but all emit one generic Cloak action. Rust has a single generic cloaked state. Covert warp permission, hull fitting restrictions, decloak triggers, and reactivation delay need distinct verified state/attributes.
- **MJD:** Single-ship spool, direction, jump distance, scramble interruption, cooldown, and failed-jump timing. Requires the module's current SDE range/duration/cooldown and hull constraints; it does not require a fleet recipient entity.
- **MJFG:** In addition to spool/jump, needs a neighborhood query and passenger eligibility/caps for nearby ships and movable drones/fighters/bombs/probes. The Wiki describes a 6 km collection field and up to 25 ships; verify current SDE and client release behavior before implementation. [Micro Jump Drive and Field Generator](https://wiki.eveuniversity.org/Micro_Jump_Drive_Field_Generator)
- **Bomb:** New flying bomb entity with heading, speed/flight time, terminal AoE, friendly-fire legality, and in-flight interception. Wiki describes a 30 km flight and 15 km blast radius; bombs are not Smartbomb cycles. [Bombs](https://wiki.eveuniversity.org/Bombs)
- **Defender/Point Defense:** Rust has interception math and generic missile-damage/intercept entry points, but no complete module action or structure defense loop. Point Defense needs a current SDE structure battery/effect and a structure entity. Defender needs ship module/charge activation, bomb acquisition, charge/cooldown state, and a simulation action. Kepler's final audit determines the final entity boundary. [Missiles/Defender](https://wiki.eveuniversity.org/Missiles)

### Siege, Triage, Bastion, Industrial Core, and Command Bursts

Mode changes need a restart/expiry model consistent with cycle state; they alter movement/warp, offensive/repair values, and remote-assistance eligibility. Exact hull/fuel requirements must come from the current SDE, not guessed type IDs. Command Bursts additionally need fleet membership, in-range recipients, charge-family/Dogma collection modifiers, stacking, expiration, and refresh. The Wiki groups them into Armor, Information, Mining Foreman, Shield, Skirmish, and Expedition bursts, each with charge-specific effects. [Bastion](https://wiki.eveuniversity.org/Bastion_Module), [Triage](https://wiki.eveuniversity.org/Triage_module), [Command Bursts](https://wiki.eveuniversity.org/Command_Burst).

### Cyno, jump portals, tractor/salvage, mining, scan and hacking

- **Cyno / jump portal / Clone Vat Bay:** a stargate transition is not equivalent. Cyno needs a discoverable system beacon/field, fuel, activation lifetime, and jump eligibility; portals need multiple session transfers and ship eligibility/access; Clone Vat Bay needs character clone state.
- **Tractor Beam:** needs container/wreck entities, local interaction, loot-right checks, surface range and velocity, and persistent motion. [EVEJS tractor runtime](https://github.com/Fokiiiiiii/evejs-v0.12.9/blob/828823a75f211fdecb945b087a26c225f111491f/server/src/space/modules/tractorBeamRuntime.js)
- **Salvager:** needs locked, salvageable wreck state, salvage chance/skill/faction pool, inventory custody/reward, and atomic wreck removal/replacement. [Salvaging](https://wiki.eveuniversity.org/Salvaging) · [EVEJS salvage runtime](https://github.com/Fokiiiiiii/evejs-v0.12.9/blob/828823a75f211fdecb945b087a26c225f111491f/server/src/space/modules/salvagerRuntime.js)
- **Mining:** needs ore/ice/gas entities, depletion, per-cycle charge/yield, and ledger custody. Probe/scanner/analyzer/hacking require signatures, sites, probes, containers, and client interaction state.
- **Bubbles:** sphere launchers and Warp Disruption Field Generators require a persistent area entity with warp-path intersection and expiry; a targeted warp scrambler does not supply this.

## Ship-operation coverage from the current Mill report

The following are retained as **reported/pending**, not independently accepted as complete. Add the exact RPC, source entry point, persistence/simulation owner, and test result when Mill hands off:

- Autopilot: worker reports implementation and focused checks; slice was committed/pushed in `770306a4230346072689b2fae3326bbc1ec56c3f`. CI run `37196142757` passed all six jobs; the final .NET test total is pending parent log review.
- Bookmark/point-warp.
- Board, eject, leave ship, store ship, safe logoff.
- Abandon loot.
- Special bays and drop operations.
- Drone reconnect, disabled-drone scoop, fighters.
- Multi-fit.
- Fleet warp and formation.
- Jump drive and cynosural travel.

### Drone Mine/Salvage discrepancy

Mill reported Drone Mine/Salvage as connected; the current service entry point contradicts that report. `Program.cs` registers `EntityService`; its `CmdMineRepeatedly` and `CmdSalvage` methods currently return the `NotServed` refusal. Treat those RPCs as **not implemented** until the actual client invocation path is changed and tests prove success. This finding is limited to the drone RPCs; it does not make a claim about a separate mining-laser module operation. `CmdReconnectToDrones` currently returns an empty result because released drones leave the scene; fighters and disabled-drone scoop need separate call-path confirmation.

## Worker-reported results and data checkpoint — 2026-10-04

The following are Mill/Kepler reports supplied to the coordinator. They are focused-slice evidence, not the final integrated CI or complete client acceptance.

### SDE cache and isolated PG18 import

- Cache: `C:\Users\yutai\eve.rs\.cache\sde\eve-online-static-data-3396210-jsonl`; Mill reports `.complete` present, build `3396210`.
- Import command: `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`.
- Mill reports importing 61 tables / 668,618 rows to dedicated PostgreSQL 18.6 database `eve_autopilot_validation_20261004` for the Autopilot slice. Worker reports both temporary validation databases were dropped after use, with active connection count 0 and database existence count 0 for each. Belfast was not connected.
- Current-SDE per-family item/effect/attribute joins are still pending. Use the supplied cache and a dedicated validation database for those reads; do not infer absent items from missing handlers.

### Autopilot warp

Mill reports `CmdWarpToStuffAutopilot` implemented in the existing Beyonce service path and focused-tested. The current method reads `arg0` as the destination, uses 10,000 m arrival range, and delegates to the existing `WarpAsync` path ([BeyonceService.cs](../dotnet/src/Eve.Services.Space/BeyonceService.cs), [SpaceServiceTests.cs](../dotnet/tests/Eve.Services.Space.Tests/SpaceServiceTests.cs)). The reported reference comparison used EVE University [Advanced Piloting Techniques](https://wiki.eveuniversity.org/Advanced_Piloting_Techniques), pinned EVEJS, and the eve-logic `autopilot.py` client path.

- Worker-reported focused .NET result: 1/1, skip 0, exit 0, after Kepler's C# test process had finished. Worker also reports a dedicated PG18.6 acceptance test 1/1, skip 0, exit 0.
- Worker reports the slice touched the service and focused-test files above and was committed/pushed in `770306a4230346072689b2fae3326bbc1ec56c3f` after the heat change `66d8380`.

### Kepler heat/ammunition focused repair

- Heat: worker reports suppressing the initial zero-value notification and replacing a fixed 5 ms timing window with a deterministic queue/barrier regression. Rust heat tests reported 1/1 and overheat tests 4/4; `cargo fmt` and `cargo clippy -- -D warnings` succeeded. Attribute restoration was covered, and the C# test process exited with no remaining process.
- Ammunition: worker reports `writer.Written` values 79/78 are valid database write-history counts; the behavioral boundary is that the final 78 durable writes complete before docking. They are not to be interpreted as a mismatch in the client-facing operation count.
- C# Ammo/Queue focused tests reported 6/6, skip 0. Do not label this as a 6/6 heat result.
- These are worker-reported focused checks. CI run `37196142757` passed; the parent is still reviewing the .NET job log for its exact suite count, recorded separately below.

## Ordered next slices and acceptance

On resume, read the S5 service backlog in [PLAN.md](PLAN.md) and this handoff first. Wiki is the primary feature/category specification; use pinned EVE.js behavior and `eve-logic` client RPC/wire contracts next, then verify numerical attributes, type availability, and fitting restrictions from the current SDE. Each slice should stay small, add a focused executable test, and be integrated before marking complete.

1. **Release blockers and ownership handoff:** Kepler's committed Rust/module changes and coordinator's final .NET suite-count review. Mill's Autopilot slice is committed. The six-job CI run is green; confirm the .NET suite total from the job log. Use the dedicated PG18.6 validation target only; do not connect to Belfast. Record exact root SHA and test commands in the verification table below.
2. **Generic assisted-effect legality:** add the assistance-vs-hostile classification and target support policy around `SpaceSessions` before adding more assistance modules. Tests: positive boost does not call weapon aggression; negative damp remains hostile; `disallowAssistance`/range/expiry behavior matches the reference.
3. **Remote Sensor Booster:** implement the SDE-backed effect name/attributes and `remoteSensorBoost` wire tag; first prove max target range/scan resolution positive stacking and expiry. Add all four sensor strengths only with the matching Dogma/EWAR state path. Acceptance must distinguish the partial two-stat slice from full parity.
4. **Single-ship Micro Jump Drive:** after the Rust module file is available, add spool/jump/interrupt/cooldown using existing ship heading and movement. Test scram interruption, facing/distance, failed-cycle expiry, and compatible hulls from SDE.
5. **Defender and Point Defense:** proceed only after Kepler's entity finding. Keep ship-mounted Defender and structure-mounted Point Defense separate if the SDE/client entity types differ. Test bomb targeting/interception, charges, range, cooldown, and structure locality.
6. **Bomb entities, then field effects:** add bomb flight/AoE/interception before MJFG/bubbles; these depend on local entity queries and area-state expiry. Keep current-SDE ship/structure restrictions as data-driven checks.
7. **Fleet/system modes:** siege/triage/bastion/industrial cores, Command Bursts, cyno/portal/clone transitions. These depend on hull constraints, fleet membership, system routing, timers, and remote-assistance rules.
8. **World-object operations:** tractor, salvage, mining, probes/analyzers/hacking, fighters and remaining drone orders. Each requires its own authoritative entity, ownership/custody, and client result shape; do not accept a service stub as an implemented operation.
9. **Passive/fit families:** validate low/mid passive modules, rigs, and subsystems by current SDE effect/modifier rows and focused dogma/fitting tests. Keep the Wiki family list intact until each group has test evidence.

### Verification checkpoint — 2026-10-04

| Gate | Result |
|---|---|
| Coordinator-confirmed source commits | `66d8380` (heat/Ammo regression), then `770306a4230346072689b2fae3326bbc1ec56c3f` (Autopilot); resume from `770306a4230346072689b2fae3326bbc1ec56c3f` |
| Root SHA used for final CI | `770306a4230346072689b2fae3326bbc1ec56c3f` |
| SDK / database | .NET SDK 10.0.401; PostgreSQL 18.6. Exact .NET suite count pending parent log review |
| SDE cache | `C:\Users\yutai\eve.rs\.cache\sde\eve-online-static-data-3396210-jsonl` (`.complete`, build 3396210); dedicated import DB was dropped after use |
| Mill Autopilot focused slice | Worker-reported .NET 1/1 and PG18.6 1/1, both skip 0, exit 0; commit/push reported |
| Kepler heat/ammunition focused slice | Worker-reported Rust heat 1/1, overheat 4/4, fmt/clippy clean, and C# Ammo/Queue 6/6 skip 0; commit/push reported |
| Final .NET suite total | [CI run 37196142757](https://github.com/Fokiiiiiii/eve.rs/actions/runs/37196142757), .NET job 111419153202: 34 test assemblies, 3,233 passed, 0 failed, 0 skipped (job succeeded) |
| Post-push CI | [Run 37196142757](https://github.com/Fokiiiiiii/eve.rs/actions/runs/37196142757): all six jobs completed successfully — .NET build/test, Rust fmt/clippy/test, latest Rust advisory, fixture/generator match, Python client golden/mock, and end-to-end server stack/headless client |
| Live client acceptance | Not recorded for this handoff |

### Implementation batch — 2026-10-05

The current working tree adds or completes these behavior slices:

- Assistance legality distinguishes positive `remoteSensorBoost` from hostile effects, checks target `disallowAssistance` (`854`), and preserves aggression handling for negative or mixed `Damp` effects.
- Remote Sensor Boost now applies targeting range (`309`), scan resolution (`566`), and all four sensor-strength channels (`238`–`241`) through the C#/Rust SimLink payload. Stacking, falloff, refresh, expiry, Dogma updates, and ECM strength use the existing EWAR path. Local `sensorBoost` uses the same modifier path for stacking and expiry.
- Micro Jump Drive supports SDE-backed range/cooldown, spool, heading-based movement, scram interruption, repeat refusal, and cooldown after an interrupted spool.
- Ship-to-ship and ship-to-static-body bumps resolve overlap and one-tick swept contacts, update position/velocity/heading, and notify observers. Swept paths use a linear segment and resolve at most one swept impact per ship per settlement; same-tick curved paths and multiple impacts remain unmodeled.
- Fitting checks subsystem slot roles. Rig size and calibration behavior already existed; focused current-SDE regression coverage was added. Dogma tests now cover existing stacked turret and missile damage amplifiers without adding a second damage multiplier.
- Docked `MultiAdd` supports client AutoFit. Drone damage amplification, Drone Link Augmentor control range (attribute `459`), and docked Drone Bay storage/retrieval and capacity are connected to their runtime paths.
- `Board` and `LeaveShip` use the client sequence: `LeaveShip` prepares/returns a capsule ID; the following `Board(capsuleID, currentShipID)` changes the active ship. `CmdAbandonLoot` clears loot rights while leaving the contents in place.
- Bookmark RPC handling accepts `CmdWarpToStuff("bookmark", bookmarkID, minRange)`, but bookmark coordinates are not yet resolved by the simulation. Drone Control Unit types are absent from the checked SDE build, so no guessed types or effects were added.

### Verification checkpoint — 2026-10-05

| Gate | Result |
|---|---|
| `cargo fmt --all --check` | Exit 0 |
| `cargo test -p eve-space --quiet` | Exit 0; 478 unit tests and all integration test binaries passed |
| `dotnet build .\dotnet\src\Eve.Host\Eve.Host.csproj --no-restore --verbosity minimal` | Exit 0; 0 warnings, 0 errors |
| Focused .NET suites | Exit 0 across `Eve.Space.Tests` (136 passed), `Eve.Fitting.Tests` (47 passed, 3 skipped), `Eve.Services.Dogma.Tests` (70 passed, 47 skipped), `Eve.Services.Inventory.Tests` (7 passed, 21 skipped), `Eve.Services.Space.Tests` (40 passed, 173 skipped), `Eve.Dogma.Tests` (51 passed, 4 skipped), and `Eve.Npc.Tests` (152 passed, 14 skipped) |
| PostgreSQL-backed tests | Skipped where gated because `EVE_PG_URL` is not set; no database connection was made |
| SimLink fixture generator | Exit 0; generated 99 valid and 10 invalid frames |
| Live client acceptance | Not performed |
