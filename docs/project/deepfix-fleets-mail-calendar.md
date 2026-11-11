# Fleets, mail, calendar and chat ACL deep fix

Updated 2026-11-11. This record separates checked behavior from remaining checks for the cx3 worktree.

## Findings and changes

### Fleet broadcasts and command boosts

- The V24.01 client checks one `lastBroadcast` value before each of its three broadcast RPCs. Same-name broadcasts wait two seconds; a different name waits `int(2 / 3 * SEC)` 100 ns ticks (`eve/client/script/parklife/fleetSvc.py:58,962-981`). In a server clock with millisecond resolution, the first whole millisecond at or beyond the client deadline is 667 ms. EveJS uses integer `Date.now()` milliseconds and floors this interval to 666 ms (`server/src/services/fleets/fleetRuntime.js:2648-2671`). The server limiter uses 667 ms so an unmodified client is never accepted before its own deadline; the host regression covers 666 ms rejected and 667 ms accepted through both `fleetMgr` and `fleetObjectHandler`.
- The Host shares one limiter instance across `BroadcastToBubble`/`BroadcastToSystem` and bound-object `SendBroadcast`, preventing a client from bypassing the interval by changing RPC routes. The test also checks `OnFleetBroadcast` notification shape and delivery to a member (`FleetBroadcastRateLimitHostWireTests.cs`). EveJS emits that notification with name, scope, sender, system, item and type (`fleetRuntime.js:2696-2725`); the V24 receiver consumes those fields (`fleetSvc.py:1542-1556`).
- Successful command bursts now send `OnFleetBoosted(moduleTypeID, numBoosted)` to the source pilot after applying buffs. This matches the client handler (`eve/client/script/parklife/dbuffClient.py:10-20`). `CommandBurstHostTests` checks the module type, affected ship count and that the ally does not receive the source-only notice.

### Mail and the notification inbox

- The first Host-level mailing-list welcome-mail regression observed two server pushes: `OnNotificationReceived(typeID=1004)` followed by `OnMailSent`. The V24 client builds the new-mail notification locally from `OnMailSent` (`eve/client/script/ui/services/mail/mailSvc.py:1119-1152,1197-1218`; `notificationSvc.py:403-415`). EveJS likewise sends `OnMailSent` and explicitly keeps no server notification row for ordinary mail (`server/src/services/mail/mailState.js:1240-1248`).
- `PersistenceMailSendHostAdapter.SendAsync` now commits the message and recipient status, then emits the client-facing `OnMailSent`; it no longer writes a second 1004 inbox row or pushes `OnNotificationReceived` for ordinary or mailing-list welcome mail. Dedicated lifecycle notifications (for example kill-right and alliance-bill notices) still use their notification records and remain covered by `HostServerTests`.
- `CharacterHostTests.MailFlow` drives Host registration and RPC dispatch, saves/joins a list, checks the welcome-mail header and decompressed body, verifies persisted mail and status, restarts the Host store, then checks sync again. It asserts the only immediate mail push is `OnMailSent` and `notificationMgr.GetUnprocessed` has no duplicate server-side mail notice. `HostServerTests.Mail_host_adapter_commits_mail_before_returning_id_without_a_server_mail_notification` checks durable mail precedes the send path and that an unrelated notification row is preserved.
- The client’s notification panel and local `notificationSvc` cache were source-traced, not run in the graphical client. The headless checks therefore verify server wire and persistence, not the rendered popup or post-login client cache.

### Calendar events and responses

- The selected flows follow the V24 client’s create/edit/delete/response and local cache transitions (`eve/client/script/ui/services/eveCalendarsvc.py:172-218,295-314,333-411`). EveJS publishes new, edit, remove and external-response notifications with the corresponding client argument shapes (`server/src/services/calendar/calendarMgrService.js:24-149`; `calendarNotifications.js:78-138`).
- No calendar production code changed in this pass. The selected PostgreSQL Host tests exercise the registered RPC routes, CSPA charging, event visibility, participant writes, delete/reconnect responses, commit ordering and current corporation/alliance audience. No divergence was found in those checked scenarios. This is not a claim that every calendar view or the graphical client was verified.

