# Deep check: bookmarkMgr / accessGroupBookmarkMgr

## Scope and client names

A whole-tree search of `/home/user/eve-logic` found no `bookmarkMgr` service binding. The client binds `accessGroupBookmarkMgr` at `eve/client/script/ui/shared/bookmarks/bookmarkSvc.py:40`. The direct service has 17 called methods. The callsite and consumed shape for each are listed below.

The client has two additional bound ballpark calls in `bookmarkSvc.py:611-628`: static location bookmarks call the manager's `BookmarkStaticLocation` at line 611; live-space locations call `michelle.GetRemotePark().BookmarkLocation` at line 616; scan results call `michelle.GetRemotePark().BookmarkScanResult` at line 628. The window chooses those flows at `eve/client/script/ui/shared/bookmarks/bookmarkLocationWindow.py:225-228`. Thus the actual bound service name is `beyonce`; there is no client caller of the manager's separate `BookmarkLocation` method. The client unpacks all three routes as the same eight-field bookmark tuple.

## Route and wire handling

- `AccessGroupBookmarkMgrService` is annotated `[Service("accessGroupBookmarkMgr")]` and its methods are registered with `[Method(...)]` in `dotnet/src/Eve.Services.Core/AccessGroupBookmarkMgrService.cs:8-240`. `BookmarkRefusals` keeps the inner service name and dispatch, translating `BookmarkError` to the exact named UserError or an explicit CustomNotify at lines 408-440.
- `CharacterServiceList.cs:82-93` creates the service, wraps it, and adds it to the character services. `HostComposition.cs:261-269` registers those services. The network path is SessionActor decode and dispatch (`HostServer.cs:721-730`), then PacketDispatcher service lookup and method dispatch (`PacketDispatcher.cs:133-180, 210-240`).
- PacketDispatcher removes only the transport `machoVersion` kwarg before creating ServiceCall (`PacketDispatcher.cs:301-309`). ServiceCall.Arg returns PyNone for missing positional arguments and Kwarg reads string or byte-string keys (`ServiceCall.cs:27-45`). The client passes method arguments positionally except `subfolderID`, which it sends by keyword for static, live-location and scan calls (`bookmarkSvc.py:611-628`); static manager parsing also accepts a sixth positional fallback (`AccessGroupBookmarkMgrService.cs:301-328`). Manager and bound-service ID readers accept PyInt and PyLong. Optional access-group and subfolder IDs accept None; the manager's static-bookmark expiry defaults None or an omitted value to mode 0, while bound location/scan calls require the integer expiry sent by their client signatures. Text conversion accepts PyBytes and Unicode, with None becoming an empty string. Bulk IDs arrive as Python sets in delete/move/copy calls (`bookmarkSvc.py:663,690-701`); the Host accepts their `__builtin__.set` ObjectEx wire form as well as list/tuple (`AccessGroupBookmarkMgrService.cs:330-345`).
- The direct Host wire test reaches PacketDispatcher through the registered service for all 17 method names and includes machoVersion on every call; it checks the static subfolderID kwarg at `dotnet/tests/Eve.Host.Tests/AccessGroupBookmarkMgrHostWireTests.cs:15-83`. That test uses an unbound character session, so it verifies routing and refusal encoding, not successful store mutations.
- Beyonce is registered as `beyonce`; its MachoBindObject handles the nested method, and the dispatcher tracks the bound OID. `BookmarkScanResult` already used the live bookmark writer. The missing bound `BookmarkLocation` was added to Beyonce and wired to SpaceSessions.PositionOfAsync and the same BookmarkStore writer in `CharacterServiceList.cs:84-101`. Its real Host socket regression test covers binding, nested keyword arguments, machoVersion, PyBytes/PyWString text, the eight-field reply, and BookmarkNotAvailable for a missing live target at `dotnet/tests/Eve.Host.Tests/BeyonceBookmarkLocationHostWireTests.cs:11-113`.

## Direct client methods

In the table, replies name the outer Python shape (KeyVal means the client-visible object fields); all reads/mutations below use the shared BookmarkStore and not a second state cache. Persistent operations execute through BookmarkStore transactions in `dotnet/src/Eve.Bookmarks/BookmarkStore.cs`. Update notifications are scheduled after the call response and sent to active connected viewers who retain view access by `AccessGroupBookmarkMgrService.Publish` at lines 267-280. The client consumes those updates in `bookmarkSvc.py:231-267`, folder deletion at line 321, and access loss/change at lines 331-350.

