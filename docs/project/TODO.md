# Current TODOs

**Updated:** 2026-10-08
**Baseline:** main source commit `93f92e633747fbadb0d12374606bd1712ce7ff51`

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

## 2. Mutation authorities and specialized service providers

| Status | Service/method | Missing work |
|---|---|---|
| OPEN | `dynamicItemService.CreateDynamicItem` | Connect the real mutaplasmid/item mutation authority, including input consumption and creation of the resulting item. Do not return synthetic success. |
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
| OPEN | `inSpaceCompressionMgr.CompressItemInSpace` | Connect the real in-space compression authority. |

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

**LIMITATION — Character clone and worldspace state.** The character store has a home station but no separate clone station or worldspace field. Character entry currently uses the home station as `cloneStationID` and `None` for `worldspaceid`; populate these from an authoritative store if one is added.

**LIMITATION — In-space resume fidelity.** The active ship's position is persisted and restored, but the current space-item store does not preserve velocity or warp state. Direction reuses any prior stored vector or falls back to +Z. The first active-space ship List now triggers a visual-only AddBalls2 prime before full bind; native-client login ordering remains unverified.

## Closed items that should stay closed

- `storeManager.buy_offer` and `insuranceSvc.InsureShip` / `UnInsureShip`: existing persistence providers are connected.
- FleetProxy's seven operations plus FleetObject advert creation/setup callbacks: connected and tested; only the legacy-row limitation above remains.
- `station.GetGuests`: alias route, wire shape, observer ledger seed, and station/structure join/leave broadcasts are implemented and unit-tested.
- `SelectCharacterID`: character session state, station/structure/space selection, deferred docked ship-id and fitting bootstrap, first-space-List visual prime, ship/module Dogma hydration, and saved-pose space resume are wired. The PostgreSQL-backed selection test was skipped without `EVE_PG_URL`; native-client acceptance remains unverified.
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
