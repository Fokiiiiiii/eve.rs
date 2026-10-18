# Shared bookmark folders and owner groups: design and state

Status: the store, rules, service, revocation notifications and corporation membership headless walk for owner groups (access lists) and
shared bookmark folders are built and tested on the PostgreSQL lane. The graphical-client walk and an alliance membership live walk remain
open. The compatibility choices are in
`docs/reference/compat/DEVIATIONS.md` ("Owner groups: ownerGroupManager" and "Bookmarks: shared (access group) folders, built").

## Sources (in priority order)

1. EVE University wiki: "Access Lists" (https://wiki.eveuniversity.org/Access_Lists) for the list roles, member kinds, precedence and
   logging; "Bookmarks" (https://wiki.eveuniversity.org/Bookmarks) for the folder access levels, limits and sub-folder rules.
2. V24.01 client (eve-logic, commit 1dbb571): `ownergroups/ownergroupConst.py` (membership types, change codes),
   `eve/client/script/ui/structure/accessGroups/accessGroupsController.py` (the owner group remote calls and `OnOwnerGroupsUpdated`),
   `eve/client/script/ui/shared/bookmarks/bookmarkSvc.py` (the `accessGroupBookmarkMgr` calls and the shared notifications),
   `eve/common/script/mgt/bookmarkConst.py`, `eve/common/lib/appConst.py` (access levels, limits).
3. EveJS v0.12.9 as a behaviour reference only: `services/character/ownerGroupManagerService.js`,
   `services/character/accessGroupBookmarkMgrService.js`, `services/bookmark/bookmarkRuntimeAccess.js`,
   `services/bookmark/bookmarkNotifications.js`. Its stubs (`GetGroupLogs`, `UpdateMemberships`) were not copied.

## Owner groups (`ownerGroupManager`)

- Store: `dotnet/src/Eve.AccessGroups` (schema `access_groups`, or `EVE_ACCESS_GROUPS_SCHEMA`). Tables: `groups`, `members` (one
  entry per member id: kind everyone/character/corporation/alliance, membership type none/member/manager/admin/excluded), `logs`.
- Rules: `AccessRules` (pure). The role is the most specific entry: character, then corporation, then alliance, then everyone. Blocked gives
  no access. Admin edits, renames and deletes and manages every entry; a manager adds, removes and changes member or blocked entries only;
  corporation, alliance and everyone entries are member or blocked only. Members see no list (only admins and managers read the lists).
- Corporation built-in list: id = corporation id (no sequence value; the sequence starts above all character, corporation and alliance
  ids), created on first read by a member, fixed (no change), usable by the corporation's members.
- Service: `OwnerGroupManagerService` (Eve.Services.Core). Each change runs in one transaction under the group's advisory lock; the change
  codes go to the audience (characters, corporations and alliances the entries name, and everyone for a public entry) after the response as
  `OnOwnerGroupsUpdated`. Member add, type change and removal also send code 8 so clients refresh their lists.

## Shared bookmark folders (`accessGroupBookmarkMgr`)

- Schema (`bookmarks`, migration 002): `folders` gains the four list ids (admin, manage, use, view); `connections(folder_id, character_id,
  is_active)` holds each character's known and active state for a shared folder. Personal folders keep their columns.
- Access level (`FolderAccess.LevelOf`): personal owner: personal (1); shared: the highest of the four lists the character holds as member,
  manager or admin (view 2, use 3, manage 4, admin 5), else none. `CanView`, `CanUse`, `CanManage`, `IsAdmin` give the rights.
- The lists the character holds come from the owner groups (`AccessGroupStore.MemberRolesAsync`) through a delegate the Host passes
  (`MemberLists`), so the bookmark store does not depend on the owner group store.
- Writes: create (admin list required; each list must be assignable by the creator), rename and lists (admin), delete (admin), subfolders
  (use creates; own edit and delete; manage edits all), bookmarks (use creates; own edit and delete; manage edits and deletes all; move needs
  use on the target; copy needs view on the source and use on the target). Capacity 500 per shared folder; 3 active shared folders; 30 known.
- Notifications: `OnSharedBookmarksFolderUpdated` (one update per notification, `[[folderID], type, args]`) to the connected characters that
  have the folder active and still have a view level, after the response, not to the actor; `OnSharedBookmarksFolderDeleted` to the
  characters that had it active at deletion. After an owner group change, `OnSharedBookmarksFoldersAccessLost` and
  `OnSharedBookmarksFoldersAccessChanged` go to the connected characters whose folders use the group (`SharedFolderNotifier`). Expired
  bookmarks are removed by a one-minute timer and by reads, and the viewers hear `bookmarksRemoved`.

- EveJS difference (v0.12.9, `828823a75f211fdecb945b087a26c225f111491f`): `bookmarkRuntimeAccess.js` derives group subjects from the
  character, corporation and alliance IDs (`buildMembershipSubjects`), while `bookmarkNotifications.js` has no affiliation-change notice
  producer. eve.rs checks affiliation changes in the character store each minute and sends the access notices consumed by V24.01
  `bookmarkSvc.py:332,343`. The corporation membership live walk covers this eve.rs behavior.

## Tests

- `dotnet/tests/Eve.AccessGroups.Tests`: the rules (no database), the store on PostgreSQL (roles, precedence, corporation built-in list,
  logs, audience, restart, concurrent adds), the service (RPC shapes, refusals, notifications, the client's dict payload).
- `dotnet/tests/Eve.Bookmarks.Tests`: personal behaviour (unchanged) plus `SharedFolderTests` (levels, admin-only writes, connections and
  limits, losing a list, the user and manager edit rules, sub-folder refusal, copy, the 500 limit, deletion, restart) and
  `SharedFolderNotificationTests` (who hears a change, when a list stops giving a view, deletion).

- Verification (2026-10-18): the corporation membership live walk passed (1 test); `SharedFolderNotificationTests` passed (5 tests);
  `OwnerGroupManagerServiceTests` passed (6 tests). The graphical client and alliance membership changes were not checked.

## Open

- The upkeep timer checks corporation and alliance changes every minute from the character store. The headless live walk covers leaving and
  rejoining a corporation; an alliance membership live walk remains open.
- The live walks in `tools/headless-client/tests/test_walk_shared_bookmarks.py` cover list revocation and corporation membership loss and recovery. The list-revocation walk was run through `tools/mitm`; the corporation-membership walk uses the eve.rs live server. Captures are kept outside the repository.