| Method | Client call and consumed reply | Host state, errors, and notification | EveJS 261018.1 handler |
|---|---|---|---|
| GetMyActiveBookmarks | `bookmarkSvc.py:162`; three lists: folders, bookmarks, subfolders | `ActiveAsync`; read plus expiry cleanup; schedules removal notice for expired bookmarks | `server/src/services/character/accessGroupBookmarkMgrService.js:168-184` |
| AddToKnownFolders | `:356`; folder KeyVal, bookmark list, subfolder list | `AddKnownFolderAsync`; saves known/active state; client fills its cache; TooManyKnownFolders carries maxKnownFolders (client guard `:355`) | `:316-335` |
| AddFolder | `:425`; folder KeyVal | `AddFolderAsync`; persists; reply populates client cache | `:190-218` |
| UpdateFolder | `:440`; integer access level | `UpdateFolderAsync`; persists; emits folderUpdated after response; stale client folder maps to BookmarkFolderNoLongerThere (`:439`) | `:220-252` |
| DeleteFolder | `:452`; reply ignored | `DeleteFolderAsync`; persists deletion; sends OnSharedBookmarksFolderDeleted to affected active viewers after response | `:254-276` |
| RemoveFromKnownFolders | `:459`; reply ignored | `RemoveKnownFolderAsync`; persists membership removal; caller clears its cache | `:337-351` |
| UpdateKnownFolderState | `:482`; folder KeyVal, bookmark list, subfolder list | `SetKnownFolderActiveAsync`; persists active state; client catches BookmarkFolderNoLongerThere and FolderAccessDenied (`:484`) | `:353-372` |
| GetFolderInfo | `:518,527`; folder KeyVal | `FolderInfoAsync`; read only; client displays FolderAccessDenied at `:550` | `:278-292` |
| CreateSubfolder | `:564`; subfolder KeyVal | `CreateSubfolderAsync`; persists; emits subfolderAdded after response | `:590-608` |
| UpdateSubfolder | `:571`; boolean | `UpdateSubfolderAsync`; persists; emits subfolderUpdated when changed; false becomes client BookmarkSubfolderNoLongerThere (`:575`) | `:610-631` |
| DeleteSubfolder | `:588`; list of removed bookmark IDs | `DeleteSubfolderAsync`; persists; sends subfolderRemoved before bookmarksRemoved; client prechecks CouldNotDeleteBookmarksInSubfolder (`:586`) | `:633-659` |
| BookmarkStaticLocation | `:611`; eight-field tuple `(bookmarkID,itemID,typeID,x,y,z,locationID,expiryDate)` | SDE station/system/constellation/region and persisted structure resolver, then `CreateBookmarkAsync`; persists; emits bookmarksAdded after response; unknown targets return BookmarkNotAvailable | `:378-419` |
| UpdateBookmark | `:645`; reply ignored | `UpdateBookmarkAsync`; persists; emits bookmarksUpdated or bookmarksMoved; missing client cache maps to BookmarkNotAvailable (`:643`) | `:459-491` |
| DeleteBookmarks | `:663`; list of deleted IDs | `DeleteBookmarksAsync`; persists; emits bookmarksRemoved; empty result becomes CouldNotDeleteBookmark (`:667`) | `:493-514` |
| MoveBookmarksToFolderAndSubfolder | `:690`; pair `(rows,message)` | `MoveBookmarksAsync`; persists; emits bookmarksMoved to source and destination | `:516-547` |
| CopyBookmarksToFolderAndSubfolder | `:701`; pair `(newBookmarksByID,message)` | `CopyBookmarksAsync`; persists copies; emits bookmarksAdded to destination | `:549-584` |
| SearchFoldersWithAdminAccess | `:714`; folder KeyVal list | `AdminFoldersAsync`; read only; no notification | `:294-310` |

The host's list / KeyVal / tuple shapes match the client unpacking above. For the manager's creation and mutation errors, BookmarkRefusals forwards the store's code as a UserError, and PacketDispatcher sends wrapped UserErrors unchanged. The only client-specific error arguments are the two explicit folder limits described above. Service-level unexpected exceptions still become the Host's normal correlated RuntimeError; they are not converted to success.

## Bound ballpark variants

| Bound method | Whole-client evidence and result use | Host / EveJS |
|---|---|---|
| BookmarkLocation | `bookmarkSvc.py:611-616`, selected for live items; `bookmarkLocationWindow.py:227`; client unpacks the eight-field tuple | EveJS has bound handler `server/src/services/ship/beyonceService.js:4348-4373`. Host previously had no Beyonce method, so this valid call produced AttributeError. The new handler resolves the target in the caller's live system, uses the shared access-checked bookmark writer, returns the same tuple, and rejects absent/cross-system/non-finite targets with BookmarkNotAvailable. EveJS uses pilot-perceived position during the pilot's own warp; eve.rs reads the authoritative simulation position, as recorded in the compatibility deviation. |
| BookmarkScanResult | `bookmarkSvc.py:622-628`, window call at `bookmarkLocationWindow.py:225`; same tuple | Host's Beyonce handler resolves only a live signature and calls the same writer; callback wiring is in `CharacterServiceList.cs:88-101`. EveJS handler is `server/src/services/ship/beyonceService.js:4375-4402`. |
| BookmarkLocation on accessGroupBookmarkMgr | No client call found; client dynamic route is the bound Beyonce method above | EveJS also exposes a manager handler (`server/src/services/character/accessGroupBookmarkMgrService.js:421-457`). Host keeps an explicit CustomNotify refusal on this unused direct route; there is no client flow to it. |

Static target resolution and bookmark creation continue through the same BookmarkStore; the Store writes folder, bookmark, subfolder, and known-folder state to its PostgreSQL owner. The returned bookmark tuples use the persisted store record, so a later active-list or folder-info call reads the same owner. No service-specific in-memory store was introduced.

## Checks and limits

Executed in this task: Host, Services.Core.Tests, and Bookmarks.Tests compile-checks; focused Host tests (5 passed, 0 skipped) for manager routing, static target resolution, expiry values, and the bound location route; and the focused Services.Core keyword/positional/None contract test (1 passed, 0 skipped). The bound socket test injects the target and save callbacks to verify real Host dispatch and reply/error wire shapes; the save callback in Host composition uses the shared BookmarkStore. The direct-manager route test intentionally cannot mutate state without a character or database.

No PostgreSQL database was used. Database-backed success, state visibility after restart, transaction rollback, concurrent mutation, and persistence retry scenarios therefore were not exercised here. No graphical V24 client session was run. The expiry value choice and exact client/EveJS differences are recorded in `docs/reference/compat/DEVIATIONS.md`.
