# Service audit implementation handoff (cx3)

## Completed increment

Implemented `achievementTrackerMgr.ResetAllForChar`. The V24.01 caller sends no arguments and ignores the return value (`achievements/client/achievementSvc.py:77-80`). The Host replaces the authenticated character's persisted completion and event-count maps with empty maps, then sends `achievementTrackerMgr.OnAchievementsReset`; the client handler clears its cache and reloads the saved maps (`achievements/client/achievementSvc.py:91-96, 120-138`). The reset leaves the separate `hasEverWarped` value intact.

The client writes both achievement maps as complete snapshots (`achievements/client/achievementSvc.py:224-230`); the existing `AchievementTrackerStore.WriteAsync` transaction already replaces both tables atomically. EveJS 261028.1 has no `ResetAllForChar` handler; this method is grounded in the client call and its reset notification consumer.

## Remaining open target rows

- `XmppChatMgr`: `EnsureResourceWarsChannelExists`, `GMMute`, `GMUnmute`. Client callers are `resourcewars/client/rwservice.py:91-103` and `chat/client/chat_service.py:239-247`. The existing channel hub has no Resource Wars membership or timed moderation owner.
- `abyssal_content_manager`: `GenerateAbyssEncounterContent`, `GenerateContentFromExistingPlan`, `GetContentDetailsForShipLocation`. All callers are QA tools in `eve/devtools/script/abyssal_content_tool.py:78-201`; reply consumers read the content id, plan, and location-to-item mapping at lines 78-83 and 110-151. No persisted content-plan provider exists.
- `achievementTrackerMgr`: `GetDebugStatsFromCharacter` remains open. The client method at `achievements/client/achievementSvc.py:62-64` caches the opaque result, but this method has no other client caller in the V24.01 source and EveJS has no handler.
- `autoMoonMining`: `RequestMiningDetails`, `RequestMiningCycleOutput`. Client wrappers are `moonmining/autoMoonMining/client/auto_moon_miner_service.py:14-20`; the window consumes cycle output at `auto_moon_miner_window.py:132-134` and detail fields at lines 152-166. The server lacks authoritative Metenox runtime state. The EVE University [Metenox Moon Drill page](https://wiki.eveuniversity.org/Metenox_Moon_Drill) describes automated extraction, fuel, and output behavior.
- `externalQueueMgr`: `PublishProxyEvent` remains open. The client call is `carbon/common/script/net/machoNet.py:887` with seven proxy metadata values.
- `i2`: `GetItem`, `IsPrimed` remain open. Their direct V24.01 calls are `eve/devtools/script/svc_dgmattr.py:249-250`; the active Host has no bound-object route.

## Checks run

- Builds succeeded with zero warnings for `Eve.Services.Core`, `Eve.Host.Tests`, `Eve.Characters.Tests`, and `Eve.Services.Core.Tests`.
- `Achievement_reset_clears_persisted_maps_and_notifies_the_client_after_the_write`: 1 passed.
- `AchievementTrackerStoreTests` on `eve_p3`: 2 passed.
- Achievement-related `Eve.Services.Core.Tests` classes on `eve_p3`: 30 passed.
- `HostServerTests` on `eve_p3`: 394 passed, 0 skipped.
- No graphical client session was used.
