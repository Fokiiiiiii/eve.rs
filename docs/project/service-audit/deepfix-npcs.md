# NPC bounty and wreck loot deep fix

## Scope and finding

This pass follows the common solo ratting loop: destroy an NPC, receive the bounty notice, then open and loot its wreck. The defect fixed was the bounty notice reporting an ESS-reduced pilot payout with `isModified=false`.

The Host already computed the ESS split and persisted/notified the pilot amount. It failed to set the wire flag. `SpaceSessions.Npc.cs` now sets `IsModified` when `pilotShare != amount`; full, unmodified fallback payouts retain false. This is the root correction, so the existing notification path and payout state remain authoritative.

## Evidence and decisions

- Client notification consumer: `/home/user/eve-logic/notifications/client/bountyNotificationAdapter.py:8-22` subscribes to `OnBountyAddedToPayout` and reads `amount`, `payoutTime`, `enemyTypeID`, and `isModified` directly. `/home/user/eve-logic/eve/client/script/ui/shared/uilog.py:264-274` selects the modified-bounty message from that flag and logs only positive amounts when bounty logging is enabled. Therefore the reduced amount and flag must agree.
- Host destruction path: `dotnet/src/Eve.Services.Space/SpaceSessions.cs:950-957` dispatches NPC deaths to `FinishNpcDeathAsync`. `dotnet/src/Eve.Services.Space/SpaceSessions.Npc.cs:288-318` determines the killer, creates the wreck, and calls bounty handling before publishing the NPC death events. `SpaceSessions.Npc.cs:327-396` computes the bounty/ESS share, records it, and sends the payout notice. `dotnet/src/Eve.Npc/PgBountyStore.cs:78-121` handles deduplication and durable recording.
- Reference behavior: EveJS 261028.1 `/home/user/evejs-261028.1/src/server/src/services/bounty/bountyRuntime.js:448-515` marks a payout modified when its final amount differs from base or bank shares apply; lines 517-538 build the notification with integer amount, FILETIME payout time, and boolean flag. This matches the Host correction.
- Mechanics/data: EVE University [Ratting](https://wiki.eveuniversity.org/NullSec_Ratting) documents fixed type bounties and periodic payouts; [ESS](https://wiki.eveuniversity.org/ESS) documents the Main Bank share. The pinned `sde/lock.json` identifies build 3396210; the local pinned SDE has `typeDogma` type 23245 attribute 481 (`entityKillBounty`) at 15000 ISK. The regression uses the Host's configured 40% ESS share, yielding 8250 ISK to pilot and 6000/750 ISK to Main/Reserve banks.
- Wreck path remains the existing state owner: `SpaceSessions.Npc.cs:402+` handles NPC wreck creation; `dotnet/src/Eve.Services.Space/NpcWrecks.cs:38-55` generates and persists wreck contents. Host inventory checks in `dotnet/src/Eve.Services.Inventory/InvBrokerService.cs:338-343,552-562,786-800` enforce access and return contents. The V24 client usage evidence for wreck inventory, Loot All, cargo checks, and the `OnWreckLootAll` result is recorded in `docs/project/structures-phase/handoff-agent-wreck-loot-cx3.md`; the compatibility decision on visible contents versus loot rights is in `docs/reference/compat/DEVIATIONS.md`.

## Verification

- Regression reproduced before the fix: focused PgFact failed because expected `isModified=true`, actual false.
- After the fix: `NpcBountyHostTests|NpcLootHostTests|SpaceEventsTests` passed locally on `eve_p4`: 28 passed, 0 failed, 0 skipped.
- `dotnet build dotnet/tests/Eve.Services.Space.Tests -p:UseSharedCompilation=false` passed.
- This is headless Host/database verification; the graphical V24 client was not exercised. Fleet bounty sharing and DBS payout modifiers were not part of this solo ESS regression scenario.