### Chat room access and expiring moderation

- V24 creates temporary mutes through `SetRole(visitor)` followed by `urn:xmpp:expiring_record` registration; temporary bans use `SetAffiliation(outcast)`, register the expiry, then clear the permanent affiliation (`eve/xmppchatclient/xmppchatsvc.py:1509-1535,1538-1548`). The client requests current mute/ban lists through `urn:xmpp:expiring_record#search` (`xmppchatsvc.py:1614-1622`) and renders the room access modes as `allowed`, `moderated` and `blocked` (`xmppchannelsettings.py:234-247`).
- Player-room ownership, configuration, organization ACLs and temporary restrictions are backed by the existing player-channel store. The expiring-record set/delete and search IQs validate the active character, room administrator and category; search items return JID, reason, creation time, expiry, and actor. An active temporary ban survives the client’s subsequent `affiliation=none`, while expiry removes only the temporary restriction. A newly created player room follows the client/EveJS open-room defaults (`xmppStubServer.js:1178-1180`; `chatRuntime.js:1129-1159`).
- `XmppOrganizationRoomTests` drives the actual session IQ entry points and PostgreSQL store for temporary mute and ban. It checks mute enforcement, list metadata, ban refusal after the V24 `outcast → expiry set → none` sequence, store reopen, and rejoin after expiry. The existing room access tests cover persistent member/outcast and visitor roles, co-owner limits, corporation allow/deny and store reopen.
- The temporary expiring-record route added here is scoped to player-owned rooms. Static/system-room ACL administration and automatic graphical-client roster refresh at the instant a live mute expires were not verified in this pass.

## Verification

The database-backed checks used the job’s own `eve_p3` database and the pinned SDE build 3396210. The SDE and mission data were loaded into that database from `.cache/sde`; no SDE files were added to the repository.

- `Eve.Host.Tests`, filter `FullyQualifiedName~Xmpp|FullyQualifiedName~Mail|FullyQualifiedName~Calendar|FullyQualifiedName~Fleet`: 88 passed, 0 failed, 0 skipped. This includes the calendar RPC/CSPA/delete/concurrency classes, mail and mailing-list wire tests, fleet wire tests, XMPP organization/player-room tests, the mail notification regression and the fleet cross-route limiter regression.
- `Eve.Services.Space.Tests`, filter `FullyQualifiedName~Burst|FullyQualifiedName~FleetBroadcastRateLimiterTests`: 34 passed, 0 failed, 0 skipped.
- `Eve.Services.Space.Tests`, filter `FullyQualifiedName~Fleet`: 57 passed, 0 failed, 0 skipped. This includes fleet creation, membership/roles and finder RPC scenarios.
- `Eve.Services.Core.Tests`, filter `FullyQualifiedName~CalendarMgrServiceTests|FullyQualifiedName~MailServiceTests|FullyQualifiedName~MailingListServiceTests|FullyQualifiedName~NotificationMgrServiceTests`: 107 passed, 0 failed, 0 skipped.
- Before the fixes, the new mail Host regression failed on the extra `OnNotificationReceived(typeID=1004)` push. The fleet unit and Host wire regressions failed when the server accepted a different broadcast name at 666 ms. After the changes, the focused checks and the selected class runs above pass.
- The Core test project initially had no NuGet assets for a `--no-restore` invocation; the same selected classes were rerun with restore and passed.

Release builds with `-warnaserror` succeeded with 0 warnings and 0 errors for `Eve.Host`, `Eve.Services.Space`, `Eve.Host.Tests`, `Eve.Services.Space.Tests` and `Eve.Services.Core.Tests`.

### Not checked

- No graphical V24.01 client session was run. These are Host, service and XMPP-session headless tests.
- EveJS has no project test script in `server/package.json`; its relevant server paths were traced, but no separate EveJS runtime instance was started for this work.
- The entire notifications UI, all calendar view combinations, static/system-room moderation, and live roster changes at mute expiry remain open.
