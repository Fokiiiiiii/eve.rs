# Structures phase (phase 3): space persistent objects and Upwell structures — work-package plan

Read-only plan. eve.rs main `99e6a0e` (`/home/user/eve.rs`), client `eve-logic` 1dbb571, EveJS v0.12.9, SDE build 3396210 (psql, schema `sde`).
Evidence tags: [V] verified in source this session, [S] SDE query, [W] EVE University wiki (fetched 2026-10-10), [J] EveJS, [C] client (eve-logic), [O] open / decision needed.
Source priority: stats = SDE; mechanics = wiki, then EveJS/CCP/client equal; wire shapes = client. Where the wiki is silent the EveJS value is used and recorded in DEVIATIONS.

## 0. Findings that change the brief (read first)

1. **In-space compression is NOT a structure module.** `industrialItemCompression` (effect 8364, category 1) is carried by the 12 published types of group 4174 "Compressors", **category 7 (ship module)** [S]: e.g. Medium Asteroid Ore Compressor I 62622 (`canFitShipType1` 1302 = 42244 Porpoise, `maxRange` 54 = 44 000 m, duration 60 000 ms, `compressibleItemsTypeList` 3255 = 334, `activationRequiresActiveIndustrialCore` 3265 = 1), Capital Asteroid Ore Compressor I 62632 (fits 28352 Rorqual, 144 000 m). `StructureModuleEffects.cs` and `OperationalRpcServices.UnavailableNotice` (`dotnet/src/Eve.Host/OperationalRpcServices.cs:162`, "needs a compressor structure") are wrong. In-space compression is package **ST-C**, independent of every structure package. Only `structureCompressionMgr` (Standup compression plant, a structure service) is structure work.
2. **Entosis targets the Sovereignty Hub (group 1012, category 40), not Upwell structures**; TCUs "can no longer be ... targeted by entosis" [W Sovereignty]. Entosis Link I 34593: range 54 = 20 000 m, duration 300 000 ms, `consumptionType` 713 = 16275 [S]. Entosis therefore depends on a sov-hub ball, not on docking/control.
3. **Structures today live in the GameStore table `structures`** (`PersistenceStructureDirectoryReadModel.cs` reads `readTable("structures")` at 15+ sites; `IsPersistentlyDeployedStructure` in `.Part2.cs:51`) and moon extractions in GameStore `moonExtractions` (`Rpc/StructureRpc.cs`, `mailCache.Read("moonExtractions")`) [V]. ADR 0003 forbids building mutations on GameStore → ST-0 must create the owner and move reads first.
4. **E-gap status moved since `wiring_gaps_E.md`:** E-02 OnTarget lost/clear/otherlost is sent by the sim (`crates/eve-space/src/targeting/unlock.rs:22-81`, `targeting/mod.rs:172`) [V]. E-06 probe notices exist (`SpaceProbes.cs` OnNewProbe/OnRemoveProbe/OnProbesIdle, `SpaceProbeScans.cs` OnSystemScanStarted/Stopped, OnProbeWarpStart/End) [V]; what is left of E-06 is the **result source**: `ICosmicSiteSource` is bound to `NoCosmicSites` (`Scanning/ICosmicSiteSource.cs`). OnDamageStateChange is sent as a destiny action (`targeting/lock.rs:173`) [V]; whether the progression widget receives it is client-only (MITM). Still no sender: OnCloakModuleForceDeactivated (E-04), OnWeaponBanksChanged (E-07, blocked by RC-5 weapon banks), OnCynoJammerChanged (E-15) [V].
5. No structure exists in the sim: `BodyKind` has Station/Stargate/etc., docking accepts only `BodyKind::Station` (`scene/docking.rs:19,107`) [V]. `geo.rs:163 SpatialKind::Structure` exists but is unused. Current `PROTOCOL_VERSION = 58` (`crates/eve-space/src/link/mod.rs:39`) [V].
6. Hacking: `hackingMgr` is not registered in `dotnet/src` [V]; the client calls `ClickedOnTile`, `UsedUtilityElement`, `UsedUtilityElementOnVirus`, `QuitHackingAttempt` and listens for `OnHackingTurnComplete` (`eve/client/script/ui/hacking/hackingUISvc.py:18,56,264,270,278`) [C]. Hackable SDE targets: attribute `hackable` 1927 on group 306 Spawn Container (242 types), 1207 Scatter Container (238), and Upwell groups 1657/1404/1406/2016... (hack reveals the reinforcement hour [W Vulnerability]) [S].

