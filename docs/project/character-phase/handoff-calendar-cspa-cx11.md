# Personal calendar CSPA charges handoff (cx11)

## Contract and implementation

V24.01 wraps personal event creation with invitees and participant additions in `CSPAChargedAction` (`eve/client/script/ui/services/eveCalendarsvc.py:171-203`). The wrapper reads `ContactCostNotApproved` from `e.args[1]`, returns on decline, and retries once with `approvedCost` (`eve/client/script/util/eveMisc.py:88-101`). Event edits are a separate earlier RPC in the client edit flow (`eve/client/script/ui/shared/eveCalendar.py:1575-1592`), so declining the invitation preserves the edit while refusing the participant mutation. The wallet journal uses `refCSPA = 35` (`eve/common/lib/appConst.py:675`).

Calendar mutation callbacks now run inside the existing PostgreSQL store transaction. The Host reads recipient CSPA settings through the existing character-setting owner, applies contact, block, and corporation exemptions, charges each newly added non-exempt recipient through `WalletTransactions`, and raises the client-shaped `ContactCostNotApproved` error before commit when approval is absent or insufficient. Calendar rows and charges roll back together; wallet balance notification follows commit. Duplicate active participant additions produce no new charge.

EVE University documents the zero default, 1,000,000 ISK maximum, and non-blocked contact / same-corporation exemptions ([EVE Mail](https://wiki.eveuniversity.org/EVE_Mail)). EveJS 261028.1's calendar manager lacks this approval/charge path; the V24.01 client and documented mechanic govern this behavior. The compatibility record is in `docs/reference/compat/DEVIATIONS.md`.

## Files

- `dotnet/src/Eve.Calendar/CalendarStore.cs`: transactional callback receives event ID and actual invitees.
- `dotnet/src/Eve.Services.Core/CalendarMgrService.cs`: approval, CSPA journal writes, and post-commit wallet notification.
- `dotnet/src/Eve.Host/Composition/CharacterServicesComposition.cs`, `dotnet/src/Eve.Host/Rpc/EconomyRpc.cs`, `dotnet/src/Eve.Host/Hosting/HostComposition.cs`, `dotnet/src/Eve.Services.Core/CoreServices.cs`: authoritative settings/wallet provider wiring.
- `dotnet/tests/Eve.Host.Tests/CalendarCspaHostTests.cs`: PostgreSQL-backed Host wire and persistence coverage.
- `dotnet/tests/Eve.Host.Tests/CalendarParticipantConcurrencyTests.cs`, `CalendarRpcHostWireTests.cs`, `HostServerTests.cs`, `dotnet/tests/Eve.Services.Core.Tests/CalendarMgrServiceTests.cs`: callback signature and test-provider updates.

## Local checks

- Host.Tests and Services.Core.Tests builds succeeded.
- Host calendar selection: 6 passed, 0 skipped.
- CalendarMgrServiceTests: 10 passed, 0 skipped.
- An initial broad partial `HostServerTests` selection ran 414 tests: 389 passed and 25 failed. Two calendar concurrency failures were caused by their fixtures lacking the new explicit zero-cost CSPA provider; that setup was corrected and the six calendar-focused Host tests passed afterward. The other failures came from unrelated partials requiring Tournament composition or imported SDE tables.
- A real graphical V24.01 client walk has not been performed.
