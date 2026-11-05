# Online-status privacy and lifecycle notifications — cx7

## Result

`GetOnlineStatus` denies requests from observers without a selected character. Selected characters retain self visibility and reciprocal personal-watchlist visibility, and the existing initial-state CRowset descriptor and contact rows remain unchanged.

Character session transitions now notify only after the session map changes. First entry and final logout/disconnect produce one transition; duplicate entry, replacement of the same character session, and stale or repeated close produce none. Reusing one session for another character emits the old character offline and the new character online. Host delivery is serialized. Eligible active reciprocal observers receive the V24 event over `clientID` with one scalar `PyLong(characterID)` argument.

Character logoff reaches the transition hook after its existing LastLogoffAt persistence and before character attributes are cleared. Connection close remains a fallback for disconnects.

## Client and reference evidence

Whole-tree V24.01 callers and consumers reviewed in `/home/user/eve-logic`:

- `eve/client/script/ui/shared/comtool/onlineStatus.py:25-34,78-83` consumes initial state and logged-on/off events.
- `eve/client/script/ui/shared/userentry.py:173-178` calls `GetOnlineStatus` only with a selected character and for self or a watchlisted target.
- Other `GetOnlineStatus` callers: `eve/client/script/ui/shared/chat/client/member_list_entry.py:119-124`, `eve/client/script/ui/shared/xmppchatclient/xmppchatuserentry2.py:119-124`, `eve/client/script/ui/shared/killRights.py:75-78`, and `eve/client/script/ui/services/addressbookService.py:149-153,809-813,938-943`.
- Other event consumers: `eve/client/script/ui/shared/userentry.py:216-225`, `eve/client/script/ui/shared/chat/client/member_list_entry.py:138-151`, `eve/client/script/ui/shared/xmppchatclient/xmppchatuserentry2.py:138-151`, `eve/client/script/ui/shared/killRights.py:190-205`, `eve/client/script/ui/services/petition.py:1162-1173`, and `/home/user/eve-logic/notifications/client/contactNotificationAdapter.py:10-30`.
- The selected-character logout RPC is called at `eve/client/script/parklife/gameui.py:493-502`.

EVE University [Contacts](https://wiki.eveuniversity.org/Contacts) requires mutual consent for online status. EveJS 261028.1 returns raw presence for observer ID 0 (`/home/user/evejs-261028.1/src/server/src/services/online/onlineStatusService.js:59-67`, `onlineStatusRuntime.js:65-81`), so the denial is an intentional compatibility deviation. EveJS event name, transport and argument are at `onlineStatusRuntime.js:109-138`. An isolated runtime scenario against EveJS confirmed the reciprocal recipient and wire tuple; no full server or client session was involved.

## Implementation path

`CharacterRpc.cs` registers the presence service with selected sessions and the watchlist reader. The Host reader checks the shared `characters` mail cache and then GameStore, matching CharMgr contact updates. `SkillsLoginHook` enters/leaves the character session for selection and `UserLogOffCharacter`; `HostServer.SessionActor.RunAsync` closes it on connection loss. `CharacterSessions` publishes map transitions and `OnlineStatusNotifications` checks both watchlists and the current selected recipient session before delivery.

## Checks

- `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false -m:2`: passed, 0 warnings/errors.
- `OnlineStatusServiceTests` through `tools/dev/db-lock.sh` on `eve_p7`: 27 passed, 0 skipped.
- `CharacterLogoffTests` through `tools/dev/db-lock.sh` on `eve_p7`: 4 passed, 0 skipped.
- Focused Host regressions through `tools/dev/db-lock.sh` on `eve_p7`: 2 passed, 0 skipped.
- The wider `CharacterHostTests` class: 35 passed, 0 skipped (9m44s).
- `eve_p7` contains pinned SDE build 3396210 and the required Host fixture mission tables.
- No graphical-client trace was captured; these checks are headless.

## Files

Runtime: `OnlineStatusService.cs`, `CharacterSessions.cs`, `ClientSession.cs`, `CharacterLogoff.cs`, `SkillsLoginHook.cs`, `IService.cs`, `OnlineStatusNotifications.cs`, and Host composition files. Tests: `OnlineStatusServiceTests.cs` and `CharacterHostTests.cs`. Compatibility record: `docs/reference/compat/DEVIATIONS.md`; progress table: `docs/project/TODO.md`.
