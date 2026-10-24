# Bookmark RPC Host coverage handoff (RPC-00716–RPC-00732)

## Change

Added `AccessGroupBookmarkMgrHostWireTests.Registered_bookmark_methods_reach_the_real_dispatcher_and_return_wire_errors_without_a_database`. It registers production `BookmarkRefusals(AccessGroupBookmarkMgrService)` in `ServiceRegistry`, serializes calls with the Macho packet codec, and dispatches through `PacketDispatcher`. It checks the 17 method names and named refusal packets for missing-character calls, plus `BookmarkNotAvailable` for an unresolved static location. The `NpgsqlDataSource` is only constructed; none of these calls reaches the store. Existing store-backed tests cover persistent state and mutations.

## V24.01 client callers and reply consumers

The client initializes `RemoteSvc('accessGroupBookmarkMgr')` in `eve/client/script/ui/shared/bookmarks/bookmarkSvc.py:40-58`. The callsites for these rows are all in that file:

| Row | Client RPC call | Reply consumer / next transition | EveJS v0.12.9 |
|---|---|---|---|
| RPC-00716 AddFolder | `bookmarkSvc.py:425` | `CreateBookmarkFolder` caches by returned `folderID` and refreshes UI (`425-435`). | `accessGroupBookmarkMgrService.js:176-204`, folder view payload. |
| RPC-00717 AddToKnownFolders | `bookmarkSvc.py:356` | Unpacks folder/bookmarks/subfolders and fills active client caches (`349-371`). | `accessGroupBookmarkMgrService.js:303-322`, same three-part list. |
| RPC-00718 BookmarkStaticLocation | `bookmarkSvc.py:611` | Unpacks eight-part tuple; `NewBookmarkCreated` populates bookmark and location caches and emits `OnBookmarkCreated` (`609-634`). | `accessGroupBookmarkMgrService.js:365-403`, bookmark tuple. |
| RPC-00719 CopyBookmarksToFolderAndSubfolder | `bookmarkSvc.py:701` | Consumes `[dict, message]`, adds copies to location and bookmark caches (`678-711`). | `accessGroupBookmarkMgrService.js:533-568`, same `[dict, null]`. |
| RPC-00720 CreateSubfolder | `bookmarkSvc.py:564` | Caches returned subfolder by ID (`563-568`). | `accessGroupBookmarkMgrService.js:574-592`, subfolder payload. |
| RPC-00721 DeleteBookmarks | `bookmarkSvc.py:663` | Groups bookmark IDs by folder and removes returned IDs from bookmark and location caches (`655-675`). | `accessGroupBookmarkMgrService.js:477-498`, deleted ID list. |
| RPC-00722 DeleteFolder | `bookmarkSvc.py:452` | Removes folder and its cached content (`452-456`). | `accessGroupBookmarkMgrService.js:240-262`, null after deletion. |
| RPC-00723 DeleteSubfolder | `bookmarkSvc.py:588` | Removes returned bookmarks from caches, then removes subfolder (`578-597`). | `accessGroupBookmarkMgrService.js:617-643`. EveJS emits subfolder removal before bookmark removal; eve.rs currently emits bookmark removal first. The client applies both update types sequentially and refreshes after the batch (`bookmarkSvc.py:232-271`); this test-only task leaves the order unchanged. |
| RPC-00724 GetFolderInfo | `bookmarkSvc.py:518,527` | Refreshes cached folder or passes info to the folder window (`518-530`; entry points `bookmarksPanel.py:656`, `addressbookService.py:409`). | `accessGroupBookmarkMgrService.js:264-278`, folder view payload. |
| RPC-00725 GetMyActiveBookmarks | `bookmarkSvc.py:162` | Primes folder/bookmark/location/subfolder caches from three lists and creates a default personal folder when empty (`157-177`). | `accessGroupBookmarkMgrService.js:154-170`, same three lists. |
| RPC-00726 MoveBookmarksToFolderAndSubfolder | `bookmarkSvc.py:690` | Consumes `[rows, message]` and updates cached folder/subfolder IDs (`678-699`). | `accessGroupBookmarkMgrService.js:500-531`, same `[rows, null]`. |
| RPC-00727 RemoveFromKnownFolders | `bookmarkSvc.py:459` | Drops folder and contents (`458-471`); UI entry `bookmarksPanel.py:445`. | `accessGroupBookmarkMgrService.js:324-338`, null after removal. |
| RPC-00728 SearchFoldersWithAdminAccess | `bookmarkSvc.py:714` | Memoizes folder results for the admin-folder menu (`713-715`; caller `bookmarksPanel.py:637-656`). | `accessGroupBookmarkMgrService.js:280-297`, folder payload list. |
| RPC-00729 UpdateBookmark | `bookmarkSvc.py:645` | Updates cached bookmark fields and refreshes UI; missing local ID is rejected first (`635-653`). | `accessGroupBookmarkMgrService.js:443-475`, null. |
| RPC-00730 UpdateFolder | `bookmarkSvc.py:440` | Applies returned access level; drops no-longer-visible content or updates cached name/description/groups (`437-450`). | `accessGroupBookmarkMgrService.js:206-238`, access level. |
| RPC-00731 UpdateKnownFolderState | `bookmarkSvc.py:482` | Unpacks folder/bookmarks/subfolders and fills or clears caches by active state (`478-508`). | `accessGroupBookmarkMgrService.js:340-359`, same three-part list. |
| RPC-00732 UpdateSubfolder | `bookmarkSvc.py:571` | True updates cached name; false raises `BookmarkSubfolderNoLongerThere` (`570-577`). | `accessGroupBookmarkMgrService.js:594-615`, boolean. |