## 1. Rules adopted (wiki first)

Structure timeline [W Vulnerability, W Anchoring_structures; J `services/structure/structureConstants.js:3-248`]:
- Anchor: 15 min `anchor_vulnerable` (damage ≥10 % of damage cap pauses it; destroyable without timers) → 24 h anchoring (invulnerable; J ANCHORING 86400) → no core: hull only, Low Power. Core inserted → 15 min onlining repair (`onlining_vulnerable`) → full heal; online a service module with fuel → `shield_vulnerable`, upkeep `full_power`.
- Sov-null not owned by your alliance: anchoring +24 h per Strategic Index level [W Anchoring_structures] (needs sov index; ST-2 reads it from `PersistenceSovFwReadModel`, else 24 h, DEVIATIONS).
- Damage cap per second per layer: SDE `shieldDamageLimit` 2034, `armorDamageLimit` 2035, `structureDamageLimit` 2036 (Astrahus: 14 400 000 / 5000 / 5000) [S]; repair pause thresholds `pause{Shield,Armor,Hull}RepairDpsThreshold` 3354-3356 = 500 (= 10 % of cap, matches wiki) [S].
- Shield depleted: Medium/Large/XL with an online service → `armor_reinforce`; FLEX, abandoned, Large/XL in Low Power skip it. Armor reinforce durations (medium) WH 2.5 d, null/low 3.5 d, high 4.5 d, ±1.5 h; Large/XL 24 h ±3 h [W] = J `ARMOR_REINFORCE_*` 216000/302400/388800/86400, jitter 5400/10800 [J]. Then 15 min repair timer → `armor_vulnerable`.
- Armor depleted: Large/XL/FLEX → `hull_reinforce` WH 1.5 d, null/low 2.5 d, high 4.5 d ±3 h, FLEX 30 min ±30 min (J FLEX 3600 / jitter 1800 — wiki 30 min; take wiki); Medium and abandoned → `hull_vulnerable` at once. Hull repair timer 30 min [W] (J 1800). Hull 0 → destroyed.
- Exit time: defender's reinforcement hour ± 3 h, never rolls into next day; shield fight fixes the dates for armor and hull [W]. Weekday/hour set at Anchor (client sends `reinforceWeekday, reinforceHour`); defaults J weekday 5 hour 18, `NO_REINFORCEMENT_WEEKDAY` 255 [J].
- Change of reinforcement hour: wiki marks "takes effect after 30 days, need to verify" → [O] D-2: store `pending_*` + `effective_at`; the delay is the open decision (wiki 30 d vs EveJS `structureVulnerabilityService.js:183-260` behaviour); MITM can show the client's displayed pending timer.
- Low Power: no online service; Large/XL hull becomes vulnerable together with armor. Abandoned: 7 days in Low Power (wiki gives two versions; take "7 days Low Power") → no timers, no tether, no asset safety, contents drop. Online any service → back to Vulnerable [W].
- Unanchor: 7 days (J UNANCHORING 604800), cancellable at any time; states where unanchor is refused/cancelled per J `STRUCTURE_UNANCHOR_CANCEL_STATES` (`structureConstants.js:231`); end → structure item + container with core/modules/fuel, rigs destroyed [W].
- Tether: range SDE `tetheringRange` 2268 (Astrahus 10 000 m) [S]; given on undock or within range; blocked by weapon timer, warp scramble beating core strength, launched fighters, no docking rights, no core, abandoned; broken by a lock starting, leaving range, launching fighters, activating burst jammer/command burst/cyno/siege/triage/bastion; while tethered: not targetable, no AoE damage, repairs ship+modules; smartbombs and bombs refused; in-space compression refused [W Tethering].
- Moon extraction: 6-56 days, 30 000 m³ per hour [W]; auto-fracture ~3 h after arrival (SDE `autoFractureDelay` 2698, rig bonus 2707; J default 10 800 s) [W][S][J]; belt decay 48 h (J `moonExtractionRules.js:29`, wiki silent → DEVIATIONS); jackpot chance J 0.1 (wiki: "bright blue flash ... all ore excellent"). Refinery needs Standup Moon Drill I 45009 [S][W].
- Hacking: virus coherence/strength from module (1909/1910/1911 after dogma), Rule of 8 core placement, Rule of Six, subsystem behaviours and utility element numbers [W Hacking]; failure twice → container destroyed; failure on window close/warp/out of range [W]. Core and firewall numbers are images on the wiki → EveJS `hackingMgrService.js:157 buildSyntheticBoard` values, DEVIATIONS.
- Sites: anomalies found by system scanner, signatures need probes; 25 % type / 75 % name / 100 % warpable; strength levels 42/72/104; spawn within 4 AU of celestials; respawn immediately elsewhere after clear, no downtime respawn [W Cosmic_signatures]. Spawn tables per system: wiki silent → EveJS `space/runtime/spaceRuntime/universeSites.js` + SDE `dungeons` (archetypeID, factionID).

