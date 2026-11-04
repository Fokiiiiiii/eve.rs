# Owner group read-only summaries (cx20)

## Implementation

- `GetGroup` and `GetGroupsMany` use the read-only `AccessGroupStore.GroupSummaryAsync` projection. It returns the caller's effective role and only admin IDs needed by the client summary; it does not load unrelated member rows or logs.
- `GetMembers`, `GetMembersForMultipleGroups`, `GetGroupLogs`, and every write continue through their existing role checks.
- This intentionally differs from EveJS 261028.1: `Handle_GetGroup` calls `getGroupForCharacter`, which filters by membership (`ownerGroupManagerService.js:190-198`, `bookmarkRuntimeState.js:850-858`). V24.01 opens id-only links through `GetGroup` and dereferences summary fields (`ownergroups/client/link.py:24-58`, `accessGroupsController.py:172-199`, `groupInfoWnd.py:34-57`). The structure settings window fetches `GetGroupsMany` before filtering the `SaveProfile(toSave)` payload (`structureSettingsWnd.py:83-103`). UniWiki's Access Lists page says a published link shows the name, description, and admins: https://wiki.eveuniversity.org/Access_Lists.

## Tests

- `HostServerTests.Owner_group_summary_RPCs_are_readable_without_management_access` goes through the registered `ownerGroupManager` Host RPC route and asserts exact Py keyval/list fields, manager/member/unrelated-reader roles, missing-group results, member/log denial, all six group-write denials, and unchanged state after denied writes.
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --no-restore` passed with 0 warnings and 0 errors.
- On `eve_p20` through `tools/dev/db-lock.sh`, the focused Host RPC test passed (1/1); `OwnerGroupManagerServiceTests` passed (7/7); `AccessGroupStoreTests` passed (14/14). All reported zero skips.
- The wider `HostServerTests` class completed with 384/407 passing and 23 failures in unrelated fixtures: missing `sde.types`, `sde.npcStations`, or `sde.dogmaAttributes`, unavailable tournament scheduler/session wiring, and one missing structure fixture id. No skips.
- Before push, Actions CI run 1488 on `b300de8b6a85` completed successfully with no failed jobs. Post-push Agent test and CI results remain pending.

## Remaining check

- No graphical V24.01 client session has been performed for this change.
