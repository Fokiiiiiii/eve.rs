# Deep fix: mailing-list join and welcome mail

## Scope and sources checked

This change follows the client path for joining a mailing list and receiving its configured welcome mail. Client references are from the whole V24.01 `/home/user/eve-logic` tree: the join window calls the service with the entered name and closes only on a non-None result (`eve/client/script/ui/shared/neocom/evemail.py:2909-2918`); the service caches the returned list ID and scatters `OnMyMaillistChanged` (`eve/client/script/ui/services/mail/mailingListsSvc.py:73-77`). Leave handling removes the local cache entry and scatters the same event (`mailingListsSvc.py:161-169`).

The client mail consumer receives `OnMailSent(messageID, senderID, sentDate, toCharacterIDs, toListID, toCorpOrAllianceID, title, statusMask)`, builds the header and uses the mailing-list display name when the automated bit is set and `senderID == toListID` (`eve/client/script/ui/services/mail/mailSvc.py:1118-1145`). The authoritative list-join permission rules and welcome-mail behavior are from current EveJS 261028.1: access values are normalized at `server/src/services/mail/mailState.js:119-124`, where 0 is blocked; join access gives any block precedence, then any allow, then the default (`481-507`); join is idempotent, checks the 3000-member cap, persists membership and sends configured welcome mail (`1356-1415`); the member cap is 3000 (`mailState.js:61`). EveJS maps full, denied and missing-list cases to the client errors `MailingListFull`, `MailingListCantJoin` and `MailingListNoSuchList` (`server/src/services/mail/mailingListsMgrService.js:168-180`).

EVE University’s [EVE Mail guide](https://wiki.eveuniversity.org/EVE_Mail) describes mailing lists as group mail and documents configurable welcome mail. The exact access numbers and capacity are taken from the executable EveJS reference because the wiki does not specify those wire-level values. No static-data-derived values are involved.

## Defects fixed

1. **Join permission values were inverted.** Host treated default access 0 as allowed, and an explicit allow (1) as denied. It now normalizes access values and allows only when no character/corporation/alliance entry blocks the join and at least one explicit entry allows it or the default allows it. Existing-member idempotence remains before the capacity check, matching EveJS. The 3000-member cap is enforced before permission failure, matching EveJS error precedence. Production policy is in `dotnet/src/Eve.Host/Composition/MailingListComposition.cs:84-89,205-220`; focused precedence assertions are in `dotnet/tests/Eve.Host.Tests/HostServerTests.cs:4480-4487`.

2. **Joining did not deliver configured welcome mail.** After the membership row has been durably persisted, Host now invokes the real mail writer for the joining character when welcome content is configured (`MailingListComposition.cs:229-241`). The recipient resolver constrains this internal send to the authenticated joining member, current list membership and the exact persisted welcome content (`dotnet/src/Eve.Host/Composition/MailComposition.cs:102-121`). The mail record uses the list ID as sender and automated status 16, and persistence precedes live notifications (`dotnet/src/Eve.Host/Persistence/Mail/PersistenceMailSendHostAdapter.cs:103-133,167-200,212-259`). The client consequently takes its mailing-list sender-name path at `mailSvc.py:1138-1140`.

## Verification

- `dotnet build dotnet/tests/Eve.Host.Tests -p:UseSharedCompilation=false`: passed, zero warnings/errors.
- On database `eve_p3`, through `tools/dev/db-lock.sh`: Host focused tests for join access precedence, mail adapter persistence/notifications, mail and mailing-list RPC wire shapes, and character contact notepad: 5 passed, 0 skipped.
- On `eve_p3`, through `tools/dev/db-lock.sh`: `MailServiceTests` and `MailingListServiceTests`: 74 passed, 0 skipped.
- The new join-policy test asserts default allow/deny, explicit allow overriding a deny default, explicit deny overriding an allow default, and deny winning when allow and deny both apply.
- The mail adapter regression test verifies persisted welcome-mail sender/list/status values and the `OnMailSent` and notification payloads.

## Remaining coverage

The tests exercise the access policy directly and exercise the mail persistence adapter, but do not yet run the full registered Host `Join` RPC against a persistent mailing-list row and then read the resulting welcome mail through `SyncMail`. Chat-channel join/message delivery and contact add/remove are outside this patch; prior service-audit notes `docs/project/service-audit/DEEPCHECK_mailMgr.md` and `docs/project/service-audit/deepfix-fleets and chat and mail and contacts.md` contain related checks. The graphical V24.01 client was not launched for this change.
