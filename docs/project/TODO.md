# Current TODOs

**Updated:** 2026-10-09
**Baseline:** main source commit `7379a70fdeee0e93d2ac38c1b7a7bd9ecd29da60`

This list distinguishes a real behavior/provider gap from an intentionally empty default and from a method whose route already exists. These items are not missing-route work.

## Status meanings

- **OPEN** — EveJS has an operation or live data path, but eve.rs has no real producer/provider connected.
- **FUTURE** — no production data source is present in the current eve.rs runtime or the relevant default path; keep the hook and compatible empty/error behavior, and connect only when a real source exists.
- **LIMITATION** — existing persisted rows lack enough source data to reconstruct their original values.

Do not fabricate successful state or fake backends to close these items.

## 1. Runtime state and notifications

| Status | Service/method | Missing work |
|---|---|---|
| OPEN | `machoNet.SetGlobalConfigValue` | Connect the runtime global-config mutation and its client notification through a real configuration owner. The Host currently constructs the service without that callback. |
| OPEN | `insurgencySolarsystem.GetAllVisibleCampaigns`; `GetLocalCampaignClientSnapshot` | Connect a campaign snapshot producer equivalent to EveJS's Insurgency runtime controller. The route and callback boundary exist, but no campaign producer was found in `dotnet/src`. |
| OPEN | `essMgr.GetDataForClientSolarSystem`; `GetMainBankTheftsForClientSolarSystem`; `GetReserveBankTheftsForClientSolarSystem`; `RequestMainBankUnlink`; `RequestReserveBankUnlink` | Connect the real ESS state and operation backend to `ReadSystemStateAsync` and `ExecuteAsync`. Do not synthesize thefts or unlink success. |
| OPEN | `abyssalMgr.ClientIsReady` | Connect the runtime client-ready path and automatic trace activation. No Abyssal runtime provider was found in the current C# source. |
| OPEN | `scanMgr.SignalTrackerRegister` | The Host can record the character/session/system registration and has a full-state publish hook. The live signal snapshot producer and the code that calls the publisher are still absent. |
| OPEN | `posMgr.GetControlTowers` | Connect rows from a real starbase/tower data source to `ReadControlTowerRowsCallback`. The route is present; no current tower provider was found. |
| OPEN (audit side effect) | publicQaToolsServer.MoveMeTo | EveJS records a move_me_to QA event and returns null. RS returns None with matching wire shape, but has no QA event recorder. Connect a real audit provider only if one becomes available. |
| OPEN | `contractProxy.GetMyContractEscrow`; `expertSystemMgr.GetMyExpertSystems` | The startup RPCs return EveJS-compatible empty-state shapes, but contract records and expert-system ownership are not persisted. Connect the authoritative read paths when those domains are added. |
| OPEN | `raffleProxy.AmIBanned` | The current false result matches the enabled-HyperNet default, but account bans are not persisted. Connect the moderation owner before supporting per-account bans. |
| FUTURE | `config.GetBlackListedPlanets` | The empty list matches EveJS; there is no runtime planet blocklist source. Connect one only if the server adds that policy. |
| LIMITATION | `home_station.get_home_station` | Reads the persisted home station and falls back to the current station. It does not resolve live structure homes or use the school's starting station when a home structure is lost, as EveJS does. Add an authoritative structure lookup and school-start resolver before claiming parity. |

## 2. Mutation authorities and specialized service providers

| Status | Service/method | Missing work |
|---|---|---|
| OPEN | `dynamicItemService.CreateDynamicItem` | Headless and PostgreSQL-tested, not yet live-verified. Done: the authority (`Eve.Inventory/Mutation/ItemMutationService.cs`: one transaction for consume, create, provenance in `inventory.item_mutations` (migration `0005_item_mutation`) and the `item.mutated` event through an injected port, typed refusals); the Host route (`DynamicItemRpcService`: `CreateDynamicItem` through the authority, `CustomNotify` refusals, `GetDynamicItemInfo` from stored provenance only, the `PyNone` stub and the customInfo path removed); one dogma factory for mutated modules, charges and drones (`Eve.Ships/ItemDogma.cs`, base type = input, skill requirements from the result, stored rolled values as own values); `OnItemChange` for the mutaplasmid, the source and the item made, after the commit and before the reply. Tests: `ItemMutationServiceTests`, `ItemMutationStoreTests`, `MutatedModuleTests` (fit, skill refusal and acceptance, rolled values, twin unchanged), `DynamicItemRpcServiceTests` (route, refusals, announcements in order). Still open: a live graphical-client run (crafting window, tooltips, how the client removes a consumed row, the place it expects for an item made); the drone launch path (base, rolls, launch, prime, recall, reconnect, restart) is covered headless by `MutatedDroneHostTests` and the route-to-launch harness `MutationHarnessTests`; `GetDynamicItemInfo` attributes are the item's own dogma without the ship's modifiers (LIMITATION, see DEVIATIONS 143); the decisions D3 (volume), D5 (fitted and damaged sources), D6 (rounding) and D7 (location). The skill-handler sites (implants, boosters) are not mutable (the mapping covers modules and drones only) and are not routed through the factory. |
| OPEN | `moonExtractions.StartNewExtraction`; `FractureChunkWithStructure`; `CancelExtraction` | Connect the missing extraction mutation authorities. Read-side extraction/event/resource methods already have GameStore data paths. |
| OPEN | `abyssalMgr.AbyssalGateActivation`; `AbyssalEndGateActivation`; `voidSpaceMgr.VoidSpaceEndGateActivation`; `VoidSpacePlayerJump` | The routes, callback hooks, and EveJS-compatible missing-provider error behavior are implemented. Connect the real gate/runtime authority before claiming successful gate operations. |
| OPEN | `structureCargoDelivery.DropOffItems` | Connect a real structure cargo-delivery authority. |
| OPEN | `structureCompressionMgr.CompressItemInStructure`; `DecompressGasInStructure`; `GetMyGasDecompressionEfficiency` | Connect structure compression and gas-efficiency data providers. |
| OPEN | `structureControl.GetStructurePilot`; `TakeControl`; `BoardStructure`; `EjectFromStructure`; `ReleaseControl` | Connect structure pilot/control state and mutations. |
| OPEN | `structureDeliveries.Deliver` | Connect the real structure delivery transaction. |
| OPEN | `structureDeployment.Anchor`; `CancelUnanchor`; `RenameStructure`; `Unanchor` | Connect structure anchoring/deployment operations and their authoritative state. |
| OPEN | `structureDocking.Dock`; `Undock` | Connect structure docking state and transitions. |
| OPEN | `structureVulnerability.UpdateReinforceTimesFromClient` | Connect the authoritative vulnerability schedule update. |
| OPEN | `eject.LaunchShipOrContainerFromWreckOrContainer` | Connect the Host callback to the real container launch operation. This is separate from the tested in-space ship Eject RPC. |
| OPEN | `inSpaceCompressionMgr.CompressItemInSpace` | No compression backend is attached (`Program.cs` passes none; owner C1 is open). The Host never answers `PyNone` here: every request it cannot serve is a named `CustomNotify` refusal. A valid request with no backend gets `UnavailableNotice`; a bad id, facility or session gets `InvalidRequestNotice`; an item not in the active ship gets `NotInActiveShipNotice`; a backend that returns no result gets `FailedNotice` (`OperationalRpcServices.cs`, pinned by `OperationalRpcServiceTests`). The V24.01 client (`itemcompression/client/itemCompression.py`) treats `None` as a failed compress with a generic message, so the old answer was not a success, but it gave no reason. Still to connect: the facility's compressible type list check, consuming the ore, creating the compressed output, the inventory sync, and the six-value result tuple. Real client behaviour for the refusal is unverified. |

The seven structure services above account for **17 methods**. `structureHangarViewMgr.GetMyHangarViewState` is connected and is not part of this TODO list.

## 3. Account, dungeon, and content data

| Status | Service/method | Missing work |
|---|---|---|
| OPEN | `userSvc.GetMultiCharactersTrainingSlots`; `GetUpdatedMultiCharacterTraining`; `ActivateMultiTraining`; `ActivateSoulboundMultiTraining` | Connect an authoritative multi-character-training slot/token provider. No matching MCT store/provider was found. `CreateRedeemTokenQA` is implemented separately. |
| FUTURE | `dungeonExplorationMgr.GetMyEscalatingPathDetails`; `keeper.ActivateAccelerationGate`; `keeper.GetLevelEditor`; `keeper.GetOperationSpawnpointSolarSystem`; `keeper.IsOperationSite` | Routes exist and the remaining callback defaults are in place. Connect real dungeon/operation data if a production provider is added. `keeper.GetCurrentDungeonForCharacter` already has a Host data callback. |
| FUTURE | `abyssal_content_manager.generate_abyss_npc_spawn_report` | The current empty report shape matches EveJS. Connect a real spawn-report producer if one is added; do not invent report rows. |
| FUTURE | `RWManager.get_closest_rw_stations`; `RWManager.solarsystem_contains_rw_instances`; `fwVictoryPointMgr.GetAllVictoryPointStates`; `fwVictoryPointMgr.GetLocalVictoryPointState` | No production state provider was found. Current empty/false results match the absence-data default; connect only when a real RW/Victory Point producer is introduced. |

## 4. Method-level compatibility evidence

The 484/484 route result is not a 484/484 wire-shape proof. CCP's public ESI material and the Wiki do not specify internal MachoNet `RemoteSvc` signatures. For every method, keep the evidence source explicit:

1. Use EveLogic/EveJS client code for argument order, response values, and observed side effects.
2. Use CCP/Wiki material for public gameplay rules where applicable, not as an RPC signature.
3. Mark a method **unknown** if source evidence does not establish its output fields or side effects.
4. If a durable per-method ledger is needed, add a source-backed `V/M/U` row for each manifest pair; `services.tsv` itself contains names and call counts only.

## 5. Known data limitation

**LIMITATION — Fleet ads created before migration 006.** The migration can only backfill fields present in the old schema. Original leader corporation/alliance/faction, solar system, advert update time, and membergroup allow/deny lists cannot be reconstructed from those rows. New advert writes persist the full payload. Revisit legacy rows only if an authoritative source for the missing fields becomes available.

## 6. Drone remote effects (implemented, not verified with the client)

Published drones that carry a non-damage effect (a stasis webifier, a neutralizer, an ECM jammer, a sensor dampener, a weapon disruptor, a target painter, a shield, armor or hull maintenance bot) are launched and run it at their target through the projected-effect rows of `ProjectedEffect` and the simulation's remote-effect machinery; a repair-only drone is sent at friendly ships only. Numbers come from each type's SDE dogma attributes (see the test comments in `DroneHostTests.cs`).

| Status | Scope | Remaining work / evidence |
|---|---|---|
| OPEN | Client verification | Covered by .NET host tests (`DroneHostTests`, 22 drone tests, real SDE types) and Rust sim tests (web, armor repair at cycle end, refusal of hostile targets); not yet run in the graphical client. EveJS v0.12.9 has no runtime for these effects (`droneDogma.js` resolves only `targetAttack`, `entityECMFalloff` and the three repair effects), so there is no executable reference for their HUD or timing. |
| OPEN | Hybrid drones (`-I`) | A hybrid carries a turret and an EW effect; they run on separate clocks. Their combined behaviour and the client's view of both are unverified. |
| LIMITATION | Recall mid-cycle | A repairer's cycle that has begun lands its mend when the drone stops (not at the cycle's end). Hold records of a destroyed drone lapse after their duration instead of being lifted at once. |
| OPEN | Mining and salvage drones | Mining drones now route through `MiningProfile` (`SpaceDrones.cs`, the same mining numbers a mining module uses; `MiningYield` and `MiningProfile` in `Eve.Ships`), covered by `MiningYieldTests` and `MiningProfileTests`. Not yet verified in the graphical client. |

## 7. Module effects wired, body pending (exploration, hacking, scanners)

Each effect below is **OPEN — module wired, effect body pending**. Its handler (`ExplorationModuleEffects.cs`) derives from `PendingModuleEffect` (`dotnet/src/Eve.Services.Space/Effects/PendingModuleEffect.cs`) and is registered in `ModuleEffects.Default`, so the module fits and onlines as usual. The effect stays listed by `Uncovered` (`Implemented` is false): a refusal is not coverage. An activation is refused with `EFFECT_NOT_IMPLEMENTED`, which the client shows as "X cannot be activated." (the same as `UNSUPPORTED_EFFECT`); no cycle, capacitor or success is reported.

**Extension point:** in the effect's handler class, override `Build` to return the `SimEffect` (and `Refusal` for its own checks). Read the attributes listed below on the module's values after dogma; `duration` (73) and `capacitorNeed` (6) are already read by `ModuleEffectContext.Cycle()`.