Other UI entry points: `linkedBookmarkFolderWindow.py:172` adds a known folder; `bookmarkSubfolderWindow.py:121,123` creates/renames one; `bookmarksPanel.py:445,486,637` removes a known folder, deletes a subfolder, and searches admin folders; `addressbookService.py:409,464` looks up a folder name and deletes bookmarks. Shared access notifications are consumed by `bookmarkSvc.py:332-343`. Static targets use `BookmarkStaticLocation`; other places use the space-bound park (`bookmarkSvc.py:609-620`).

## Host, persistence, and checks

- `dotnet/src/Eve.Host/Composition/BookmarkComposition.cs:12-16` migrates and owns `BookmarkStore`; `dotnet/src/Eve.Host/Rpc/CharacterServiceList.cs:81` registers the production service through `BookmarkRefusals`.
- `dotnet/src/Eve.Services.Core/AccessGroupBookmarkMgrService.cs:18-286` implements the handlers, payloads, mutations, and response-deferred notifications; `393-403` maps store refusals to Macho errors.
- `dotnet/src/Eve.Bookmarks/BookmarkStore.cs` owns transactional persistence. Store-backed coverage is in `AccessGroupBookmarkMgrTests.cs`, `SharedFolderTests.cs`, and `SharedFolderNotificationTests.cs`.
- This Host wire regression covers dispatch and refusal branches only. It does not replace database-backed mutation, persistence, notification, restart, or graphical-client checks.

## Verification and publication

- Local `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1`: passed, 0 warnings and 0 errors.
- Local focused test `Registered_bookmark_methods_reach_the_real_dispatcher_and_return_wire_errors_without_a_database`: passed (1 passed, 0 skipped); it does not connect to PostgreSQL.
- Published change: commit `1bf8b206` (`Test bookmark RPC Host wire coverage`), merged with origin/main and pushed as `84ad0c1b`.
- GitHub Actions `agent-test.yml` run `40085367825` passed for `84ad0c1ba363` (no failed tests or jobs; 0 skipped).
- Push-triggered `ci.yml` run `40085361438` for `84ad0c1b` was still queued when this handoff update was prepared; record its final result after completion.
- No graphical-client session was run.
