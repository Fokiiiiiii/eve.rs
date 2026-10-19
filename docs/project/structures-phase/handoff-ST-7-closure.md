# ST-7 closure: structure services (cynosural jammer, structure beacon, fuel and owner notices)

Owner: structures phase, package ST-7. Worktree wt-st7b from origin/main 581f82d (pushed per increment to main).
Predecessor state: `handoff-ST-7.md` (fuel, compression, jammer E-03/E-15 and the beacon checks were pushed earlier; the jump was open).

## Verified upstream before this package (commit 7335699, Integrate structure deployment, control, beacon and per-pulse authorization)

- Kind 87 RemoveCynoBeacon and kind 73 PlaceCynoBeacon are wired end to end (link, sim dispatch, SimCodec); no new protocol number was needed.
- `SpaceStructureCynoBeacons` places the published Pharolux beacon (type 35840, service 16 online, published type, valid placement data) and removes it on service offline,
  structure removal or unanchor; `RestoreAsync` puts the online ones back when a system scene is built.
- `StructureBeaconTravel` (structureCynoBeaconMgr, CmdJumpToStructureBeacon) runs the EveJS resolveUsableBeacon checks in order, then `SpaceJumpDrive.JumpToStructureBeaconAsync`
  (ship, range, fuel, fatigue, spool, high-security refusal) and `SpaceSessions.CynoJumpAsync` (sim command with the structure id as the field).
- `AgentRpc` registers `structureCynoBeaconMgr` with `StructureBeaconAsync`, `CynoJammedAsync` and `CharacterHasServiceAsync`.
- EveJS v0.12.9 structureCynoBeaconMgrService has no list method (Handle_ methods: CmdJumpToStructureBeacon, CmdGroupJumpToStructureBeacon, CmdBridgeToStructureBeacon).
- Gap that remained at the start: no Host-registration test of the beacon or the jammer gate; the StructureBeaconTravel comment still said the jump was not made.

## Increments of this package

1. Cynosural Suppression gate (open item 2): `PersistenceSovFwReadModel.IsCynoSuppressionOnlineAsync` reads the online flag of upgrade 81619 in the system hub configuration;
   `StructureComposition.JammerStateAsync` returns null unless the jammer service is online and that upgrade is online; the E-15 subscriber uses the same gate.
   Shortcut recorded in DEVIATIONS: the power and workforce budget (EveJS computeHubUpgradeSnapshot) is not ported.
2. Fuel burn owner notices (open item 3, fuel part): `StructureFuelOwnerNotifications` on the existing fuel bay topic. Online owner members outside the structure get OnItemChange;
   when none is online the fuel alert (type 181, group Structures, EveJS createStructureFuelAlertNotifications) is written to the notifications mailbox in the burn transaction with the fuel left.
   The burn event now carries the remaining fuel per type.
3. Host-wiring tests (open item 4): `StructureCompositionTests` (real HostComposition, registry calls): the jammer through `beyonce` (GetCynoJammerState) with and without the sov upgrade
   (restart between them), and the beacon jump through `structureCynoBeaconMgr`: jammed, no access, beacon offline, in that order.
   `StructureFuelOwnerNotificationTests`: the owner notice through a real event bus (OnItemChange with a member online; alert with no member online; no alert when no fuel is left).

## Open (not built; recorded in DEVIATIONS "ST-7 structure services" and TODO section 44)

- Corporation-office compression path: needs the corporation hangar take-access model (EveJS itemIsInDockableCorpOfficeWithTakeAccess, canTakeFromOwnerLocation, CORP_HANGAR_FLAGS). Not in the repo; EveJS has no compression notification type, so the notice would be OnItemChange only.
- E-15 for a sov change of the suppression upgrade alone (the jammer state changes, no notice is sent).
- Group jumps and bridges to a structure beacon (refused by name).
- Sov upgrade power and workforce budget.
- Native client acceptance of the jammer notice, the beacon jump and the fuel notices (no graphical client run).

## Tests

- Full Eve.Host.Tests (dotnet build, then dotnet test --no-build, PostgreSQL database eve, EVE_PG_URL key=value): 343 passed, 0 failed, 0 skipped (11 min).
- Focused: StructureCompositionTests 13 (incl. the two ST-7 registry tests), StructureFuelOwnerNotificationTests 3, StructureCynoSuppressionTests 5: 21 passed.
- The Host composition tests for the space services start the host with EVE_SIM=on at an unused socket (the beyonce and structureCynoBeaconMgr services are registered only with a simulation client; no simulation answers, because these checks refuse before the simulation is asked).
- E-15 (open item 4): the jammer notice moved out of the composition into StructureCynoJammerNotifications (same behaviour: OnCynoJammerChanged to the pilots in space in the system, with the state the jammer answers); StructureCynoJammerNotificationTests (PgFact, real event bus): told in the system, not elsewhere, and a beacon change is not a jammer change. Passes 1 of 1.
- Pharolux beacon in the simulation (open item 1, placement): StructureCynoBeaconSimTests (Eve.Services.Space.Tests, PgFact, rig fake simulation): placed as SimPlaceCynoBeacon when service 16 comes online through the event bus, taken out (SimRemoveCynoBeacon) when it goes offline. Passes 1 of 1.
- Eve.Services.Space.Tests: StructureBeaconTravelTests, BeaconHostTests and the new test passed (22 of 23 before the new test; the new test then passed alone). Full Eve.Services.Space.Tests: 860 passed, 0 failed, 0 skipped (16 min).
- Jump to a structure beacon through the drive (open item 1, jump): CynoJumpHostTests.A_revelation_jumps_to_an_accessible_structure_beacon_as_it_does_to_a_field (PgFact): the spool is the sim CynoJump with the structure id, the fuel is debited, the fatigue and cooldown are written. CynoJumpHostTests passes 24 of 24. The host-level refusals (access, jammer, service) run through structureCynoBeaconMgr in StructureCompositionTests.
- The full Eve.Host.Tests was rerun after the E-15 refactor: 330 passed and 15 failed with the connection-limit error of the shared PostgreSQL; those 15 rerun alone and pass (17 of 17 in the five classes).
- not exercised: graphical client.

## Update: corporation-office compression source (2026-10-15, later)

- Built on the existing models: StructureCompressionAuthority.SourceItemAsync accepts a corporation hangar item of the pilot corporation at the structure when the corporation has an active office rental there (CorporationOfficeStore row) and the pilot may take from the division (HostInventoryServiceFactory.HangarAccessOf, CorporationRoles.HasHangarAccess). The same source serves gas decompression, as in EveJS.
- The corporation members online who may query the division are told each change (OnItemChange), through the sessions of the corporation; no mailbox notice (EveJS has no notification type for a compression result).
- Corrected ItemFlags.CorpSAG1..7 from 116..122 to 115..121 (the client flagCorpSAG1 = 115). The repo already used 115 in CorporationRoles and IndustryRules.
- Tests: StructureCompressionTests 9 of 9 (three new: compressed by a member who may take; refused without take access or without an office, changes nothing; members who may query are told and outsiders are not). Eve.Services.Inventory.Tests 63 of 63, Eve.Corporations.Tests 125 of 125.
- Still open: office rental at a structure (the repo rents offices at NPC stations only). Queued as a separate task.
- Also run after the flag correction: full Eve.Host.Tests 348 of 348, Eve.Market.Tests 105 passed with one benchmark test skipped (OrderBookBenchmarkTests, pre-existing), Eve.Ships.Tests 229 of 229.