| Status | Module effect (handler, SDE types) | Missing work and attributes the body must read |
|---|---|---|
| OPEN | `doHacking`: `DataAnalyzerEffect` (23 types, 10 published) | The hacking site: `maxRange` 54 (the effect's range), `accessDifficultyBonus` 902, `virusCoherence` 1909, `virusStrength` 1910, `virusElementSlots` 1911. The hacking and archaeology skills reach the virus stats through dogma. Reference: EveJS `moduleOperation.js`, analyzer section (about lines 1273-1600). |
| OPEN | `hackOrbital`: `DataSubverterEffect` (Data Subverter I, unpublished) | No EveJS reference was found for this effect name. Specify it from the client before writing the body. |
| DONE (M-6) | `cargoScan`: `CargoScanEffect` (6 types, 4 published) | Built in track M-6, section 21: the hold reveal, `OnCargoScanComplete`, range 126 as a hard cutoff. |
| DONE (M-6) | `shipScan`: `ShipScanEffect` (7 types, 3 published) | Built in track M-6, section 21: the fitted reveal with the falloff of attribute 510, `OnShipScanCompleted`. |
| OPEN | `scanStrengthBonusPercentActivate`: `ScanStrengthActivateEffect` (44 types, none published) | The SDE effect carries four ship-scope `modifierInfo` entries (`scanRadarStrengthPercent` 1030 and siblings onto `scanRadarStrength` 208 and siblings, PostPercent). The body may be the existing `DogmaModifierEffect` path; decide that when it is written. |
| OPEN | `scanStrengthTargetPercentBonus`: `ScanStrengthTargetEffect` (7 types, none published) | A target effect: `maxRange` 54 and `falloff` 158, and four `targetID` modifiers (the same percent attributes). Reference: EveJS `space/modules/assistanceModuleRuntime.js` (lines 86-100 and 190-210, projected path). |

Not wired here, and why:

- `probeLaunching` is carried by probe charges (group 479, category 8), not by fitted modules. The client launches probes with `dogmaIM.LaunchProbes` (`eve/client/script/parklife/scanSvc.py`), and eve.rs has no `LaunchProbes` route. This is a separate OPEN item for the probe owner.
- `surveyScan` is carried by the mining survey chipsets (ML-3 Mining Probe, Dactyl Asteroid Analyzer) and the QA Survey Scanner. It belongs to the mining work.
- `targetHostiles` (Automated Targeting Systems), `ECMBurstJammerQA`, `pacifierDebuffQA` and `structureScanResolutionActivePercentage` (a structure module) are outside the exploration, hacking and scanner set.

## 8. Polarized pin and ship resonance propagation (implemented, not verified with the client)

A fitted Polarized weapon pins the ship's twelve resonances at one (effects 5994 and 5995, PostAssign, caps 1527/1528/2768-2771 at 1.0). The pin
holds whatever runs on the ship: hardeners, damage control, rigs, and whether the weapon is online or active (fitted is what counts, per CCP). The
numbers a hardener is told (`TypeAttributeResistanceEffect`, `DogmaModifierEffect`) are read from the world with the module assumed in the state it
will have (`DogmaWorld.Assume`), so precedence, the stacking penalty and the pin apply to them. The simulation is sent the ship's effective resonances
through one path, `SpaceSessions.SendShipAsync`, after an accepted activation, an accepted stop, and an online or offline switch in space, whenever
those resonances or hit points change. The started modules are kept per pilot (`Pilot.ActiveModules`), which is in-memory only.

**No wire change for this section.** The resend uses the existing `ShipChanged` (`SimShipChanged`, kind 8). The pin flag that the simulation needs for section 8b is the one wire change (W2, see 8b).

| Status | Scope | Remaining work / evidence |
|---|---|---|
| OPEN | Client verification | Covered by .NET tests only: `PolarizedPinTests` and `AssumeTests` (Eve.Dogma.Tests, real SDE for 34274, 2293, 2295, 2291, 2046, 587), `PolarizedShipTests` and `PolarizedHostTests` (Eve.Services.Space.Tests). Not run in the graphical client. |
| LIMITATION | Stop of a hardener | The host takes a module out of the active set when the simulation accepts the stop, but the simulation keeps the effect until its cycle ends. The sim's resonances therefore return to the bare values up to one cycle early. Exact timing needs a simulation-to-host "effect stopped" message: a new wire message, for the next wire batch. |
| LIMITATION | Whole-ship resend | A resend carries everything dogma has for the ship, so an activation effect that changes a number other than the resistances (mass, speed, sensors) is in the resend too while it runs. That is consistent with the sim only where the sim applies the same effect through dogma. Each activation effect has not been checked for this. |
| LIMITATION | Overload | Overloaded state is not in the active set; an overload does not refresh the ship. No resonance effect of an overload is known from the SDE; not checked. |
| LIMITATION | Pilot state across a jump or a survivor | A new `Pilot` (system jump arrival, a survivor flying the wreck's ship) starts with no active modules. If the simulation keeps a hardener running across that, the next refresh drops it. Unverified against the client. |
| LIMITATION | Fitting in space | No route fits a module in space; a fit made while docked reaches the simulation at undock (spawn). The refresh covers online and offline switches in space. |
| DECISION | Pin is fitted, not online | The owner rule R5 (an offline weapon restores the resistances) conflicts with CCP's "when fitted" wording and was not implemented. Kept fitted (see the Polarized spec, D1). |
| CHANGED | Adaptive hardener (effect 4928) | It is no longer a world modifier: the simulation owns its profile and multiplies the ship's own armor resonances by it (section 8b). The world, and so the ship the simulation is sent, holds the ship's own resonances. Two hardeners of one ship cannot be fitted (group limit), so no stacking of the two arises. |
| FIXED | Emergency hull energizer | Its multiplier (commit a9f7d33): it multiplies the ship's 113/110/109/111 by the module's 974-977. The burn-out that is not kept is in section 9. |

## 8b. Reactive armor hardener: per-cycle resistance shift (implemented, not verified with the client)

Type 4403 (`adaptiveArmorHardener`, effect 4928). While it runs the armor's resonances are the ship's own times the module's 0.85 (the seed). At the end of each
cycle (10 s) the resonances move by the SDE's `resistanceShiftAmount` (1849 = 6 points, 0.06): the kinds the armor took least rise, the others fall by
what they rose, and the total is kept. The simulation owns the profile (`crates/eve-space/src/resistance_shift.rs`, `Health::shift_cycle`); the host
(`ReactiveArmorHardenerEffect`) sends the seed, the step and the four armor attributes, and tells the client the armor's resonances at the start and the
stop. A fitted Polarized weapon pins the armor at one: the profile is then frozen (no tally, no shift), and the pin is sent with the ship (`armor_pinned`).

**Wire (W2, bumped to 22).** `SimAction` tag 12 (`ResistanceShift`) and one armor-pin byte at the end of the ship's vitals, in `link.rs`, `SimCodec.cs` and
`oracle/fixtures/simlink/messages.json` (regenerated by `tools/dev/gen-simlink-fixtures.py`). `ProtocolVersion` is 34 after track B's 33 (`crates/eve-space/src/link/mod.rs`); each bump since 22 is listed in ADR 0013's wire epoch log.

| Status | Scope | Remaining work / evidence |
|---|---|---|
| OPEN | The redistribution rule | CCP gives the six points a cycle and the zero-sum total (patch notes Retribution 1.0). The donor count (at least two), the tie order (em, explosive, kinetic, thermal) and the donor cap (one) are the EVE.js and pyfa model, not CCP. A hit equal in every kind still moves the resonances (the two first declared kinds rise). Checked by the unit tests in `resistance_shift.rs` (the reference table, the total, the bounds, the stall) only. |
| OPEN | Client verification | Not run in the graphical client. Open checks: whether the client re-derives 267-270 from its own PreMul of the module (snap-back after a told change), and that the told values are shown. |
| LIMITATION | Restart and death | The profile is in memory and not persisted. A host restart ends the pilot's running modules (the active set is in memory), and a death removes the ship's health. A new activation starts from the seed. |
| LIMITATION | Gate jump | The CCP patch notes say a stargate jump resets the hardener (Inferno 1.1). Not tested: whether a jump removes the pilot's running modules in the simulation, and so the hardener, is unverified. |
| LIMITATION | Overload and stacking with static hardeners | Overload changes the cycle length, not the shift (not modelled). The armor's base includes the static armor hardeners, the profile multiplies it without a stacking penalty (unverified). |
| LIMITATION | Stop timing | As for section 8: the simulation keeps the effect to the end of its cycle; the armor is told the base when it ends. |
| DEVIATION | Model | Recorded in `docs/reference/compat/DEVIATIONS.md`. |

## 9. Module effects: gaps left after the parity fixes

Each item below is **OPEN**. Evidence is the code path named; none of them is covered by a passing test that proves the reference behavior.

| Status | Gap | Evidence and what it needs |
|---|---|---|
| OPEN | Shield ancillary booster stops when its charges run out (`fallback: null`), where the reference continues at the base amount and pays the capacitor again | `dotnet/src/Eve.Services.Space/Effects/ModuleEffects.cs` (`AncillaryRemoteShieldBoosterEffect`) tells a capacitor need of 0 while charged; `crates/eve-space/src/modules.rs` `finish_cycle` draws the effect's single `capacitor_need` every cycle. A fallback needs a per-cycle capacitor in the simulation, which is a link change. A host-only fallback would make the booster free after its charges are gone. |
| OPEN | A second cloaking device, and a running module that cannot be stopped early (`disallowEarlyDeactivation` 906), are refused by the client (`FakeCloaking.PreStartChecks`) but not by eve.rs | The host does not know which modules the simulation runs (`ShipSnapshot` loads only the persisted Online/Offline states); the refusal belongs in `crates/eve-space/src/modules.rs` `activate`. |
| DONE | Warp is refused while a cloak that is not warp-safe (`cloaking`, `cloakingPrototype`) runs: `WARP_BLOCKED_BY_CLOAK`, the covert ops cloak (`cloakingWarpSafe`) does not hold a ship | `crates/eve-space/src/scene/warping.rs` (`cloak_blocks_warp`), `SimAction.Cloak` tag 5 (protocol 34); `Eve.Services.Space.Effects.CloakingEffect`; test `deployable::tests::a_cloak_that_is_not_warp_safe_holds_its_ship_and_a_covert_one_does_not`. Not checked in the graphical client. |
| OPEN | Emergency hull energizer: the module's burn-out is told to the client but not kept, so a burned-out module can be activated again | `dotnet/src/Eve.Services.Dogma/DogmaInfo.cs` shows a module's stored condition (`ModuleConditions`) to the client, but no activation path reads that condition or writes a burn-out to it (`ModuleEffects.cs`, `TypeAttributeResistanceEffect`). The resonance change reaches the simulation through the resend after an accepted activation (section 8, `SpaceSessions.SendShipAsync`). |
| OPEN | Launchers still refused by name: scan and survey probe launchers (481, 1226: the probe lifecycle, not written), structure launchers (1327, 1328, 1562, 1974: category 66, they run on a structure, and no structure service exists until the structure phase). DONE in this change: the festival launcher (501: snowball charges, no damage, no aggression, a target), the countermeasure launcher (308: missiles and torpedoes by MissileEffect, defenders by DefenderEffect, chosen by the loaded charge), and F.O.F. missiles (fofMissileLaunching: no lock; the launch goes for the nearest valid hostile in the missile's flight range) | `dotnet/src/Eve.Services.Space/Effects/MissileEffect.cs` (`RefusedLauncherGroups`, `OtherLauncherNames`, `AutoTargetId`), `DefenderEffect.cs`; `crates/eve-space/src/missile_entity.rs` (`nearest_hostile`, `AUTO_TARGET_ID`); tests `deployable::tests::an_fof_*`, `a_launch_that_does_no_damage_*`; DEVIATIONS, Bombs, bubbles and warp disruption. |
| DONE | Bombs (launcher 862: damage, ECM and neutralising charges, released at the ship and detonating where it is), the defender launcher (512: target-less intercept of a hostile missile in reach), the stasis webification burst (charge 4088: a web of 15 km for 30 s), the interdiction sphere launcher (589, warp disruption probes) | `Effects/BombEffect.cs`, `DefenderEffect.cs`, `InterdictionSphereEffect.cs`, `DeployableEffects.cs`; `crates/eve-space/src/deployable.rs` (`detonate`, `stasis_burst`, `defender_intercept`); tests `deployable::tests::*`; protocol 34 tags 18 to 22. No V24.01 trace of a bomb or a defender yet (DEVIATIONS, Bombs, bubbles and warp disruption). |
| DONE (simulation and wire; the client's attacker list is not yet) | Breacher pod launchers (4807; charges SCARAB Breacher Pod S 85088 and M 85089, charge effect `dotMissileLaunching` 12174): a hit does no damage and starts a damage over time on the ship it reaches: 40 s (SDE 5735), one tick a second, the lower of the charge's percentage of the target's total hit points (0.6, SDE 5737) and its cap (SDE 5736: 200 for S, 600 for M), rounded down, through the layers without resistances; one working application per ship, the strongest, the others inert (wiki); the effect ends when the ship leaves the scene; the slim item shows `dotHpPercentageApplied`. Protocol 42: the missile carries a damage-over-time block. Not built: `OnDotVictimUpdated` (its payload is a Python set, which eve-marshal does not encode and no source in the repo gives the opcode for), and the SDE skill bonuses (5749, 5750) | `crates/eve-space/src/dot_weapons.rs` (`apply_dot`, `advance_dots`, `dot_tick`, `dot_slim`), `missile_entity.rs` (`missile_lands`, the aggression of a launch), `health/mod.rs` (`apply_unresisted`), `link/encode.rs` and `decode.rs` (the block), `dotnet/src/Eve.Services.Space/Effects/MissileEffect.cs` (`BreacherLauncherGroup`, `MissileOf`), `dotnet/src/Eve.Space/SimCodec.Encode.cs`; tests `dot_weapons::tests::*`, `health::tests::a_damage_over_time_tick_*`, simlink frame "activate a breacher pod launcher", `MissileHostTests.A_breacher_pod_launcher_*`; DEVIATIONS, Breacher pods. |
| OPEN | Warp is refused by a warp bubble and by a probe in space (`WARP_DISRUPTED_BY_BUBBLE`), and an interdiction nullifier makes its ship immune to bubbles; a warp core stabilizer takes two points off the scramble status. The warp disruption field generator is a bubble with its ship penalties from the SDE. OPEN: the mobile warp disruptors (`anchorDrop`, `anchorLift`), which need a deployable store and jobs (not started) | `crates/eve-space/src/scene/warping.rs`, `deployable.rs` (`warp_disrupted_at`), `Effects/WarpDisruptSphereEffect.cs`, `WarpModifierEffect.cs`; tests `deployable::tests::*`, `InterdictionNullifierTests`. Protocol 34 tags 16 to 18. |
| DONE | The missile family is derived from the static data (`LauncherFamilies`, `DogmaStatic.MissileLauncherGroups`), not from a list of groups. Served set on the pinned SDE: 506-511, 524, 771, 1245, 1673, 1674 | `dotnet/src/Eve.Dogma/LauncherFamilies.cs`; `Eve.Dogma.Tests/LauncherFamilyTests.cs` (synthetic) and `Eve.Services.Space.Tests/MissileFamilyDataTests.cs` (PgFact, real SDE) |
| CHANGED | Armor hardeners and other dogma modifiers reach the simulation only at spawn | Their told values are the world's with the module assumed active (`DogmaWorld.Assume`, section 8), and the simulation is sent the resonances after an accepted activation (`SpaceSessions.ActivateAsync`). The stop timing stays the section 8 limitation. |
| CHANGED | A barrage is refused, not fired as a plain shot | `TurretEffect.cs` `BarragePendingEffect` (a pending handler: refused `EFFECT_NOT_IMPLEMENTED`, listed as uncovered). The barrage falloff (328) and damage multiplier (326) are not run. Evidence: `ModuleRuleTests.A_module_whose_effect_has_no_body_yet_is_refused_and_stays_uncovered`. OPEN: the barrage body, when a reference or recording exists. |
| CHANGED | A mutadaptive repairer's first cycle is x1.0 (was x1.1) | `ProjectedEffect.cs` `MutadaptiveRepairSpool.TryNextCycle` applies the bonus, then advances (`assistanceModuleRuntime.js` `advanceAssistanceRepairSpool`). Evidence: `ModuleEffectsTests` and `RepairHostTests` mutadaptive tests. |
| CHANGED | The spool starts on an accepted activation only | `IEffectHandler.Accepted` (default no-op) runs after the simulation accepts the start (`ModulePackages.Result.Accepted`); the name branch in `SpaceSessions.ActivateAsync` is gone. |
| CHANGED | Refusal wording: a microwarpdrive held by a scrambler, and a module too damaged to start | `dotnet/src/Eve.Services.Dogma/ModuleRefusals.cs` (texts from `moduleOperation.js`). Evidence: `ModuleOrderTests`. |
| CHANGED | The group limit on online modules (maxGroupOnline, 978) is enforced at the online | `CharacterShips.MaxGroupOnlineRefusal`, refused as `CannotOnlineReachedMaxGroupOnline {type: (4, typeId), group: (7, groupId), maxGroupOnline}` (`eveexceptions/const.py`). Evidence: `Eve.Ships.Tests/MaxGroupOnlineTests` (real SDE, capacitor boosters, 10 limit). OPEN: lowering a limit does not offline the excess (`_OfflineExcessModulesInGroup`). |
| CHANGED | A target that disallows offensive modules (872) is refused an offensive module, or a module with 1798 | `SpaceSessions.ActivateAsync` throws `DeniedActivateTargetModuleDisallowed`. Only the target type's static attributes are read (not passive or skill bonuses). OPEN: no host test yet; needs a target with 872. |
| CHANGED | A module that may run in a gate cloak (canActivateInGateCloak, 3123) keeps a jump cloak | `SpaceSessions.EndInvulnerability(keepsJumpCloak)`. OPEN: no host test yet; a fitted 3123 module is needed. |
| DONE | A module that burns a resource (consumptionType 713) asks the host for the fuel of its first cycle at activation and of each following cycle at its end, takes the module's consumption quantity (714) from the ship's fuel bay or cargo in one transaction with the answer, and stops with reason 6 when it cannot pay. Its refusal is `NO_FUEL` at activation. | `SimEffect.BurnsFuel` (flags bit 16) set for every handler in `ShipEffect`; ask = `SimModuleWorkCycle` command 9; `Scene::park_start`/`park_fuel_cycle` (`modules.rs`); `CharacterShips.ConsumeModuleFuelAsync` (`CharacterShips.Fuel.cs`); ADR 0013 epoch 24. Tests: `modules::tests` fuel cases; `ModuleFuelHostTests`; `ModuleRuleTests.Each_fuel_family...`. Not checked in the client: the notice of a stop at a cycle end (reason 6; no client text is sent, DEVIATIONS 125); the cyno field, jump portal and clone vat bodies stay `EFFECT_NOT_IMPLEMENTED` (no fuel is taken for them). Known gaps (ADR 0013 epoch 24): a capacitor refusal after the fuel is taken loses it; a target is not re-checked at the fuel answer; a deactivation racing an accepted answer does not refund it. |
| OPEN | The propulsion group limit (afterburner and microwarpdrive together, maxGroupActive 763) is not enforced | Running state is in the simulation (`crates/eve-space/src/modules.rs` `activate` has no group count). Needs group fields on `SimEffect` and a link wire bump (audit A FP-2a and FP-2b). |
| OPEN | The tracking computer is hostile in the simulation | `Remote::hostile` (`crates/eve-space/src/modules.rs`) classes `shipModuleRemoteTrackingComputer` as hostile, and the HUD name is `ewTrackingDisrupt` (audit A FP-3). Needs Rust. |
| OPEN | Module rules that need the running state (906, 104, 2354, 669, 763, 3265, 1245, 1349, 1334, 2608) | DEVIATIONS 126. Needs the simulation and one wire bump. |

## 10. Mining, salvage, tractor, targeting and entosis modules (mining track)

Each effect below was in the gap manifest (`dotnet/tests/Eve.Services.Space.Tests/ModuleEffectsTests.cs`, `currentSdeGaps`), so its activation was refused. The rows
marked DONE run through the host and the simulation and are covered by headless tests; none of them has been checked in the graphical client.

| Status | Gap | Evidence and what it needs |
|---|---|---|
| DONE | Mining lasers, ore and ice (`miningLaser`) | The handler (`MiningLaserEffect`), the host cycle (`MineAsteroidAsync`, one transaction for the grant, the ledger row and the `mining.ore_mined` fact), `OnOreMined` to the miner and `OnOreMinedByOther` to the other pilots, the family check at activation, the short cycle for ore (M2, M3, M4, M6a, M6b, M7, M8). Tests: `MiningProfileTests`, `MiningLedgerTests`, `MiningActivationTests`, `WorkCycleHostTests`. |
| DONE | Frequency mining lasers (group 483, crystal-fed) | Refused `NO_AMMO` with no crystal; `CHARGE_NOT_COMPATIBLE` when the crystal's SDE list does not name the rock; the crystal's volatility rolled per cycle, its damage kept as the item's custom info and the crystal removed at full damage (M9). Tests: `CrystalVolatilityTests`, `SpaceServiceTests` (frequency laser). The crystal's yield multiplier (attribute 782) is not applied directly (see DEVIATIONS). |
| DONE | Gas harvesters (`miningClouds`) | A harvestable cloud is mined as a rock is, and yields its SDE material or its `Compressed <name>` gas. A cloud is seeded beside its belt's rock (a shortcut, see DEVIATIONS). Tests: `GasCloudMiningTests`, `SpaceContainerTests` (gas cloud seeding). |
| DONE | Salvager (`salvaging`) and salvage notices and events (S1, S2) | A failed roll tells the pilot `SalvagingFailure` and a kept grant `CustomNotify`; `salvage.wreck_salvaged` and `salvage.salvage_failed` are announced with their transactions. Tests: `WorkCycleHostTests`. The client text of the notices is not checked in the graphical client. |
| DONE | Passive targeter (`targetPassively`, A1) and auto-targeting system (`targetHostiles`, A2) | A passive lock locks its target without telling it (`LockOrigin::Passive`); the auto-target sweep locks the ships that target the pilot within the module's range at each cycle start (`LockOrigin::Auto`). Tests: `modules.rs` (`a_passive_targeter_locks...`, `an_auto_targeter_locks...`), `SpaceServiceTests`. |
| DONE | Belt rocks reseed (B5) | A belt whose rocks are all depleted is seeded again when a pilot arrives (`SpaceContainers.SeedAsteroidBeltAsync`). Test: `SpaceContainerTests`. |
| DONE | Tractor beam (`tractorBeamCan`, T1, T2, T3) | A beam pulls the wreck or can its pilot holds a lock on (`tractor.rs`): the ball follows the ship at 500 m (EVE.js `TRACTOR_FOLLOW_RANGE_METERS`) at the module's `maxTractorVelocity`; a target past the module's range is `TARGET_OUT_OF_RANGE`; the pull ends when the beam stops, the target is lost, or the puller leaves. The host refuses a wreck the pilot may not take from (`TARGET_LOOT_RIGHTS_REQUIRED`). Wire: SimAction tag 9 carries the range (protocol 30). Tests: `modules.rs` (`a_tractor_*`, `a_wreck_pulled_by_a_ship_that_leaves_the_scene_is_let_go`), `motion.rs`, `SimCodecTests`, `ModuleEffectsTests`. Not checked in the graphical client. |
| BLOCKED (phase 3) | Entosis link (`entosisLink`, E1) | Refused `STRUCTURES_PHASE_3` before a target is asked for (`Effects/StructureModuleEffects.cs`): the link acts on a sovereignty structure, and no structure is in space on this server. Its lock and range rules apply to that target and are not run. |
| BLOCKED (phase 3) | Compression (`industrialItemCompression`, `CompressItemInSpace`, C1) | The compressor is a structure module (refused `STRUCTURES_PHASE_3`, `StructureModuleEffects.cs`); the in-space RPC refuses with `UnavailableNotice`, which names the same blocker. |
| DONE | Belt ore model (D8) | A belt holds the ores of its system's true security from the EVE University belt rows (`BeltOres`), one rock each (15,000 m3, a shortcut: the belt radius is not passed to the host). Seeded when the store holds no live rock in the belt, so a belt emptied in one process is seeded again. The gas cloud keeps 1,000 units (shortcut, DEVIATIONS). Mercoxit and the Drone regions are not seeded. Tests: `BeltOresTests`, `SpaceContainerTests` (seeding, reseed, cloud). |
| DONE | Daily goals of mining and salvage (X-6) | `DailyGoalRecorder` (Eve.Host): "Mine 2000 units of Ore" and "Salvage 5 Wrecks" (EVE.js `dailyGoalsCatalog.js`, industrialist pool) as content goals, one of each per pilot per period (11:00 UTC to 11:00 UTC, `DOWNTIME_HOUR_UTC`). Advanced by the subscriber `mining.daily_goal` on `mining.ore_mined` (ore only) and `salvage.wreck_salvaged`, in its own transaction. `ContentRuntime.FindAsync` finds a finished goal so it is not started again in its period. Both goals run every day (DEVIATIONS). The client's daily goal window is not wired: no RPC reads these goals yet. Tests: `DailyGoalTests`. |
| DONE | Mining ledger as a table (X-7) | `mining_ledger_entries` and `mining_ledger_observers` (migration 0003, space schema). The writer is the subscriber `mining.ledger` in its own transaction (`MiningLedgerStore`: replay-safe, 90-day prune). The readers are `MiningLedgerQueries` on the rowset read side (`ReadSide.ToKeyValList` for the EVE.js list of KeyVals). The game-store `miningLedger` rows are copied at start-up (adding only the missing entries) and kept as an archive. Deviations in DEVIATIONS (mining tracks). Tests: `MiningLedgerTests`, `MiningLedgerServiceTests`. |
| OPEN | Mining scan list (X-7, scan) | `MiningScanRpcReadModel` (Eve.Host) keeps its loop: the survey is a distance test on the live simulation positions (`SpaceSessions.PositionOfAsync`), which a SQL rowset cannot answer. Open: the candidate rows (the stored asteroids of the system) can come from one `RowsetQuery`; the distance stays in code. |

## 11. Pulse Activated Nexus Invulnerability Core (P.A.N.I.C., type 42522): rules done, body pending (needs a fleet-buff action and the buff registry, DEVIATIONS 140)

SDE facts (`typeDogma` of type 42522, effect 6719 `moduleBonusIndustrialInvulnerability`, category 1 activation, no `modifierInfo`): `canFitShipGroup01` 1298 = 883 (Capital Industrial Ship: the Rorqual 28352 and the unpublished Rorqual ORE Development Edition 33687), `maxTypeFitted` 2431 = 1, `requiredSkill1` 182 = 44067 (Invulnerability Core Operation) at level 1, `disallowActivateOnWarp` 1245 = 1, `disallowRepeatingActivation` 1014 = 1, `panicDuration` 2788 = 240000 ms (the effect's duration attribute), range 54 = 200000 m (the effect's range attribute), `disallowEarlyDeactivation` 906 = 1, `disallowDocking` 2354 = 1 (on the module). No security attribute (1074, 1970, 5561) is on the type: the static data names no security class, so no security refusal exists.

| Status | Item | Evidence and what it needs |
|---|---|---|
| VERIFIED (tests) | Fitting: only a Capital Industrial Ship (a Rorqual; an Orca is refused with `InvalidShipGroup`), one per ship, and only a pilot trained in 44067 (`SkillRequired`). Enforced by `FittingRules` (generic). | `IndustrialInvulnerabilityFittingTests` (4 tests, real SDE, `PgFact`). |
| VERIFIED (tests) | Activation while the ship warps: refused with `MODULE_WARP_BLOCKED` before any package is built, nothing sent to the simulation. The rule reads 1245 of the module (`ModuleRestrictions.CheckWarp`) and the host's warp state (`SpaceSessions.IsWarping`, set when a warp command is accepted). | `ModuleRestrictionTests`-style unit tests in `IndustrialInvulnerabilityTests` (real SDE value of 1245), host tests in `IndustrialInvulnerabilityHostTests`. The client wording `Notify` is ours: the client has no UserError for 1245 (constant only in `dogma/const.py`), so the wording is the server's own and the graphical client is not checked for it. |
| VERIFIED (tests) | The effect is refused as `EFFECT_NOT_IMPLEMENTED` (no cycle, no capacitor, no success) at a target and without one, and it stays in the gap manifest. | `IndustrialInvulnerabilityTests`, `IndustrialInvulnerabilityHostTests`, `ModuleEffectsTests` (`currentSdeGaps`). |
| BLOCKED (needs a fleet-buff action and the buff registry, DEVIATIONS 140) | The fleet effect itself: while it runs, every subcapital mining and hauling ship of the Rorqual's fleet within 200 km loses weapons, cloak, warp, dock, jump and tether, takes drone damage -100 %, gains shield resistance (-99.99 %) and -90 % shield recharge, and gets +100 % mass, -50 % top speed, +100 % warp penalty and -75 % scan resolution. | The numbers are the V24.01 client's `dbuff/common/industrialInvulnConst.py` (`DBUFF_VALUES`); the SDE text states the set in words (the attributes do not). The sim cannot apply a buff to a list of ships: `SimAction` and `SimRemote` address one target, and the flags and shield and mass numbers have no sim effect. Needs a new `SimAction` (recipient list from the host's fleet membership, the buff set and a cycle) and the codec, which is a wire change (`SimCodec.cs` and `link.rs`, one bump by the integrator). |
| OPEN | Which ships are recipients: "subcapital mining and hauling ships in the same fleet as the Rorqual", excluding other Rorquals. | The SDE text states it; no attribute or group list in the static data and no server-side code in the client tree (`dbuff` and `FakeIndustrialInvulnerability` only). Group choice (mining barge, exhumer, hauler and transport groups) is not stated by the static data and must be decided with the body. |
| OPEN | Activation needs an active lock on an ore or ice asteroid (SDE text, not an attribute). | Needs the rock's type in the host: rock ids and body ids are separate spaces (section 10). |
| OPEN | Burn-out: the module burns out completely after one activation (SDE text). | Same mechanism as the emergency hull energizer (module damage set to its hit points as the effect starts; `TypeAttributeResistanceEffect`). Not written because the effect is refused. |
| OPEN | Effect lifetime: expiry in `LocalTimers` (in-process), reset on stop, death, ship change, jump, undock, and the buff set re-sent when a recipient joins or leaves the fleet. | Not written; the buff expiry belongs to the body (decision D4, DEVIATIONS 140). |
| OPEN | `disallowEarlyDeactivation` 906 (SDE: "prevent ejection from the ship ... extend the log out ship removal timer") and `disallowDocking` 2354 on the module. | Not enforced. The client uses 906 only for cloaking (`FakeCloaking.PreStartChecks`); no ejection or logout timer is written for modules. See section 9. |
| VERIFIED (SDE) | Fuel and consumption: type 42522 carries none of the consumption attributes (`consumptionType` 713, `consumptionQuantity` 714, `hasFuelCargo` 1085, `fuelCargoCapacity` 1086, `specialFuelBayCapacity` 1549, `serviceModuleFuel*` 2108-2110, `jumpDriveConsumption*` 866/868, `groupJumpConsumptionType` 3130), and the effect 6719 has no `modifierInfo`; the English description names no fuel. So the SDE does not say the core burns fuel and no fuel refusal is written. The core is refused as `EFFECT_NOT_IMPLEMENTED` at every activation, so it cannot run free. If the body is written and a consumption attribute appears, the shared per-cycle consumption mechanism (not PANIC-specific code) decides it. | Checked by query on `sde.typeDogma` / `sde.dogmaEffects` for build of this worktree. |
| LIMITATION | The warp refusal also covers a ship that is in warp (the host's warp state is set from the accepted warp until arrival, stop, dock, jump or destruction), a superset of "aligning to warp" in the SDE. | `SpaceSessions.IsWarping`. Whether the client refuses an activation in warp is not read from the client yet. |
| OPEN | Client verification of all of the above. | Not run in the graphical client. |

**LIMITATION — Character clone and worldspace state.** The character store has a home station but no separate clone station or worldspace field. Character entry currently uses the home station as `cloneStationID` and `None` for `worldspaceid`; populate these from an authoritative store if one is added.

**LIMITATION — In-space resume fidelity.** The active ship's position is persisted and restored, but the current space-item store does not preserve velocity or warp state. Direction reuses any prior stored vector or falls back to +Z. The first active-space ship List now triggers a visual-only AddBalls2 prime before full bind; native-client login ordering is not checked in the graphical client.

## 12. Superweapons, group 588: the strike, the Bosonic cone, the titan doomsdays, the lances, the reapers, the disruptive lances and the transport field have bodies; the structure variants are refused by name

SDE facts (build 3396210): group 588 "Super Weapon" has 20 types. Catmageddon (37474) is unpublished, so the published group listing has 19; the tests add it by id. The effect rows and the attributes are read from `sde.typeDogma`, `sde.dogmaEffects` and `sde.dogmaAttributes`; the family table is in `dotnet/src/Eve.Services.Space/Effects/SuperWeaponEffects.cs`. Protocol 25 adds the strike (`SimAction` tag 13, ADR 0013 epoch 25): the simulation runs the area, the pulses, the windows and the neutralising wave (`crates/eve-space/src/strike.rs`, `modules.rs` `strike_start` and `strike_pulse`).

| Status | Item | Evidence and what it needs |
|---|---|---|
| VERIFIED (tests) | Bosonic Field Generator 40633 (`doomsdayConeDOT`): the cone body. Aimed at a point, it builds one strike from its SDE data: cone shape 3, range 30000 m (`maxRange`), warning 10000 ms, 21 pulses at 1000 ms (the reference's window is closed at its end, 20000 ms), damage em, thermal, kinetic, explosive 10000 each, weapon signature 10000, immobility 30000 ms, no jump or cloak 300000 ms, energy neutralisation 30000 within 10000 m against signature 20000. The fuel (2000 of 16275 per cycle) is the shared per-cycle mechanism (`BurnsFuel`). Without a point it is refused `TARGET_POINT_REQUIRED`; in high security the empire rule refuses it first (`CantInEmpireSpace`). | `SuperWeaponEffectsTests.A_hog_is_refused...and_a_cone_is_built...` (PgFact, real SDE numbers); `ModuleRuleTests` (aimed: `BurnsFuel`); `SuperWeaponHostTests.A_cone_through_superWeaponMgr...` and the empire-rule host test; Rust `strike::tests`, `weapons::tests::a_strike_*` (pulses after the warning, no hit behind or off the axis, the window holds the ship, no cloak or jump in its window, the neutralising wave); `SimCodecTests` and `simlink_fixtures` (wire). Mutation: widening the cone fails `a_cone_widens_to_45_degrees_along_its_length`. Not checked in the client: the visuals (the cone has no graphic in the SDE, the client table or EveJS); the windows' client messages. The accepted path through the host is tested in ZERO security (`SuperWeaponHostTests.A_cone_in_zero_security_starts_through_the_host...`, fuel taken when the simulation asks): the cone carries `disallowInEmpireSpace` 1074, so the client's rule refuses it in LOW security too (a low-security fixture is a refusal, not an accept). Docking is refused in the window now (DEVIATIONS 131); the target's capacitor resistance is applied since protocol 27 (DEVIATIONS 132). |
| VERIFIED (tests) | The strike action (protocol 25): `SimAction` tag 13, the shape, aim, length, radius, warning, pulse cadence and count, damage, signature, the two windows and the neutralising wave. Both codecs, the fixtures (`oracle/fixtures/simlink/messages.json`, generated by `tools/dev/gen-simlink-fixtures.py`), `Hello` and `HelloAck` at 25. | `SimCodecTests.A_doomsday_strike_roundtrips...` and `A_strike_that_the_simulation_would_refuse...`; `simlink_fixtures` (valid and invalid strike frames). ADR 0013 epoch 25. |
| VERIFIED (tests) | Point targeting: `superWeaponMgr` `ActivateSinglePointTargetedModule` carries the client's point (a tuple of three numbers, from the ship) into the package (`EffectContext.Aim`, `ModulePackages.Of`, `SpaceSessions.ActivateAsync`). An activation that the simulation accepts reports success; a refused one is its refusal as a user error. A slash is still refused by its handler. | `SuperWeaponHostTests.The_client_point_is_a_tuple...`; `SuperWeaponHostTests.A_point_targeted_doomsday...`; `SuperWeaponHostTests.A_cone_through_superWeaponMgr...`. |
| VERIFIED (tests) | The group 588 families: every activation is refused by the reason its family names, or built. P.A.N.I.C. 42522 keeps `EFFECT_NOT_IMPLEMENTED`. The structure variants (category 66) are refused by name. A titan without a target is `TARGET_REQUIRED`. | `SuperWeaponEffectsTests.Every_activation_of_every_group_588_type...` (PgFact; 20 activations, each refused or built as its family says). |
| VERIFIED (tests) | Gravitational Transportation Field Oscillator 40634 (`doomsdayHOG`): a transport field, protocol 29 (`SimAction` tag 15, `Field`: a sphere strike of shape 4, centred on the aimed point, radius 10 km, one pulse at the 15 s warning). The capsuleer ships of the system inside it are carried to the aimed point (the host lists them: `TransportNeighbour`); the capital ships inside it cannot warp for 60 s (`WARP_BLOCKED`). The firing ship is held for its windows and takes the strike's energy neutralisation. Sources and the resolution of their conflicts: DEVIATIONS 135 and 141. Not built: the tether exclusion (142). The structure variant (47068) is refused by name (`STRUCTURE_SUPERWEAPON_NOT_IMPLEMENTED`). | `SuperWeaponEffectsTests.A_hog_builds_a_transport_field_...` (PgFact, real SDE); `SuperWeaponHostTests.A_hog_aimed_at_a_point_...` (the field reaches the simulation, fuel settled per cycle); Rust `weapons::tests::a_transport_field_carries_the_ships_of_its_sphere_and_holds_the_capitals_in_it`, `strike::tests::a_sphere_covers...`; `simlink_fixtures` and `SimCodecTests` (the field frame). |
| VERIFIED (tests) | Target's capacitor resistance (protocol 27): each ship's `energyWarfareResistance` (2045) is a sixth number of the sensors block (`SimSensors.EnergyWarfareResistance`, read from the hull's attributes; 0 is immune). The neutraliser's amount and the strike's neutralising wave are multiplied by it. The nosferatu is not weakened (reference resistance not read). | `modules::tests::an_energy_neutralizer_is_weakened...`, `weapons::tests::a_strike_neutralisation_is_weakened...`, `SimCodecTests`, `simlink_fixtures` (0.25 on the Ibis), `RepairHostTests` (ordinary hull = 1). ADR 0013 epoch 27. |
| VERIFIED (tests) | Test wart from protocol 26 (a mutation that stops a titan hit being recorded made `a_titan_doomsday_hits...` loop forever): the test now runs its body on a thread with a 10 s deadline (`weapons.rs` `finishes_within`) and fails with `did not finish within 10 s`. Re-run as a mutation: fails in about 10 s. OPEN, production: `Scene::settle_modules` (`modules.rs`, `while let Some(..) = next_module_moment`) has no cap, so a module moment that is due again at once spins the simulation thread; the test bounds it, the simulation does not yet. | `weapons::tests::a_titan_doomsday_hits_its_locked_target...` |
| VERIFIED (tests) | `superWeaponMgr` (X0): an unwired route answers a user error and never `PyNone`. | `UnportedFeatureRpcServicesTests` (Core). |
| VERIFIED (tests) | The structure cyno beacon route (`structureCynoBeaconMgr`, P2-6) answers the user error `Structure travel is not wired on this server.` for each of its three calls while no backend is wired, and forwards to a backend when one is given. | `StructureTravelTests` (new file). |
| VERIFIED (tests) | Attribute constants for the superweapon, cyno, jump portal and clone rules are the SDE's attributes (`ModuleAttributes`). | `SuperWeaponAttributeIdsTests` (PgFact, real SDE). |
| VERIFIED (tests) | Superweapon graphics (`SuperWeaponFx`): the titan doomsday guids are the SDE effect rows; lance and disruptive lance graphics follow the module's raceID. Reaper graphics are EveJS's catalog only (EveJS's catalog, not read from the client's effect data). The cone has none. Nothing reads the table yet. | `SuperWeaponFxTests` (PgFact). |
| VERIFIED (tests) | Cynosural field types from the SDE: 21094, 28650, 52696, each radius 250. Fields are spawned by the generator's cycle (section 22, track B7). | `CynoFieldTypesTests` (PgFact). |
| DONE | Fuel of a superweapon with a body goes through the shared per-cycle mechanism (`ShipEffect.Build` sets `BurnsFuel` for 713 > 0). | `ModuleFuelHostTests`; `ModuleRuleTests` (the DDD module is aimed and burns its fuel). |
| DONE | Module gap manifest (manifest test): the cone is removed from the uncovered list because it has a body; the refusals stay listed. | `ModuleEffectsTests` (the manifest test). |
| VERIFIED (tests) | Titan doomsdays (`superWeapon{Amarr,Caldari,Gallente,Minmatar}`: Judgment 24550, Gjallarhorn 23674, Oblivion 24552, Aurora Ominae 24554), protocol 26 (`SimAction` tag 14, `Doomsday`). The client activates a titan with its LOCKED TARGET, not a point (no `isPointTargeted` on the type; `shipmodulebutton.py` calls `def_effect.Activate(targetID)`), so the brief's "point targeting" does not apply to them. One hit on the target after `damageDelayDuration` (7500 to 12000 ms), its one damage kind 2 400 000, no signature factor; the immobility (30 s) and no-jump window (300 s) at activation; fuel 16274 x 50000 per cycle through the shared mechanism. No chain: the titans carry no lightning attributes (2104 to 2106), so EveJS hits the primary target alone. Not checked in the client: the titan graphic (`SuperWeaponFx`) and the texts of the refusals. The structure variant (category 66) is still refused by name (O5). | `SuperWeaponEffectsTests.A_titan_doomsday_is_built...` (PgFact, SDE numbers); `SuperWeaponHostTests.A_titan_doomsday_on_a_locked_target...` (through the host, fuel taken when the simulation asks) and `..._without_a_target...`; Rust `weapons::tests::a_titan_doomsday_hits...` and `..._is_refused_without_a_locked_target...`; `SimCodecTests.A_titan_doomsday_roundtrips...`; `simlink_fixtures` (a valid hit, a negative delay). ADR 0013 epoch 26. |
| VERIFIED (tests) | Titan lances (`doomsdayBeamDOT`: 40631, 41439, 41440, 41441) as a cylinder strike (`DoomsdayLanceEffect.cs`, `DoomsdayStrike.cs`): radius 2500 m (`doomsdayDamageRadius`), length 200 km (`maxRange`, fixed), warning 15 s, one pulse per second through the 15 s window (16 pulses), 50 000 EM each, signature 5000, the immobility and no-jump windows, the energy neutralisation (35 000 within 10 km against 20 km), fuel 16274 x 50 000 per cycle. Empire space: `disallowInEmpireSpace` (1074) is set, so the client's rule refuses it in LOW and high security (`CantInEmpireSpace`); it runs in zero security only. The reference names the low-security case `MODULE_RESTRICTED_IN_LOWSEC`: the same outcome under the client's name (see DEVIATIONS 136). BUILT (track C3, protocol 37): the lance beacon (EveJS `spawnLanceBeacon`), a transient "Modular Effect Beacon" container (type 41233) at the aim clamped to the lance's range, put in space when the strike starts and taken out when its effect ends; the beam is played on it. Not checked in the client (DEVIATIONS, Lance beacon). | `SuperWeaponEffectsTests.A_lance_is_built_from_its_static_data...` (PgFact); `SuperWeaponHostTests.A_lance_is_refused_by_the_empire_space_rule_in_high_and_low_security...`; `..._in_zero_security_starts_through_the_host...` (fuel taken when the simulation asks); Rust `strike::tests` (cylinder) and the strike window tests. |
| VERIFIED (tests) | Reapers and Catmageddon (`doomsdaySlash`: 40632, 41442, 41443, 41444, 37474), protocol 28: the two-point slash (`ActivateSlashModule`, the client's arc checked against the fixed geometry: `SLASH_POINTS_REQUIRED`, `SLASH_POINTS_INVALID`), the cylinder that sweeps from the start point to the end point over the damage window (11 pulses, 10 s window, 5 km radius), the windows, the neutralising wave, fuel 50 000 of 16 274 per cycle. The sweep is the simulation's (`SimAction` tag 13, `SweepEnd`). The empire rule refuses it in high and low security (1074). Catmageddon (37474) is not built by its data (its shape and radius are missing: `SUPERWEAPON_REAPER_DATA_MISSING`). The graphic is EveJS's catalog only (DEVIATIONS 138). | `SuperWeaponEffectsTests.A_reaper_is_a_sweep...`; `SuperWeaponHostTests.A_reaper_slash_through_superWeaponMgr...`; Rust `weapons::tests::a_sweeping_strike...`, `strike::tests::a_sweep_turns...`; `simlink_fixtures`. |
| VERIFIED (tests) | Disruptive lances (`debuffLance`: 77398 to 77401), protocol 28: the strike with its 60 s debuff (no warp, jump or dock, half the remote repair; DEVIATIONS 139). The siege requirement (5426) is read from the ship's own state: an active `moduleBonusSiegeModule` module, otherwise `ACTIVE_SIEGE_MODULE_REQUIRED`. The cloak is refused `CLOAK_DISALLOWED` while a lance is fitted (5425). Not a buff of the registry (the reference has no dbuff collection for it). | `SuperWeaponEffectsTests.A_disruptive_lance_needs_an_active_siege_module_...`, `SuperWeaponEffectsTests.A_fitted_disruptive_lance_disallows_the_cloak_...` (PgFact, real SDE); Rust `weapons::tests::a_disruptive_pulse_disrupts...`, `scene::tests::a_disrupted_ship...`, `weapons::tests::a_disrupted_ship_takes_half...`. |
| PARTLY (track C5, section 21; C3 and C4 in DEVIATIONS and section 20) | Buff registry (D4) and the per-cycle recipients are built in the dogma library and the host's rule (track C5); command bursts and P.A.N.I.C.'s fleet effect: not built (section 21). P.A.N.I.C. has no EVE.js body (the client's `dbuff/common/industrialInvulnConst.py` has the numbers only, and no recipient list); its numbers need simulation state the wire does not carry (shield resistance, shield recharge, drone damage, weapons, warp penalty). Command bursts need live numbers for running modules (a wire message). The registry has no consumer until one of the two is built, so it is not built alone. | DEVIATIONS, "Track C3" |
| PARTLY | Strike follow-ups: docking is refused in the no-jump window (`DOCKING_BLOCKED`) and in the immobility window (`IMMOBILE`), a simulation rule with no wire change (Rust `scene::tests::a_ship_in_a_doomsdays_windows...`). Tethering: eve.rs has no tether at all (no `tether` in `crates/eve-space`), so there is nothing for the reference's `disallowTethering` to refuse; that flag is a gap until a tether exists (DEVIATIONS 142). The target's capacitor resistance is applied since protocol 27 (see the protocol 27 row). The cone's graphic is absent from the static data and from EveJS, so none is sent (DEVIATIONS 133). | DEVIATIONS 131 to 133. |
| PARTLY | Structure variants (category 66, the Standup doomsdays and the structure HOG, 47068): fitted and refused by name (`STRUCTURE_SUPERWEAPON_NOT_IMPLEMENTED`, `StructureHogEffect`); no structure host exists (decision O5, DEVIATIONS 142). | DEVIATIONS 142. |
| DONE (track C3, protocol 39) | Lance beacon (EveJS `spawnLanceBeacon`): a Deployable of the simulation (`crates/eve-space/src/deployable.rs`, role `Beacon`, the ball of type 41233), released at the strike's start at the aim clamped to the lance's range; the lance's FX is played on it, and the beacon is removed when the effect ends. Titan lances, disruptive lances and the Bosonic cone (EVE.js family `lance`). | `deployable::tests::a_lances_beacon_*`, `deployable::tests::a_strike_with_no_beacon_*`, `SuperWeaponHostTests.A_lance_carries_its_beacon_*`, `SimCodecTests` and `simlink_fixtures` (the beacon flag); DEVIATIONS, Lance beacon |
| DONE | P2-10 (the interdiction nullifier's warp-bubble immunity test): `InterdictionNullifierTests.A_ship_with_warpBubbleImmune_is_not_held_by_a_bubble` reads the nullifier's 1538 from its 3120 through the package (`WarpImmunity`), and the simulation test `deployable::tests::an_immune_ship_is_not_held_by_a_bubble` holds the ship in a bubble without it. | decision D10 and DEVIATIONS (Bombs, bubbles and warp disruption). |
| OPEN | Burst projectors (`doomsdayAOE*`, categories 7) and the Area type (ADR 0011): the strike is the first area action; the general `Area` type with recipient lists (decision D1, D3) is not built. | ADR 0011. |

## 13. Player fighters (2026-10-09)

Decisions: ADR 0015 and its amendments. Deviations and their evidence: DEVIATIONS "Player fighters".

| Status | Item | Where |
|---|---|---|
| DONE | Fighter bay and launch tubes: load, unload, the tube rules and the bay volume check, in the database | `dotnet/src/Eve.Ships/CharacterShips.Fighters.cs`, `FighterTubes.cs` |
| DONE | `fighterMgr` refuses every call it cannot serve with a user error; no fallback answers success | `ClientStartupServices.cs`, `ISpaceFighters.cs`, `SpaceFighters.cs` |
| DONE | Ability slot table (EVE.js data, families checked against the SDE) | `dotnet/src/Eve.Services.Space/Effects/FighterAbilityCatalogue.cs` |
| DONE (not run in the client) | Squadrons in space: launch, recall and landing, orbit, follow, stop, GOTO_POINT (a point course), abandon, scoop back within 5000 m of the ship it came from, recovery on logoff (within 5000 m lands, else let go) | ADR 0015; `crates/eve-space/src/fighters.rs`, `dotnet/src/Eve.Services.Space/SpaceFighters.cs` |
| DONE (not run in the client) | Squadron health: the points of all members together; a member lost is told as `OnInSpaceSquadronSizeChanged` and the item keeps the size; the last member destroys the squadron and its item is removed | `fighters.rs` (`tell_squadron_size`), `SpaceFighters.cs` (`OnReceived`, `LoseAsync`) |
| DONE (not run in the client) | Abilities that run in the simulation: warp disruption, stasis webifier, energy neutralizer, ECM, tackle (one remote: a scramble and a web), microwarpdrive, afterburner, evasive maneuvers, micro jump drive, missiles and AttackM (per member, on the locked target) | `Effects/FighterAbilityEffects.cs`, `Effects/ProjectedEffect.cs`, `ewar.rs`, `modules.rs` (`Remote::Tackle`) |
| DONE | Ability charges and cooldowns are kept on the squadron's item and resumed after a restart (DEVIATIONS, Player fighters and Track C remainder). | `SpaceFightersRemainderTests` |
| DONE | A squadron let go writes the size it loses to its item; a pilot entering space takes up its squadrons out in the system (position not kept). | `fighters.rs`, `SpaceFightersRemainderTests` |
| DONE (protocol 36) | Kamikaze: the squadron closes in on its target to its range and detonates (`SimAction` tag 23); missiles apply the explosion's application factor and check the reach (tag 24). | `fighters.rs`, `SpaceFightersRemainderTests` |
| DONE (track C3, protocol 37) | LaunchBomb: the blast is the shared area of ADR 0011 (`SimAction.Blast`, tag 25; `Scene::blast_at` over `ships_in_area` and `damage_ship`). The squadron names a point (`ActivateAbilitySlotsAtPoint`), the bomb is the squadron type's bomb (SDE 2324), the blast is its `empFieldRange` (else `explosionRange`) and four damages, applied once when the 60 s window ends; refused in high and low security by the ability's flags. Not run in the client. | `FighterLaunchBombTests` (host), `deployable::tests::a_fighter_blast_*` (sim), `SimCodecTests`, `simlink_fixtures`; DEVIATIONS, Fighter LaunchBomb |
| DONE (protocol 32) | Protocol: fighter kinds 54 to 60, `LaunchFighter` with the squadron size, GOTO_POINT (kind 59), `FighterSize` (kind 60), Tackle remote tag 12 | `link.rs`, `SimCodec.cs`, `oracle/fixtures/simlink/messages.json` |
| REPLACED | Live recording against EVE.js (WP-S, `oracle/fixtures/scenarios/fighter_flight.json`): no graphical client runs on this host, so no recording was made. The wire shapes are pinned by `oracle/fixtures/simlink/messages.json`, whose fighter entries were written from the EVE.js and eve-logic source (see DEVIATIONS, Player fighters) | ADR 0015 WP-S |

## 14. Windows and the production start (2026-10-09)

The start scripts are `tools/windows/` (`start-server.bat`, `start-server-dev.bat`, `stop-server.bat`, the `.ps1` files) and
`tools/server/start-server.sh`; the tested and untested parts are listed in [tools/windows/README.md](../../tools/windows/README.md).

| Status | Item | Missing work |
|---|---|---|
| OPEN | Windows is not in CI | No Windows job runs `cargo build`, `dotnet test` or the scripts. Add one, or keep the verification in the README current. |
| OPEN | `.bat` files and PowerShell 5.1 | Run `start-server.bat`, `stop-server.bat` and the `.ps1` files under Windows PowerShell 5.1 and `cmd.exe` on a real Windows machine with PostgreSQL 18. |
| OPEN | Windows console behaviour | Verify Ctrl-C (and the `Terminate batch job` prompt) and closing the window; closing does not run the orderly shutdown. |
| OPEN | `global.json` SDK path `../.dotnet` | Confirm the SDK resolves on Windows without that directory. |
| OPEN | Live test runners on Windows | `tools/dev/run-eve-live.sh`, `run-evejs-live.sh` and `pg18.sh` are bash and use `.venv/bin` and `/tmp`; port them if Windows must run the live tests. |
| OPEN | Host `tcp:` listener | `HostListener` takes numeric IPv4 addresses only (`IPAddress.Parse`); accept host names or IPv6 if needed. |
| DONE | Platform-aware defaults | `eve-net` (`DEFAULT_HOST`), `eve-sim` (`127.0.0.1:26011` off Unix) and `HostConfiguration.DefaultEndpoint`/`DefaultSimSocket` default to TCP on Windows; unit tests cover them. |
| DONE | Ctrl-C of the simulation | `eve-sim` races the connection against Ctrl-C; before, it ignored Ctrl-C while a host was connected. |
| DONE | Line endings | `.gitattributes`: `* text=auto`, `.sh` LF, `.bat`/`.ps1` CRLF. |

## Closed items that should stay closed

- `storeManager.buy_offer` and `insuranceSvc.InsureShip` / `UnInsureShip`: existing persistence providers are connected.
- FleetProxy's seven operations plus FleetObject advert creation/setup callbacks: connected and tested; only the legacy-row limitation above remains.
- `station.GetGuests`: alias route, wire shape, observer ledger seed, live-session `warfactionid`, and station/structure join/leave broadcasts are implemented and unit-tested.
- `CreateCharacterWithDoll`: starter inventory, modules, skills and wallet persist; the new character's welcome mail/notification is durable before selection, and `GetCharacterSelectionData` reads the live unread-mail count.
- `SelectCharacterID`: named arguments, `OnCharacterSelected("clientID", [])`, session state, station/structure/space selection, deferred docked ship-id and fitting bootstrap, first-space-List visual prime, ship/module Dogma hydration, and saved-pose space resume are covered by PostgreSQL and TCP headless tests.
- The observed post-selection bootstrap calls `config.GetBlackListedPlanets`, `ProjectDiscovery.is_enabled`, `charMgr.MachoResolveObject` for global assets container 10002, and `jumpCloneSvc.MachoResolveObject` have focused wire-shape tests. Paper-doll normalization has a regression test for missing sculpt weights and `SculptingRow` headers. Data-backed gaps remain listed above.
- `GetCharacterSelectionData`: full rows keep a renamed ship, report live skill-training projection and relation-backed security status, and derive the latest finished-skill count from history; same-FILETIME completion batches remain indistinguishable.
- `industryMonitor.ConnectJob`: inventory-flag filtering and blueprint-category exclusion are implemented and tested.
- `structureHangarViewMgr.GetMyHangarViewState`: existing persistence callback is connected.
- publicQaToolsServer.SlashCmd delegates to the existing slash service. CanGiveItemForMultifit returns false in EveJS and RS, so those responses are aligned.
- SignalTracker registration: Host callback and session registration are implemented; the snapshot producer remains open.
- `BeyonceService.CurrentFileTime`: the alleged compile failure does not reproduce on main; the FILETIME helper and Fleet tests/builds pass.

## Source files for the open provider checks

Re-read these files before implementing a provider; the Host composition root determines whether an optional callback is actually connected.

| TODO group | Current source to inspect |
|---|---|
| Global config and dynamic-item mutations | [MachoNetService.cs](../../dotnet/src/Eve.Host/MachoNetService.cs), [DynamicItemRpcService.cs](../../dotnet/src/Eve.Host/DynamicItemRpcService.cs), [Program.cs](../../dotnet/src/Eve.Host/Program.cs) |
| Insurgency, ESS, Abyssal readiness, and SignalTracker | [FactionActivityRpcServices.cs](../../dotnet/src/Eve.Services.Core/FactionActivityRpcServices.cs), [EssMgrService.cs](../../dotnet/src/Eve.Services.Space/EssMgrService.cs), [LoginInitializationRpcServices.cs](../../dotnet/src/Eve.Services.Core/LoginInitializationRpcServices.cs), [SignalTrackerRegistrationProvider.cs](../../dotnet/src/Eve.Host/SignalTrackerRegistrationProvider.cs) |
| POS, station guest notifications, and public QA | [PosMgrService.cs](../../dotnet/src/Eve.Services.Core/PosMgrService.cs), [StationService.cs](../../dotnet/src/Eve.Services.Station/StationService.cs), [StationGuestLedger.cs](../../dotnet/src/Eve.Services.Station/StationGuestLedger.cs), [UnportedFeatureRpcServices.cs](../../dotnet/src/Eve.Services.Core/UnportedFeatureRpcServices.cs), [Program.cs](../../dotnet/src/Eve.Host/Program.cs) |
| Structure operations and container/compression hooks | [StructureRpcServices.cs](../../dotnet/src/Eve.Services.Core/StructureRpcServices.cs), [StructureDataRpcServices.cs](../../dotnet/src/Eve.Services.Core/StructureDataRpcServices.cs), [BeyonceService.cs](../../dotnet/src/Eve.Services.Space/BeyonceService.cs), [Program.cs](../../dotnet/src/Eve.Host/Program.cs) |
| MCT, dungeon defaults, and extraction authorities | [UserSvcCompatibility.cs](../../dotnet/src/Eve.Services.Core/UserSvcCompatibility.cs), [StoreOffersService.cs](../../dotnet/src/Eve.Services.Core/StoreOffersService.cs), [DungeonRpcServices.cs](../../dotnet/src/Eve.Services.Core/DungeonRpcServices.cs), [GameRpcServices.cs](../../dotnet/src/Eve.Services.Core/GameRpcServices.cs), [CoreServices.cs](../../dotnet/src/Eve.Services.Core/CoreServices.cs) |

## Client preparation (tools/client-patch)

- **OPEN** — Real-client check of `tools/client-patch`: the patched `start.ini` (`server`, `cryptoPack = Placebo`) and the launch arguments have not been run against a V24.01 client. Verify that login reaches eve-net on port 26000.
- **OPEN** — Decide whether the unmodified client needs EveJS's `blue.dll` branch change (EveJS `Play.bat` refuses to launch without it; its purpose is not documented). If it is needed, record a deviation in `docs/reference/compat/DEVIATIONS.md`. Do not commit a patched binary.
- See [tools/client-patch/README.md](../../tools/client-patch/README.md) for the patch list, evidence and limits.

## Debugging tools

- `EVE_STRICT_RPC=1` (eve-host): an unknown service, an unknown method, a call with no service and an unhandled
  exception are answered with a Macho error carrying a correlation id, and logged with the stack trace to stderr. The
  default answers None, as the reference does. Not covered: services that answer None on missing callbacks by design
  (for example `StructureRpcServices`), and the unknown-method check trusts each service's `HasMethod`, which has not
  been audited against all hand-written services.
- `EVE_TRACE_RPC=1|full`: `full` adds each call's arguments and each result's type tree to the trace lines.
- `tools/mitm` captures and diffs the client's traffic against the reference. Rule and recipe:
  `docs/design/REFERENCE-TRACES.md`.

## 15. CI hygiene and the pre-push check (2026-10-09)

The primary check before a push is `tools/dev/check-all.sh`: the jobs of `.github/workflows/ci.yml` (manual only: the repository is private and Actions minutes are limited) in the same order and environment, on a local PostgreSQL 18. `CHECK_SKIP=live,dotnet` leaves steps out.

| Status | Item | Where |
|---|---|---|
| DONE | `cargo fmt --all --check` clean, with the pinned toolchain 1.94.1 (17 eve-space files were unformatted, not 8) | `rustfmt.toml`; commit "Format the Rust workspace" |
| DONE | `cargo clippy --workspace --all-targets -- -D warnings` clean on 1.94.1 (no blanket `allow`) | `crates/eve-space` |
| DONE | `Sim::handle` split into `handle_session`, `handle_fitting`, `handle_command`, `handle_fleet`, `handle_world`; arms moved verbatim; golden fixtures unchanged | `crates/eve-space/src/sim.rs` |
| DONE | Rust job has the PostgreSQL service and `EVE_PG_URL` (postgres:// form); the .NET test step gets the Npgsql keyword string | `.github/workflows/ci.yml` |
| DONE | Live script sets `EVE_DEV_AUTO_CREATE=1`: the host refused every test login, as its header promises accounts are created | `tools/dev/run-eve-live.sh` |
| DONE | `.NET` build is warnings-as-errors already (`dotnet/Directory.Build.props`); CI does not run `dotnet format`, so it is not checked here | |
| FIXED | `test_call_request_wire_shape` expected `isBound` clear; direct calls set it since 4d289a7 | `tools/headless-client/tests/test_handshake_mock.py` |
| OPEN | Live E2E: undock and align-to-station produce one extra `DoDestinyUpdate` after `OnInvulnCancelled`. Failing: `test_a_ship_undocks_flies_and_docks_as_the_reference_did[race-1]` and `[race-4]`, `test_orders_in_space_make_the_updates_the_reference_sent[race-1]`. Reference order: `DoDestinyUpdate`, `OnInvulnCancelled`; eve.rs: plus a trailing `DoDestinyUpdate`. The invulnerability ends in `SpaceSessions.EndInvulnerability` / the target-lock path (`SpaceSessions.cs` ~1005, ~1088), after the simulation's notify; the trailing update is from a later simulation tick, not yet traced | `tools/headless-client/tests/test_movement.py:85`; run 2026-10-09 with `tools/dev/check-all.sh` |
| OBSERVED ONCE | Live run: the host's event pump reported `relation "live_<pid>_events.outbox" does not exist` at start (PostgreSQL log, one statement); the same schema was complete in the next run, and logins then failed only because of the auto-create flag. Not reproduced; watch for it | `dotnet/src/Eve.Events/EventBus.cs` (horizon query), `Eve.Data/Migrations.cs` |

Known failures in the .NET runs of 2026-10-09 (4,244 tests). The first full run had 4 failures, all on the baseline list; the final run had those 4 and 2 Safe-logoff tests:

| Test | Status |
|---|---|
| `Eve.Corporations.Tests` `Character_login_restores_direct_war_faction_then_corporation_faction` (CorpWarFaction) | fails |
| `Eve.Services.Tests` `SessionChangeTests.The_client_visible_set_is_the_reference_list` (SessionChange) | fails |
| `Eve.Services.Space.Tests` `Fleet_finder_advert_operations_match_evejs_arguments_and_persist_the_full_payload` | fails |
| `Eve.Services.Space.Tests` `When_a_session_ends_in_space_the_simulation_is_told_and_the_ship_is_put_back_in_the_hangar` | fails |
| ConfigPrime, SessionEndsHangar, capsule death | not failing in this run |
| Safe logoff: `A_movement_command_aborts_a_running_safe_logoff`, `Safe_logoff_closes_the_space_session_after_its_timer` and `The_module_orders_of_a_pilot_are_asked_one_at_a_time` (final run); `Aborting_safe_logoff_keeps_the_session_and_ship_in_space` (one rerun). Flaky: each passed when rerun alone | flaky |
| NpcBounty, Mutadaptive | not failing in these runs |

Rust: 1,272 tests passed, 0 failed. Python client: 1,066 passed, 97 skipped (the live tests need a server).

## 16. Module rules and audit remainders (track B, 2026-10-09)

Done (tests in the crates named): settle budget and stall guard, M-1 (`crates/eve-space` `modules`); warp, dock and module-activation locks from running
overlays (104, 2354, 1349), WP6 (`scene/warping.rs`, `scene/docking.rs`); the dock refusal notice, WP7 (`SpaceSessions.Movement.cs`); the module rules payload
(flag 32, protocol 33) with the group limit (763, FP-2), early deactivation (906, cloak), industrial core (3265, compressors stop with their core), reactivation
delay (669, generic), burnt-out modules (energizer), WP3, WP4, WP5 (in part), WP14; fixed 1014 repeat cap (FP-C2); tracking computer not hostile (FP-3); guidance
disruptor is an attack (FP-C6, Rust and host); the fixture generator carries the rules (`tools/dev/gen-simlink-fixtures.py`).

Track B2 (2026-10-09), done (tests in the crates named; source and mutation checks in DEVIATIONS "Module rules, track B2"): WG-1 entropic disintegrator stops off optimal (`scene`/`weapons`); WG-2 charge exemption pinned to the V24.01 client (`Eve.Dogma.Tests`); a stargate jump stops every running module, so a reactive armor hardener's profile resets (`scene/jump.rs`); a dock leaves no rack heat; FP-C4a overheat is Overloaded to dogma for a running module (`SpaceSessions.Modules.cs`); FP-10 tracking disruption reaches gunnery weapons only (protocol 35, `SimTurret.RequiresGunnery`, ADR 0013 epoch 35). FP-C3 closed: the V24.01 client reads 5804/5818 nowhere.

Open (each with its exact blocker):
- WG-3 module scripts: the tracking speed (29001) and optimal range (28999) scripts are verified with a loaded script (`ModuleScriptTests`, PostgreSQL). Sensor booster and ECCM (group 910, 41155), guidance and flex hardener (armour resistance scripts, group 1701): not started; each needs its own SDE-backed case.
- WG-4 smartbomb near gates and stations: not built; no sourced rule (wiki distances carry a [Verify] mark; EVE.js and the client have no check).
- WG-5 nanite paste while overheated: not built; no refusal text in EVE.js or the V24.01 client.
- WG-6 safety-colour gating of remote assistance and WG-7 jam weapons timer: not started; both need the Crimewatch assistance facts and the timer code, and WG-7 follows WG-6.
- WG-8 fitting restrictions on the server: not started (low priority).
- FP-8 target resistances for ewar and assistance (2112 to 2116, 2135): not started; needs `resist` fields on the ship's sensors block and on each `Remote` (a second wire change, not in this bump).
- FP-C4b and FP-C5 (refresh running effects when overheat or a computer changes): not started; needs a new `SimUpdateEffect` message (wire).
- FP-C7 ECM burst aggression and its offensive gate: DONE (track B6a, protocol 46, section 24).
- FP-C8 automatic reload from the hold: not started. EVE.js queues the reload when the last round is spent (`localCycleRuntime.js`) and resumes at the next cycle; eve.rs would need the host to reload and re-activate and to suppress the no-charges error that the simulation sends now.
- Cloak types (covert, prototype, non-warp cloak states): not started.
- M-6 scanner, analyzer and relic modules: not started (Cargo and Ship Scanner results, Data Analyzer and Subverter bodies).
- M-3 leftovers outside track A: not started.
- WP13 mode events (MR-D2): stays deferred. No subscriber exists in the reference, and a subscriber with no consumer is the no-op AGENTS rule 5 forbids.
- WP16 bastion micro jump drive block: needs the client's modifier 5788 to 6658 mapping (MR-D7), which the decisions brief marks as a client-derived table needing sign-off; not built.
- WP17 stacking: the penalty curve, operations and unpenalised categories match the V24.01 client (`eve-logic dogma/attributes/stackingNurfedAttribute.py`, `const.py`); the SDE `stackable=false` flag selects the penalty (`DogmaWorld.ApplyModifiers`). The assistance and jam overlays are stackable=true in the SDE (2112 to 2424, 2253), so their modifiers multiply unpenalised (`itemAttributeFactory.py:35`). The sim-side overlays themselves are not built.
- WP19 velocity limits (1334, 2608): the bomb launcher (2608) is refused outside its band and the refusal is tested (`deployable::tests::a_bomb_launcher_refuses_a_ship_at_rest_and_releases_its_bomb_once_the_ship_moves`). The cynosural field generator (1334) has a body since track B7 (section 22); its band is reached through the generic velocity rule.
- 906 eject and logoff, 1934, WP12: 1934 is the SDE text (default 1: an offensive module deactivates when its pilot disconnects). The path is `OnSessionClosedAsync` (`SpaceSessions.cs:820`) to `_sim.Leave(LoggedOff)` to `Scene::leave` (`crates/eve-space/src/scene/docking.rs:64`), which drops the pilot and its running effects (`modules::tests::a_ship_that_leaves_takes_its_effects_with_it`). Open: a bastion (1934 = 0) keeps running after its pilot logs off in CCP; eve.rs has no logged-off ship left in space to keep it running. The 906 log-out timer is not built.
- Reactivation refusal and MAX_GROUP_ACTIVE: the refusal does not carry the time since the stop or the running count yet (the client's `ModuleReactivationDelayed2` and `EffectCrowdedOut` need them).
- Reactive armor hardener: restart clears the profile (in memory, by design); death and gate jump are covered (section 8b). A host-level test of a jump with a running module is not in this change.
- Protocol: main is at 34 (the bombs and bubbles track, `mod-a`); this track's bump is 35 (ADR 0013 epoch 35). Re-check `main` before a push.

## 17. Module rules track B3 (2026-10-09)

Done (decisions and tests in DEVIATIONS "Module rules, track B3"): WG-4 smartbomb refused within the larger of 5 km and its range of a station or stargate (`crates/eve-space` `modules/activation.rs`, host notice in `ModuleRefusals.cs`); WG-6 safety-colour policy pinned by a test (the Crimewatch policy already applied it); the bastion micro jump drive block, WP16 (`BuiltInEffects.ClientModifiers`, the loader in `DogmaStatic.cs`, the host refusal `MODULE_BLOCKED` in `SpaceSessions.Modules.cs`); cloak types verified present.

Decided, not applied: WG-7 (no source says a jam gives the target a weapons timer; EveJS starts none); WG-8 (no fitting restriction in the SDE for the heavy assault launchers); the 906 eject rule (eve.rs has no eject command).

Open, each with its exact blocker:
- WG-5 nanite paste repair: the client's `godma.RepairModule` and `StopRepairModule` have no host handler; paste repair, its clock, its cancellation on a jump and the overheat interlock need building. Heat repair on a dock is done (B2).
- WG-3 module scripts: PostgreSQL cases per script family are in section 22 (track B7); the disruption scripts are open there.
- FP-8, FP-C4b, FP-C5, FP-C7: each needs a wire change (resist fields, `SimUpdateEffect`, a burst's jammed ships).

- FP-8, FP-C4b, FP-C5: each needs a wire change (resist fields, `SimUpdateEffect`). FP-C7 is closed (section 24).
- FP-C8 automatic reload: the simulation ends the effect at the last round (`NO_CHARGES`) before the host sees the spent charge; a pending-reload state is needed in the simulation.
- M-6 scanner, analyzer and relic modules: no simulation bodies.
- WP17 assistance and jam overlays (the sim-side overlays are not built); WP19 cynosural field (the body is built in track B7, section 22, with open items there); WP13 mode events (no subscriber, so none is published).
- Host-level database tests for the smartbomb refusal and the bastion refusal are not written (unit and simulation tests only).

## 18. Alliance registry (2026-10-10)

The client-facing allianceRegistry paths used by V24.01 alliance wrappers now persist bulletins, executor support, member departure, alliance war bills, bill payment and GML application acceptance. Member-affiliation, alliance and corporation notifications are emitted after the database transaction. alliance_employment records a null-alliance row when a corporation leaves so GetEmploymentRecord retains the transition.

Open before calling Alliance parity complete:

- Maintenance invoices, executor-wallet auto-payment, paid-cycle renewal and bill-issued notifications are implemented. Unpaid-alliance closure and last-member closure now run at daily downtime; they snapshot members, close applications, release corporation/character affiliations, drop SOV claims, inherit active wars to member corporations and end source Alliance wars. Confirmed by `Unpaid_alliance_maintenance_closes_at_downtime_and_releases_member_affiliations`, `Last_alliance_member_leaves_but_closure_waits_for_next_downtime`, `Unpaid_alliance_closure_inherits_current_wars_before_ending_the_alliance_wars`, and `Closing_an_alliance_drops_only_its_sovereignty_claims_and_hub_projection`. Sources: CCP [Corporation/Alliance Closure](https://support.eveonline.com/hc/en-us/articles/203209062-Corporation-Alliance-Closure) and [Mutual War](https://support.eveonline.com/hc/en-us/articles/360005659379-Mutual-War); EveJS v0.12.9 commit `828823a75f211fdecb945b087a26c225f111491f`, `allianceMaintenanceBilling.js` (273-475), `allianceDepartureState.js` (588-688, 1156-1436), `allianceWarInheritance.js` (108-434), `allianceSovereigntyLifecycle.js` (1098-1346).
- Recurring type-4 war bills now use transactionally scheduled 7-day due jobs, debtor auto-pay (executor wallet for Alliance bills), prior-due renewal, and durable default/inactive/orphaned/superseded processing. Unpaid wars carry `reasonEnded=10` through the official 24-hour end window before relation expiry. PostgreSQL coverage: `A_departing_alliance_member_inherits_the_active_war_and_processes_due_war_bills`. Sources: CCP [Wars](https://support.eveonline.com/hc/en-us/articles/115004152745-Wars); EveJS v0.12.9 commit `828823a75f211fdecb945b087a26c225f111491f`, `warRuntimeState.js` (217-235, 974-1064).
- Intentional EveJS difference: unpaid war bills do not create its 14-day `PEACE_REASON_UNPAID_BILL` treaty. CCP's [Wars](https://support.eveonline.com/hc/en-us/articles/115004152745-Wars) rules allow re-declaration once the 24-hour cooldown ends; the 14-day restriction applies to surrender or War HQ loss. V24.01 `eve/client/script/ui/shared/neocom/corporation/war/warDeclare.py` disables normal war targets with outgoing treaty rows, so copying EveJS here would also disable the declaration UI.
- When a corporation leaves an otherwise active alliance, its SOV system and core TCU/IHub ownership now transfers to the active executor through a durable job scheduled in the departure transaction. Verified by `Alliance_departure_schedules_sovereignty_transfer_to_the_active_executor`, `Alliance_departure_transfer_still_runs_if_leaver_joins_a_different_alliance_before_job`, `Alliance_departure_transfer_waits_until_the_suspended_alliance_has_an_executor`, and Host persistence test `Leaving_corporation_sovereignty_and_core_structure_owners_move_to_the_executor`. Reference: EveJS v0.12.9 commit `828823a75f211fdecb945b087a26c225f111491f`, `allianceSovereigntyLifecycle.js` (387-471, 753-1063).
- `GetAllianceSovereigntyStructuresInfo` returns the alliance TCU/IHub rows and campaign scores from the SOV read model. V24.01 client evidence: `Fokiiiiiii/eve-logic` commit `1dbb5711a072a146e1d228365668ac05655eefe1`, `eve/client/script/ui/services/sovSvc.py` (332-371) and `corporationWindow.py` (402-414). EveJS v0.12.9 commit `828823a75f211fdecb945b087a26c225f111491f`, `allianceRegistryRuntime.js` (1082-1087) and `sovState.js` (1109-1404). PostgreSQL test: `Alliance_sov_and_owner_queries_return_rows_from_their_authoritative_sources`.
- Alliance `GetEveOwners` returns requested character, corporation, alliance, NPC-character and faction owner rows from persisted stores and the matching SDE. EveJS v0.12.9 commit `828823a75f211fdecb945b087a26c225f111491f`, `allianceRegistryRuntime.js` (1222-1236). The direct Alliance-side V24.01 callsite remains unverified; the observed corporation owner-row consumer is `Fokiiiiiii/eve-logic` commit `1dbb5711a072a146e1d228365668ac05655eefe1`, `eve/client/script/ui/services/corporation/bco_members.py` (134-143).
- Real graphical-client verification remains unrun.

## 19. Module rules track B4 (2026-10-09)

Done (decisions in DEVIATIONS "Module rules, track B4"; tests in the projects named): WG-5 nanite paste repair end to end (`dotnet/src/Eve.Ships/CharacterShips.Repair.cs`, `Eve.Services.Space/SpaceSessions.Repair.cs`, `Eve.Services.Dogma/DogmaIMService.Repair.cs`; tests `RepairTests`, `ModuleRepairHostTests`); the 906 eject refusal (`SpaceSessions.Ships.cs`); WP13 mode events (`ShipModeTopics`, `ShipModeEventTests`); FP-8 target resistance, host-side (`Effects/RemoteResistance.cs`, `RemoteResistanceTests`); WG-3 sensor booster strengths (`SensorBoosterEffect.cs`, `ModuleEffectsTests`); WG-7 and WG-8 decided (DEVIATIONS). The protocol is not bumped in this track.

Open, each with its exact blocker:
- WG-5: a repair ended by a leave takes its paste and damage, but the client is not told the cargo change then (no notification path from the space layer to the client's cargo for a ship that has left). The rate and cost come from the character's dogma world; only the no-skill values are tested.
- WG-3: the tracking and optimal range scripts are verified with a loaded script (`ModuleScriptTests`, through the charge pairing in `Eve.Dogma/DogmaWorld.cs`, `otherID`). Guidance, flex hardener and ECCM are verified with a loaded script in track B7 (section 22).
- FP-C4b and FP-C5 (refresh a running effect when the overheat or a computer changes): not built. Closed in track B5 (section 20). Needs a new `SimUpdateEffect` message: `crates/eve-space/src/link`, `SimCodec`, the Python generator `tools/dev/gen-simlink-fixtures.py`, and one protocol bump at the next free number (37 if track C2 takes 36).
- FP-C7 (an ECM burst: the ships it jams, and its offensive gate): closed in track B6a (section 24).
- FP-C8 (automatic reload from the hold): not built. Closed in track B5 (section 20). The simulation ends the run at the last round with `NO_CHARGES` (`crates/eve-space/src/modules/effects.rs` around 613) before the host sees `ChargeSpent`. The design: a pending-reload state that waits for the next cycle boundary for an `AdjustCharge` from the host (the message exists, `SimAdjustCharge`), and resumes if one arrives. Needs the simulation change and host tests.
- M-6 (Cargo Scanner, Ship Scanner, Data Analyzer, Subverter, relics): not built. The reference is EVE.js `server/src/space/modules/scannerModuleRuntime.js` (ranges, rows, and the client notifications `OnCargoScanComplete` and `OnShipScanCompleted`); none of the simulation bodies exist.
- WP17 (assistance and jam overlays): the resistance part is FP-8. The stacking rule is the SDE stackable flag (all overlay attributes are stackable=true, so no penalty; client `itemAttributeFactory.py:35`). EveJS gives one resistance per remote (`hostileModuleRuntime.js`, `assistanceModuleRuntime.js`).
- WP19 (velocity limits 1334 and 2608): the rule and the simulation check are in track B5 (section 20). The bomb launcher has a body (`BombEffect`) and its band is tested; the cynosural field generator has no body.
- WP12 and 1934 (logout and the 906 log-out timer): the disconnect path is located (TODO line 388); the bastion continuation (1934 = 0) and the 906 timer are not built.
- Host PG tests for the sensor booster strengths in a fitted scene: the effect is tested through `ModulePackages` only.

## 20. Module rules track B5 (2026-10-09)

Done (decisions in DEVIATIONS "Module rules, track B5"; wire in ADR 0013 epoch 38, protocol 37 to 38 in one bump; track C3 took 37):
- FP-C4b and FP-C5 (refresh a running effect when an overheat or a computer changes): `SimUpdateEffect` (kind 61), applied by the simulation at the start of the effect's next cycle; the host rebuilds each running effect with `ModulePackages.Of` after overheat, activation, deactivation, offline and a simulation end, and sends it only when the numbers differ (`crates/eve-space/src/modules/cycle.rs` `refresh_running`, `dotnet/src/Eve.Services.Space/SpaceSessions.Refresh.cs`). Tests: `a_refresh_of_a_running_effect_takes_effect_at_the_start_of_its_next_cycle`, `RefreshHostTests`, the codec fixtures. The computer path shares the activation refresh and has no test yet.
- FP-C8 (automatic reload from the hold): the simulation parks a reloading turret when its rounds run out (`Charge.reloads`, `crates/eve-space/src/modules/activation.rs` `park_for_reload`, `adjust_charge`); the host loads the kind from the hold when a gun runs dry (`GunRanDry`, `DogmaIMService.Reloads.cs` `ReloadEmptiedGunAsync`) and always answers the simulation. Tests: `a_turret_that_reloads_waits_for_its_load_and_resumes_with_it_at_the_next_cycle`, `a_reloading_gun_that_is_switched_off_while_it_waits_stops_at_once`, and `ReloadServiceTests` (two PostgreSQL cases). Client reload window: the in-space load's notices, not checked against the client.
- WP19 (velocity limits 1334 and 2608): the rule fields are on the wire (protocol 38) and the simulation refuses an activation outside the band (`velocity_refusal`). The bomb launcher reaches it (tested); the cynosural field generator does not (no body).

Open, each with its exact blocker:
- FP-C7: closed in track B6a (section 24).
- M-6 (Cargo Scanner, Ship Scanner, Data Analyzer, Subverter, relics): no simulation bodies; EVE.js `scannerModuleRuntime.js` is the reference and was not ported in this track.
- WP17 (stacking of overlapping assistance and jam modifiers): answered by the SDE stackable flag (all overlay attributes are stackable=true, so no penalty; client `itemAttributeFactory.py:35`).
- WG-3 (scripts and charges with a loaded script): tracking and optimal range have PostgreSQL cases (`ModuleScriptTests`); flex, ECCM and guidance have none yet.
- WP12 and 1934 (`deactivateIfOffensive`, SDE default 1): located. `OnSessionClosedAsync` (`SpaceSessions.cs:820`) calls `_sim.Leave(LoggedOff)`, and `Scene::leave` (`scene/docking.rs:64`) drops the pilot, so default-1934 modules stop. A bastion (1934 = 0) is not kept running after logoff (open, DEVIATIONS).
- Host PostgreSQL run (the Safe logoff refusal): closed in track B6a (section 24).

## 21. Scanner modules (track M-6, 2026-10-09)

Done (decisions in DEVIATIONS "Scanner modules, track M-6"; wire in ADR 0013 epoch 40, protocol 39 to 40 in one bump; track C3 took 39):
- Cargo Scanner (effect 47) and Ship Scanner (effect 46). Ranges and falloff come from the SDE effect records (cargo 126 with no falloff; ship 125 with the
  falloff 510). One cycle (attribute 1014). The simulation decides the lock and the reach at activation and at the cycle's end (`crates/eve-space/src/modules/`,
  `Action::Scan`). The host settles the reveal through the one work-cycle dispatcher (`dotnet/src/Eve.Services.Space/SpaceContainers.Scan.cs`, rules in
  `Scanning/ScanRows.cs`). `OnCargoScanComplete` and `OnShipScanCompleted` go to the pilot alone, once per granted cycle (a repeat is answered from the record).
  This is the cargo and ship part of the wiring gap E-08.
- Refusals by name: `TARGET_REQUIRED`, `TARGET_SELF` (`DeniedTargetSelf`), `TARGET_NOT_FOUND`, `TARGET_NOT_LOCKED`, `TARGET_OUT_OF_RANGE` (the client's
  `TargetNotWithinRangeGeneric` with the reach), `UNSUPPORTED_EFFECT` for a scanner with no range, and `That ship is resistant to your scan.` when the target's
  scan resistance (188, 511) takes the cycle.
- Tests: sim `modules::tests` (the six `a_scan*` and `a_scanner*` tests), simlink fixtures (two valid scans, two invalid scan frames, an unknown work command),
  `ScanRowsTests` and `ModuleEffectsTests` (the scanners' build), PostgreSQL `ScannerHostTests` (cargo, ship, refusals and replay).

Open, each with its blocker:
- Data Analyzer (`doHacking`, effect 1738), Relic Analyzer (same effect) and Data Subverter (`hackOrbital`, effect 4773): not started. The reference is EVE.js
  `moduleOperation.js` (the analyzer section) and the hacking window of the client; the minigame and the loot container interior are the only named refusal
  allowed for them, and neither is written. The EVE University wiki "Hacking" page gives the analyzer numbers (base virus coherence 40 and strength 20 for tier 1,
  60 and 30 for tier 2; Hacking and Archaeology add 10 coherence per level; the minigame's failure penalties). It does not give the range, the cycle, the activation
  or the Data Subverter: those stay on the SDE attributes (54, 73, 902, 1909 to 1911). The body is blocked: eve.rs has no hackable site entity (a materialised
  container with a hacking state) for the target to be validated against.
- Survey Scanner: not part of this runtime. The reference's scanner module runtime has no survey effect; `surveyScan` belongs to the mining chipsets (section 7).
- Resistance at activation: EVE.js refuses the start before a cycle is spent. The host reads the target's resistance at the cycle's end, so the eve.rs scanner
  spends one cycle. A pre-activation check needs the target's resistance in the activation request (a wire field).
- NPC targets: a target needs an item record for its hold and fittings. NPC ships have none, so they are refused as `NO_TARGET`.
- Probe lifecycle notifications (E-06: `OnNewProbe`, `OnProbeWarpStart` and the rest): not started; they depend on `LaunchProbes` (RC-7).
- Client check: not run. The MITM recipe (tools/mitm/README.md) needs a live EVE.js run and a headless scenario that scripts a scanner; neither exists.

## 20. Track C4: command bursts, buff registry, P.A.N.I.C. (2026-10-09): specified, not built

Sources, the SDE facts and the blockers are in DEVIATIONS "Track C4". Summary of what is open, in order:

- OPEN (data): the burst value chain (effect 6737, `warfareBuffNValue` = module value times the charge multiplier, module value default 0) evaluates to 0 on this SDE build, and EveJS reads the same chain. A reference trace of a live burst's buff values is needed before any value is written.
- BLOCKED (wire, one batch, one bump at the next free epoch): a burst action carrying the fleet's recipient list per cycle and the buff set; a live-numbers message for running recipient modules; the codecs, `tools/dev/gen-simlink-fixtures.py` and the fixtures.
- NOT BUILT: the buff registry (persistent source list on `DogmaWorld` items, expiry by the scheduler, restart drops it, client `OnDogmaAttributeChanged` for the recipients). Built with its first consumer, the burst.
- NOT BUILT: P.A.N.I.C. (42522). Its fleet effect needs the registry, the wire batch, and the simulation state for shield resistance, shield recharge, drone damage, weapons, mass, scan resolution, entosis and jump/dock/tether flags.
- NOT RUN: a MITM capture of a burst or P.A.N.I.C. cycle (`tools/mitm/README.md`). No scenario was driven.

## 22. Module rules track B7: ECCM, scripts, the cynosural field generator (2026-10-09)

Done (decisions and sources in DEVIATIONS "Module rules, track B7"; ADR 0017 for the wire):
- WG-3 ECCM, self-applied (effect 2231): runs through the dogma modifiers (`DogmaModifierEffect`); the projected ECCM stays pending. Tests: `ModuleScriptTests` (ECCM Script on ECCM - Radar I, category 7 with its category 8 script).
- WG-3 flex armour and shield scripts (41517 to 41524): PostgreSQL cases in `ModuleScriptTests` (the hardener's bonus and the ship's resonance it sets).
- WG-3 guidance scripts (Missile Range and Missile Precision, 35794 and 35795) on the Missile Guidance Computer: PostgreSQL cases in `ModuleScriptTests`.
- The cynosural field generator (21096, 28646, 52694) has a body: `CynoGeneratorEffect` (host) and `SimAction.CynoField` (tag 27, protocol 41). The field is released at the end of the cycle, lives explosionDelay (600000 ms), costs the cycle's fuel through the shared mechanism, and is announced with `AddBalls2` and removed with `RemoveBalls`. Tests: `deployable::tests::a_cynosural_field_is_lit_...` (sim), the golden entry in `messages.json` (codec), `CynoFieldHostTests` (fleet and high-security refusals, PostgreSQL).
- Refusals: a pilot outside a fleet (`CYNO_NOT_IN_FLEET`, the host's fleet store through `SpaceSessions.IsInFleetAsync`), a fleet member in high-security space (`CantInHighSecSpace`). The generic 500 m/s rule (1334) applies.

Open, each with its exact blocker:
- Jump-in restrictions (normal fields to fleet members with jump drives, covert to Black Ops, industrial to jump freighters and capital industrial ships): no capital jump drive in eve.rs.
- Mobile Cynosural Inhibitor (200 km lighting block): no inhibitor body.
- Cynosural System Jammer (E-03): the host callback `BeyonceService.CynoJammerOnlineSimTimeForSystem` is not assigned, so the jammed state has no source.
- Fleet beacon state when a field is lit (EveJS `setFleetCynoBeaconState`): no beacon state in eve.rs.
- Persistence (ADR 0012): the field is a sim-timed deployable, so a restart drops it; the `Deployed` record and the sweep are not built.
- The positive lighting path has no host PostgreSQL test: the rig's station is in Manifest (high-sec), and no low-sec undock is in the rig.
- The Guidance Disruption scripts (40334, 40335): not tested; their launcher group is the computer's (1396) but the computer's charge group is 1400, so the pairing is not decided.
- The projected ECCM (2246): pending.
- WP12 bastion and 1934 (keep running after logoff): not built. The wiki page says nothing about logoff; the SDE sets 1934 = 0. Needs a logged-off ship that stays in space.
- WP17 sim-side assistance and jam overlays: not started; the part to build is not settled in the sources read.
- E-06 probe lifecycle notifications: not started; needs the probe launcher (`LaunchProbes`, RC-7).
## 21. Track C5: command bursts and the buff registry (2026-10-09)

This supersedes the OPEN data item of section 20: the burst strength is on the SDE (the charge's multiplier, 2596, times the module's value, 2469), and the C4 reading of 0 was wrong. Sources and choices are in DEVIATIONS, "Command bursts and the buff registry (track C5, 2026-10-09)".

DONE (dogma library and the host's recipient rule, tested on the real SDE where the data is):
- Burst strength from the SDE: `CommandBurstValueTests` (Tech I Shield Harmonizing -8, Tech II -10; the shield resistance buff scales the ship's resonances by 0.9 and runs out at 60 s).
- The buff collections (`sde.dbuffCollections`) load as `BuffCollection` (operation, aggregate mode, item, group and skill modifiers).
- `BuffRegistry`: timed inputs per item and collection; the strongest (Maximum) or weakest (Minimum) active input; active up to and including the expiry; a refresh is a new input; prune; forget; restart drops everything. EVE.js rules. `BuffRegistryTests` (8: aggregation, expiry, invalid input, restart, sync, the stacking penalty).
- `DogmaWorld.SetBuffs`: an item's buffs run as passive effects with their own values (`Modifier.Literal`); a buff's stacking penalty follows its attribute, as EVE.js does.
- The per-cycle recipients, `BurstRecipients.Resolve`: the source, fleet or affinity members in range (surface distance plus one metre), at most 256 ships with the source among them (wiki). `BurstRecipientsTests` (8).

OPEN (each with its blocker):
- The burst module's activation, cycle, charge use and refusal. It is still refused as `UNSUPPORTED_EFFECT` (`dotnet/src/Eve.Services.Space/ModulePackages.cs`). Blocker: the cycle must be parked and settled each cycle on the work-cycle path (`SpaceContainers.Work.cs`, ADR 0013), with the recipient list and the buff set on that path.
- The wire for a burst: a work-cycle command (or a `SimAction`) that carries the recipients and the buff set, its reply, and a live-numbers message for the recipients' running modules (the simulation reads a running effect's numbers at activation, `SimEffect.on_changes`, and they change while the modules run). Blocker: a protocol change. The Rust link and `SimCodec.cs` both say `PROTOCOL_VERSION` 35 on this tree, while the task names protocol 40 on main; that must be reconciled before the next number is taken.
- The client notification: eve-logic's `OnDbuffUpdated` (the Destiny payload that EVE.js sends from `buildClientDbuffStateEntries`). The C# host has no sender; `OnDbuffUpdated` does not occur in `dotnet/`.
- Expedition bursts (charge group 4905, EVE.js `EXPEDITION_DBUFF_COLLECTION_IDS`): not built.
- The Phenomena Generator (`moduleTitanEffectGenerator`, EVE.js): not a command burst; not built.
- P.A.N.I.C. (42522): its body is not built. Its fleet effect needs the registry (built), the disallow collections (59 weapons, 61 entosis), shield resistance and recharge in the simulation, drone damage and the warp penalty. The wiki's rules (Capital Ship Modules, read 2026-10-09): a Rorqual's high slot module, activated with an asteroid or ice asteroid targeted; the Rorqual and the mining and industrial ships of its fleet within 200 km are invulnerable (other Rorquals are excluded); protected ships cannot move, deal damage, warp, cloak, dock, tether or jump; SCARAB Breacher pods of the Deathless Circle bypass it; single use; 4 minutes, up to 6 with Invulnerability Core Operation. The wiki gives no cooldown, fuel or per-level bonus. Blocker: the simulation state (wire) for the shield, drone, weapon and warp numbers, and a source for the per-level bonus.
- MITM: no scenario driven (`tools/mitm/README.md`); there is no client trace of a burst cycle yet.
- Skill, ship and mindlink bonuses are not tested with a skilled character; the wiki's Max column (Shield Harmonizing +23.44 %) is the check.

## 22. Track C6: command bursts end to end, P.A.N.I.C. (2026-10-09)

Supersedes the OPEN items of section 21. Sources and choices: DEVIATIONS, "Command bursts end to end and P.A.N.I.C. (track C6, 2026-10-09)". Wire: protocol 43 (ADR 0013, epoch 42).

DONE (each has tests; the PostgreSQL-backed ones are listed as run under the shared lock in the track's report):
- The burst's activation and cycle: `CommandBurstEffect` for the five families and the expedition bursts (`Charged`, one round a cycle; no charge is `NO_AMMO`). At each cycle's end the simulation asks the host with the ships it found in reach (`WORK_BURST`, the recipients on `ModuleWorkCycle`). The host keeps the source's fleet, caps at 256, applies the buffs through the registry for the buff duration, sends each granted ship its numbers and running modules, and tells its pilot its state (`BuffState`, the simulation's `OnDbuffUpdated`). The sweep ends buffs that ran out and tells their pilots. Tests: `modules::tests` (burst cycle, reach, refusal), `CommandBurstHostTests`, `CommandBurstFamiliesTests` (SDE: Armor, Information, Mining Foreman, Shield, Skirmish, Expedition, named and presidential), `BuffRegistryTests`, the fixtures (`simlink_fixtures`, `SimCodecTests`).
- P.A.N.I.C. (42522): the start needs a locked asteroid (the simulation: `TARGET_REQUIRED`) and parks until the host grants the fleet (`WORK_PANIC`, the ships within 200 km). The grant gives the source and the subcapital mining and hauling ships of its fleet the twelve SDE collections with the client's values, for the core's duration. The numbers reach the simulation through the ship (resonances, recharge, mass, scan resolution, warp speed). Refused by the host: warp (`WARP_SCRAMBLED`), docking, jump, and the cloak (existing check). Running modules that the buffs block are stopped. Tests: `modules::tests` (four P.A.N.I.C. tests), `CommandBurstHostTests` (grant, group, not running, SDE collections), the fixtures.
- Found on the way and fixed here: a pending start was marked active only after the simulation's answer, which a parked start gives only once it is granted, so the host's own check of a fuel ask or a grant saw the module as not running. The module is now marked while the start is pending and unmarked on refusal. The Rust decoder of module work cycles accepted only commands 7 to 9, refusing the scans (10, 11) of protocol 40; its set is now the C# codec's (7 to 13).

OPEN (each with its blocker):
- Login and re-entry: a pilot who enters space within a burst's time is not told its buff state (the client's HUD). Blocker: the enter path sends the ship but not the state. Upgrade: send `BuffState` after the ship enters (eve-logic `michelle.py` takes `bag.dbuffState` at the ball's bootstrap).
- Drone damage (the wiki: the protected ships cannot deal damage; collection 58 writes attribute 2580 on the ship): no drone path in eve.rs reads it, and running drones are not recalled. Blocker: which attribute a drone's damage takes from its ship is not read in this track.
- Velocity penalty (collection 3) writes attribute 3171, which the SDE marks as deprecated, so no top speed changes. Blocker: a source that links 3171 to the speed (the wiki's "cannot move" gives no number).
- The jump refusal's words ("You cannot jump at this time.") are ours: the client's notice is not in the tables read.
- Tether (57) and entosis (61) are applied; eve.rs has no tether or entosis use. The SCARAB Breacher bypass does not apply (no such pods in eve.rs).
- No separate state for the buffs in the simulation (the request for "general status effects" in the simulation is not built): the numbers and the host's checks carry them (DEVIATIONS).
- MITM: no client trace of a burst cycle or a grant was driven (`tools/mitm/README.md`).
- The burst wiki pages were not re-read in this track (the rules are those C4 and C5 recorded); the Capital Ship Modules page was re-read for P.A.N.I.C.
## 23. Track A5: OnDotVictimUpdated, the cynosural field record, probes, cyno jammer, jump drive and mobile objects (2026-10-09)

Done (tests in the crates named; decisions in DEVIATIONS "Track A5"):
- The Python `set` in both marshal codecs: `Value::python_set` (`crates/eve-marshal`) and `PyValue.PythonSet` (`Eve.Marshal`); `InventoryWire.Set` delegates. Round-trip tests in both.
- OnDotVictimUpdated (A4's open item): the simulation tells a ship its breacher applications as a destiny action on each hit, on each change of the working application and on each end (`crates/eve-space/src/dot_weapons.rs`, `dot_victim_update`). Tests: `dot_weapons/tests.rs`. The client was not run.
- The cynosural field record (ADR 0012, kind `cyno_field`): the simulation says a field was lit (`Deployed`, kind 63), the host keeps a row in `space.deployed` (migration 0004), the expiry sweep takes the field out and forgets the row (`RemoveDeployed`, kind 64), and a ship coming into the system puts a field back with its id and what is left of its life (`crates/eve-space/src/deployable.rs` `restore_field`, `remove_field`; `dotnet/src/Eve.Services.Space/SpaceContainers.Deployed.cs`). Protocol 44 (track C6 took 43). Tests: `deployable/tests.rs` (three), `CynoFieldRecordHostTests` (PostgreSQL), codec fixtures.

Open, each with its exact blocker:
- LaunchProbes (RC-7, E-06, section 7): not started. EveJS `services/dogma/dogmaService/moduleOperation.js` `Handle_LaunchProbes` (line 3388) and `_resolveValidatedProbeLaunchContext` (425 to 567) give the checks and messages (NOT_IN_SPACE, MODULE_NOT_FOUND, INVALID_LAUNCHER, MODULE_NOT_ONLINE, NO_CHARGES, INVALID_CHARGE, INCOMPATIBLE_CHARGE, NOT_ENOUGH_CHARGES, and the cap of 8 active probes). Blocker: no probe record, no probe object in the simulation, and no scan lifecycle (E-06's notifications follow the scan). ADR 0012 keeps probes simulation-only.
- GetCynoJammerState (E-03): the host callback stays unassigned, so the answer is None (no jammer), correct while no jammer exists. A source would be the Tenebrex cyno suppression (EveJS `services/sovereignty/sovSuppressionState.js` `getCynoJammerOnlineSimTime`, line 259) or the Mobile Cynosural Inhibitor. Blocker: neither is built.
- WP-8 mobile warp disruptors: not started. The record store exists (`space.deployed`), so a new kind is the store side. Blockers: ADR 0012's addendum says the SDE has no lifetime for them and the EveJS table is unverified; the anchoring job `space.deployed.anchor`, the custody path from cargo, the item-level anchorDrop activation and the scramble source are not built.
- Capital jump drive, cyno targeting, jump range and fuel, and the jump to the cyno field: not started. The field exists (B7, persisted by this track), so the jump can target it. Blocker: the jump-drive module body and the cross-system jump still to build; the restrictions follow the drive.
- Breacher pods (A4): the per-tick damage message (`OnDamageMessage`) has no wire shape in the sources read.
- Known: `IndustrialInvulnerabilityTests` and `IndustrialInvulnerabilityHostTests` (`Eve.Services.Space.Tests`) expect the Nexus core's `EFFECT_NOT_IMPLEMENTED` refusal. C6 (`ee324cc`) gave it a body and left those two tests stale on origin/main; they fail there too. Not changed by track A5.
