# Personal calendar CSPA charges handoff (cx11)

## Contract and implementation

V24.01 wraps personal event creation with invitees and participant additions in `CSPAChargedAction` (`eve/client/script/ui/services/eveCalendarsvc.py:171-203`). The wrapper reads `ContactCostNotApproved` from `e.args[1]`, returns on decline, and retries once with `approvedCost` (`eve/client/script/util/eveMisc.py:88-101`). Event edits are a separate earlier RPC in the client edit flow (`eve/client/script/ui/shared/eveCalendar.py:1575-1592`), so declining the invitation preserves the edit while refusing the participant mutation. The wallet journal uses `refCSPA = 35` (`eve/common/lib/appConst.py:675`).

Calendar mutation callbacks now run inside the existing PostgreSQL store transaction. The Host reads recipient CSPA settings through the existing character-setting owner, applies contact, block, and corporation exemptions, charges each newly added non-exempt recipient through `WalletTransactions`, and raises the client-shaped `ContactCostNotApproved` error before commit when approval is absent or insufficient. Calendar rows and charges roll back together; wallet balance notification follows commit. Duplicate active participant additions produce no new charge.

EVE University documents the zero default, 1,000,000 ISK maximum, and non-blocked contact / same-corporation exemptions ([EVE Mail](https://wiki.eveuniversity.org/EVE_Mail)). EveJS 261028.1 routes creation to `runtime.createEventRecord` and participant additions to `runtime.updateEventParticipants` without an approval step (`server/src/services/calendar/calendarMgrService.js:24-42,129-150`); the V24.01 client and documented mechanic govern this behavior. The compatibility record is in `docs/reference/compat/DEVIATIONS.md`.

## Client call graph checked

- The V24.01 caller gates personal event creation with invitees and participant additions through `CSPAChargedAction` (`eve/client/script/ui/services/eveCalendarsvc.py:171-201`). It reads `ContactCostNotApproved` from `e.args[1]`, asks using `totalCost` and `totalCostISK`, returns without retry on decline, and retries with `approvedCost=totalCost` (`eve/client/script/util/eveMisc.py:88-101`). Create-side calendar UI work happens only after a non-None RPC result (`eveCalendarsvc.py:185-187`); participant cache invalidation follows the RPC (`:201`).
- Whole-client caller tracing found the other direct callers of the shared wrapper: address book (`eve/client/script/ui/shared/neocom/addressBook/addressbookService.py:788`), fleet (`eve/client/script/parklife/fleetSvc.py:361`), and mail (`eve/client/script/ui/services/mail/mailSvc.py:964`). Those RPCs are unchanged.
- CSPA cost settings are stored through `AccountMgr.SetContactCost` and loaded by `GetDefaultContactCost`; the client maps a missing value to -1 and `BlockAll` persists null (`eve/client/script/ui/services/accountsvc.py:144-160`).
- Calendar event consumers are `OnNewCalendarEvent`, `OnEditCalendarEvent`, `OnRemoveCalendarEvent`, and `OnEventResponseByExternal` in `eve/client/script/ui/services/eveCalendarsvc.py:332-409`; the event window consumes removal at `eve/client/script/ui/shared/eveCalendar.py:1672-1676`. The change preserves existing success notifications and prevents mutation notifications on declined/invalid approval.
- The wallet balance notification `OnAccountChange` has five direct client consumers: account (`accountsvc.py:64-67`), industry (`industry/industrySvc.py:281-284`), security office (`ui/station/securityOfficeWindow.py:150-153`), loyalty point store service (`ui/station/loyaltyPointStore/lpStoreSvc.py:390-393`), and wallet service (`ui/shared/neocom/wallet/walletSvc.py:81-90`). WalletSvc emits `OnAccountChange_Local` (`:88`), consumed by the loyalty store panel (`ui/station/loyaltyPointStore/lpStorePanel.py:400-412`) and wallet balance (`ui/shared/market/wallet_balance.py:135-140`). No session attribute is changed by this feature.

## Files

- `dotnet/src/Eve.Calendar/CalendarStore.cs`: calendar mutation callback participates in the store transaction and receives the persisted invitee set.
- `dotnet/src/Eve.Services.Core/CalendarMgrService.cs`, `CoreServices.cs`, and `Eve.Services.Core.csproj`: approval validation, CSPA journal entries, post-commit wallet notification, and service wiring.
- `dotnet/src/Eve.Host/Rpc/EconomyRpc.cs`, `Composition/CharacterServicesComposition.cs`, and `Hosting/HostComposition.cs`: authoritative saved-cost, recipient eligibility, and wallet providers.
- `dotnet/tests/Eve.Host.Tests/CalendarCspaHostTests.cs`, `CalendarParticipantConcurrencyTests.cs`, `CalendarRpcHostWireTests.cs`, and `HostServerTests.cs`; `dotnet/tests/Eve.Services.Core.Tests/CalendarMgrServiceTests.cs`: PostgreSQL Host, concurrency, wire, registration, and service coverage.
- `docs/reference/compat/DEVIATIONS.md` and `docs/project/TODO.md`: compatibility decision and project status.

## Local checks

- Builds passed with zero warnings or errors for `Eve.Calendar`, `Eve.Services.Core`, `Eve.Host`, `Eve.Host.Tests`, and `Eve.Services.Core.Tests`.
- After `git fetch origin main` and `git merge origin/main` (already up to date), PostgreSQL Host checks through `tools/dev/db-lock.sh` on `eve_p11` passed without skips: personal CSPA zero-cost/exemption and approval/retry cases (2); participant concurrency (3); calendar RPC and error wire cases (2); calendar batch writes (2); account contact-cost wire case (1). The approved create test asserts separate 600 ISK and 400 ISK journal entries with their recipient IDs.
- After the merge, `CalendarMgrServiceTests` (10) and `CoreCatalogTests.The_catalog_names_the_core_services` (1) also passed. All selected tests reported zero skips.
- Two adjacent contact persistence Host tests failed before reaching their test actions: `Character_contact_labels_persist_through_the_host_and_a_restart` and `Contact_add_persists_and_routes_the_recipient_notification` stop in `PersistenceTournamentMatches.RegisterActionHandlers` with `Tournament actions require scheduler and sessions`. This is in `HostComposition` setup and outside the calendar RPC path.
- The graphical V24.01 client flow has not been exercised.
- The latest completed main CI run is 1524 on `a5e2507fbf96` and passed all jobs. Run 1523 on that SHA previously failed while waiting for `structureControl.TakeControl` in `tests/test_structures.py:618`; the successful rerun cleared the gate. No CSPA changes have been pushed yet. The feature changes remain in the existing local commits and uncommitted work.
