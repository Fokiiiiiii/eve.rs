# Medal RPC Host coverage handoff (RPC-01317–RPC-01323)

## Result

These rows were already implemented in the real `corporationSvc`; the focused socket regression covers their registered Host routes and records the wire outcomes. The award notification now uses `OnMedalIssued`, the V24.01 client event, rather than the previous `OnMedalsReceived` name, which no client handler consumes. EveJS v0.12.9 emits no award notification; this client-driven difference is recorded in `docs/reference/compat/DEVIATIONS.md`.

The test registers the actual `CorporationService` with `ServiceRegistry`, sends marshaled `corporationSvc` calls through Host, and checks the empty recipient Rowset plus Macho error replies from both GiveMedalToCharacters arities and SetMedalStatus for a session without a character. It includes transport `machoVersion`. The no-character route avoids opening a database connection. Database-backed award/status persistence and notification delivery were not exercised in this no-database job.

## Whole-client trace

- `eve/client/script/ui/services/medals.py:52-58`: `SetMedalStatus` skips a `None` status map and otherwise sends it as one positional argument. `decorationsPanel.py:97,125` sends status dictionaries for removing (1), private (2), and public (3) medal states.
- `eve/client/script/ui/services/medals.py:63-69`: `GetRecipientsOfMedal(medalID)` returns the RPC result after touching each recipient's `reason`. `eve/client/script/ui/shared/neocom/corporation/corp_ui_member_decorations.py:110-146` consumes `recepientID`, `issuerID`, `date`, and `reason` and counts the returned recipient entries.
- `eve/client/script/ui/services/medals.py:93-120`: `GiveMedalToCharacters` first sends `medalID, recipientID, reason`; after the two `ConfirmGivingMedal*` user errors it retries with the same arguments and a fourth `True`. A scalar recipient is normalized to a list; the menu action at `eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:71` passes recipients through this wrapper. The wrapper handles `NotEnoughMoney` separately and returns no payload.
- `eve/client/script/ui/services/medals.py:121-146`: medal notifications invalidate medal and recipient caches and notify the corporation UI or current character's medal UI.

The tree-wide search for these RPC names found the call sites and consumers above; no other direct medal RPC callers were found in `/home/user/eve-logic`.

## EveJS and Host comparison

EveJS v0.12.9 `server/src/services/corporation/corpService.js:547-565` applies medal statuses for the current corporation and returns null; `:595-605` returns the recipients Rowset; `:607-630` awards each recipient and returns null. It accepts the extra confirmation argument without changing its result.

The current Host `dotnet/src/Eve.Services.Corporations/CorporationService.cs` returns the matching five-column recipient Rowset, authorizes medal changes against the corporation manager role, and persists status/award changes. Its after-response notification is now named `OnMedalIssued`, consumed by `medals.py:21,131-137`; the previous `OnMedalsReceived` event was not consumed by V24. The focused socket test exercises route resolution, the no-character empty Rowset, transport `machoVersion`, and authorization errors for SetMedalStatus and both Give arities. Existing corporation protocol tests exercise persisted awards/status and the after-response recipient notice. This no-database task does not exercise the database-backed path through HostComposition.

## Verification

- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false -m:1` — passed, 0 warnings and 0 errors.
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Corporations.Tests/Eve.Corporations.Tests.csproj -p:UseSharedCompilation=false -m:1` — passed, 0 warnings and 0 errors.
- Latest local focused Host test after the notification change: 1 passed, 0 failed, 0 skipped.
- GitHub Agent test run `40175314720`, dispatched with ref `495d74b1`: succeeded with no failed tests or jobs. The completed workflow reports tested SHA `c752afd43372`, which contains the medal Host test and notification changes.
- `python3 tools/dev/ci-status.py failures c752afd43372`: no `ci.yml` run was found for that SHA.
- Real graphical V24.01 client behavior was not exercised.
- Push-triggered CI for `a9592bb09c3c` also ran the unrelated `MarketTradeHostWireTests.Registered_market_and_trade_calls_reply_and_update_persisted_state` and failed because nested `PyList` equality compared record fields instead of list contents. The assertion now checks the trader values as an element sequence; Host.Tests builds successfully after the correction.