## 2. Work packages

Shared files needing a single owner (merge serially, one package at a time):
- **Sim wire**: `crates/eve-space/src/link/mod.rs` (PROTOCOL_VERSION), `link/kind.rs`, `link/decode*.rs`, `link/payload.rs`, `crates/eve-space/src/sim/dispatch.rs`, `dotnet/src/Eve.Space/SimCodec*.cs`, `SimMessages.cs`, `oracle/fixtures/simlink/messages.json` (regenerate with `tools/dev/gen-simlink-fixtures.py`). Each wire package takes the next protocol number at merge time (59, 60, ...), never in parallel branches.
- **Structure RPC wiring**: `dotnet/src/Eve.Host/Rpc/StructureRpc.cs`, `dotnet/src/Eve.Host/Composition/StructureComposition.cs`, `dotnet/src/Eve.Services.Core/StructureRpcServices.cs` (`StructureRpcCallbacks`).
- **Effect registry**: `dotnet/src/Eve.Services.Space/Effects/ModuleEffects.cs`, gap manifest `dotnet/tests/Eve.Services.Space.Tests/ModuleEffectsTests.cs`.
- **Docs**: `docs/project/TODO.md`, `docs/reference/compat/DEVIATIONS.md` (append own section only).

### ST-0 Structure record owner and state machine (host only, no wire) — L
- New module `dotnet/src/Eve.Structures/` (csproj, `Migrations/0001_structures.sql`, `StructureStore.cs`, `StructureStateMachine.cs`, `StructureTimers.cs`). Schema `structures`: `structures(structure_id, type_id, solar_system_id, x,y,z, yaw, owner_corporation_id, alliance_id, name, bio, profile_id, state smallint, state_started_ms, state_ends_ms, timer_paused_ms, upkeep smallint, core_installed bool, reinforce_weekday, reinforce_hour, pending_weekday, pending_hour, pending_effective_ms, unanchor_ends_ms, low_power_since_ms, destroyed_ms, version)`, `structure_services(structure_id, service_id, state)`. State ids = client `structures` constants = J `STRUCTURE_STATE` (1, 2, 101, 102, 110-116, 118) and upkeep 1/2/3 [J][C `structureBracket.py:46-95`].
- `StructureStateMachine`: pure function (state, event, clock, rules from SDE group/size/security) → next state + deadline; events: AnchorPlaced, Damaged(layer depleted / ≥ threshold), TimerDue, CoreInserted, ServiceOnline/Offline, FuelOut, UnanchorStarted/Cancelled. Size class from group (Medium/Large/XL/FLEX) — [O] D-1: source of size class (SDE `structureRequiresDeedType`/typeLists vs a table from J `structureConstants.js`).
- Deadlines: Durable Scheduler job kind `structure.timer` (one per structure, `uniqueKey structure:{id}`), handler idempotent (FOUNDATIONS rules 1-3). Each transition publishes `structure.state_changed {structureId, solarSystemId, state, upkeep, timer}` in the same transaction.
- Import: copy GameStore `structures` rows once at start-up (adding missing ids, the mining-ledger X-7 pattern), then switch every `readTable("structures")` in `dotnet/src/Eve.Host/Persistence/Structures/PersistenceStructureDirectoryReadModel{,.Part2}.cs`, `PersistenceStructureAssetSafetyReadModel*.cs`, `Composition/StructureComposition.cs` (`StructureExistsAsync`), `Composition/IndustryComposition.cs:65,164`, `Rpc/CharacterRpc.cs:100` to the store. Register the migration in `dotnet/src/Eve.Host/Foundations.cs`/`Program.cs` like `Eve.Ships`; extend `dotnet/tests/Eve.Host.Tests/ArchitectureTests.cs` (no Marshal/Services reference).
- Tests (new `dotnet/tests/Eve.Structures.Tests`, PgFact, fake clock): every arrow of section 1 (anchor → anchoring → no core → onlining → shield_vulnerable; shield 0 → armor_reinforce duration per security/size with jitter inside ±; repair-timer completion heals; pause at ≥ threshold; Low Power skips; abandoned after 7 d; unanchor/cancel); restart: job survives process restart and fires once; replay of the same job is a no-op; import is idempotent; existing `StructureDirectoryServiceTests`, `StructureDataRpcPersistenceTests`, `PersistenceStructureHangarViewRpcTests` still pass against the new store.

