# ST-21 handoff: live success flows of the structure services (eve_p4, 2026-10-16)

Worktree `wt-st21` from origin/main 686143bd; database `eve_p4`, live port 26103, `tools/dev/db-lock.sh`. Commits on main: `fd6167e4` (control, deliveries, moon extraction, fixes) and `11ef1caa` (legal-system anchor).

## 1. Live scenarios (`tools/headless-client/tests/test_structures.py`, 19 tests, all passed on eve_p4)

Seed (fixture `seeded_structure`): the seeded structure is a refinery: an Athanor (35835, rigSize 2) in system 30100032 with its docking (1) and moon mining (14) services online and a moon (40000001) with its resource shares in `structure_moon_resources`. The owner is NPC corporation 1000006 (the recorded pilots' corporation), so the owner-corporation access rule admits them; with no profile row, setting 17 (`DefenseCanControlStructure`) is granted to that corporation.

| Flow | Live result | Reference |
|---|---|---|
| `structureControl.TakeControl` by a docked pilot | None; `GetStructurePilot` is the pilot; `OnShipCosmeticsChanged` | structureControlService.js `Handle_TakeControl`; structureControlState.js `assumeStructureControl` |
| `ReleaseControl` | None; no pilot | `Handle_ReleaseControl` |
| `BoardStructure` from space, then `EjectFromStructure(0)` | None, then None; pilot cleared | `Handle_BoardStructure`, `Handle_EjectFromStructure` |
| Second pilot's `TakeControl` while the first controls | None; the second is the pilot | assumeStructureControl relinquishes the previous controller (`override`); scene `bindControlledStructureSession` lets it through. The user's premise of a refusal is not the reference's behaviour: the refusal (`STRUCTURE_ALREADY_CONTROLLED`) needs a controller other than the recorded one |
| `structureDeliveries.Deliver` (sender docked) | None; items move to the recipient, flag 173, location the structure; recipient gets `OnNotificationReceived` type 199 | structureDeliveriesService.js `Handle_Deliver`, `createStructureItemsDeliveredNotification` |
| `structureCargoDelivery.DropOffItems` (ship in space) | the moved ids; owner, flag 173, location the structure | `Handle_DropOffItems` (returns `movedItemIDs`) |
| `moonExtractions.StartNewExtraction` by the controlling pilot | the ten-field extraction KeyVal; `OnMoonExtractionUpdate` carries it; a second start answers the same extraction | moonExtractionsService.js `Handle_StartNewExtraction`, `buildExtractionPayload`, `sendMoonExtractionUpdate` |
| `structureDeployment.Anchor` by a Director, corporation eight days old, legal system 30000903 | None; item consumed; structure row (corporation, system, `AnchorVulnerable` 115); the ballpark shows it; `OnStructureStateChanged` to the corporation | sovPlayerDeployment.js; Host test `Anchoring_an_item_from_the_ship_consumes_it...` |
| `Unanchor` / `CancelUnanchor` | None once the structure is `ShieldVulnerable` (110) | STRUCTURE_UNANCHOR_CANCEL_STATES (structureConstants.js) lists `AnchorVulnerable`: an unanchor in the anchoring state is refused, correctly |
| `Anchor` in Manifest (30100032) | `CantDeployBlocked` with typeID 35832 | the pinned SDE record of 30100032 lists category 65 (Upwell structures) in `disallowedAnchorCategories`; the refusal is the pinned rule |

How the pilots got there: the recorded pilot is created at Manifest's station (60015249). The legal-system scenario moves the character and its ship to station 60014062 of system 30000903 through the database (`characters.station_id`, `solar_system_id` and the ship item's hangar), then re-selects the character (`_reenter`), undocks there (`_undock(..., station, system)`), and warps 2000 km out (`CmdWarpToStuff("position", ...)`) so the free-space rule (1000 km from every station, belt and structure) holds. The warp ends with a `CmdStop` (the server clears its warp flag on an accepted stop). The repair timer of a new anchor runs in real time, so the test sets the structure to its online state before unanchoring.

Scenario 3 (moon) needed the server fixes below; the first moon start that reached the rig read was refused by name, then by an exception.

## 2. Server bugs found by the live scenarios (fixed at the root, with Host tests)

1. Moon service access (`PersistenceStructureDirectoryReadModel.Part2.cs`): `SettingIdForService` had no entry for service 14 (MOON_MINING), so every moon start was refused `StructureDefenseDenied`. Service 14 now maps to setting 17 (EVE.js `structureServiceAuthority.js`). Test: `HostServerTests.Moon_mining_service_is_admitted_by_the_control_setting_of_the_owner_corporation`.
2. Moon rig rules (`MoonRigReadModel.cs`): read the structure record (type, owner corporation, system) instead of the structure's inventory item. The anchor consumes that item, so every moon start of an anchored refinery threw `Moon rig rules require the structure item`. Tests: `MoonRigReadModelTests` (fixture creates the structure record; disposal drops its schema).
3. Boarding from space (`StructureControlBackend.cs`): the gate `type.Attributes[1547] >= 2` refused every medium Upwell structure (the Astrahus has rigSize 2 in the pinned SDE). EVE.js `boardStructureControlFromSpace` has no size gate. The gate is removed; the refusal reason for the remaining checks is `NOT_BOARDABLE_FROM_SPACE`. Test: `StructureControlBackendTests.A_medium_upwell_structure_is_boarded_from_space_as_the_reference_allows`.

## 3. Test runs (eve_p4, port 26103)

- Live `test_structures.py` alone: 19 passed (last run 11, after the final edits).
- Default live list (`tools/dev/run-eve-live.sh`, no `EVE_RUN`): exit 0; all passed; skipped only the slow-mode tests (movement, warp, jump, others, target, module, health) and the skill-speed test.
- Slow-mode list (`EVE_LIVE_SLOW=1`): exit 0; all passed; the one skip is the skill-speed test (`EVE_SKILL_TRAINING_SPEED=60000`, its own run).
- Host tests of the changed code (filter `StructureControl|MoonRig|MoonExtraction|Moon_mining_service|Missing_default_profile|StructureAnchor|StructureComposition|StructureDelivery`, eve_p4): 83 passed, 0 failed, 0 skipped.

## 4. Not done or not checked

- Eject back to the pilot's own ship (`EjectFromStructure` with a ship id after a boarding from space): Host-tested, not live.
- Moon extraction's later stages (fracture, completion, ore delivery) and the moon's yield numbers: not checked.
- The inbox notice is checked on the recipient's live session only (the mailbox row itself is not read back).
- Graphical client and MITM comparison of the notices: not run.

## Things that will bite

- `grep`, `cat` and `head` pipelines are denied in Bash here; use Python for reads, `psql` for SDE queries.
- The SDE stores attribute and system restrictions as JSON arrays; a missing key means no restriction. Use `coalesce(... @> ..., false)` in SQL, or every system looks restricted.
- Live pilots need `EVE_MARKET_SEED=skip` on this machine; without it the host waits on ESI and the run gives up after 60 s, exiting 0.
