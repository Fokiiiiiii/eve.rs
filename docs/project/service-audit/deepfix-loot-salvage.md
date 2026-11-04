# Deep fix: wreck salvage cycle

## Scope and decision

This change targets the frequently repeated salvage-drone/module cycle: target a wreck, attempt access, then either retry a failed cycle or finish the wreck and receive salvage. EVE Uni documents three outcomes per cycle: unsuccessful cycles continue, while successful cycles may yield nothing or materials and remove the wreck; unlooted contents move to a can. It also says loot and salvage are calculated at destruction, and salvaging skill/equipment affect cycles rather than the result ([Salvaging, “Salvaging wrecks”](https://wiki.eveuniversity.org/Salvaging#Salvaging_wrecks), lines 127–135). Pinned static data is build 3396210 (sde/lock.json:2–3, 166).

The EVEJS reference (261028.1, server/src/space/modules/salvagerRuntime.js:734–789) rolls reward/empty outcomes on salvage completion. It builds the faction pool followed by the common T1 pool, filters large-only entries for large and capital wrecks, and selects each material by positive weight (lines 118–184, 692–719, 734–789). eve.rs now follows that filtering and weighted order in SpaceContainers.Work.cs, while resolving every type ID from the pinned SDE product list. EVE University does not publish numeric weights, so these weights are copied as observed EveJS behavior, not asserted as wiki-derived mechanics. Reward generation still occurs at salvage completion; the wiki says salvage is determined at destruction, so that separate timing difference remains open.

## Client path and wire evidence

- The whole V24.01 client has one CmdSalvage callsite: eve/client/script/ui/services/menuSvcExtras/droneFunctions.py:159–160. The menu reaches it through menusvc.py:524–525 and :3609–3610; the helper sends drone IDs and the active target, then passes the return to HandleMultipleCallError. The client does not consume reward type IDs from this RPC.
- The same file, lines 18–34, turns a per-drone error tuple into UserError (or MultiDroneCmdResult when errors differ). Thus refusal paths must remain errors and a successful no-yield cycle must remain success.
- Client loot-right interpretation is in eve/client/script/remote/michelle.py:1440–1472: IsAbandoned unpacks (ownerID, corpID, fleetID, abandoned), and HaveLootRight considers NPC ownership, abandonment, owner/corp/fleet, and CanAttackFreely. Slim updates are consumed at lines 1481–1488.
- LootAll first runs crimewatch checks, adds contents to ship cargo, then scatters OnWreckLootAll (eve/client/script/environment/invControllers.py:1353–1365); inventory windows consume it at eve/client/script/ui/shared/inventory/invWindow.py:1243–1252. Container opening is invoked through GetContainerContents at eve/client/script/ui/services/menuSvcExtras/invItemFunctions.py:461–475.
- The existing rights/abandonment implementation and its client trace are recorded in docs/project/structures-phase/handoff-agent-wreck-loot-cx3.md. This change does not alter those flows.

## eve.rs behavior and fix

EntityService.CmdSalvage is the Host entry point (dotnet/src/Eve.Services.Space/EntityService.cs:63–70); SpaceSessions settles the drone work cycle and routes its result (dotnet/src/Eve.Services.Space/SpaceSessions.cs:563–587, 589–774). A failed access retains the wreck and emits the SalvagingFailure notification. A successful cycle now permits an empty result; it removes the wreck, preserves remaining contents in a replacement can, records the cycle and event atomically, and returns success. When products exist, cargo capacity is checked for the whole grant and the material items, wreck removal, can contents, cycle ledger, and fact are committed together (dotnet/src/Eve.Ships/CharacterShips.Cargo.cs:480–581). Replayed cycles do not send a second success notice (dotnet/src/Eve.Services.Space/SpaceContainers.Work.cs:422–455).

The client has no separate salvage-result RPC payload consumer: the user-visible result is CustomNotify after the committed state/item notification; the ship cargo notification is sent first. An empty success now sends an explicit CustomNotify. This change only replaces candidate filtering and weighted selection; it leaves quantity, timing, item notifications and RPC shape unchanged.

## Regression coverage

dotnet/tests/Eve.Services.Space.Tests/SalvagePoolTests.cs pins all seven faction pools and the generic pool for small and large wrecks, verifies faction/size classification, SDE TypeId mapping, boundary rolls and invalid/empty pools. SpaceContainerTests.cs:525–605 still covers cycle retry, empty success, size quantities, wreck removal, retained contents and the notice; its deterministic Amarr generic case now asserts Malfunctioning Shield Emitter. WorkCycleHostTests.cs:210–227 checks the Host notification and salvage fact, and DroneHostTests.cs:671 onward verifies an SDE salvage item is persisted. Local focused checks passed 33 pool tests and 80 database tests across SpaceContainerTests, WorkCycleHostTests, DroneHostTests and LootAllTests on eve_p4, with no skips.

The graphical V24.01 client was not exercised. EveJS source was inspected, not run end-to-end for this scenario.

## Push and checks

Implementation commit `304f4970992a` is on `main`. Push-triggered CI run [41471425498](https://github.com/Fokiiiiiii/eve.rs/actions/runs/41471425498) completed successfully with no failed jobs. The dispatched focused Actions run [41471470725](https://github.com/Fokiiiiiii/eve.rs/actions/runs/41471470725) also succeeded. The local focused suite passed 74 tests across SpaceContainerTests, WorkCycleHostTests, DroneHostTests, and LootAllTests, with no skips. For the weighted-pool update, both touched .NET projects built cleanly; SalvagePoolTests passed 33/33, and the four related database test classes passed 80/80 on eve_p4 with no skips. The fresh lane initially lacked its pinned SDE; build 3396210 was loaded before the passing database run. Weighted-pool parity is addressed; reward-generation timing remains open.
