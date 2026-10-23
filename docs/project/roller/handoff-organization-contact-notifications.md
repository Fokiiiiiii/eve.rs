# Organization contact notifications handoff

## State

`OnOrganizationContactsUpdated(ownerID, updates)` is wired into corporate add/edit/remove and bulk relationship RPCs, and alliance add/edit/remove and bulk relationship RPCs. The shared corporation notification adapter emits two RPC arguments: the organization owner ID and update rows. Rows are Python tuples containing contact ID, relationship ID or None, and label mask or None.

The adapter schedules delivery with `ServiceCall.AfterResponse`, then fans out to currently registered organization sessions. Corporate recipients receive `corpid`; alliance recipients receive `allianceid`. Each recipient gets its own packet and notification sequence.

## Evidence checked

- V24.01 `addressbookService.py`, `AddressBookSvc.OnOrganizationContactsUpdated` consumes owner ID and three-field update rows.
- EveJS 261018.1 `corporationNotifications.notifyOrganizationContactsUpdated` and the corporation/alliance registry contact handlers use the same owner and row values, and notify after their mutation calls.
- The Host composition passes `CharacterSessions` into `AllianceRegistryService`; the corporation registry already accepts `CharacterSessions`.
- The focused `OrganizationContactsNotificationTests` check the two-argument wire payload, tuple rows, response ordering, organization fanout, and corp/alliance idType selection.
- `Eve.Services.Corporations` build passed. The focused test passed 1/1. GitHub Actions focused run 39945348344 passed 1/1.

## Remaining work

The focused test exercises the shared notification adapter with live `ClientSession` objects. It does not dispatch each mutation through Host or assert the PostgreSQL state after mutation. The broader acceptance walk still needs Host-dispatch coverage for corp and alliance add/edit/remove/bulk relationship updates, refusal/rollback and retry, reconnect followed by a fresh contact query, and the live headless contact edit/remove flow. Do not mark the feature complete until those paths have passing checks.

The push CI for `d09df23e` exposed a stale Host RPC inventory count after the recently added `activityMgr` service contributed four routes. The inventory expectation is now 1378 (the prior expectation was 1374); the notification implementation adds no RPC routes.


## Personal contact notification audit

V24.01 personal `AddContact` and `EditContact` calls can request a notice to the contacted character with the relationship level and message. The Host-bound `charMgr` now writes notification types 89/90 in group 9 via the existing persistent mailbox and sends the five-argument `OnNotificationReceived` event to the recipient session using the existing `clientID` route. Recipient IDs must resolve to a character and differ from the sender; no notice is written when the client flag is false.

- Wire contract source: V24.01 `addressbookService.py` and notification constants; matching producer in EveJS 261018.1 `charMgrService.js`.
- Regression coverage: `Contact_add_and_edit_forward_the_recipient_notice_only_when_requested` checks Add/Edit types, level/message, and opt-out; `Contact_add_persists_and_routes_the_recipient_notification` checks the Host mailbox, live packet and restart readback.
- Local database tests and graphical-client behavior await the Actions run; the local machine must not run PostgreSQL tests.
