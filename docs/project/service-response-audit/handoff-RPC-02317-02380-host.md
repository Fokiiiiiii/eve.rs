# Host RPC handoff: RPC-02317–02380

## Current increment

`dotnet/tests/Eve.Host.Tests/PublicQaAndPvpHostWireTests.cs` adds three real TCP `HostServer` wire regressions. The shared test host registers the real `CoreServices` public QA service; the PvP test registers the production `PvpFilamentMgrService` in the real `ServiceRegistry`. Calls include transport `machoVersion`.

Verified coverage:

- RPC-02324/02325 `CanGiveItemForMultifit`, RPC-02326/02327 `MoveMeTo`, and RPC-02328/02329 `SlashCmd`: an ordinary account gets the observed `False` / `None` replies.
- RPC-02330 `AbyssalPVPEndGateActivation`: the Host returns `DeniedTargetAttemptFailed` when no active Proving Grounds origin trace exists.
- RPC-02379/02380 `GetDelayedRewardsByGroupIDs`: the Host preserves the client-consumed mapping from group ID to reward rows with `rewardQuantity`, and forwards requested IDs to the rewards provider.

The QA test covers the non-staff route. Staff callback execution for `MoveMeTo` and `SlashCmd` is not covered by this increment. EveJS records QA tool events; eve.rs has optional callback hooks, while its normal Host wiring does not supply an audit recorder for these events.

## Client flow and reference comparison

| RPC family | V24.01 callers and consumers | EveJS v0.12.9 behavior | eve.rs status after this increment |
|---|---|---|---|
| POS moon processing and tower settings | `eve/client/script/ui/inflight/moonmining.py:1343-1351` calls `RunMoonProcessCycleforTower`; `:738-743` saves permissions; `:1128-1138` saves sentry settings; `:1195-1220` saves notifications/password. `eve/client/script/parklife/playerOwned.py:173-180` also calls `SetTowerPassword` with only tower ID and password. Replies are ignored; persisted tower state and scene/runtime state drive what follows. | `server/src/services/planet/posMgrService.js:407-473,606-610` validates and delegates to starbase settings/production runtimes; password changes reconcile the force field. | Open. eve.rs `PosMgrService` currently exposes tower reads and an unanchor callback, without the five mutations/process cycle. |
| Orbital unanchor | `eve/client/script/ui/services/menusvc.py:3673-3676` calls `UnanchorOrbital(itemID)` and ignores the reply; the next visible effect is the orbital scene/state update. | `server/src/services/planet/posMgrService.js:631-643` mutates orbital state and refreshes the scene on success. | Open. |
| Repackage | `eve/client/script/ui/services/menuSvcExtras/invItemFunctions.py:544-585` groups items by station/location, retries selected upgrade cases with `destroyable=True`, and plays the sound; the client consumes errors and later inventory changes, not a success payload. Other discovered callsites: `eve/client/script/ui/shared/market/sellMulti.py:155-187` and `eve/client/script/ui/services/menusvc.py:3412-3414`. | `server/src/services/station/repackagingService.js:16-24` delegates to `repackagingSupport.js:492-737` for inventory mutation and notifications. | Open. eve.rs has `InvBrokerService.RepackageItems` and the inventory state owner; this increment adds no Host regression for grouped requests or item updates. |
| Repair | `eve/client/script/ui/station/repairshop/base_repairshop.py:62-72` binds the repair moniker for a station or structure; quote rows are consumed in `:218-290`. It calls `RepairItemsInStation(itemIDs, payment)` for station repair or `RepairItemsInStructure(itemIDs)` for structure repair. Replies are ignored; the UI updates from damage/wallet state. | `server/src/services/station/repairService.js:138-238` has quote, station, and structure handlers. | Open. eve.rs has NPC station repair coverage; `docs/reference/compat/DEVIATIONS.md` records structure repair as refused. No Host regression or structure implementation was added here. |
| Public QA tools | `eve/client/script/util/publicQaToolsClient.py:36-62` calls all three methods. `eve/client/script/ui/shared/moveMeWindow.py:37-41` consumes no `MoveMeTo` reply; `eve/client/script/ui/shared/fitting/multiFitWnd.py:323,329` consumes the multifit boolean; `eve/client/script/ui/services/menuSvcExtras/devFunctions.py:13-19` invokes slash commands. | `server/src/services/support/publicQaToolsServerService.js:48-80` records events, denies non-staff slash execution, delegates staff slash execution, and returns `false` for multifit. | Host regression added for non-staff reply shapes only. Staff operational callbacks/audit recording remain open. |
| Abyssal PvP end gate | `eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:583-585` invokes inside `PerformSessionChange`; no success payload is consumed. | `server/src/services/activity/pvpFilamentMgrService.js:171-177` rejects when no active origin trace is available. | Host regression added for the existing rejection path. |
| Delayed incursion rewards | `eve/client/script/ui/shared/incursionSvc.py:257-260` calls the method. `eve/client/script/ui/shared/neocom/journal.py:783-818` reads `delayedRewards[groupID][0].rewardQuantity` and displays zero when there is no entry. | `server/src/services/rewards/rewardMgrService.js:41-50` returns an empty dict when no delayed rewards exist. | Host regression added for a populated group and an omitted group. Production Host wiring reads from `IncursionDirector.PendingLoyaltyPointsAsync` (`dotnet/src/Eve.Host/Rpc/RewardRpc.cs:59-64`); the client shape is preserved. |

## Remaining rows

The following rows still need their assigned end-to-end implementation or Host regression; this increment does not mark them complete:

- RPC-02317–02323: `posMgr` moon-processing, tower settings, and orbital unanchor.
- RPC-02370–02375: all `RepackageItems` grouped/burst input variants and mutation notifications.
- RPC-02376–02378: repair quote/station Host regressions and structure repair implementation.

## Checks

- PASS: focused filter `FullyQualifiedName~Public_qa_calls_keep_the_client_wire_shapes_through_host_dispatch|FullyQualifiedName~Abyssal_pvp_end_gate_rejects_without_an_active_origin_trace_through_host|FullyQualifiedName~Delayed_incursion_rewards_return_the_rows_consumed_by_the_journal_through_host_dispatch` — 3 passed, 0 failed, 0 skipped (`Eve.Host.Tests`).
- The initial compile found a missing `using Eve.Services;`; it was added before the passing run.
- No PostgreSQL database was used. No graphical-client run was performed.
