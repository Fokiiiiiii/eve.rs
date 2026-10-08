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
| OPEN | `inSpaceCompressionMgr.CompressItemInSpace` | No compression backend is attached (`Program.cs` passes none; owner C1 is open). The Host now refuses a valid request with the named `CustomNotify` notice (`InSpaceCompressionMgrRpcService.UnavailableNotice`) instead of returning `PyNone`. The V24.01 client (`itemcompression/client/itemCompression.py`) treats `None` as a failed compress with a generic message, so the old answer was not a success, but it gave no reason. Still to connect: the facility's compressible type list check, consuming the ore, creating the compressed output, the inventory sync, and the six-value result tuple. Real client behaviour for the refusal is unverified. |

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