### ST-1 Structure ball in the simulation (protocol +1) — L
- Sim: `crates/eve-space/src/world.rs` add `BodyKind::Structure` (runtime body with owner/state/slim fields) and host commands `PlaceStructure`, `RemoveStructure`, `SetStructureState` (`link/kind.rs`, `link/decode_message.rs`, `sim/dispatch.rs`); slim item fields `state`, `timer (start,end,paused)`, `upkeepState`, `unanchoring`, `repairing`, `categoryID` 65 [C `structureBracket.py:46-95`, `activeitem.py:608-616`, `stateFlag.py:262-289`] in `slim.rs`/`payload.rs`; `OnSlimItemChange` on state change.
- Health: structure bodies carry layers (SDE 263/265/9) and the per-second damage cap (2034-2036) in `crates/eve-space/src/health/mod.rs`; a new `Out::StructureDamaged {id, layer_depleted, dps_over_threshold}` to the host; the sim never decides state (ADR 0012 rule 3). [O] D-3: body-with-health vs NPC-like entity (body recommended: targeting `targeting/mod.rs:235`, docking and slim already key on bodies).
- Docking generalised from Station to "dockable body": `scene/docking.rs` (`dock`, `accept_docking`, `undock`, `undock_place`), ship-size rule from `locators.rs` `EXCLUDED_DOCKS`/`ONE_WAY_UNDOCKS` + wiki "Max docking" column (Astrahus subcap, Fortizar capital, Keepstar supercap) [W]; only states not in J `STRUCTURE_DISABLED_STATES` accept docking.
- Host: `dotnet/src/Eve.Services.Space/SpaceStructures.cs` (new): place every non-destroyed structure of a system when the first pilot enters (the `RestoreDeployedAsync` pattern in `SpaceContainers.Deployed.cs`), subscriber on `structure.state_changed` → `SetStructureState`; `StructureDamaged` → ST-0 machine in one transaction.
- Wire: SimCodec + fixtures; PROTOCOL_VERSION 58 → 59.
- Tests: `crates/eve-space/src/scene/tests.rs` (a placed structure is in AddBalls2 with category 65 slim; damage above cap is clipped per second; depleted shield reports once); `dotnet/tests/Eve.Space.Tests` codec round-trip; `dotnet/tests/Eve.Services.Space.Tests/SpaceStructuresTests.cs` (restore on entry, state change → slim update, damage → state transition → slim timer); `tools/headless-client/tests/test_structures.py` (new): login in a system with an imported Astrahus, see it in the ballpark with state/timer.

