# Host RPC coverage handoff: RPC-01868–RPC-01894 (mail)

Updated 2026-10-25.

## V24.01 callers and consumers

| Rows | Client call and consumer evidence |
|---|---|
| RPC-01868, RPC-01871 | `mailSvc.py:322-348` invokes `MarkAsUnread`/`MarkAsRead`; `evemail.py:1075-1083` calls these for selected messages. `mailSvc.py:496-518` also marks read after `GetBody`. Call results are ignored; the local cached flags and counters update. |
| RPC-01869–RPC-01873, RPC-01878–RPC-01879 | `mailSvc.py:359-380,392-446` sends list or label IDs; folder actions at `evemail.py:1138-1161` call mark-read/trash-by-label/list, then reload. The client applies local trash/read state and consumes no success payload. |
| RPC-01874–RPC-01877 | `mailSvc.py:291-311,349-359,447-456` sends message IDs or no arguments for all-message actions. Selected-message actions originate at `evemail.py:1057-1072,2541`; all-mail actions originate at `evemail.py:1114-1131`. The reply is ignored. |
| RPC-01882–RPC-01883 | `mailSvc.py:773-807` sends selected IDs plus a label mask; UI callers include `evemail.py:1051,1398,1701`. The service updates local label masks and counters; it ignores the RPC reply. |
| RPC-01884 | `mailSvc.py:133-234` calls `SyncMail(firstID,lastID)`, merges `newMail` and `oldMail`, and consumes every `mailStatus` row's `messageID`, `statusMask`, and `labelMask`. The UI subsequently reads cached headers and bodies through the mail service. |
| RPC-01885–RPC-01886 | `mailingListsSvc.py:117-119,190-192` forwards `ClearEntityAccess` and `ClearWelcomeMail`; the config UI invokes them at `evemailingListConfig.py:305,482-498`. Both replies are ignored. |
| RPC-01887–RPC-01888 | `mailingListsSvc.py:66-71` returns the new list ID and uses it as the key in `myMailingLists`; `evemail.py:2904` creates a list. |
| RPC-01889 | `mailingListsSvc.py:89-98` forwards deletion and updates the local joined-list map; `evemail.py:504-507` invokes it. |
| RPC-01890–RPC-01891 | `mailingListsSvc.py:42,54-64` loads the keyed joined-list map and consumes `GetInfo.displayName`; `evemail.py:367,479,1816,2020,2049,2137,2198,2250,2928` consumes joined list rows, and `mailSvc.py:216,664,1037,1139` resolves list display names. |
| RPC-01892 | `mailingListsSvc.py:104-108` consumes the member-ID/access-level map and primes owner names. The config UI reads members at `evemailingListConfig.py:166`. |
| RPC-01893 | `mailingListsSvc.py:140-142` returns settings; `evemailingListConfig.py:377-388,436` reads default access, default member role, and the entity access map. |
| RPC-01894 | `mailingListsSvc.py:181-183` returns welcome-mail rows; `evemailingListConfig.py:482-498` reads title/body and clears saved text. |

Whole-tree Serena searches across `/home/user/eve-logic/eve/client` found no additional direct callers of these manager methods. UI consumers route through `mailSvc.py` and `mailingListsSvc.py`. Mail notifications are consumed at `mailSvc.py:1174-1195`: update events apply read/label state, while trash/restore events refresh those flags.

## EveJS v0.12.9 comparison

`server/src/services/mail/mailMgrService.js:192-568` implements the corresponding mailMgr calls. `server/src/services/mail/mailState.js:1963-2136` changes mailbox status and label masks; `server/src/services/mail/mailNotifications.js:68-93` sends `OnMailUpdatedByExternal([ids,isRead,newLabel])`, `OnMailTrashed([ids])`, and `OnMailRestored([ids])`. Mailing-list manager calls and their read shapes are in `server/src/services/mail/mailingListsMgrService.js:115-283`.

## eve.rs implementation and checks

- `MailService` now advertises the selected missing mailMgr methods, validates IDs/label masks and the 500-message bulk ceiling, then forwards a typed mutation through the Host callback.
- `MailboxMutation.Apply` applies read, trash, and label-mask changes to the selected mailbox rows. Host wiring in `MailRpc.cs` serializes writes with the existing mail-send gate, flushes the mail table before returning, and notifies a different live session with the client-consumed notification names and shapes.
- The existing `MailingListService` routes remain registered in the mail Host composition; the real Host wire regression exercises Create, Delete, ClearEntityAccess, ClearWelcomeMail and the five read methods with the transport's `machoVersion` keyword.
- `MailRpcHostWireTests` also checks SyncMail's populated client fields, all selected mail mutation routes, persisted in-memory status/label transitions, and an accepted 500-ID MarkAsRead burst.
- Local Host.Tests build succeeded with 0 warnings and 0 errors. The focused wire test passed (1 passed, 0 skipped). No PostgreSQL database or graphical client was used.

Remote Actions results will be added after dispatch and the push-triggered gate finish.
