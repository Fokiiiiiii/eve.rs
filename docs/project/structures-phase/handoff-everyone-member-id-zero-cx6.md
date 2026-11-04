# Preserve Everyone member ID zero through owner-group RPCs (cx6)

## Outcome

AddMembers now passes every integral member key, including 0, to the Host's authoritative kind resolver. DeleteMembers and UpdateMembershipTypes use a member-ID parser that preserves zero and negative IDs; group-ID lists still use the positive-only parser. Non-integral member IDs and duplicate normalized keys in an add dictionary are refused. Negative member IDs produce explicit per-entry failures, and repeated IDs in lists are processed once.

AccessRules.ValidForKind permits Everyone only as Member. Corporation and alliance entries retain Member/Excluded. The Store and registered Host path return NotAllowed for invalid Everyone roles.

## Evidence

- V24.01 defines PUBLIC_MEMBER_ID = 0 (ownergroups/ownergroupConst.py:18). AddPublicMember submits {0: MEMBER} (accessGroupsController.py:203-205); add/update/delete consume (memberID, reason) failures to update or retain their caches (accessGroupsController.py:207-225, 226-247, 260-271). OnOwnerGroupsUpdated invalidates the member/group caches (accessGroupsController.py:317-338). The public row is rendered as PublicUser (accessGroupMemberCont.py:101-103) and is excluded from the role menu (menuProvider.py:69-71).
- Host registration wraps OwnerGroupManagerService in OwnerGroupRefusals (dotnet/src/Eve.Host/Rpc/CharacterServiceList.cs:48-84). Its OwnerKind resolver maps ID 0 to MemberKind.Everyone (dotnet/src/Eve.Host/Composition/AccessGroupComposition.cs:35-43).
- EveJS 261028.1 maps add keys and delete/update arrays through Number (ownerGroupManagerService.js:353-391, 395-409, 417-436); its bookmark runtime also accepts zero for member add/remove (bookmarkRuntimeState.js:758-813). Its access-group store does not validate member roles (accessGroupStore.js:185-210, 244-260). Following UniWiki's higher-priority rule that Everyone can only be Member, eve.rs rejects Everyone/Excluded and the other invalid Everyone roles. This intentional difference is recorded in docs/reference/compat/DEVIATIONS.md.
- Wiki source: EVE University, Access Lists, “Types of members”: https://wiki.eveuniversity.org/Access_Lists

## Checks

- Eve.AccessGroups.Tests built successfully with 0 warnings and 0 errors. The filtered AccessRulesTests, AccessGroupStoreTests, and OwnerGroupManagerServiceTests run passed 26/26, 0 skipped on the final merged tree.
- Eve.Host.Tests built successfully with 0 warnings and 0 errors after the latest main merge. The complete StructureCompositionTests class passed 18/18, 0 skipped on the final merged tree.
- PostgreSQL tests ran under tools/dev/db-lock.sh on eve_p6. Its sde and sde_mission schemas were copied from the populated static-data database with tools/dev/copy-sde-database.py; each test's feature schemas were isolated.
- No graphical V24.01 client session has been performed. Record the selected Actions run and push-triggered CI result here after the push.
