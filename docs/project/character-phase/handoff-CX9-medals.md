# CX9 medals RPC handoff

## Scope and implementation

Rows RPC-01317 through RPC-01323 use the existing registered `corporationSvc` implementation. The Host medal award notification now uses `OnMedalIssued`, the V24 event consumed by `medals.py`; the previous `OnMedalsReceived` name had no V24 subscriber. A Host integration regression uses `HostComposition.BuildAsync`, calls the registered service, checks refusal and row shapes, verifies the after-response event to an online recipient, and reads status and awards after restarting Host.

## V24 client trace

- `eve/client/script/ui/services/medals.py:52-58`: `SetMedalStatus(statusdict)` forwards its one dictionary argument to `corporationSvc.SetMedalStatus`; `None` is a local no-op.
- `eve/client/script/ui/shared/neocom/charsheet/decorationsPanel.py:89-98,105-126`: changed visibility values are medalID-to-status entries; bulk visibility calls `GetMedalsReceived` before sending those entries.
- `eve/client/script/ui/services/medals.py:63-68`: `GetRecipientsOfMedal(medalID)` returns recipient rows and reads `reason`.
- `eve/client/script/ui/shared/neocom/corporation/corp_ui_member_decorations.py:107-140`: the awardee view consumes `recepientID`, `issuerID`, `date`, and `reason`, grouping repeated awards by recipient.
- `eve/client/script/ui/services/medals.py:93-120`: `GiveMedalToCharacters` accepts an integer or list, forwards three arguments, and retries with a fourth `True` after confirmation.
- `eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:59-71`: the corporation member menu normalizes selected character IDs to a list before calling the medals service.
- `eve/client/script/ui/services/medals.py:21,131-137`: recipients subscribe to `OnMedalIssued`; the event invalidates `GetMedalsReceived(session.charid)` and scatters `OnUpdatedMedalsAvailable`.
- `eve/client/script/ui/services/medals.py:139-146`: the client also consumes `OnMedalStatusChanged` to invalidate the current character's medal cache. The local visibility editor clears and reloads its own panel after `SetMedalStatus`.

The corpus-wide search found no additional direct callers of these three RPC methods. Additional medal read consumers include `decorationsPanel.py:198-203`, `charSheetUtil.py:31-35`, `infoContentBase.py:675,1450`, and `notificationUtil.py:138-140`.

## EveJS comparison

EveJS v0.12.9 `server/src/services/corporation/corpService.js:547-565` mutates medal status and invalidates its process-side caches. Lines 595-605 return a recipient rowset. Lines 607-630 append each award with the session issuer and reason, invalidate process-side caches, and return null. EveJS emits no medal notification. The V24 client has a concrete `OnMedalIssued` consumer, so eve.rs now sends that event after the award response; this client-visible difference is recorded in `docs/reference/compat/DEVIATIONS.md`.

## Verification

The focused Host test at `dotnet/tests/Eve.Host.Tests/CorporationMedalsHostTests.cs` is database-backed. Compile checks passed for `Eve.Host.Tests` and `Eve.Corporations.Tests`, with zero warnings and errors; the Host test assembly was rebuilt after the final call-shape assertions. No PostgreSQL database was used locally. Run the focused Host test on Actions and record whether it executed or was skipped; a skip is not a pass. The graphical client was not used.

## Files

- `dotnet/src/Eve.Services.Corporations/CorporationNotificationAdapter.cs`
- `dotnet/tests/Eve.Corporations.Tests/CorporationProtocolTests.cs`
- `dotnet/tests/Eve.Host.Tests/CorporationMedalsHostTests.cs`
- `docs/reference/compat/DEVIATIONS.md`
- `docs/project/TODO.md`
