# EveJS 261028.1 migration audit

Date: 2026-10-29

## Scope and result

Checked the highest-priority 40 EveJS source citations in the earlier audit trail, covering login/session close, docking, stargate travel, warp, market, wallet, skills, fitting and combat. The cited functions and current files listed below exist in `/home/user/evejs-261028.1/src`; their current responsibilities match the cited feature areas. Targeted source reads also checked session closure, docking admission/completion, stargate gating, warp request/order resolution, the market/wallet/skill/fitting RPC handlers, weapon cycles, bomb launch/detonation and defender launch.

The old EveJS source snapshots were discarded and now resolve to 261028.1. There is no independent 261018.1 or v0.12.9 tree to diff, so this audit cannot establish a complete release-to-release semantic delta. No new release-specific behavior change affecting V24.01 was established from the available evidence; consequently no server change or new DEVIATIONS entry is justified by this audit.

## Moved or changed citations

- The earlier `server/src/network/tickAlignedSends.js` citation no longer names a current file. Tick dispatch is now part of `server/src/space/runtime/destinyTick.js:115-117` (`doPostTick` calls `sendDestinyTickOutput`); `server/src/space/runtime/scene/tick.js:2474-2476` routes that to `sendDestinyBatches`. The current scene comment specifies one batch per Grid bubble with history and one per Observer with character history, stamped with the tick number. This is a path/ownership change; the client-visible batching behavior cited earlier remains.
- The paths `server/src/services/bounty/bountyRuntime.js` and `server/src/services/station/repairRuntime.js` still exist. The prior audit’s other named runtime files checked in this pass also remain in the current tree: `corporation/corporationTax.js`, `inventory/invBroker/fittingOperations.js`, `dogma/activationHeatState.js`, `inventory/shipRackHeatState.js`, `ship/shipSkinSequencingRuntime.js`, `security/customsInspectionRuntime.js`, `sovereignty/skyhookRaidRuntime.js`, `space/runtime/destinyMovementEngine.js`, and `space/runtime/savedWarpLanding.js`.
- Current space code is organized under `server/src/space/runtime`; there is no current `server/src/services/space` directory. Do not update old citations by mechanically renaming files; resolve each symbol to its current owner.

## Forty core source checks

Numbers are current EveJS source line numbers. Each entry confirms that the cited method/file exists in 261028.1; these are source-location checks, not a claim that every path was executed in a live client.

1. Login session close: `network/tcp/accountLoginClose.js:31` `closeSessionTransport`; `:63` `closeOtherSessionsOfUser`.
2. Docking: `space/runtime/spaceRuntime/docking.js:113` `acceptDocking`; `:131` `startDockingApproach`; `:154` `completeDockingApproach`; `:172` `completeArrivedDockingApproaches`.
3. Stargate: `space/runtime/spaceRuntime/stargateJump.js:58` `startStargateJump`.
4. Warp request and orders: `space/destiny/commands/warpCommands.js:423` `buildWarpRequest`; `:460` `applyWarpOrder`; `:700` `issueWarpOrder`; `:735` `issueEntityWarpIn`.
5. Warp runtime/save: `space/runtime/destinyMovementEngine.js:424` `resolveWarpSphereOutcomeForRuntime`; `space/runtime/savedWarpLanding.js:55` `normalizeSavedWarpLanding`; `:77` `describeWarpForSave`.
6. Market: `services/market/marketProxyService.js:6417` `Handle_GetOrders`; `:6921` `Handle_CancelCharOrder`; `:7013` `Handle_BuyMultipleItems`; `:7160` `Handle_ModifyCharOrder`.
7. Wallet: `services/account/accountService.js:722` `Handle_GetCashBalance`; `:841` `Handle_GetJournal`; `:881` `Handle_GetTransactions`; `:913` `Handle_GiveCash`; `:981` `Handle_GiveCashFromCorpAccount`.
8. Skills: `services/skills/skillMgrService.js:347` `Handle_GetMySkillQueue`; `:455` `Handle_CharStartTrainingSkill`; `:594` `Handle_SaveNewQueue`; `:612` `Handle_InjectSkillpoints`; `:643` `Handle_ExtractSkills`; `:673` `Handle_CheckAndSendNotifications`.
9. Fitting: `services/fitting/charFittingMgrService.js:34` `Handle_GetFittings`; `:41` `Handle_SaveFitting`; `:55` `Handle_UpdateFitting`; `:89` `Handle_DeleteFitting`.
10. Combat: `space/runtime/weaponCycle.js:135` `executeTurretCycle`; `:656` `executeMissileCycle`; `space/modules/bombRuntime.js:125` `executeBombLaunchCycle`; `:206` `detonateBombEntity`; `space/modules/defenderMissileRuntime.js:32` `resolveDefenderActivation`; `:150` `executeDefenderLaunch`.
11. Structure jump: `services/structure/structureJumpBridgeMgrService.js:1713` `Handle_CmdJumpThroughStructureStargate`.

