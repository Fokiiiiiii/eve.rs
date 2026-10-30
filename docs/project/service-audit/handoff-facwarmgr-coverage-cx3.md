# facWarMgr coverage handoff (cx3, 2026-10-30)

## Result

All 14 requested P1 rows now have passing registered Host wire coverage. Rows 01454, 01456–01458, 01462–01465, and 01467–01471 use `FacWarMgrReadPersistenceHostWireTests.Faction_warfare_reads_round_trip_from_persisted_state_through_host_registration`; row 01453 uses `FacWarMgrDonationHostWireTests.Solar_system_lp_donation_debits_the_wallet_and_persists_the_pool_through_host`. The read test seeds and commits a unique PostgreSQL schema, then reads through a newly constructed FwStore/FwService and the real Host socket/dispatcher. The donation test verifies stale-tax refusal leaves both stores unchanged, then checks the accepted call debits loyalty LP and persists the pool.

## Whole-client call and consumer trace

Whole-tree Serena searches of `/home/user/eve-logic` confirmed the RPC wrappers in `eve/client/script/ui/services/facWarSvc.py` and their consumers:

- RPC-01453 `DonateLPsToSolarSystem`: `infrastructureHub.py:165-183` sends the donation and retries once when `taxNow` changes; its prechecks and LP-pool cap are in `facWarSvc.py:455-469`.
- RPC-01454 `GetAllianceWarFactionID`: wrapper only at `facWarSvc.py:99-100`; no other client caller was found.
- RPC-01456 `GetCorpFactionalWarStatus`: dashboard reads `status/factionID/startDate` at `fwWarzoneDashboard.py:190,199,350`; enlist/retire flows consume it at `enlistCont.py:96`, `enlistmentChecks.py:27,44`, and `enlistmentUtil.py:110,119,142,161`.
- RPC-01457 `GetCorporationWarFactionID`: consumed by corporation UI, agent filtering, info and stargate deployment at `corp_ui_wars.py:291`, `corp_ui_member_tracking.py:111`, `starModeHandler.py:818`, `systemMapSvc.py:974`, `agents.py:214`, `infosvc.py:2916,2931`, `mapViewColorHandler.py:837`, `mapFilterMyAgents.py:40`, `stargateDeploymentCont.py:79`, and `agentUtil.py:32,60`. Whole-tree search also found `corporationInfoWindow.py:102` and `base_corporation.py:1093`.
- RPC-01458 `GetFactionCorporations`: wrapper only at `facWarSvc.py:96-97`; no other client caller was found.
- RPC-01462 `GetSolarSystemLPs`: LP balance consumers are `infrastructureHub.py:46`, `factionalWarfareSystemContentPiece.py:73`, and `eve/common/script/util/facwarCommon.py:166-168`.
- RPC-01463 `GetStats_Alliance`: dashboard selects it at `statisticsController.py:107`; `facWarSvc.py:367-380` combines its six metric values with the alliance top/all group.
- RPC-01464 `GetStats_Character`: theaters page reads weekly kills and victory points at `personal.py:142-143,157-158`; `facWarSvc.py:349-350` supplies the pilot window and `:334-350` combines personal/top/all values.
- RPC-01465 `GetStats_Corp`: dashboard selects it at `statisticsController.py:105`; `facWarSvc.py:352-365` combines its six metrics with corporation top/all values.
- RPC-01467 `GetStats_FactionInfo`: dashboard reads member and system counts at `statisticsController.py:54-65`, and derives corporation pilot counts at `:69-90`.
- RPC-01468 `GetStats_Militia`: dashboard selects it at `statisticsController.py:99-100`; `GetStatisticsScrollData` consumes its header and six metric maps at `:37-53`.
- RPC-01469/01470/01471 `GetStats_TopAndAllKillsAndVPs`: wrappers consume character, corporation and alliance groups at `facWarSvc.py:339-342,357-360,372-375`; the dashboard and theaters consumers above display those values.

## Verification and handoff

Production registration is `dotnet/src/Eve.Host/Rpc/CoreRpc.cs:126-146`; `FacWarMgrService` reads through `FwService` and the persisted FwStore. The 2026-10-30 focused Host run passed 3 tests with 0 skipped, including the pre-existing rank/enlistment wire test; `Eve.FactionWarfare.Tests` passed `FacWarMgrRpcTests|FwDatabaseTests` (18 passed, 0 skipped) on eve_p3. No rows remain untested in this 14-row batch. The whole-client call/consumer evidence above remains the reference for UI use; no graphical-client session was run.
