# Dungeon layout audit fixes

Audit source: `/home/user/reports/90-audit-eve-dungeons-r14.md`. The two reported layout findings were checked against the full V24.01 client tree at `/home/user/eve-logic`, the working EveJS reference at `/home/user/evejs-261028.1`, and the Host-to-space implementation. Both findings are confirmed. No finding was skipped.

The audit's dungeon 283 distance says “about 31.5 km.” The saved EveJS room positions differ by **31,457,280 metres (about 31,457 km)**, with the next room's X offset negative. The magnitude in the audit is off by 1,000; the layout finding remains valid. Dungeon 2734 has the same magnitude with a positive X offset.

## P1 — gate placement and gate clearance used one inferred room

The client menu calls `RealActivateAccelerationGate` for an activated gate (`/home/user/eve-logic/eve/client/script/ui/services/menusvc.py:3033-3039`). That function calls `keeper.ActivateAccelerationGate` during a session change (`.../menuSvcExtras/movementFunctions.py:542-565`). The client then tracks the keeper's current room and applies room-specific environment templates (`.../parklife/dungeonTracking.py:68-71,104-113`, `.../parklife/environmentSvc.py:241-245`). Gate position and the resulting room transition are therefore client-visible state.

In EveJS dungeon 1215, the scene profile lists the entry and rooms 17492-17494 (`/home/user/evejs-261028.1/tools/DatabaseCreator/staticTables/dungeonAuthority/data.json:748103-748127`). Its two gate profiles target 17492 and 17493 and neither contains a `roomKey` (`:748129-748164`). EveJS resolves a missing gate source room to the site position (`server/src/services/dungeon/dungeonUniverseSiteService.js:5814-5817`), so both gates are placed at the entry site. Gate unlock progression is independent: after a room clears, EveJS unlocks the gate targeting the next ordered room (`.../dungeonUniverseSiteService.js:7471-7488`). Thus the 17493 gate remains locked until 17492 clears even though its physical anchor is the entry site.

The layout now stores a gate's physical `SourceRoomKey` separately from `ClearanceRoomKey`. A gate without an authored source anchors at the entry room; its clearance room is the previous ordered room for a non-entry destination. `SpaceAccelerationGates` checks that clearance key before sim transit. The regression test verifies that the 17493 gate is at the entry, stays locked after only the entry clears, and transits and records room 17493 after room 17492 clears.

The Host path remains real and stateful: `OperationsComposition` registers the Keeper callback (`dotnet/src/Eve.Host/Composition/OperationsComposition.cs:61-76`); `SpaceComposition` provides active pocket layouts, cleared rooms, sim transit, and room persistence (`dotnet/src/Eve.Host/Composition/SpaceComposition.cs:617-648`); the transit uses the simulation command (`dotnet/src/Eve.Services.Space/SpaceSessions.Gates.cs:11-16`); and the accepted transition writes `currentRoomID` and `currentRoomKey` to the dungeon runtime store (`OperationsComposition.cs:105-119`). The existing Keeper wire test confirms the registered RPC still forwards the gate ID and returns the client's `None` result.

## P2 — scene room order and recorded coordinates were discarded

EveJS dungeon 2734 records room 20206 as active and room 20191 as pending, with the gate in 20206 and a +13,000 m X offset (`/home/user/evejs-261028.1/tools/DatabaseCreator/staticTables/dungeonAuthority/data.json:950981-951039`). Its room offset from the active entry is +31,457,280 m X. Dungeon 283 records 13849 as active and 13850 as pending, and its gate is in 13849 with a -750 m Y offset (`.../data.json:555271-555317`); room 13850 is -31,457,280 m X from the active room. EveJS computes room world positions from the profile offset relative to the active entry room (`server/src/services/dungeon/dungeonUniverseSiteService.js:5674-5705`).

The catalog extractor now writes a separate `layout-profiles.json` containing authored initial room state, room offsets relative to the active entry, gate source room, and gate position offset. The loader validates and applies those profiles. `DungeonRoomLayout.Build` orders the entry/active room first and uses the recorded offsets; it retains the existing deterministic placement only when a profile has no position. Gate position offsets are applied relative to the authored source room. All 81 extracted layout profiles were compared with the EveJS authority/overlay/mining-pack projection; the count and projected values matched. For these profiles, the authored gate source also matched EveJS's preceding-room unlock relation; dungeon 1215 is the missing-source case covered above.

## Test-first evidence and verification

The regression tests were committed before the implementation (`12c462add`) and run in a detached worktree against that pre-fix code. They failed on the wrong inferred source for dungeon 1215, the wrong active-room order for dungeon 2734, and the missing recorded room offset. While separating physical gate placement from clearance, an additional test failed against the intermediate implementation because the 17493 gate unlocked after only the entry cleared; it passes with the final separation.

Verified locally:

- Release builds with zero warnings and zero errors: `Eve.Dungeons`, `Eve.Dungeons.Tests`, `Eve.Services.Space`, and `Eve.Services.Space.Tests`.
- Referenced Dungeons test classes: 61 passed, 0 failed, 0 skipped.
- Referenced Core `MissionSdeDataTests`: 5 passed, 0 failed, 0 skipped.
- Referenced Space test classes, including database-backed tests on `eve_p6`: 54 passed, 0 failed, 0 skipped.
- Registered Host Keeper gate wire test: 1 passed, 0 failed, 0 skipped. The worktree already contained the unrelated untracked `InventoryAuditHostWireTests.cs`, which does not compile (`CS1503`); the Host test was built with only that untracked file excluded. It was not edited or staged.
- `dotnet/src/Eve.Dungeons/Waves/WaveRules.cs:165-168` still rejects `on_room_active`; these layout tests do not establish that dungeons 283 or 2734 can start or complete in the current live runtime.

Not checked: graphical-client gameplay, live/headless server transitions, the complete catalog extraction against the pinned SDE (the required 3405148 dataset was not available in this worktree), and post-push GitHub Actions. The source/client checks and tests above do not claim graphical-client verification.
