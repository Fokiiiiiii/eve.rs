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


## Personal notification verification update (2026-10-23)

The Host test distinguishes the event payload type from the persisted row type: live senderID is PyLong; the stored senderID and receiverID are PyInt. The initial Actions runs exposed these type expectations in sequence. The latest focused run is run 40013613450 for commit 48163285 and is pending. The corrected live senderID assertion is in commit d82fe4b0 (merged and pushed as 48163285).

- Local Host.Tests compile-check passed with 0 warnings and 0 errors. No local PostgreSQL tests were run.
- Push-triggered CI for 737ba657 was still running at this update. Its focused contact run failed only on the live senderID assertion and service test passed 1/1; the corrected assertion is under the latest focused run.
- Graphical-client behavior has not been checked.


## Focused Actions result (2026-10-23)

The corrected focused run 40013613450 for commit 48163285 passed: Eve.Services.CharacterInfo.Tests 1/1 and Eve.Host.Tests 1/1, with no skips. The local Host.Tests build also passed with 0 warnings and 0 errors. Push-triggered CI for 48163285 and the latest documentation push d7f45242 were still in progress at this update.

The earlier push CI for 737ba657 failed in two unrelated checks: the live SkinR consume test returned INVALID_COMPONENT_LICENSE_ITEM, and MarketHostTests.The_host_dispatches_accessible_structure_market_orders_and_recovers_partial_settlement_after_restart threw a wrapped exception. These failures need triage in their owning areas. No graphical-client verification has been done.


## Push CI follow-up (2026-10-23)

CI run 40014207144 for 8610990c is still active. Its .NET build, Rust checks, Rust tests, fixtures and completed Host test shards passed. The E2E job failed in the unrelated SKINR fixture query (text = bigint), and Host shard 1 failed in the unrelated structure market sell case (StructureMarketDenied). Other slow live tests were skipped by their documented settings; those are not passes. The corrected focused contact run 40013613450 remains green (2/2, 0 skipped). No notification-related failure appeared in the focused run. The Windows job has not completed yet.


## Chat, mail, contacts, notes, and bookmarks slice (2026-10-23)

The V24.01 chat inventory in `docs/reference/compat/DEVIATIONS.md` identifies XMPP as the client transport; the native LSC response methods do not have a V24.01 Macho callsite. Mail reads, send and mark-as-read are implemented; the remaining client-used mailbox mutations need individual wire and durable-state review. Personal notes, contacts and bookmark read/write paths use their existing owner-scoped stores.

- Added `ExpiredBookmarkPrimeTests.Priming_removes_expired_shared_bookmarks_and_notifies_other_active_viewers_after_the_reply`: covers `GetMyActiveBookmarks` three-list reply, expiry removal, exact shared-folder update, after-response ordering and no duplicate notice to the reader.
- Added `ContactEditHostWireTests.EditContact_persists_type_90_and_routes_the_client_notification_to_the_recipient`: dispatches the Host `charMgr.EditContact` RPC and checks persisted notification fields and the recipient event.
- Local compile-checks passed: `Eve.Bookmarks.Tests` and `Eve.Host.Tests`, each with 0 warnings and 0 errors. No PostgreSQL tests were run locally.
- Focused Actions run 40039315645 for `fd4828ca` and push CI run 40039308083 were in progress at this update.
- Remaining gaps include mail mutations, XMPP offline invites/room deletion/configuration/history/alliance MOTD, in-space `BookmarkLocation`, broader organization contact mutation coverage, and graphical-client validation.
