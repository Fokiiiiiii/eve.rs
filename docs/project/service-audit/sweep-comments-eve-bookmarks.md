# Bookmark slice comment and dead-code sweep

Date: 2026-11-07

## Changes

- Removed the unused private `BookmarkSelect` constant from `BookmarkStore.Helpers.cs`. In the indexed main checkout, CBM found the field at line 10; its inbound trace had zero callers, and the file-scoped literal search found only the declaration. The neighboring `BookmarkColumns` remains used by bookmark queries.
- Corrected the malformed summaries for `DeleteFolderAsync` and `UpdateBookmarkAsync`; clarified the expiry and active-viewer return values; corrected lock summaries; removed the empty XML summary above `FolderAccess.LevelOf`.
- No runtime logic, wire shape, public API, or tests changed.

## V24.01 client evidence

Whole-client searches found one call to `GetMyActiveBookmarks`: `eve/client/script/ui/shared/bookmarks/bookmarkSvc.py:162`. `ACLPrimeBookmarks` unpacks folders, bookmarks, and subfolders, fills the caches, and creates a default folder only when its folder cache is empty (`bookmarkSvc.py:162-175`).

The client handles `bookmarksRemoved` as a list of bookmark IDs and removes those IDs from its cache (`eve/common/script/mgt/bookmarkConst.py:9`, `bookmarkSvc.py:231-250`). The server's expiry notifier groups store results by folder before sending that client payload; no notification contract changed.

The only UI caller of `DeleteBookmarkFolder` is `eve/client/script/ui/shared/neocom/addressBook/bookmarksPanel.py:440`. The client calls remote `DeleteFolder` before removing the folder and contents from its local cache (`bookmarkSvc.py:451-455`); the deletion notification handler also removes the folder and content and refreshes the UI (`bookmarkSvc.py:321-328`).

The only UI caller of `UpdateACLBookmark` is `eve/client/script/ui/shared/bookmarks/bookmarkLocationWindow.py:230`. The client calls remote `UpdateBookmark` and then updates its cached folder, memo, note, subfolder, and expiry (`bookmarkSvc.py:641-653`). These call and cache paths are unchanged.

## EveJS reference check

Current EveJS 261028.1 deletes shared folders and notifies recipients after successful deletion (`server/src/services/character/accessGroupBookmarkMgrService.js:254-276`); its bookmark update handler validates and publishes updates (`:459-491`), and runtime state checks editor rights and destination use (`src/server/src/services/bookmark/bookmarkRuntimeState.js:1301-1339`). These paths support the corrected explanations; this sweep changes no behavior.

## Verification

- `Eve.Bookmarks.csproj`: build succeeded, 0 warnings and 0 errors.
- `Eve.Bookmarks.Tests.csproj`: build succeeded, 0 warnings and 0 errors.
- Focused database-backed `BookmarkStoreTests` on `eve_p4`: 9 passed, 0 failed, 0 skipped.
- Full `Eve.Bookmarks.Tests` slice on `eve_p4`: 34 passed, 0 failed, 0 skipped.
- Before push, the newest completed main CI run was green (`186c2335eb42`). The graphical V24.01 client was not exercised; this change only alters comments and removes a private constant with no references.
