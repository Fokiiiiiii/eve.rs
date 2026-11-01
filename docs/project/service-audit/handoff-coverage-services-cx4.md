# Service coverage handoff — cx4

## Completed group: calendarProxy

The Host wire test `dotnet/tests/Eve.Host.Tests/CalendarRpcHostWireTests.cs:Calendar_rpc_methods_are_reachable_over_host_and_share_state_and_notifications` passed on PostgreSQL database `eve_p4` (1 passed, 0 failed, 0 skipped). It starts the Host listener with strict RPC routing, creates an event through `calendarMgr`, reads it through registered `calendarProxy.GetEventList` and `GetEventDetails`, mutates the event, then reads the persisted updates and checks notifications. The corresponding CSV rows RPC-00979 and RPC-00980 now point to this test.

### Whole-client evidence

- `eve/client/script/ui/services/eveCalendarsvc.py:235-248` binds the month/year call to `calendarProxy.GetEventList` and consumes the tuple slots and rows. `CalendarSvc.GetEventsNextXMonths` fans into `FetchNextEvents` and `FetchNextEventsDict` (same file: graph trace shows callers); UI callers include `eve/client/script/ui/shared/eveCalendar.py:1106,1447,1579,1584,1588,1597,1601,1664`.
- `eve/client/script/ui/services/eveCalendarsvc.py:259-262` calls `calendarProxy.GetEventDetails`; `eve/client/script/ui/shared/eveCalendar.py:1106-1111` reads `eventText` and `creatorID`. The notification formatter also consumes the facade at `notifications/common/formatters/calendar.py:25`.
- Current EveJS reference `server/src/services/calendar/calendarProxyService.js:15-20,22-26` delegates list and detail to runtime and payload builders. The Host test compares the returned rows/details with subsequent mutations and persisted database-backed state.

## Completed group: account

`dotnet/tests/Eve.Host.Tests/AccountWalletHostRpcTests.cs:Account_wallet_rpcs_route_real_wire_calls_persist_balances_and_notify` now checks the personal and corporate month-filtered transaction calls and the `GetWalletDivisionsInfo` row shape for both a corporation and a personal wallet. It uses the registered account service, encoded/decoded Macho RPC packets, real PostgreSQL wallet/corporation stores, and checks persisted balances and account notifications. The focused test passed on `eve_p4` (1 passed, 0 failed, 0 skipped). CSV rows RPC-00814 through RPC-00816 now point to it.

### Whole-client evidence

- `eve/client/script/ui/services/accountsvc.py:116,124-135` sends personal and corporation transaction calls and retrieves wallet division rows. `wallet/panels/transactionsPanel.py:121-140,188-191` primes owners and displays transaction fields; base, corporation, and personal panels issue the remote calls (`baseTransactionsPanel.py:141-160`, `corp/corpTransactionsPanel.py:12-15`, `personal/personalTransactionsPanel.py:10-12`). `corpDivisionsPanel.py:33-51` consumes division `key` and `balance`.
- Current EveJS `server/src/services/account/accountService.js:786-823,882-912` filters corporation wallet divisions using persisted roles and reads year/month/accountKey/ownership from the RPC arguments before returning transaction rows. The test checks the account key, month, owner class, division keys, balances, and persisted journal state.

## Completed group: corpmgr

`dotnet/tests/Eve.Host.Tests/CorporationPublicInfoHostWireTests.cs:Corporation_identity_and_row_reads_use_persisted_records_through_registered_host` passed on `eve_p4` (1 passed, 0 failed, 0 skipped). The test dispatches real encoded RPC packets through the registered `corpmgr` service and verifies a persisted member's corporation, an NPC corporation resolved from the pinned `npcCharacters` SDE, and `None` for an unknown character. The RPC is read-only, so it has no mutation notification. CSV rows RPC-01244 through RPC-01247 now point to this test.

### Whole-client evidence and behavior

