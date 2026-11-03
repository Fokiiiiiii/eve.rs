# Eve.AccessGroups Optimization Sweep

## Scope and changes

- `AccessGroupStore.VisibleGroupsAsync` and `MemberRolesAsync` now read matching groups and all their member rows in one ordered `LEFT JOIN` query. The existing corporation-built-in group insertion is unchanged. Group and member ordering stays `group_id, member_id`.
- `AccessRules.RoleOf` now scans the member rows once while retaining the character, corporation, alliance, everyone precedence.
- `AddMembersAsync`, `DeleteMembersAsync`, and `ChangeMembershipTypesAsync` use hash lookups for submitted IDs. Delete compacts the members list once; type changes update the existing list in one pass. Existing refusal, duplicate filtering, mutation, log, and notification inputs are unchanged.

## Cost evidence

For G matching groups, the two broad read methods previously executed one touching-groups query followed by G member queries. They now execute one joined read query (plus the unchanged built-in-group insert when the character has a corporation). This removes G database round trips.

For M entries in a group and K submitted IDs, membership checks previously scanned the members list for each ID, making lookup work O(M×K). Hash lookup and one final list pass make it expected O(M+K). Role resolution previously made up to four list traversals and now makes at most one. Per-member database writes and log ordering remain as before.

No wall-clock benchmark was added; the query count and complexity changes are directly visible in the implementation. The database regression checks cover multiple matching groups, built-in groups, roles, admins, refusal order, duplicate IDs, and remaining member state.

## Client and reference evidence

- V24.01 `eve/client/script/ui/structure/accessGroups/accessGroupsController.py:101-109` calls `ownerGroupManager.GetMyGroups` and indexes the returned records by `groupID`. `accessGroupListCont.py:42-58` consumes `groupID`, `name`, `creatorID`, and `membershipType`; `groupCont.py:70-73` consumes `creatorID`, `name`, `membershipType`, and `description`; `groupInfoWnd.py:34-57` consumes `creatorID`, `name`, `description`, and `admins`. The server adapter remains `dotnet/src/Eve.Services.Core/OwnerGroupManagerService.cs:18-23, 187-193`; this change leaves its keyval fields and list projection intact.
- The controller turns `GetMembers` records into a `memberID -> membershipType` map at `eve/client/script/ui/structure/accessGroups/accessGroupsController.py:43-50`, and consumes `GetMembersForMultipleGroups` as group-keyed member lists at lines 74-96. `ownerGroupUtils.py:66-68` builds the same map for each response, and `accessGroupMemberCont.py:78-99` and `accessMemberEntry.py:56-67` filter, sort, and render membership types. The joined store read preserves the per-group member IDs and types; it does not change either wire shape.
- Multi-select addition gathers owner IDs at `eve/client/script/ui/structure/accessGroups/addCont.py:97-106`; `eve/client/script/ui/structure/accessGroups/groupCont.py:115-117` creates the membership dict and invokes the controller. `eve/client/script/ui/structure/accessGroups/accessGroupsController.py:206-225` filters already-cached IDs, calls `AddMembers`, and consumes the returned failures. Update and removal callers are at lines 234-266. The server still caps raw bulk requests at 500 in `dotnet/src/Eve.Services.Core/OwnerGroupManagerService.cs:16, 95-97`; mutation result and failure ordering remain unchanged.
- `accessGroupsController.py:317-342` consumes `OnOwnerGroupsUpdated` change codes to invalidate the matching group/member caches and refresh the visible list. Change codes and notification timing were not changed.
- `AccessGroupComposition.cs:27-29` also feeds `MemberRolesAsync` into bookmark `AccessLists`; `BookmarkAccess.cs` uses those list roles to resolve folder access. The notifier still recalculates affected levels after a mutation and sends `OnSharedBookmarksFoldersAccessLost` or `OnSharedBookmarksFoldersAccessChanged`. V24.01 consumes those notifications in `eve/client/script/ui/shared/bookmarks/bookmarkSvc.py:332-348`, removing lost folders or updating each cached access level. The optimized role lookup preserves the same role map; `OwnerGroupManagerServiceTests.Removing_a_member_whose_list_gave_a_shared_folder_its_view_sends_the_loss_after_the_response` remains the end-to-end regression.
- EveJS 261028.1 uses `src/server/src/services/character/accessGroupStore.js:132-152` for group visibility and `:185-210` for member addition; `ownerGroupManagerService.js:353-391` parses and validates the member dictionary before invoking its store. Its storage algorithm is not copied. eve.rs keeps its existing documented refusal keys and rules in `docs/reference/compat/DEVIATIONS.md` and existing tests.
- The server's `GetGroupsMany` and `GetMembersForMultipleGroups` adapters still loop over requested IDs and call individual store reads (`dotnet/src/Eve.Services.Core/OwnerGroupManagerService.cs:30-37, 45-56`). Those adapters are outside this slice's allowed source files and were not changed.

## Local verification

- `dotnet build dotnet/src/Eve.AccessGroups/Eve.AccessGroups.csproj -p:UseSharedCompilation=false`: passed, 0 warnings.
- `dotnet build dotnet/tests/Eve.AccessGroups.Tests/Eve.AccessGroups.Tests.csproj -p:UseSharedCompilation=false`: passed, 0 warnings.
- `Eve.AccessGroups.Tests` on `eve_p3`: 26 passed, 0 failed, 0 skipped.