This is 40 function citations (the numbered categories contain multiple functions). The current Ansiblex handler has a real settlement path: validates session, bridge state/link/access, ship/range/disruption, LY range, mass, fuel and fee; then stages durable settlement and returns a result. Its server counterpart remains an existing, already-recorded gap, not a newly introduced 261028 delta.

## Current EveJS services called by V24.01 and eve.rs coverage

| Current EveJS entry | V24.01 client evidence | eve.rs coverage and finding |
| --- | --- | --- |
| `services/npe/airNpeService.js` (`air_npe`) | `eve/client/script/storylines/client/airnpe.py:60,73,81,106` calls enabled/state/skip operations. | `EVE_RS_COVERAGE.csv` RPC-00855–00858 marks these IMPLEMENTED-TESTED through `LoginCompatibilityRpcHostWireTests`; no migration gap found. |
| Homefront site list (runtime under `services/homefront`) | `eve/client/script/evedungeons/client/anomalyTracker.py:304` calls `dungeonInstanceCacheMgr.GetHomefrontSiteInstances`; call matrix rows 1354–1355. | Coverage rows 702–703 mark the dungeon anomaly reads IMPLEMENTED-TESTED by `ClientVerifiedBatch28CdHostWireTests`. |
| Season manager | `eve/client/script/seasons/client/seasonservice.py` calls challenge, claim, progress, loot, active-season and end-time reads; `eve/client/script/achievements/client/eventHandler.py:119` sends client events. | Coverage rows 1588–1608 mark the recorded season methods IMPLEMENTED-TESTED. This audit does not infer unlisted payload completeness from the service name. |
| Ship cosmetics | `eve/client/script/shipcosmetics/client/fittingsgateway/quasar.py:35` calls `GetEnabledCosmetics`. | Coverage rows 1654–1655 mark it IMPLEMENTED-TESTED; the recorded Host wire test covers empty/None reply shapes and providerless mutation refusal. |
| Ansiblex structure jump | `eve/client/script/parklife/autopilot.py:349-360` passes the RPC to session change and consumes the result; `eve/client/script/ui/services/menusvc.py:2106-2109` consumes its bool result. | Coverage rows 2027–2028 are HOLLOW. The refusal and current Host wiring are already recorded in `docs/reference/compat/DEVIATIONS.md:5260-5264`. EveJS 261028.1 has the full operation described above, but the old snapshot is unavailable, so this is not evidence of a newly added method. |

Additional end-to-end client evidence for the checked core areas includes market UI calls in `eve/client/script/ui/shared/market/quote.py:95-103` and `marketbase.py:2766-2774`; wallet transfer replies in `eve/client/script/ui/shared/money/giveIskPanel.py:103-104` and `transferMoneyWnd.py:148-150`; skill queue operations in `eve/client/script/ui/services/skillQueueSvc.py:247-283` and injector use in `skillsvc.py:977`; fitting save in `eve/client/script/environment/fittingSvc.py:1150-1151`; module activation in `eve/client/script/ui/inflight/shipModuleButton/shipmodulebutton.py:960-963`. Stargate autopilot also routes `CmdStargateJump` through `PerformSessionChange` at `autopilot.py:358-360`.

## Follow-up decision

No behavior change from 261028.1 was established that requires changing eve.rs. No wire assertion was relaxed or altered. The Ansiblex Host gap remains tracked in DEVIATIONS and coverage; implementing it requires the persisted settlement, authoritative mutation, response and follow-up transition to be wired end to end, and is outside a source-migration-only change without a proven release delta.
