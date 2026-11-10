# Medal RPC Host coverage handoff (RPC-01317–RPC-01323)

## Result

These rows were already implemented in the real `corporationSvc`; the focused socket regression covers their registered Host routes and records the wire outcomes. The award notification now uses `OnMedalIssued`, the V24.01 client event, rather than the previous `OnMedalsReceived` name, which no client handler consumes. EveJS v0.12.9 emits no award notification; this client-driven difference is recorded in `docs/reference/compat/DEVIATIONS.md`.

The test registers the actual `CorporationService` with `ServiceRegistry`, sends marshaled `corporationSvc` calls through Host, and checks the empty recipient Rowset plus Macho error replies from both GiveMedalToCharacters arities and SetMedalStatus for a session without a character. It includes transport `machoVersion`. The no-character route avoids opening a database connection. Database-backed award/status persistence and notification delivery were not exercised in this no-database job.

## Whole-client trace

- `eve/client/script/ui/services/medals.py:53-58`: `SetMedalStatus` skips a `None` map and otherwise sends one positional argument. `decorationsPanel.py:86-128` builds map entries from the active character’s received medals, with status 1/2/3 for removed/private/public; the bulk path explicitly starts from `GetMedalsReceived(session.charid)`. This corrects the earlier interpretation that the map edits a corporation-wide medal definition.
- `eve/client/script/ui/services/medals.py:63-69`: `GetRecipientsOfMedal(medalID)` returns the RPC result after touching each recipient's `reason`. `eve/client/script/ui/shared/neocom/corporation/corp_ui_member_decorations.py:110-146` consumes `recepientID`, `issuerID`, `date`, and `reason` and counts the returned recipient entries.
- `eve/client/script/ui/services/medals.py:93-120`: `GiveMedalToCharacters` first sends `medalID, recipientID, reason`; after the two `ConfirmGivingMedal*` user errors it retries with the same arguments and a fourth `True`. A scalar recipient is normalized to a list; the menu action at `eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:71` passes recipients through this wrapper. The wrapper handles `NotEnoughMoney` separately and returns no payload.
- `eve/client/script/ui/services/medals.py:121-146`: medal notifications invalidate medal and recipient caches and notify the corporation UI or current character's medal UI.

The tree-wide search for these RPC names found the call sites and consumers above; no other direct medal RPC callers were found in `/home/user/eve-logic`.

## EveJS and Host comparison

EveJS 261108.1 `server/src/services/corporation/corpService.js:526-559` validates that the session character received each medal and updates only that character’s recipient rows. `:472-479` returns each received award’s own status and filters cross-character reads to public awards; `:591-599` filters recipient lists to self or public awards. The service methods were also directly executed against an isolated GameStore during CX3. It accepts the extra confirmation argument without changing its result.

The Host `dotnet/src/Eve.Services.Corporations/CorporationService.cs` returns the matching five-column recipient Rowset, scopes status changes to the active character’s own award rows, and persists them in PostgreSQL. Migration 018 carries legacy shared visibility into existing award rows. The prior manager-gated interpretation is superseded by the client/EveJS trace in `docs/project/deepfix-corporations.md`. Its after-response notification is now named `OnMedalIssued`, consumed by `medals.py:21,131-137`; the previous `OnMedalsReceived` event was not consumed by V24. The focused socket test exercises route resolution, the no-character empty Rowset, transport `machoVersion`, no-character denial for SetMedalStatus, and authorization errors for both Give arities. The registered Host regression `CharacterHostTests.Corporation_medal_privacy_is_owned_by_each_recipient_through_registered_host_wire` now exercises recipient-specific updates, filtering, SQL persistence, restart, mixed-batch rollback, and the no-notification outcome on `eve_p3`. The award issuance notification remains after the RPC response. This handoff’s earlier no-database limitation is historical; the later CX3 checks are recorded in `docs/project/deepfix-corporations.md`.

## Verification

- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false -m:1` — passed, 0 warnings and 0 errors.
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Corporations.Tests/Eve.Corporations.Tests.csproj -p:UseSharedCompilation=false -m:1` — passed, 0 warnings and 0 errors.
- Latest local focused Host test after the notification change: 1 passed, 0 failed, 0 skipped.
- GitHub Agent test run `40175314720`, dispatched with ref `495d74b1`: succeeded with no failed tests or jobs. The completed workflow reports tested SHA `c752afd43372`, which contains the medal Host test and notification changes.
- `python3 tools/dev/ci-status.py failures c752afd43372`: no `ci.yml` run was found for that SHA.
- Real graphical V24.01 client behavior was not exercised.
- Push-triggered CI for `a9592bb09c3c` also ran the unrelated `MarketTradeHostWireTests.Registered_market_and_trade_calls_reply_and_update_persisted_state` and failed because nested `PyList` equality compared record fields instead of list contents. The assertion now checks the trader values as an element sequence and confirms their `PyInt` wire types. `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false -m:1 --no-restore` succeeded after the correction (four transient file-lock retry warnings from concurrent builds). Focused Actions rerun `40176992596` passed with no failed tests or jobs. `ci-status.py failures a6a8ffbf` reported no `ci.yml` run for that SHA at check time.