### ST-2 Deployment and vulnerability RPCs — M (needs ST-0, ST-1)
- `structureDeployment.Anchor(itemID, x, z, yaw, profileID, name, bio, weekday, hour, extraConfig)` [C `structureDeployment.py:173`; J `structureDeploymentService.js:20-41` → `sovPlayerDeployment.js:942 deployStructureFromInventoryItem`], `Unanchor`/`CancelUnanchor` [C `:65,:69`], `RenameStructure` [C `menuFunctions.py:378`], `structureVulnerability.UpdateReinforceTimesFromClient(infoOnStructures, day, hour)` [C `reinforcementTimeWnd.py:172`; J `structureVulnerabilityService.js:183`].
- Checks (wiki): Station Manager/Director/CEO role (`StationManagerRole` 2048 already in `StructureRpcServices.cs`), corp ≥ 7 days, ≥ 1000 km from stations/structures/belts, 50-75 km from the anchoring ship by size, forbidden systems (trade hubs, starter/career systems, shattered WH, Thera), Keepstar/Sotiyo not in high-sec, ship not in warp [W Upwell_structures, W Anchoring_structures]; error names per J (`CantDeployBlocked`, `CrpAccessDenied`, `ShipInWarp`, `DropNeedsPlayerCorp`, `CharNameTooShort`, `CustomNotify`) [J `sovPlayerDeployment.js:261-1249`].
- Item consumed and structure created in one transaction (inventory + structures), event `structure.anchored`; notifications `OnStructureStateChanged(solarSystemID, structureID, state)` [C `structureBrowserController.py:102`], `OnCorporationStructuresUpdated()` [C `structureDirectory.py:93`], `OnCurrentStructureStateChanged(solarSystemID, structureID, state)` [C `cosmeticFitting.py:119`] from one Event Bus subscriber `structure.notify` (J `structureLifecycleFanout.js:234-450`).
- Files: `StructureComposition.cs` (callbacks), `dotnet/src/Eve.Host/StructureDeploymentAuthority.cs` (new), `Eve.Structures`.
- Tests: Host dispatch through registered `structureDeployment` service (real Host registration, `dotnet/tests/Eve.Host.Tests/StructureDeploymentRpcTests.cs`): success creates row + ball + notifications; each refusal; Unanchor → 7 d deadline → container drop; CancelUnanchor; Rename length/role; reinforce update stores pending + effective time; restart mid-anchoring resumes.

### ST-3 Docking and hangars — L (needs ST-1, ST-0)
- `structureDocking.Dock(structureID, shipID)` (approach: `DockingApproach` with distance, follow at 2500 m [J `structureDockingService.js:134-179`; C `structureDocking.py:49-65`]); acceptance through the sim (`OnDockingAccepted`, `Docked`) then the session change with `structureid` (attributes already in `SpaceSessions.cs:505-520`). Refusals `DockingRequestDenied{reason}` (ShipTooLarge label), `DeniedShipChanged`, `TargetingAttemptCancelled`, criminal timer `CustomInfo` [J:74-127].
- `Undock(structureID, shipID, ignoreContraband=)` via `PerformSessionChange('undock')` [C `structureDocking.py:26`]; contraband prompt `ShipContrabandWarningUndock`; structure undock point = structure radius rule (no locator table for structures: `undock_place` fallback, DEVIATIONS).
- Hangars: items located at structure id (inventory `locationID = structureID`, hangar flag 4), `OnHangarViewStateUpdated(structureID, state)` [C `structureHangarViewSvc.py:31`]; access from profiles (`GetDockingAccessSnapshotsAsync` exists).
- Tests: Host + sim: dock refused out of range then accepted, session has `structureid`, ship and items in structure hangar persist, undock places ship near structure, restart while docked logs back in docked (`CharacterSelect` structure path), denied access/size; `tools/headless-client/tests/test_structures.py::dock_undock`.

### ST-4 Tether (sim) — M (needs ST-1)
- Sim rules of section 1 in a new `crates/eve-space/src/scene/tether.rs` (state per pilot), hooks in targeting lock start, fighters launch, module activation by effect family, AoE filters (`Area` recipients), smartbomb/bomb refusals (`TETHERED`), repair on engage; FX via `OnSpecialFX` guid from J `structureTether.js:388-475`. Host gives weapon timer and docking right per pilot (pilot spec flag) — protocol +1.
- Tests: `scene/tests.rs` each make/break condition; host test that a tethered ship cannot be locked and is repaired; compression refused while tethered (with ST-C).

