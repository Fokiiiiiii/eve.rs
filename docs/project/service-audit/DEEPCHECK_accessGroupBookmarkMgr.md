# Deep check: accessGroupBookmarkMgr

This service’s full folder, subfolder and bookmark behavior is also documented in DEEPCHECK_bookmarkMgr.md. This file records the service-specific client path and whole-tree call evidence requested for accessGroupBookmarkMgr.

## Client calls and reply consumption

All calls below bind accessGroupBookmarkMgr through eve/client/script/ui/shared/bookmarks/bookmarkSvc.py. The transport-only machoVersion keyword is stripped by PacketDispatcher. IDs are Python integers (the Host reader accepts PyInt and PyLong); bulk delete/move/copy inputs are client sets.

| Method | Client call and consumed result |
|---|---|
| GetMyActiveBookmarks | bookmarkSvc.py:162-175; three lists: folders, bookmarks, subfolders; cached by IDs. |
| AddToKnownFolders | :356-366; folder, bookmarks, subfolders; caller fills its cache. |
| AddFolder | :425-430; folder KeyVal enters cache. |
| UpdateFolder | :440-459; integer accessLevel updates or removes cached access. |
| DeleteFolder | :452-453; reply ignored; subscribed views are refreshed through deletion notice. |
| RemoveFromKnownFolders | :459-460; reply ignored; local cache entry is removed. |
| UpdateKnownFolderState | :482-490; folder, bookmarks and subfolders refresh cache. |
| GetFolderInfo | :518-528; folder KeyVal is displayed or used by callers. |
| CreateSubfolder | :564-566; subfolder KeyVal enters cache. |
| UpdateSubfolder | :571-575; bool controls whether the cached name is kept. |
| DeleteSubfolder | :586-589; deleted bookmark ID list removes cache entries. |
| BookmarkStaticLocation | :611-635; eight-field tuple bookmarkID, itemID, typeID, x, y, z, locationID, expiryDate; client then caches and emits bookmark-created event. |
| UpdateBookmark | :645-646; reply ignored; cache refresh follows. |
| DeleteBookmarks | :663-668; deleted ID list updates cache. |
| MoveBookmarksToFolderAndSubfolder | :690-699; (rows, message) drives cache and dialog. |
| CopyBookmarksToFolderAndSubfolder | :701-711; (newBookmarks, message) drives cache and dialog. |
| SearchFoldersWithAdminAccess | :714-715; folder list used by admin folder picker. |

The whole client also has bound Beyonce methods for live bookmarks, not this service: bookmarkSvc.py:611-628 routes live-space creation and scan results to the ballpark object. The existing combined deep check records those callsites and host behavior.

## Registration, persistence, notifications

CharacterServiceList.cs:81-93 creates AccessGroupBookmarkMgrService, wraps it with BookmarkRefusals, and registers it; PacketDispatcher.cs:519 preserves service dispatch through that wrapper. BookmarkStore owns persistent folder, bookmark, subfolder, membership and active-state writes. SharedFolderNotifier publishes access and content changes after the successful RPC response. The client consumes these at bookmarkSvc.py:231-267, 321 and 331-350.

EveJS v0.12.9 handlers are in server/src/services/character/accessGroupBookmarkMgrService.js:154-659. The Host returns the same outer lists, KeyVals, tuple, bool and integer that the client unpacks. Store errors become their named UserErrors; unexpected failures remain Host errors. BookmarkStaticLocation resolves a static target through SDE and the persisted bookmark store. A live-space BookmarkLocation call belongs to Beyonce and is not an accessGroupBookmarkMgr handler.

## Existing Host evidence and limits

AccessGroupBookmarkMgrHostWireTests.cs:23-83 reaches the actual dispatcher and covers all 17 registered manager method names, machoVersion removal and error encoding. BeyonceBookmarkLocationHostWireTests.cs:11-113 separately exercises the bound live-location route and eight-field reply. SharedFolderNotificationTests.cs:201 onward checks observer notices. The combined DEEPCHECK_bookmarkMgr.md has the detailed error and method-by-method EveJS comparison.

No PostgreSQL database or graphical V24 client was used for this service-specific pass. No code was changed.
