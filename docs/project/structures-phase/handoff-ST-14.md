# ST-14: structures phase live check and reconciliation (2026-10-15)

Base: origin/main 46e164b (CI green). Database `eve_p2`, live port 26101, PostgreSQL 18 on 127.0.0.1:5432, `tools/dev/db-lock.sh`.

## 1. Live headless suite (final, after the docking root cause)

Run (`EVE_RUN` = the default list, then the structure seed, then `test_structures.py`; `EVE_LIVE_START_TENTHS=3000`, database `eve_p2`,
port 26101): **73 passed, 0 failed, 8 skipped**. The skips: 7 slow-mode tests (`EVE_LIVE_SLOW=1` not set: movement, warp, jump, others, target,
module, health) and 1 skill-speed test (`test_login_to_character.py`, needs the host at `EVE_SKILL_TRAINING_SPEED=60000`; that setting breaks the
speed-1.0 skill and dogma tests, so it needs its own run). not exercised: the slow tests, the graphical client, the MITM comparison.

Both structure tests pass live:

- `test_a_pilot_in_the_system_sees_the_structure_with_its_state_and_timer`: a new pilot undocks at station 60015249 (system 30100032) and sees the
  seeded Astrahus (category 65) with its state and timer (restore on undock, `SpaceSessions.Ships.cs` raises `SystemEntered`).
- `test_a_pilot_docks_at_the_structure_and_undocks_from_it`: `Dock` is accepted, `OnDockingFinished` arrives, `Undock` gives the item change.

### Root cause of the earlier "Structure docking access denied." (fixed)

Not a seed gap and not the profile or access rule. The pilot's `affiliation.corporation` relation did not exist: the relation is written only when
corporation membership changes (`CorporationStore` writer) and reconciled at startup (`AllianceRegistryService.RegisterAsync`). A character created
while the server runs (`CharacterCreation.CreateAsync`) got no relation, so `AffiliationOfAsync` returned corporation 0. The docking check then passed
corporation 0 to the snapshot, which returned nothing, and the refusal followed. Diagnosed live with a temporary log in `StructureDockingAllowedAsync`
(`corp 0`; the snapshot list was empty); the log was removed before the commit.

Fix: `CharacterCreation` takes an optional `writeCorporationMembership` callback and calls it in the creation transaction after the character insert;
the composition passes `foundations.Relations.SetAsync(... CorporationMember ...)`. Test: `CreationTests.The_new_character_gets_its_corporation_membership_written_in_the_creation_transaction`
(Eve.Characters.Tests, PostgreSQL; the 20 creation tests pass on eve_p3).

Host test on the real Anchor: `StructureAnchorTests.An_anchored_structure_is_dockable_by_its_owner_corporation_pilot_with_the_default_profile`
anchors an Astrahus through `StructureAnchorAuthority` (the default profile, id 41), reads the structures table as the directory sees it and the profile
table the profile backend writes, and checks `StructureDirectoryService.CanDock`: the owner corporation's pilot may dock, another corporation may not.
It passes with the other `StructureAnchorTests` on eve_p3 (10 passed, 0 skipped, including the pinned SDE restriction test).

EveJS comparison (read, not exercised): `structureProfilesState.js` `characterHasDockingAccessForProfile` requires `canCharacterDockAtStructure` and the profile
setting `HOUSING_CAN_DOCK` for the session, and the access groups match the session's corporation (specificity 300), alliance (200) or everyone (0). A
member of the owner corporation is admitted through the corporation chain, as in `CanDock`'s owner-corporation fallback. Not checked in this pass: the
default value of `HOUSING_CAN_DOCK` in EveJS, and the wiki's default docking rule. The Host's explicit profile-setting path (profile with settings for
setting 19) is not exercised by any live or Host test in this pass.

### The seeded structure (what the run inserts, after the default list)

```sql
INSERT INTO :"schema".structures (structure_id, type_id, solar_system_id, x, y, z, owner_corporation_id, name, state, state_started_ms, upkeep, core_installed, doc)
VALUES (1040000000777, 35832, 30100032, 311989875775, -13069156275, 156646281447, 1000006, 'Live Astrahus', 110, 0, 1, true, '{}');
INSERT INTO :"schema".structure_services (structure_id, service_id, state) VALUES (1040000000777, 1, 1);
```

`schema` is `EVE_STRUCTURES_SCHEMA` (psql `-v schema=`). The station position is `sde."npcStations"` key 60015249 (`doc.position`), plus 5 km in x.
The owner is the corporation of bloodline 1 (`sde.bloodlines`, 1000006). The seed is not an anchored structure: it has no profile row, so it relies
on the owner-corporation access fallback. The live test does not depend on the profile.

## 2. TODO reconciliation

Rows set from code and tests (section 2 and sections 43 to 57; file names are the evidence): moonExtractions (DONE), structureCargoDelivery,
structureCompressionMgr, structureControl, structureDeliveries (DONE), structureDeployment (PARTLY: Anchor refusals for starter/career systems,
Thera, shattered wormholes and the sovereignty index delay are not built, row 1464), structureDocking (DONE, live: see section 1; the refusal was the missing corporation relation of new characters),
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

- The slow tests (`EVE_LIVE_SLOW=1`), the skill-speed test (own run with `EVE_SKILL_TRAINING_SPEED=60000`), the graphical client and the MITM check of
  notices 43 and 44.
- Live scenarios for the control/board calls, the anchor refusals by name, the moon-extraction answer shape and the hacking manager's no-attempt
  answer: not added (they need seeded moon, role or hacking data).
- Shattered wormholes and the sovereignty index delay are not refused at anchor (TODO row 1464). Thera, starter and career systems and trade hubs are
  refused by the pinned SDE rule (`Pinned_SDE_anchor_restrictions_refuse_before_consumption`, passing).
- The client text of notices 43 and 44 (MITM not exercised).
