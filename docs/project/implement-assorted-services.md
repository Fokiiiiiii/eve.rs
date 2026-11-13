# Assorted service coverage audit and implementation

Date: 2026-11-13  
Client target: EVE Online V24.01, build 3396210  
EveJS reference: `/home/user/evejs-261108.1`

This records the requested `MISSING` rows in `service-audit/EVE_RS_COVERAGE.csv`, the production Host registrations, and the client-visible behavior found in the full `/home/user/eve-logic` tree. The CSV is the status source of truth.

## Host registrations before and after this change

| Service | Host registration and code owner | Registered behavior relevant to the audit |
| --- | --- | --- |
| `inventoryAssetHoldingMgr` | `CoreServices.Create` (`dotnet/src/Eve.Services.Core/CoreServices.cs:70`); `InventoryAssetHoldingMgrService.cs` | Four QA toggle calls. State belongs to a connected `ClientSession`; defaults are failure off and validation on. |
| `abyssal_content_manager` | `CoreServices.Create` (`CoreServices.cs:72`); `UnportedFeatureRpcServices.cs` | NPC spawn report plus three explicit V24 devtool endpoints. EveJS rejects those three endpoints and returns `None`; the added handlers preserve that response and do not generate or mutate content. |
| `autoMoonMining` | `CharacterServicesComposition.StructureRpc` (`dotnet/src/Eve.Host/Rpc/StructureRpc.cs:68-71`); `StructureAutoMoonMining.cs` and `AutoMoonMiningRpcService.cs` | Both read calls are wired to `StructureStore`, `MoonExtractionStore`, `PgItemStore`, and SDE material/type data. |
| `achievementTrackerMgr` | `ProgressRpc` (`dotnet/src/Eve.Host/Rpc/ProgressRpc.cs:69`); `AchievementTrackerMgrService` in `LoginInitializationRpcServices.cs` | `HasEverWarped`, `GetCompletedAchievementsAndClientEventCount`, `ResetAllForChar`, and `UpdateClientAchievmentsAndCounters` use persistent achievement state and reset notification. There is no debug-stats method. |
| `facWarMgr` | `CoreRpc` (`dotnet/src/Eve.Host/Rpc/CoreRpc.cs:145`); `FacWarMgrService.cs` | The registered method set includes the active enlistment, rank, faction, LP, and statistics calls. It excludes `GetStats_CorpPilots` and `RefreshCorps`. |
| `userSvc` | `RewardRpc` (`dotnet/src/Eve.Host/Rpc/RewardRpc.cs:102`); `RedeemService` and `UserSvcCompatibility.cs` | Redeem, user information, reporting, multi-character training, token, and supported compatibility calls are registered. `ProcessEVESessionCreated` is absent. |
| `seasonManager` | `CoreServices.Create` (`CoreServices.cs:126`); `SeasonManagerService` in `SessionStartupRpcServices.cs` | Existing season reads/events are registered; `reload_fsd_data` now records the request and returns `False`, matching EveJS. |
| `ship` | `AgentRpc` (`dotnet/src/Eve.Host/Rpc/AgentRpc.cs:180-183`); `ShipService.cs` -> `ISpaceContainers` -> `SpaceContainers` | In-space sessions receive the full ship service. `LaunchFromContainer` accepts ship-hull maintenance bays and POS ship maintenance / assembly arrays; POS use is backed by persisted module-to-control-tower association and checks the live tower state and permissions. |

## Client and EveJS traces

### `inventoryAssetHoldingMgr`

The V24 QA service calls the four methods in `assetholding/client/assetholding_svc.py:34-61`. `InventoryAssetHoldingMgrService` returns `False`/`True` by default, stores setters per session, normalizes the supplied boolean, and returns `PyNone` from setters. The Host wire test checks defaults, mutation, and isolation across two sessions.

This matches EveJS `server/src/services/inventory/inventoryAssetHoldingMgrService.js:3-14,20-25,50-100`: its own comment identifies a session-only compatibility toggle and explicitly says it does not alter asset-holding failure simulation or validation. No durable flag or inventory hook is appropriate for this observed behavior.

### `abyssal_content_manager`

The V24 Insider tool makes these direct calls:

| Method | Arguments | Client evidence |
| --- | --- | --- |
| `GetContentDetailsForShipLocation` | none | `eve/devtools/script/abyssal_content_tool.py:111-119` |
| `GenerateContentFromExistingPlan` | serialized plan | `abyssal_content_tool.py:139-153` |
| `GenerateAbyssEncounterContent` | difficulty tier, dungeon ID, weather type ID, nebula graphic ID, lens flare type ID, spawn table ID | `abyssal_content_tool.py:181-203` |