### ST-5 Control, boarding and deliveries — M (needs ST-3)
- `structureControl.GetStructurePilot/TakeControl/ReleaseControl/BoardStructure/EjectFromStructure` [C `structureControl.py:31-103`; J `structureControlService.js:81-200`]: pilot record in `Eve.Structures` (`structure_pilot`), Board/Eject are `board` session changes (`shipid = structureid`), setting `SETTING_DEFENSE_CAN_CONTROL_STRUCTURE` from profiles.
- `structureDeliveries.Deliver(structureID, characterID, itemIDs)` [C `invItemFunctions.py:146`], `structureCargoDelivery.DropOffItems(itemIDs, shipID, structureID) → movedItemIDs` [C `dropboxController.py:117`; J `structureCargoDeliveryService.js`, `structureDeliverySaga.js`]: one inventory transaction each.
- Tests: control is exclusive, released on undock/logoff/disconnect; board/eject session attributes; delivery moves items and notifies `OnItemChange`; DropOff returns only moved ids; restart keeps pilot cleared.

### ST-6 Moon extraction (E-01) — L (ST-6a host after ST-0; ST-6b in-space after ST-1)
- Now (S, independent): replace the `PyNone` of `StartNewExtraction`/`FractureChunkWithStructure`/`CancelExtraction` when no backend (`dotnet/src/Eve.Services.Core/StructureDataRpcServices.cs:41-46`) with a named `CustomNotify` refusal, because the client reports success on any return [C `schedulingSvc.py:55-56`]. Test in `StructureDataRpcServicesTests.cs`.
- 6a: `structure_moon_extractions` table in `Eve.Structures`, import GameStore `moonExtractions`, read methods switched; `StartNewExtraction(structureID, durationSeconds, addToCalendar)` validates Standup Moon Drill I online, 6-56 d, refinery near beacon; chunk volume 30 000 m³/h (× rig yield); jobs `moon.chunk_arrival`, `moon.auto_fracture` (2698 ± 2707), `moon.field_decay` (48 h). `CancelExtraction` / `FractureChunkWithStructure` per J `moonExtractionsService.js:1167-1270`; `OnMoonExtractionUpdate(extraction)` [C `schedulingSvc.py:112`].
- 6b: fracture spawns moon-ore rocks (groups 1884, 1920-1923; jackpot meta 5, improved 3 [J `moonExtractionRules.js:162-260`]) through `SpaceContainers.Seeding.cs`, ledger observer row (`mining_ledger_observers`).
- Tests: timer chain with fake clock and restart; cancel before arrival; manual fracture window; belt decays; Host RPC wire shapes of the three mutations; ledger observer entry after mining a moon rock.

### ST-7 Structure services: compression plant, cyno jammer, cyno beacon — M (needs ST-0)
- Service state from `structure_services` (online needs fuel for 3 days [W Anchoring_structures]; fuel burn = [O] D-4, J `structureFlexFuelRuntime.js`/`structureServiceModules.js`).
- `structureCompressionMgr.CompressItemInStructure(itemID)`, `DecompressGasInStructure`, `GetMyGasDecompressionEfficiency → [structure, character]` [C `itemcompression/client/itemCompression.py:19`, `gasDecompression.py:10,21`; J `services/mining/structureCompressionMgrService.js`]; compression map from SDE `compressibleTypes` (`compressedTypeID`) [S].
- E-03: assign `BeyonceService.CynoJammerOnlineSimTimeForSystem` (`BeyonceService.cs:129-138`) from an online Upwell Cyno Jammer (group 2016, Standup Cynosural System Jammer I 35914); E-15 `OnCynoJammerChanged` on change [C `infoPanelLocationInfo.py:482`; J `sovNotifications.js:281`]; sim side: cyno lighting refused in a jammed system (reuse `Role::CynoInhibitor` conflict path, system-wide).
- `structureCynoBeaconMgr` backend (Upwell Cyno Beacon group 2017) replacing the user error in `StructureTravelRpcService.cs:47-87` (J `structureCynoBeaconMgrService.js`).
- Tests: compress in structure (item consumed/created, wire 6-tuple), jammer online → GetCynoJammerState returns time and cyno refused; offline → None; beacon list/jump.