- `eve/client/script/ui/shared/info/characterInfoWindow.py:107-113` and `eve/client/script/ui/shared/info/infoContentBase.py:468-474` call the RPC for NPC characters without agent records; the returned corporation ID feeds the corporation banner and owner display.
- `eve/client/script/ui/shared/neocom/contracts/createContractWnd.py:1203-1207` compares the result with the caller's corporation to calculate the internal contract limit. `evestations/specialStationRules.py:97-100` calls the corporation service facade and passes the corporation ID to faction access rules.
- Current EveJS `server/src/services/corporation/corpmgrService.js:507-524` resolves player affiliations, then NPC corporation IDs, and returns null for an unknown other character. eve.rs previously lacked the NPC fallback. `CorpMgrService` now reads dynamic affiliations first, then the startup-loaded pinned SDE mapping, then preserves the unknown-character `None` response. This follows the client NPC fallback and current EveJS behavior.

## Completed group: voteManager

`dotnet/tests/Eve.Host.Tests/VoteManagerHostTests.cs:Vote_manager_rpc_flow_uses_the_host_wire_and_persists_cases_and_ballots` passed on `eve_p4` (1 passed, 0 failed, 0 skipped). It sends the real registered `voteManager` route over Host packets, persists a normal vote and ballot, closes and activates a sanctioned share vote, verifies the resulting share state, and observes `OnVoteCaseChanged`, `OnVoteCast`, and `OnSanctionedActionChanged` after their successful replies. The corporation sanctions regression `Corporation_votes_apply_official_sanctions_and_restore_roles_after_failed_votes` also passed on `eve_p4` (1 passed, 0 failed, 0 skipped). CSV rows RPC-02796, RPC-02808, RPC-02809 now point to the Host test.

### Whole-client evidence and behavior

- `eve/client/script/ui/services/corporation/voting.py:237,240-242,263-265` issues `InsertVoteCase`, `InsertVote`, and `ActivateSanctionedAction`. `corp_ui_votes.py:1010-1022` submits ballot/proposal actions, and `corp_ui_sanctionableactions.py:256-264` invokes sanctioned action application.
- The client consumes `OnVoteCaseChanged(corporationID, voteCaseID)` to clear vote caches (`voting.py:77-83`, `corp_ui_votes.py:810-815`, `base_corporation_ui.py:190-195`), `OnVoteCast` (`voting.py:85-89`, `corp_ui_votes.py:817-822`, `base_corporation_ui.py:197-202`), and `OnSanctionedActionChanged` (`voting.py:106-109`, `corp_ui_sanctionableactions.py:82-84`).
- Current EveJS handles these mutations in `server/src/services/corporation/voteManagerService.js:249-303,316-323` but does not emit these client events. eve.rs sends the corresponding two-argument broadcasts to online corporation sessions only after the committed mutation and reply. This follows the named cache invalidations present in the V24.01 client.

## Completed group: repairSvc

`dotnet/tests/Eve.Host.Tests/RepairStructureHostWireTests.cs:Registered_repair_service_quotes_charges_tax_and_persists_or_refuses_atomically` passed on `eve_p4` as part of the final two-test Host focus (2 passed, 0 failed, 0 skipped). It dispatches bound-object `repairSvc` packets through `ServiceRegistry` and `PacketDispatcher`, checks the real quote tuple and fields, station repair, structure tax, wallet journal and balances, `OnItemChange`, persisted state after rebuilding the service, and the wire ErrorResponse when funds are insufficient. It uses the official ESI adjusted-price cache so no repair price is fabricated. CSV rows RPC-02214 through RPC-02216 now point to this test.

### Whole-client evidence and behavior

- `eve/client/script/ui/station/repairshop/base_repairshop.py:62-72` routes `GetRepairQuotes`, station repair with `(itemIDs, payment)`, and structure repair. `DisplayRepairQuote` at `:218-290` consumes quote rows, including `itemID`, `typeID`, `damage`, `maxHealth`, and `costToRepairOneUnitOfDamage`; actions are submitted from `:292-343`.
- The client consumes item-change notifications in `eve/client/script/environment/invCache.py:229-309` and `eve/client/script/environment/godma.py:1553-1645`; `RepairService` emits `OnItemChange` after a committed repair (`dotnet/src/Eve.Services.Station/RepairService.cs:336`).
- Current EveJS `server/src/services/station/repairService.js:138-155,181-205` routes quote and station repair calls into the repair runtime; eve.rs keeps the same call shapes while persisting charges and item condition in one database transaction. The Host test exercises station and structure behavior and the failure path.

A graphical V24.01 client session was not run.

A graphical V24.01 client session was not run.