All three direct client calls are real, so they are not marked `CLIENT-DEAD`. EveJS `server/src/abyssal/transport/abyssalContentManagerService.js:941-979` allows only `generate_abyss_npc_spawn_report`; its `callMethod` returns `null` for other methods. The V24 tool interprets a false/empty reply as a failed load or generation (`abyssal_content_tool.py:113-117,143-152,193-202`). The added Host methods return `PyNone` for those exact EveJS-rejected calls and do not invent content, state changes, or notifications. `Abyssal_content_tool_calls_keep_the_evejs_rejected_method_none_reply_over_host_wire` exercises all three through the registered Host wire.

### `autoMoonMining`

V24 sends `(solar_system_id, structure_id)` for `RequestMiningDetails` and `RequestMiningCycleOutput` (`moonmining/autoMoonMining/client/auto_moon_miner_service.py:16,20`). EveJS `server/src/services/structure/autoMoonMiningService.js:427-458` returns a details dictionary with `moonID`, `nextHarvestTime`, `availableFuel`, `outputMaterials`, and `remainingOutputBayCapacity`; cycle output is a type-ID-to-quantity dictionary, and a missing structure ID returns an empty dictionary for that call. The details call reports a `CustomInfo` user error when its structure ID is missing.

The Host resolves structure ownership/access and Pochven/reinforcement policy before reading the authoritative moon profile and inventory. Material type lists, type materials, type volumes, and structure capacity come from the build-3396210 SDE. Cycle output is computed with the EveJS `metenoxHarvest.js:4-15,25-31,54-89` formula: 30,000 m³ per one-hour cycle, 40% efficiency, material quantities from SDE, and the client's Moon Material whitelist (typeList 611 / group 427). CCP's [Equinox expansion notes](https://www.eveonline.com/news/view/equinox-expansion-notes#h2-7) independently confirm 40% efficiency; the cycle volume is not specified there. EveJS documents the 30,000 m³ figure and its observed 42.59%-Zeolites-to-332-gases cross-check in its source comments. The calculation is therefore a source-backed match to EveJS, not a captured packet value.

Unlike EveJS, eve.rs derives cycle output on each read instead of caching `autoMoonMiningCycleOutput` into the moon profile. The result is deterministic from the persisted profile and pinned SDE. `AutoMoonMiningHostTests.Auto_moon_mining_reads_authoritative_profile_and_inventory_over_registered_host_service` checks exact wire fields, SDE-derived quantities, inventory values, and the same values after Host restart. The focused missing-ID Host test checks the error envelope. These are reads and emit no notifications.

The raw `.cache/sde` directory was not present in this worktree. The existing `eve_p4` database has `sde._sde` build number `3396210`; the Host tests use its loaded SDE data. No SDE files were added to the repository.

### `achievementTrackerMgr`, `facWarMgr`, and `userSvc` rows marked `CLIENT-DEAD`

These rows have wrapper definitions but no V24.01 client call path that consumes them. Implementing guessed return values or server-only plumbing would create behavior the client does not use.

| Row(s) | Client evidence and Host check |
| --- | --- |
| `RPC-00826` `GetDebugStatsFromCharacter` | Only the wrapper definition exists at `achievements/client/achievementSvc.py:61-63`; a whole-tree search found no caller or consumer. `AchievementTrackerMgrService.HasMethod` at `LoginInitializationRpcServices.cs:143-151` excludes it. |
| `RPC-01466` `GetStats_CorpPilots` | Wrapper at `eve/client/script/ui/services/facWarSvc.py:386-387`, but no caller/result consumer in the V24 tree. `FacWarMgrService.cs:49-55` excludes it; EveJS has no route. |
| `RPC-01476` `RefreshCorps` | Definition at `facWarSvc.py:305-306` is not exported or called anywhere in the V24 tree. `FacWarMgrService.cs:49-55` excludes it; EveJS has no route. |
| `RPC-02777` and `RPC-02778` `ProcessEVESessionCreated` | `carbon/common/script/net/machoNet.py:4027-4030` invokes the remote `userSvc` method only in Carbon proxy mode; server mode uses local `sm.ChainEvent`. This is a server lifecycle callback, not a retail EVE client request. EveJS has no route. |

The CSV records those callsites and the negative whole-tree call search as the evidence, rather than treating the wrapper declaration itself as a client use.

### `seasonManager.reload_fsd_data`

The Insider calls the no-argument RPC at `eve/devtools/script/insider.py:1528`. EveJS `server/src/services/character/seasonManagerService.js:304-306` audits the request and returns `false`; the Host method does the same via its audit callback (or a server log if one is not supplied). The wire test verifies `PyBool.False` and the empty argument list. It changes no game state and emits no notification.

### `ship.LaunchFromContainer`

The V24 menu offers this call for ship maintenance locations and passes `invItem.locationID` plus `invItem.itemID` (`eve/client/script/ui/services/menusvc.py:452-464`; `menuSvcExtras/invItemFunctions.py:36-52`). The client rejects warp locally with `ShipInWarp` before sending the call. EveJS `server/src/services/ship/shipService.js:1594-1610` also rejects abyssal ship swaps and returns `null` on success. Its `ejectRuntime.js:206-265,370-525,604-634,920-975` accepts both ship-hull maintenance bays and POS ship maintenance/assembly arrays; the POS route requires a live tower association and starbase access.

The Host handler now accepts both ship hull maintenance bays and the V24 POS sources (SDE groups 363 and 397). A hull is a maintenance bay when the SDE `hasShipMaintenanceBay` or `shipMaintenanceBayCapacity` attribute is positive. For a POS array, the persisted `DeployedRecord.ControlTowerId` supplies the tower association; the operation checks the array and tower are anchored and online, checks both `use` and `viewputtake` against persisted tower settings and the session's corporation/alliance roles, and applies the SDE `maxConfigureDistance` or the online tower-shield distance rule. It moves the assembled ship and writes its durable `SpaceItem` pose in one PostgreSQL transaction, places it in simulation, then sends `OnItemChange`. A failed simulation spawn keeps the durable state for retry; per-item serialization prevents concurrent duplicate launches.

The DB-backed `SpaceContainerTests.Ship_launch_from_maintenance_bay_moves_persists_notifies_and_can_be_boarded` covers the hull path, retry after a failed spawn, concurrent retries, restore, and boarding. `SpaceContainerTests.Ship_launch_from_POS_maintenance_array_checks_tower_permissions_and_persists_the_boardable_ship` runs against both an SDE Ship Maintenance Array (group 363) and Small Ship Assembly Array (group 397); it checks each permission refusal leaves inventory/space state unchanged and sends no item notice, uses the shield exception for a ship beyond the array's configure range, restores the committed `SpaceItem`, and completes the subsequent `Board` transition. `SpaceContainerTests.Ship_launch_from_maintenance_bay_is_refused_while_the_pilot_is_warping` checks the client `ShipInWarp` refusal. `HostServerTests.Ship_launch_from_container_forwards_client_ids_and_returns_none_over_the_bound_host_route` sends the V24-shaped nested `MachoBindObject` through a real Host packet loop with the actual `ShipService`, checks both forwarded IDs, and verifies the `None` bound-call reply. Production registration is `AgentRpc` (`dotnet/src/Eve.Host/Rpc/AgentRpc.cs:179-187`). No graphical client session was run.

## Verification record

DB-backed tests use `eve_p4` at `postgres://postgres@127.0.0.1:5432/eve_p4`. The POS-array regression was first copied into a detached worktree at the pre-change `HEAD`; it failed because the old handler rejected the POS source. The final Release test runs passed with no skips:

- `SpaceContainerTests`: 34 passed, including hull and both POS SDE groups, permission refusals, persistence/restore, notifications, retry/concurrency, warp refusal, and boarding.
- `ShipCoverageTests`: 2 passed.
- `HostServerTests`: 496 passed. This is the actual partial class containing `ClientVerifiedNSHostWireTests` and `AutoMoonMiningHostWireTests`; it also exercises the registered Host wire, error and faction/user/season contracts referenced by this audit, including the added `ship.LaunchFromContainer` bound-call packet loop.
- `AutoMoonMiningHostTests`: 1 passed against PostgreSQL, including Host restart persistence.
- `ProjectDiscoveryHostTests`: 15 passed against `eve_p4`, covering the independent season-manager Host composition.
- After merging `origin/main`, `SpaceContainerTests` (34), `ShipCoverageTests` (2), and `ControlTowerHostTests` (8) passed again against the merged Space implementation; no tests were skipped.

Release builds with `-warnaserror` completed with 0 warnings for `Eve.Services.Core`, `Eve.Services.Inventory`, `Eve.Services.Ships`, `Eve.Services.Space`, `Eve.Ships`, `Eve.Host`, `Eve.Host.Tests`, and `Eve.Services.Space.Tests`; the post-merge builds included the new main-branch POS inventory code, and `Eve.Host.Tests` was rebuilt after adding the ship packet-loop case. EveJS source was traced for each active method; no focused EveJS test file for these services was found, so no EveJS test execution is claimed. No graphical V24 client session was run.

## Remaining gaps

- The three Abyssal content-authoring RPCs remain unsupported in EveJS: V24 Insider code handles the `None` response as an explicit generation/load failure. The Host preserves that refusal and does not claim encounter generation or content inspection.
- The full graphical V24 client was not used.
