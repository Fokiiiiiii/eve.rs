# Deep fix: wreck salvage cycle

## Scope and decision

This change targets the frequently repeated salvage-drone/module cycle: target a wreck, attempt access, then either retry a failed cycle or finish the wreck and receive salvage. EVE Uni documents three outcomes per cycle: unsuccessful cycles continue, while successful cycles may yield nothing or materials and remove the wreck; unlooted contents move to a can. It also says loot and salvage are calculated at destruction, and salvaging skill/equipment affect cycles rather than the result ([Salvaging, “Salvaging wrecks”](https://wiki.eveuniversity.org/Salvaging#Salvaging_wrecks), lines 127–135). Pinned static data is build 3396210 (sde/lock.json:2–3, 166).

The EVEJS reference (261028.1, server/src/space/modules/salvagerRuntime.js:734–789) does not implement that timing: it rolls reward/empty outcomes on salvage completion. It uses size-based empty chances, roll counts, quantities, and weighted faction/common pools (lines 118–184, 705–719, 734–789). eve.rs follows those observable reward categories and cycle outcomes for this change, but selects uniformly from the matching SDE product names in SpaceContainers.Work.cs:448–566. This distribution and the generation timing differ from the wiki statement and remain a compatibility gap; no claim is made that the reward calculation matches CCP.

## Client path and wire evidence

- The whole V24.01 client has one CmdSalvage callsite: eve/client/script/ui/services/menuSvcExtras/droneFunctions.py:156–162. It sends drone IDs and the active target through entity.CmdSalvage. The menu delegates to this helper.
- The same file, lines 18–34, turns a per-drone error tuple into UserError (or MultiDroneCmdResult when errors differ). Thus refusal paths must remain errors and a successful no-yield cycle must remain success.
- Client loot-right interpretation is in eve/client/script/remote/michelle.py:1440–1472: IsAbandoned unpacks (ownerID, corpID, fleetID, abandoned), and HaveLootRight considers NPC ownership, abandonment, owner/corp/fleet, and CanAttackFreely. Slim updates are consumed at lines 1481–1488.
- LootAll first runs crimewatch checks, adds contents to ship cargo, then scatters OnWreckLootAll (eve/client/script/environment/invControllers.py:1353–1365); inventory windows consume it at eve/client/script/ui/shared/inventory/invWindow.py:1243–1252. Container opening is invoked through GetContainerContents at eve/client/script/ui/services/menuSvcExtras/invItemFunctions.py:461–475.
- The existing rights/abandonment implementation and its client trace are recorded in docs/project/structures-phase/handoff-agent-wreck-loot-cx3.md. This change does not alter those flows.

## eve.rs behavior and fix

EntityService.CmdSalvage is the Host entry point (dotnet/src/Eve.Services.Space/EntityService.cs:63–70); SpaceSessions settles the drone work cycle and routes its result (dotnet/src/Eve.Services.Space/SpaceSessions.cs:563–587, 589–774). A failed access retains the wreck and emits the SalvagingFailure notification. A successful cycle now permits an empty result; it removes the wreck, preserves remaining contents in a replacement can, records the cycle and event atomically, and returns success. When products exist, cargo capacity is checked for the whole grant and the material items, wreck removal, can contents, cycle ledger, and fact are committed together (dotnet/src/Eve.Ships/CharacterShips.Cargo.cs:480–581). Replayed cycles do not send a second success notice (dotnet/src/Eve.Services.Space/SpaceContainers.Work.cs:422–455).

The client has no separate salvage-result RPC payload consumer: the user-visible result is CustomNotify after the committed state/item notification; the ship cargo notification is sent first. An empty success now sends an explicit CustomNotify. The tested reward distribution remains the source gap stated above.

## Regression coverage

dotnet/tests/Eve.Services.Space.Tests/SpaceContainerTests.cs:525–605 covers retry after failed access, empty success, multiple size-based reward quantities, wreck removal, retained contents in a can, and the success notice. WorkCycleHostTests.cs:210–227 checks the Host notification and salvage fact. DroneHostTests.cs:671 onward verifies an SDE salvage item is persisted. These fixtures pin reward rolls without weakening their assertions. The four affected database test classes passed together on eve_p4: 74 passed, 0 failed, 0 skipped.

The graphical V24.01 client was not exercised. EveJS source was inspected, not run end-to-end for this scenario.

## Push and checks

Implementation commit `304f4970992a` is on `main`. Push-triggered CI run [41471425498](https://github.com/Fokiiiiiii/eve.rs/actions/runs/41471425498) completed successfully with no failed jobs. The dispatched focused Actions run [41471470725](https://github.com/Fokiiiiiii/eve.rs/actions/runs/41471470725) also succeeded. The local focused suite passed 74 tests across SpaceContainerTests, WorkCycleHostTests, DroneHostTests, and LootAllTests, with no skips. Reward generation timing and weighted-pool parity remain open as described above.
