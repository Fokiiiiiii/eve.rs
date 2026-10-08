# Current TODOs

**Updated:** 2026-10-08
**Baseline:** main source commit `39fcd15224b78f94d4e2c68b0dcbc8eee2d88c6e

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
| OPEN | `station.GetGuests` join/leave broadcast side effect | Snapshot response, alias route, observer ledger seed, and ledger tests are complete. The backend that sends station guest join/leave notifications is still absent. |

## 2. Mutation authorities and specialized service providers

| Status | Service/method | Missing work |
|---|---|---|
| OPEN | `dynamicItemService.CreateDynamicItem` | Headless and PostgreSQL-tested, not yet live-verified. Done: the authority (`Eve.Inventory/Mutation/ItemMutationService.cs`: one transaction for consume, create, provenance in `inventory.item_mutations` (migration `0005_item_mutation`) and the `item.mutated` event through an injected port, typed refusals); the Host route (`DynamicItemRpcService`: `CreateDynamicItem` through the authority, `CustomNotify` refusals, `GetDynamicItemInfo` from stored provenance only, the `PyNone` stub and the customInfo path removed); one dogma factory for mutated modules, charges and drones (`Eve.Ships/ItemDogma.cs`, base type = input, skill requirements from the result, stored rolled values as own values); `OnItemChange` for the mutaplasmid, the source and the item made, after the commit and before the reply. Tests: `ItemMutationServiceTests`, `ItemMutationStoreTests`, `MutatedModuleTests` (fit, skill refusal and acceptance, rolled values, twin unchanged), `DynamicItemRpcServiceTests` (route, refusals, announcements in order). Still open: a live graphical-client run (crafting window, tooltips, how the client removes a consumed row, the place it expects for an item made); the drone launch path end to end (the factory is used, no mutated-drone test yet); the decisions D3 (volume), D5 (fitted and damaged sources), D6 (rounding) and D7 (location). The skill-handler sites (implants, boosters) are not mutable (the mapping covers modules and drones only) and are not routed through the factory. |
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
| OPEN | `cargoScan`: `CargoScanEffect` (6 types, 4 published) | The cargo reveal: `cargoScanRange` 126 (hard cutoff, no falloff); `OnCargoScanComplete`. Reference: EveJS `space/modules/scannerModuleRuntime.js`. |
| OPEN | `shipScan`: `ShipScanEffect` (7 types, 3 published) | The ship reveal: `shipScanRange` 125 and `shipScanFalloff` 510 (standard falloff curve); `OnShipScanCompleted`. Reference: EveJS `space/modules/scannerModuleRuntime.js`. |
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
| FIXED | Emergency hull energizer | Its multiplier is in section 9 (FP-01): it multiplies the ship's 113/110/109/111 by the module's 974-977. |

## 8b. Reactive armor hardener: per-cycle resistance shift (implemented, not verified with the client)

Type 4403 (`adaptiveArmorHardener`, effect 4928). While it runs the armor's resonances are the ship's own times the module's 0.85 (the seed). At the end of each
cycle (10 s) the resonances move by the SDE's `resistanceShiftAmount` (1849 = 6 points, 0.06): the kinds the armor took least rise, the others fall by
what they rose, and the total is kept. The simulation owns the profile (`crates/eve-space/src/resistance_shift.rs`, `Health::shift_cycle`); the host
(`ReactiveArmorHardenerEffect`) sends the seed, the step and the four armor attributes, and tells the client the armor's resonances at the start and the
stop. A fitted Polarized weapon pins the armor at one: the profile is then frozen (no tally, no shift), and the pin is sent with the ship (`armor_pinned`).

**Wire (W2, bumped to 22).** `SimAction` tag 12 (`ResistanceShift`) and one armor-pin byte at the end of the ship's vitals, in `link.rs`, `SimCodec.cs` and
`oracle/fixtures/simlink/messages.json` (regenerated by `tools/dev/gen-simlink-fixtures.py`). `ProtocolVersion` is 22 (bumped from 21 by the integrator at this merge, ADR 0013 rule 7); later W2 additions stay on 22 until the repo is first pushed.

| Status | Scope | Remaining work / evidence |
|---|---|---|
| UNVERIFIED-CCP | The redistribution rule | CCP gives the six points a cycle and the zero-sum total (patch notes Retribution 1.0). The donor count (at least two), the tie order (em, explosive, kinetic, thermal) and the donor cap (one) are the EVE.js and pyfa model, not CCP. A hit equal in every kind still moves the resonances (the two first declared kinds rise). Checked by the unit tests in `resistance_shift.rs` (the reference table, the total, the bounds, the stall) only. |
| OPEN | Client verification | Not run in the graphical client. Open checks: whether the client re-derives 267-270 from its own PreMul of the module (snap-back after a told change), and that the told values are shown. |
| LIMITATION | Restart and death | The profile is in memory and not persisted. A host restart ends the pilot's running modules (the active set is in memory), and a death removes the ship's health. A new activation starts from the seed. |
| LIMITATION | Gate jump | The CCP patch notes say a stargate jump resets the hardener (Inferno 1.1). Not tested: whether a jump removes the pilot's running modules in the simulation, and so the hardener, is unverified. |
| LIMITATION | Overload and stacking with static hardeners | Overload changes the cycle length, not the shift (not modelled). The armor's base includes the static armor hardeners, the profile multiplies it without a stacking penalty (unverified). |
| LIMITATION | Stop timing | As for section 8: the simulation keeps the effect to the end of its cycle; the armor is told the base when it ends. |
| DEVIATION | Model | Recorded in `docs/reference/compat/DEVIATIONS.md`. |

## 10. Mining, salvage, tractor, targeting and entosis modules (partly done)

Each effect below is in the gap manifest (`dotnet/tests/Eve.Services.Space.Tests/ModuleEffectsTests.cs`, `currentSdeGaps`), so its activation is refused, not run.

| Status | Gap | Evidence and what it needs |
|---|---|---|
| OPEN | Mining lasers (`miningLaser`, ore and ice) and gas harvesters (`miningClouds`) do not run. | No handler is registered. The work cycle needs a sim-side module cycle (a new `SimAction` tag and a `SimModuleWorkCycle`/`Result` pair): a wire change that is not made here and needs one ProtocolVersion bump by the integrator. The shared yield (`MiningYield`, `MiningProfile`) exists. Gas clouds are not generated (decision D9). |
| OPEN | Rock locks: a mineable rock is locked through `Scene::subject` (`crates/eve-space/src/targeting.rs`) with the normal range and visibility checks; a depleted rock drops its locks. | Rust tests `a_mineable_rock_in_range_is_locked_and_an_out_of_range_one_is_refused` and `a_depleted_rock_refuses_a_new_lock_and_drops_the_lock_held_on_it` pass. Not verified in the graphical client. Rock ids vs system body ids: UNVERIFIED (see DEVIATIONS). |
| OPEN | Belt rocks: one 1,000-unit seed rock per belt (`SpaceContainers.cs`, "ponytail" seed); a depleted persisted rock is not replaced; no respawn. | Decision D8 (per-belt data, restart reset) is not implemented. |
| OPEN | Salvager (`salvaging`): not run. | Needs the shared salvage cycle and the sim cycle (decision D7, SDE-derived chance from attributes 901/902; the empty chance and weights are provisional). |
| OPEN | Tractor beam (`tractorBeamCan`): not run. | Pulling a non-ship ball needs a Destiny FOLLOW check that the client accepts it (decision D6); until then the module must refuse with a named reason. |
| OPEN | Passive targeter (`targetPassively`) and auto-targeting (`targetHostiles`): not run. | The lock path already takes a `LockOrigin` (player, passive, auto). Each needs a handler and an auto-target sweep entry in the sim (decision X1). |
| BLOCKED | Entosis link (`entosisLink`): not run. | No sovereignty state exists (decision D10); blocked until the sovereignty owner is decided. |
| BLOCKED | Compression (`industrialItemCompression`, `CompressItemInSpace`): not run. | Owner C1 (structures) is open; see the `CompressItemInSpace` row above. |

## 11. Pulse Activated Nexus Invulnerability Core (P.A.N.I.C., type 42522): rules done, body pending (needs the W2 wire additions, on protocol 22)

SDE facts (`typeDogma` of type 42522, effect 6719 `moduleBonusIndustrialInvulnerability`, category 1 activation, no `modifierInfo`): `canFitShipGroup01` 1298 = 883 (Capital Industrial Ship: the Rorqual 28352 and the unpublished Rorqual ORE Development Edition 33687), `maxTypeFitted` 2431 = 1, `requiredSkill1` 182 = 44067 (Invulnerability Core Operation) at level 1, `disallowActivateOnWarp` 1245 = 1, `disallowRepeatingActivation` 1014 = 1, `panicDuration` 2788 = 240000 ms (the effect's duration attribute), range 54 = 200000 m (the effect's range attribute), `disallowEarlyDeactivation` 906 = 1, `disallowDocking` 2354 = 1 (on the module). No security attribute (1074, 1970, 5561) is on the type: the static data names no security class, so no security refusal exists.

| Status | Item | Evidence and what it needs |
|---|---|---|
| VERIFIED (tests) | Fitting: only a Capital Industrial Ship (a Rorqual; an Orca is refused with `InvalidShipGroup`), one per ship, and only a pilot trained in 44067 (`SkillRequired`). Enforced by `FittingRules` (generic). | `IndustrialInvulnerabilityFittingTests` (4 tests, real SDE, `PgFact`). |
| VERIFIED (tests) | Activation while the ship warps: refused with `MODULE_WARP_BLOCKED` before any package is built, nothing sent to the simulation. The rule reads 1245 of the module (`ModuleRestrictions.CheckWarp`) and the host's warp state (`SpaceSessions.IsWarping`, set when a warp command is accepted). | `ModuleRestrictionTests`-style unit tests in `IndustrialInvulnerabilityTests` (real SDE value of 1245), host tests in `IndustrialInvulnerabilityHostTests`. The client wording `Notify` is ours: the client has no UserError for 1245 (constant only in `dogma/const.py`), so it is UNVERIFIED in the graphical client. |
| VERIFIED (tests) | The effect is refused as `EFFECT_NOT_IMPLEMENTED` (no cycle, no capacitor, no success) at a target and without one, and it stays in the gap manifest. | `IndustrialInvulnerabilityTests`, `IndustrialInvulnerabilityHostTests`, `ModuleEffectsTests` (`currentSdeGaps`). |
| BLOCKED (needs the W2 wire additions, on protocol 22) | The fleet effect itself: while it runs, every subcapital mining and hauling ship of the Rorqual's fleet within 200 km loses weapons, cloak, warp, dock, jump and tether, takes drone damage -100 %, gains shield resistance (-99.99 %) and -90 % shield recharge, and gets +100 % mass, -50 % top speed, +100 % warp penalty and -75 % scan resolution. | The numbers are the V24.01 client's `dbuff/common/industrialInvulnConst.py` (`DBUFF_VALUES`); the SDE text states the set in words (the attributes do not). The sim cannot apply a buff to a list of ships: `SimAction` and `SimRemote` address one target, and the flags and shield and mass numbers have no sim effect. Needs a new `SimAction` (recipient list from the host's fleet membership, the buff set and a cycle) and the codec, which is a wire change (W2: `SimCodec.cs` and `link.rs`, one bump by the integrator). |
| OPEN | Which ships are recipients: "subcapital mining and hauling ships in the same fleet as the Rorqual", excluding other Rorquals. | The SDE text states it; no attribute or group list in the static data and no server-side code in the client tree (`dbuff` and `FakeIndustrialInvulnerability` only). Group choice (mining barge, exhumer, hauler and transport groups) is UNVERIFIED and must be decided with the body. |
| OPEN | Activation needs an active lock on an ore or ice asteroid (SDE text, not an attribute). | Needs the rock's type in the host: rock ids and body ids are separate spaces (MIN-D4, section 10). |
| OPEN | Burn-out: the module burns out completely after one activation (SDE text). | Same mechanism as the emergency hull energizer (module damage set to its hit points as the effect starts; `TypeAttributeResistanceEffect`). Not written because the effect is refused. |
| OPEN | Effect lifetime: expiry in `LocalTimers` (in-process), reset on stop, death, ship change, jump, undock, and the buff set re-sent when a recipient joins or leaves the fleet. | Not written; the buff expiry belongs to the body (SW-D4 / SW-O2 in the superweapons decisions). |
| OPEN | `disallowEarlyDeactivation` 906 (SDE: "prevent ejection from the ship ... extend the log out ship removal timer") and `disallowDocking` 2354 on the module. | Not enforced. The client uses 906 only for cloaking (`FakeCloaking.PreStartChecks`); no ejection or logout timer is written for modules. See section 9. |
| VERIFIED (SDE) | Fuel and consumption: type 42522 carries none of the consumption attributes (`consumptionType` 713, `consumptionQuantity` 714, `hasFuelCargo` 1085, `fuelCargoCapacity` 1086, `specialFuelBayCapacity` 1549, `serviceModuleFuel*` 2108-2110, `jumpDriveConsumption*` 866/868, `groupJumpConsumptionType` 3130), and the effect 6719 has no `modifierInfo`; the English description names no fuel. So the SDE does not say the core burns fuel and no fuel refusal is written. The core is refused as `EFFECT_NOT_IMPLEMENTED` at every activation, so it cannot run free. If the body is written and a consumption attribute appears, the shared per-cycle consumption mechanism (not PANIC-specific code) decides it. | Checked by query on `sde.typeDogma` / `sde.dogmaEffects` for build of this worktree. |
| LIMITATION | The warp refusal also covers a ship that is in warp (the host's warp state is set from the accepted warp until arrival, stop, dock, jump or destruction), a superset of "aligning to warp" in the SDE. | `SpaceSessions.IsWarping`. Whether the client refuses an activation in warp is UNVERIFIED. |
| OPEN | Client verification of all of the above. | Not run in the graphical client. |

## Closed items that should stay closed

- `storeManager.buy_offer` and `insuranceSvc.InsureShip` / `UnInsureShip`: existing persistence providers are connected.
- FleetProxy's seven operations plus FleetObject advert creation/setup callbacks: connected and tested; only the legacy-row limitation above remains.
- `station.GetGuests`: alias route, wire shape, and observer ledger seed are implemented; only the broadcast sender remains open.
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

## 9. Module effects: gaps left after the parity fixes

Each item below is **OPEN**. Evidence is the code path named; none of them is covered by a passing test that proves the reference behavior.

| Status | Gap | Evidence and what it needs |
|---|---|---|
| OPEN | Shield ancillary booster stops when its charges run out (`fallback: null`), where the reference continues at the base amount and pays the capacitor again | `dotnet/src/Eve.Services.Space/Effects/ModuleEffects.cs` (`AncillaryRemoteShieldBoosterEffect`) tells a capacitor need of 0 while charged; `crates/eve-space/src/modules.rs` `finish_cycle` draws the effect's single `capacitor_need` every cycle. A fallback needs a per-cycle capacitor in the simulation, which is a link change. A host-only fallback would make the booster free after its charges are gone. |
| OPEN | A second cloaking device, and a running module that cannot be stopped early (`disallowEarlyDeactivation` 906), are refused by the client (`FakeCloaking.PreStartChecks`) but not by eve.rs | The host does not know which modules the simulation runs (`ShipSnapshot` loads only the persisted Online/Offline states); the refusal belongs in `crates/eve-space/src/modules.rs` `activate`. |
| OPEN | Warp is not refused while a cloak that is not warp-safe (`cloaking`, `cloakingPrototype`) is running | The warp is started in the simulation (`crates/eve-space/src/scene.rs` `begin_warp`); the reference blocks it in `shipCloak.js` (`isEntityWarpBlockedByCloak`). |
| OPEN | Emergency hull energizer: the module's burn-out is told to the client but not kept, so a burned-out module can be activated again | `dotnet/src/Eve.Services.Dogma/DogmaInfo.cs` shows a module's stored condition (`ModuleConditions`) to the client, but no activation path reads that condition or writes a burn-out to it (`ModuleEffects.cs`, `TypeAttributeResistanceEffect`). The resonance change reaches the simulation through the resend after an accepted activation (section 8, `SpaceSessions.SendShipAsync`). |
| OPEN | Launchers of groups that are not run (defender 512, bomb 862, probe, festival, pod, sphere and structure launchers) are refused with `*_LAUNCHER_NOT_IMPLEMENTED`, not run | `dotnet/src/Eve.Services.Space/Effects/MissileEffect.cs` (`RefusedLauncherGroups`). Defender interception exists in `crates/eve-space/src/missiles.rs` but is not wired; guided bombs have no flight code. |
| OPEN | Bombs (launcher 862), the stasis web probe (4088), the interdiction probe sphere (589), the survey, scan and festival launchers, breacher pods and defenders are refused by group, not run. The warp disruption field generator (`warpDisruptSphere`) is refused `EFFECT_NOT_IMPLEMENTED`, its ship penalties having no SDE source. F.O.F. missiles are refused `FOF_MISSILE_NOT_IMPLEMENTED` on the served launchers | `dotnet/src/Eve.Services.Space/Effects/MissileEffect.cs`, `WarpDisruptSphereEffect.cs`; `docs/reference/compat/DEVIATIONS.md` ("Not yet matched"). Needs the bombs brief decisions D1-D7 and a captured V24.01 trace of a bomb launch and detonation. |
| OPEN | Warp is not refused by a warp bubble, probe or mobile disruptor: `begin_warp` (`crates/eve-space/src/scene.rs`) checks only scrambles. Immunity (1538) and the core stabilizer's modifier (104) do not reach the simulation | Warp-source registry (bombs brief WP-4, WP-5): wire W2, one bump by the integrator. Waits for the mining and module-rule changes to `scene.rs`. |
| DONE | The missile family is derived from the static data (`LauncherFamilies`, `DogmaStatic.MissileLauncherGroups`), not from a list of groups. Served set on the pinned SDE: 506-511, 524, 771, 1245, 1673, 1674 | `dotnet/src/Eve.Dogma/LauncherFamilies.cs`; `Eve.Dogma.Tests/LauncherFamilyTests.cs` (synthetic) and `Eve.Services.Space.Tests/MissileFamilyDataTests.cs` (PgFact, real SDE) |
| CHANGED | Armor hardeners and other dogma modifiers reach the simulation only at spawn | Their told values are the world's with the module assumed active (`DogmaWorld.Assume`, section 8), and the simulation is sent the resonances after an accepted activation (`SpaceSessions.ActivateAsync`). The stop timing stays the section 8 limitation. |
| CHANGED | A barrage is refused, not fired as a plain shot | `TurretEffect.cs` `BarragePendingEffect` (a pending handler: refused `EFFECT_NOT_IMPLEMENTED`, listed as uncovered). The barrage falloff (328) and damage multiplier (326) are not run. Evidence: `ModuleRuleTests.A_module_whose_effect_has_no_body_yet_is_refused_and_stays_uncovered`. OPEN: the barrage body, when a reference or recording exists. |
| CHANGED | A mutadaptive repairer's first cycle is x1.0 (was x1.1) | `ProjectedEffect.cs` `MutadaptiveRepairSpool.TryNextCycle` applies the bonus, then advances (`assistanceModuleRuntime.js` `advanceAssistanceRepairSpool`). Evidence: `ModuleEffectsTests` and `RepairHostTests` mutadaptive tests. |
| CHANGED | The spool starts on an accepted activation only | `IEffectHandler.Accepted` (default no-op) runs after the simulation accepts the start (`ModulePackages.Result.Accepted`); the name branch in `SpaceSessions.ActivateAsync` is gone. |
| CHANGED | Refusal wording: a microwarpdrive held by a scrambler, and a module too damaged to start | `dotnet/src/Eve.Services.Dogma/ModuleRefusals.cs` (texts from `moduleOperation.js`). Evidence: `ModuleOrderTests`. |
| CHANGED | The group limit on online modules (maxGroupOnline, 978) is enforced at the online | `CharacterShips.MaxGroupOnlineRefusal`, refused as `CannotOnlineReachedMaxGroupOnline {type: (4, typeId), group: (7, groupId), maxGroupOnline}` (`eveexceptions/const.py`). Evidence: `Eve.Ships.Tests/MaxGroupOnlineTests` (real SDE, capacitor boosters, 10 limit). OPEN: lowering a limit does not offline the excess (`_OfflineExcessModulesInGroup`). |
| CHANGED | A target that disallows offensive modules (872) is refused an offensive module, or a module with 1798 | `SpaceSessions.ActivateAsync` throws `DeniedActivateTargetModuleDisallowed`. Only the target type's static attributes are read (not passive or skill bonuses). OPEN: no host test yet; needs a target with 872. |
| CHANGED | A module that may run in a gate cloak (canActivateInGateCloak, 3123) keeps a jump cloak | `SpaceSessions.EndInvulnerability(keepsJumpCloak)`. OPEN: no host test yet; a fitted 3123 module is needed. |
| OPEN | A module that burns a resource (consumptionType 713: siege, triage, cores, jump portals, clone vats) is refused as not implemented | `ModuleEffects.cs` `DogmaModifierEffect.Refusal` (`FUEL_NOT_IMPLEMENTED`). The fuel is not taken from the pilot's inventory yet. Evidence: `ModuleRuleTests.A_module_that_burns_fuel_is_refused_as_not_implemented_and_is_not_run_free`. |
| OPEN | The propulsion group limit (afterburner and microwarpdrive together, maxGroupActive 763) is not enforced | Running state is in the simulation (`crates/eve-space/src/modules.rs` `activate` has no group count). Needs group fields on `SimEffect` and a link wire bump (audit A FP-2a and FP-2b). |
| OPEN | The tracking computer is hostile in the simulation | `Remote::hostile` (`crates/eve-space/src/modules.rs`) classes `shipModuleRemoteTrackingComputer` as hostile, and the HUD name is `ewTrackingDisrupt` (audit A FP-3). Needs Rust. |
| OPEN | Module rules that need the running state (906, 104, 2354, 669, 763, 3265, 1245, 1349, 1334, 2608) | DEVIATIONS 126. Needs the simulation and one wire bump. |
