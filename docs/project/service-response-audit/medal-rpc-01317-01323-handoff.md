# Corporation medal RPC Host coverage: RPC-01317–01323

## Outcome

The seven audit rows map to three existing methods on `corporationSvc`; production Host composition already registers `CorporationService` in `dotnet/src/Eve.Host/Rpc/CorporationRpc.cs:60`. The regression `Corporation_medal_rpcs_use_registered_wire_shapes_with_macho_version` sends real socket RPCs through Host registration with the transport keyword `machoVersion=1`.

## V24.01 whole-client call and consumer trace

- **RPC-01317, `GetRecipientsOfMedal(medalID)`:** `eve/client/script/ui/services/medals.py:63-69` forwards the medal ID and returns the recipient rows. The sole UI consumer found across the full `eve-logic/eve` tree is `eve/client/script/ui/shared/neocom/corporation/corp_ui_member_decorations.py:109-145`: it groups repeated awards by the wire field `recepientID`, then reads `issuerID`, `date`, and `reason` to show issuer, time, reason, and award count. `medals.py:121-128` invalidates both the corporation medal list and the recipient cache after `OnCorporationMedalAdded`; the panel reloads on the resulting UI signal at `corp_ui_member_decorations.py:33,156-159`.
- **RPC-01318–01321, `GiveMedalToCharacters`:** the wrapper at `medals.py:93-120` checks the client role, prompts for a reason, converts one integer recipient into a list, then calls the RPC with medal ID, recipient list, and reason. A `ConfirmGivingMedal` refusal triggers the confirmation dialog and retries with a fourth `True` argument at lines 107-113. The other full-tree direct caller, `eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:54-72`, supplies the selected character ID list. Both client paths ignore the successful RPC result. The recipient panel above consumes subsequent award rows.
- **RPC-01322–01323, `SetMedalStatus(statusdict)`:** `medals.py:52-57` forwards the dictionary and returns no value. Full-tree callers are `eve/client/script/ui/shared/neocom/charsheet/decorationsPanel.py:86-101` (changed medal ID to permission values) and `:103-129` (bulk permission changes); both refresh or invalidate their decoration view locally. `medals.py:139-145` invalidates the current character's received-medal cache on `OnMedalStatusChanged`; the RPC itself does not consume a result.

The notification subscribers declared in `medals.py:20-21` are handled at `:121-145`: corporation medal addition refreshes the corporation list and recipient list, medal issue and status change refresh received medals. The Host award path sends `OnMedalsReceived` to online recipients after persistence; the existing corporation integration regression checks its after-commit ordering.

## EveJS v0.12.9 comparison

- `server/src/services/corporation/corpService.js:595-605` returns the medal's recipient rowset.
- `:607-630` accepts scalar or list recipient IDs, records each award with issuer, reason, and date, invalidates medal caches, and returns null. The fourth confirmation argument is not used by this handler.
- `:547-565` applies a medalID-keyed status map to the corporation medal definition, invalidates medal caches, and returns null.

eve.rs preserves those client-visible shapes. Its source of truth is the corporation store: `CorporationService.cs:124-132` authorizes and persists status changes, `:153-160` returns recipient rows with columns `medalID, recepientID, issuerID, reason, date`, and `:166-196` validates and persists awards before scheduling recipient notification. The backing store is durable PostgreSQL rather than EveJS's process runtime; this changes restart behavior in favor of persistence and does not change the client protocol. No compatibility deviation was required.

## Verification and limits

- Local focused Host wire test: 1 passed, 0 failed, 0 skipped. It checks the empty recipient rowset and the registered Status, list-award, scalar-award, and confirmed-award RPC routes with `machoVersion=1`; the no-character mutation attempts receive Host error responses before any storage access.
- Local `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --no-restore`: succeeded, 0 warnings and 0 errors.
- Existing `CorporationProtocolTests.Corporation_medal_rpcs_preserve_wire_rows_persist_awards_and_follow_reference_authorization` and `Corporation_membership_role_and_medal_notifications_run_after_commit` cover authorized persistence, row data, role checks, and notification order. They use PostgreSQL and were not executed because this task forbids database use.
- No graphical V24.01 client session was run. The new database-free Host test does not exercise a successful authorized mutation; the existing PostgreSQL corporation tests cover that state and persistence path.
