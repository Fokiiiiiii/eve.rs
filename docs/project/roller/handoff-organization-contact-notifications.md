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


## Follow-up verification (2026-10-23)

- Focused run 40039315645 passed the bookmark prime regression 1/1, but the Host EditContact test failed because its test helper treated the notification row as a `PyObject`; the actual row and nested data are `PyDict`. Commit `9f7ad7a0` now handles both forms. Its focused run 40041021170 passed with no failed tests or jobs.
- Push CI 40039308083 also exposed an inherited mission keyword gap in `MissionViews.Keywords`: the response lacked state-derived `objectiveDestinationID` and reward fields. Commit `cf391eec` restores these values from the courier/fetch view and mission reward.
- The same push CI had unrelated concurrent structure/market failures: Fortizar docking timed out in the live market walk; the control-tower scoop fixture had only 5040 free volume for an 8000-volume item; and the space scoop test returned a wrapped refusal. Its Host RPC inventory expectation was 1378 while the registry exposed 1382 after four other routes were added. These do not involve the chat/contact/bookmark edits.
- Local focused `AgentMissionServiceTests.Bound_agent_mission_queries_project_courier_cargo_keywords_standing_gains_and_offer_removal` passed 1/1 without a database. Local Host and Bookmarks compile-checks passed with 0 warnings and 0 errors.
- Follow-up focused Actions and push CI are being monitored; no real graphical-client run was performed.


## Current verification (2026-10-23)

Commit `cf391eec` is on main. Focused Actions run `40041993458` passed, with no failed tests or jobs, for the bookmark prime, Host EditContact and AgentMission service regressions. The local AgentMission focused test passed 1/1. Push CI run `40041988259` remains in progress. No PostgreSQL tests were run locally.


## Follow-up CI result (2026-10-24)

Push CI run `40041988259` completed. The focused service regressions and all Host test shards except `RpcSecurityInventoryTests.Real_host_registry_methods_are_enumerable` passed. That test expected 1378 RPC rows but the current registry contains 1382, so the assertion is updated to 1382 in the follow-up. CI also found that `EjectFromStructure` refused `RETURN_SHIP_NOT_BOARDABLE` in the headless structure walk. Main commit `486812c1` contains Structures-side changes for this path and was merged locally; the next push CI will recheck it. Windows, Rust, .NET build, and selected remaining test shards passed. Slow headless tests were skipped by their documented settings and are not counted as passes.

- Local focused AgentMission regression passed 1/1 without a database.
- Local Host.Tests build after merging `486812c1` passed with 0 warnings and 0 errors.
- New Actions verification for the updated RPC inventory expectation has not started yet; No graphical-client verification was performed.


## Inventory follow-up (2026-10-24)

Main commit `486812c1` was merged before this correction. The Host inventory assertion now expects the observed 1382 registered RPC routes instead of 1378. Local `Eve.Host.Tests` build passed with 0 warnings and 0 errors. Actions run `40043761052` covers the updated inventory regression plus the bookmark, contact and mission keyword regressions; push CI run `40043754414` is active. Their results are pending. No local PostgreSQL tests were run.


## Focused inventory verification (2026-10-24)

Actions run `40043761052` passed all selected tests: bookmark prime, Host EditContact, AgentMission keywords and RPC inventory. No failed tests or jobs. Push CI `40043754414` for commit `35964099` is still running; the full CI status remains pending. The local Host.Tests build passed with 0 warnings and 0 errors. No local PostgreSQL tests or graphical-client checks were run.


## Latest CI follow-up (2026-10-24)

Push CI `40043754414` for `35964099` completed. The updated RPC inventory count and all four focused service tests passed. Three unrelated failures remained: the live market test decoded a tuple row where it expected a named KeyVal; a Space scoop test's fixture character creation returned `CreationRefused`; and a Host structure test expected a refusal where the implementation now allows the return. Main commit `4930fd25` contains market, Space fixture and StructureControl follow-up changes; the next CI run will verify them.

The inherited mission journal serializer was also corrected: `expirationTime` now uses `PyLong` for the 64-bit FileTime, with an assertion in the existing AgentMission regression. Local `Eve.Services.Core.Tests` build passed with 0 warnings and 0 errors. Actions run `40045225224` reruns that regression; push CI `40045219089` is active. Results are pending. No local PostgreSQL test or graphical-client run was performed.



## Focused mission expiry verification (2026-10-24)

Focused Actions run `40045225224` for commit `343cd0fd` passed the AgentMission keyword regression, including the 64-bit `expirationTime` assertion. The push CI run `40045219089` for the same code commit is still running; its Rust format/clippy, Python, fixture, .NET build, Rust tests, and latest-stable advisory jobs have passed so far, while several .NET shards, the end-to-end walk, and Windows job are still active. No failure has been reported yet. Earlier focused run `40043761052` also passed the bookmark expiry, contact edit, mission keywords, and RPC inventory tests. No local PostgreSQL tests or graphical-client run were performed.



## Completed push CI result (2026-10-24)

Push CI run `40045219089` for `343cd0fd` completed with a failed required gate. Rust format/clippy, Rust tests, Python client checks, fixture generation, .NET build, Windows, all Host shards, and the Services.Core shard passed. The focused AgentMission run `40045225224` passed, as did the four selected bookmark/contact/mission/inventory regressions in `40043761052`.

The full run's failures were outside this service slice: the headless E2E job received Macho error 2; `SpaceContainersUnanchorDropsTests.Scoop_transaction_rejects_an_upwell_hull_owner_that_disagrees_with_its_recorded_corporation` expected a custody-record error but got a nullable-value error; and two Market protocol tests failed (`Native_market_rpc_reads_descriptor_rows_buys_sells_and_cancels_without_node` observed two notifications where one was expected, plus `Market_client_plex_history_escrow_and_order_history_reads_are_native`). The push-triggered CI for the later main commit `808933b7` will check the newer Space fixture adjustment. The slow live movement, warp, jump, target, module, health, and others tests plus the skill-speed case were skipped by workflow settings; these are not passes. No local PostgreSQL or graphical-client checks were performed.



## Neighboring main CI follow-up (2026-10-24)

CI run `40045506688` for later main commit `808933b7` is still active. Rust checks/tests, Python, fixtures, .NET build, Windows-independent Host shards, and Space shard 2 have passed so far; E2E, Space shard 1, and Market shard 3 have failed again. The run has not completed, so detailed outcomes remain pending. This shows the previous Space fixture adjustment did not yet clear the full Space shard failure. The chat/contact/bookmark focused regressions remain green in their selected run; these neighboring failures are not from the service changes in this handoff.
