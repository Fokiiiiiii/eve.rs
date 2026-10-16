# ST-20 handoff: live scenarios of the structure services, slow and skill-speed runs, full Host suite (2026-10-16)

Worktree `wt-st20` from origin/main 6569f683; database `eve_p4`, live port 26103 (`flock` on `/tmp/eve-heavy-eve_p4.lock`). Rows: TODO section 60 and the structure rows it names. Rules: `agent-rules.md`.

## 1. Live scenarios (`tools/headless-client/tests/test_structures.py`)

The module seeds its own Astrahus: the autouse fixture `seeded_structure` inserts the structures row and its service row (`where not exists`), into `EVE_STRUCTURES_SCHEMA`, which `tools/dev/run-eve-live.sh` exports with `EVE_PG_URL`. The default live run now lists the file (`tools/dev/run-eve-live.sh`). Before, the seed was a manual SQL step (ST-14).

Expected answers come from the reference (EVE.js, cited per test), not from a recording of the server:

| Scenario | Expected | Reference |
|---|---|---|
| Anchor by a pilot in an NPC corporation, in space | UserError `DropNeedsPlayerCorp` | `sovPlayerDeployment.js`; the server gate runs before the role check |
| Anchor by a member of a player corporation with no role | `CrpAccessDenied`, reason `Insufficient roles` | wiki "Anchoring structures": Station Manager, Director or CEO |
| `structureControl.GetStructurePilot` of an uncontrolled structure | None | `structureControlService.js` `Handle_GetStructurePilot` |
| `TakeControl` from space | CustomNotify "You must be docked in this structure to take control." | `Handle_TakeControl` (NOT_DOCKED_IN_STRUCTURE) |
| `BoardStructure` of an unknown id | `TargetingAttemptCancelled` | `throwControlDenied("STRUCTURE_NOT_FOUND")` |
| `hackingMgr.QuitHackingAttempt` with no attempt | None | `hackingMgrService.js` `Handle_QuitHackingAttempt` |
| `hackingMgr.ClickedOnTile` with no attempt | None, and an ACK (`OnHackingTurnComplete`) | `Handle_ClickedOnTile` emits `EVENT_ACK` |
| `structureDeliveries.Deliver` from space | CustomNotify "You must be inside the structure to deliver items." | `structureDeliveriesService.js` `Handle_Deliver` |
| `moonExtractions.StartNewExtraction` with duration 0 | `MoonminingEventTooShort` | DEVIATIONS "Moon extraction (ST-6a)": the reference answers None, the server refuses by name on purpose |
| `moonExtractions.StartNewExtraction` from space | `StructureDefenseDenied` | `validateMoonExtractionMutationAccess` |

Result on eve_p4: `test_structures.py` 11 passed (`tools/dev/run-eve-live.sh` with `EVE_RUN`, the file alone).

Server change found by the scenarios: `dotnet/src/Eve.Host/Persistence/Structures/StructureControlBackend.cs`. The old `ControlAsync` checked the structure's setting (17) before the docked and system checks, and every refusal was `CustomNotify "Structure control refused: <REASON>"`. The reference orders the checks (not found, docked for TakeControl, system, setting) and sends its own labels. The change keeps the owner lookup first, moves the docked check ahead of the setting, uses the reference's order for the system check, and maps `STRUCTURE_CONTROL_DENIED` to `StructureDefenseDenied`, `STRUCTURE_NOT_FOUND` to `TargetingAttemptCancelled`, and the two notices for the docked and system refusals. Other reasons keep the old text. By code reading, the old order answered `STRUCTURE_CONTROL_DENIED` to the pilot in space; this was not run before the change.

### Not live (named precisely)

1. A successful `TakeControl`, `BoardStructure` and `EjectFromStructure`. They need a pilot docked inside the structure (the ST-14 docking flow), and the structure's profile must grant setting 17 (`DefenseCanControlStructure`) to the pilot's corporation. The seeded structure has no profile row. Next step: seed a profile with the grant (structures profile tables; see `StructureControlReadModel`'s `hasSetting`), dock as in `test_a_pilot_docks_at_the_structure_and_undocks_from_it`, then take control, board and eject, and check the pilot's session and the structure's observer.
2. A successful `structureDeliveries.Deliver`: a pilot docked inside the structure with an item in its ship (the item must be in the owned hangar subset).
3. A successful `moonExtractions.StartNewExtraction`: an online Standup Moon Drill, moon resources in the profile (`MoonExtractionStore`), and the caller docked as the controller. Its answer shape (the extraction payload) is therefore not checked live.
4. A successful anchor (the item is in the ship, the pilot is in a corporation older than seven days, not in a trade hub). The refusals are live; a successful anchor is covered by the Host tests (`StructureAnchorTests`).

## 2. Slow and skill-speed runs

- `EVE_LIVE_SLOW=1 tools/dev/run-eve-live.sh` on eve_p4 (the default list plus `test_structures.py`): exit 0, 1 skipped (the skill-speed test). Nothing failed, so there was no root cause to fix in the suite.
- The skill-speed test `tests/test_login_to_character.py::test_a_queue_that_finishes_while_the_client_watches_is_told_level_by_level` with `EVE_SKILL_TRAINING_SPEED=60000` on the host: 1 passed (it skipped at speed 1). It runs alone because that speed breaks the speed-1.0 skill and dogma tests (ST-14).
- The run used `flock -o -w 7200 /tmp/eve-heavy-eve_p4.lock timeout 10800 tools/dev/run-eve-live.sh` (the same lock as `db-lock.sh`, with a 3-hour timeout for the slow run; `db-lock.sh` kills after 30 minutes).

## 3. Full Eve.Host.Tests (eve_p4)

373 passed, 0 failed, 0 skipped, on the origin/main tree before the control change and again after it (`dotnet build` + `dotnet test`, `UseSharedCompilation=false`, under the eve_p4 lock).

## 4. Checks

- No Rust was changed, so clippy was not run for this package. `cargo fmt --all` was run before the push.
- Python tests: the live file above. No Python lint is configured for `tools/headless-client`.

## Things that will bite

- The worktree has no `.cache`, `.venv` or `target` directories of its own. They are symlinks to `/home/user/eve.rs` (the static data copy, the headless venv, the shared `target` and `target-194`). Remove them with the worktree.
- Serena's default project for this session was `/home/user/wt-st18`, which does not exist; activate `/home/user/wt-st20` explicitly.
- `grep`, `cat` and `head` are denied in Bash here. Use Serena, Python or `psql`.
