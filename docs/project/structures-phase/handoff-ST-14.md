# ST-14: structures phase live check and reconciliation (2026-10-15)

Base: origin/main 46e164b (CI green). Database `eve_p2`, live port 26101, PostgreSQL 18 on 127.0.0.1:5432, `tools/dev/db-lock.sh`.

## 1. Live headless suite

Run A, the default list of `tools/dev/run-eve-live.sh` plus `tests/test_structures.py` (EVE_RUN; `EVE_LIVE_START_TENTHS=3000`, the host
needed more than the default 60 s to load its catalogs after PostgreSQL recovery):

- 71 passed, 0 failed, 10 skipped. The skips: 7 slow-mode tests (`EVE_LIVE_SLOW=1` not set: movement, warp, jump, others, target, module,
  health), 1 skill-speed test (`test_login_to_character.py`, needs the host at `EVE_SKILL_TRAINING_SPEED=60000`; a run with that setting breaks the
  speed-1.0 skill and dogma tests, so it needs its own run), and the 2 structure tests (environment not set).
- Nothing was run with `EVE_LIVE_SLOW=1` or the graphical client.

Run B, `test_structures.py` against a seeded structure (EVE_RUN prelude; the scenario creates its own pilot as `test_undock.py` does):

- `test_a_pilot_in_the_system_sees_the_structure_with_its_state_and_timer`: passes live. A new pilot undocks at station 60015249 (system
  30100032) and sees the seeded Astrahus (category 65) with its state and timer. This shows the restore on undock
  (`SpaceSessions.Ships.cs` raises `SystemEntered`, `SpaceStructures.RestoreAsync`) works through the real stack.
- `test_a_pilot_docks_at_the_structure_and_undocks_from_it`: fails live. `structureDocking.Dock` answers `DockingRequestDenied`
  "Structure docking access denied." (`StructureComposition.cs` `StructureDockingAllowedAsync`). Checked: the docking service row is online (the
  services read is `structure_services`, service 1 state 1); the owner corporation 1000006 is the bloodline's starter corporation. Not isolated: the
  access snapshot (`PersistenceStructureDirectoryReadModel.Part2.cs`, `StructureDirectoryService.CanDock`: profile resolved, docking setting,
  access list members). Open. The dock flight itself is not reached.

The rewritten `test_structures.py` is on the local branch `st14-work`, not on main, because its dock test fails.

Seed used (after the host starts, into the scratch schema `EVE_STRUCTURES_SCHEMA`):

```sql
INSERT INTO :"schema".structures (structure_id, type_id, solar_system_id, x, y, z, owner_corporation_id, name, state, state_started_ms, upkeep, core_installed, doc)
VALUES (1040000000777, 35832, 30100032, 311989875775, -13069156275, 156646281447, 1000006, 'Live Astrahus', 110, 0, 1, true, '{}');
INSERT INTO :"schema".structure_services (structure_id, service_id, state) VALUES (1040000000777, 1, 1);
```

The station position is `sde.npcStations` key 60015249 (`doc.position`), offset 5 km. The owner must be a corporation: a row with owner 0 is not
a deployed structure (`IsPersistentlyDeployedStructure`). A real structure also has a profile and access state written by Anchor; the seed has none.

Not run: the live scenarios for the control/board calls, the anchor refusal by name, the moon-extraction refusal and answer shape, and the
hacking manager's no-attempt answer. They need a seeded moon, corporation roles or hacking data, which the harness does not provide. None was added.

## 2. TODO reconciliation

Rows set from code and tests (section 2 and sections 43 to 57; file names are the evidence): moonExtractions (DONE), structureCargoDelivery,
structureCompressionMgr, structureControl, structureDeliveries (DONE), structureDeployment (PARTLY: Anchor refusals for starter/career systems,
Thera, shattered wormholes and the sovereignty index delay are not built, row 1464), structureDocking (PARTLY: live dock refused, see above),
launchers (structure launcher groups served by the shared handlers when fitted to a ship; running them on a controlled structure is ST-8, OPEN),
entosisLink (DONE, the `STRUCTURES_PHASE_3` refusal is gone from the code), SpaceStructures and dockable body rows (DONE), placement on anchoring
(DONE), reinforcement-change notice (type 209, DONE), unanchor drop (DONE).

## 3. Neutral Sovereignty Hub claim notices (V24.01 43 and 44)

`SovereigntyClaimNotifications` (Host, `Persistence/Sovereignty`) writes through `PersistenceTransactionMailbox.WriteNotificationsAsync`, after the
claim is persisted (`ClaimCapturedHubAsync`) and the ball is replaced, in `StructureEntosisComposition.cs` (`EntosisHandoverAsync`):

- 43 (AllianceClaimAquired) to every character of the capturing alliance; 44 (CorpClaimAquired) to every character of the executor corporation.
  Payload `corpID` and `allianceID` (eve-logic `notificationUtil.py`). The recipient rule is ours (DEVIATIONS "Sovereignty claim notices").
- Key `sov-claim:{type}:hub:{hub}:claim:{claim time}`. The claim time is the one the records hold, so a retried handover writes nothing twice, and
  a later claim of the same hub is a new notice. A failure leaves the handover pending for the job.

Tests (pass on eve_p3): `SovereigntyClaimNotificationTests` (1, new), `SovHubClaimTests` (3), `EntosisOwnerNotificationTests` (1).

## Open

- Live dock (section 1). The rewritten structure test is on `st14-work` only.
- The slow tests, the skill-speed test and the client-side checks above.
- Anchor refusals of starter/career systems, Thera, shattered wormholes and the sovereignty index delay (TODO row 1464).
- The client text of 43 and 44 (MITM not run).