### ST-C In-space compression (ship module, not a structure) — M (independent)
- `CompressionModuleEffect` body (`Effects/StructureModuleEffects.cs` → move to `Effects/CompressionModuleEffect.cs`): cycle 73, range 54, 3265 already enforced (`ShipEffect.cs:94`); while active the ship ball is a compression facility for its fleet within range (wiki Tethering mentions "in your fleet").
- `inSpaceCompressionMgr.CompressItemInSpace(itemID, facilityBallID)` [C `itemcompression/client/itemCompression.py:9`; J `services/mining/inSpaceCompressionMgrService.js:36-73`] → `(sourceItemID, sourceTypeID, sourceQuantity, outputItemID, outputTypeID, outputQuantity)`; type family from typeList 3255 (`sde.typeLists` 334), output from `compressibleTypes`. Keep the named refusals in `OperationalRpcServices.cs:153-200`, fix the notice text.
- Tests: Porpoise with compressor + core active → fleet member in range compresses ore in its cargo; out of range / not fleet / module off / ore family mismatch refused; Host registration and tuple shape (`OperationalRpcServiceTests.cs`).

### ST-8 Structure modules on a structure host — XL, split 8a/8b (needs ST-5)
- 8a: structure dogma host (`EffectRegistry<THost,TResult>` over a structure snapshot, see `superweapons_bursts_worldobjects.md` 5.12), activation by the controlling pilot (ST-5), structure launchers 1327/1328/1562/1974 (`MissileEffect.cs:99-102` refusals removed), structure ewar `structureModuleEffect*` 6682-6686, neutralizer 6216, scrambler 6222, point defense 6443, Structure Scan Resolution Booster 35953 (effect 6451, category 1, unpublished: keep refused if unpublished policy says so).
- 8b: Standup doomsdays and HOG 47068 (`SuperWeaponEffects.cs:104 StructureHogEffect`), burst projectors 7010, `lightningWeapon` 6447 (chain piece D9).
- Tests: per family activation/refusal from a controlled structure; gap manifest shrinks.

### ST-9 Entosis link and Sov Hub capture — L (needs ST-1 for the hub ball; sov read model exists)
- Body of `EntosisLinkEffect` (`Effects/StructureModuleEffects.cs:9`): target Sov Hub only; capture progress starts after the first cycle; one link from the attacking side is enough (no speed-up); defender link pauses or reverses; base 10 min × ADM (1-6) [W Sovereignty]; capture state in sovereignty owner (EveJS `space/modules/entosisLinkRuntime.js`, `space/runtime/entosisCapture.js`, `services/sovereignty/sovCaptureState.js`). Command nodes = later package (not in scope).
- Tests: capture completes after cycle + 10 min at ADM 1 with fake clock; defender pause; target lost resets per J.

### ST-10 Hacking (E-10 doHacking) — L (needs a hackable container: ST-11, or a seeded test container)
- `DataAnalyzerEffect` / relic analyzer body (`Effects/ExplorationModuleEffects.cs:22`): on cycle completion the minigame starts (`OnHackingTurnComplete([events])`, start events per J `hackingMgrService.js:403-428`); new `hackingMgr` service with `ClickedOnTile`, `UsedUtilityElement`, `UsedUtilityElementOnVirus`, `QuitHackingAttempt` (and `StartNewGameInstance` debug, refuse in production); board generator per wiki rules (Rule of 8, Rule of Six, subsystems, utility elements); failure counter per container (2 → destroyed); window closes on warp/out of `maxRange` 54. Success → loot can (spew mechanics: [O] D-5, wiki silent; J `maybeCompleteAttemptSite`).
- `hackOrbital` (Data Subverter 3793, unpublished): stays refused.
- Tests: deterministic board with seed; win/lose event sequences match client `_GameEventHandler` keys (`hackingUISvc.py:84-260`); two failures destroy the container; warp cancels.

### ST-11 Cosmic sites as space persistent objects (E-06 source) — L (independent)
- Content definitions `anomaly`/`signature` (FOUNDATIONS "A feature, end to end: an anomaly"), recurring director job `site.spawn` per system with `uniqueKey system:{id}:slot:{n}`, sites from `sde.dungeons` + EveJS spawn tables; position within 4 AU of a celestial [W]; completion → `content.*.completed` → respawn elsewhere [W]. Readers already exist (`PersistenceDungeonReadModel.cs:41-49`, `PersistenceIncursionAnomalyReadModel.cs:77`).
- `ICosmicSiteSource` implementation reading active instances (replace `NoCosmicSites` binding) → probe results get signatures (strength by level 42/72/104 [W] via `ProbeScanMath.RequiredStrength`); signature IDs ABC-123 [O] (client format, MITM).
- Site contents: beacon + containers (groups 306/1207) placed through the deployable/container path (`SpaceContainers.cs`), NPCs via existing spawner.
- Tests: director spawns N per system and is idempotent; probe scan finds the signature at the wiki percentages; clear → respawn; restart keeps instances.

### ST-E Remaining E wiring — S each (independent)
- E-01 refusal (in ST-6 "now" step). E-04 `OnCloakModuleForceDeactivated` from sim cloak state (proximity reason; J `scene/cloaking.js:949,969`). E-07 waits for RC-5 weapon banks (not this phase). E-05 and E-02: add headless assertions only; client reception = MITM. E-15 in ST-7.

## 3. Not provable without the real client (MITM / graphical client)
- Structure slim item rendering (brackets, timers, state labels, upkeep icons) and `OnSlimItemChange` refresh of a structure.
- Deployment UI flow: placement validity shading, `Position` button, the Anchor kwargs actually sent, pending reinforcement display.
- Dock/undock session-change visuals, hangar scene, structure undock direction (no locator table for structures).
- Tether FX guids, tether repair visuals; structure control camera (TakeControl) and module HUD for structures.
- Hacking window event sequence acceptance; moon chunk/fracture FX and jackpot flash; signature ID format in the probe scanner.
- OnDamageStateChange reaching the progression health widget (E-05).

## 4. Decisions to make before coding
D-1 size class source (Medium/Large/XL/FLEX). D-2 reinforcement-hour change delay. D-3 structure in sim as body-with-health (recommended) or NPC-like entity. D-4 service fuel burn source. D-5 hacking loot spew rules.

## 5. Order and parallel groups
- Group A (now, no shared files between them): ST-0 (Eve.Structures + Host persistence), ST-C (Effects + OperationalRpcServices), ST-11 (Content + Scanning), ST-E/E-01 refusal (StructureDataRpcServices only).
- Group B (after ST-0): ST-1 (sim wire owner), ST-6a (host moon), ST-7 (services; owns StructureComposition while ST-2 waits).
- Group C (after ST-1): ST-2, ST-9 (ST-9 takes the wire after ST-1), ST-10 (after ST-11 or seeded container).
- Group D: ST-3 → ST-4 (wire) → ST-5 → ST-6b → ST-8a → ST-8b.

| id | title | depends-on | parallel-group | size |
|---|---|---|---|---|
| ST-0 | Structure record owner, state machine, timers, GameStore import | — | A | L |
| ST-C | In-space compression (ship compressor module + CompressItemInSpace) | — | A | M |
| ST-11 | Cosmic sites director + ICosmicSiteSource | — | A | L |
| ST-E | E-01 named refusal, E-04 cloak notice, E-02/E-05 assertions | — | A | S |
| ST-1 | Structure ball, health/damage cap, dockable body (protocol 59) | ST-0 | B | L |
| ST-6a | Moon extraction record, timers, RPC mutations | ST-0 | B | M |
| ST-7 | Compression plant, cyno jammer (E-03/E-15), cyno beacon | ST-0 | B | M |
| ST-2 | Anchor/Unanchor/CancelUnanchor/Rename/UpdateReinforceTimes + notifications | ST-0, ST-1 | C | M |
| ST-9 | Entosis link on Sov Hub capture | ST-1 | C | L |
| ST-10 | Hacking minigame + hackingMgr | ST-11 (or seeded container) | C | L |
| ST-3 | structureDocking Dock/Undock, hangars | ST-1 | D | L |
| ST-4 | Tether in the sim | ST-1, ST-3 | D | M |
| ST-5 | structureControl, structureDeliveries, structureCargoDelivery | ST-3 | D | M |
| ST-6b | Moon chunk fracture belt in space | ST-6a, ST-1 | D | M |
| ST-8a | Structure dogma host, launchers, structure ewar, point defense | ST-5 | D | L |
| ST-8b | Structure doomsdays, HOG 47068, burst projectors, lightning | ST-8a | D | L |
