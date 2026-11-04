# Owner group manager self-management handoff (cx7)

## Change

The store permits a Manager to change a Manager entry only when the active character ID equals that entry ID, and only to Member or Excluded. It permits removal of that same entry. Other Manager/Admin entries remain refused with the existing per-member `NotAllowed` result. The Host PostgreSQL RPC test covers self-demotion, self-block, self-removal, four protected-target attempts, self-upgrade refusal, persisted rows, one mutation log each, and audience notifications after the response.

## Client evidence

- V24.01 `eve/client/script/ui/structure/accessGroups/accessGroupsController.py:227-247` presents the self-demotion confirmation and calls `UpdateMembershipTypes`; lines 248-258 show the Manager demotion dialogue.
- `eve/client/script/ui/structure/accessGroups/accessGroupsController.py:260-271` prompts before self-removal and calls `DeleteMembers`.
- `eve/client/script/ui/structure/accessGroups/accessGroupsController.py:317-345` consumes `OnOwnerGroupsUpdated`, including member type changes and removals; `eve/client/script/ui/structure/accessGroups/ownerGroupUtils.py:41-44` consumes per-member failure tuples.
- `eve/client/script/ui/structure/accessGroups/menuProvider.py:15-62` routes the membership and removal actions to those controller methods.
- The server uses `ClientSession.CharacterId` as the acting character. The store compares it with the target member ID; the host test verifies the existing response tuple shape and `clientID` notification payload `{groupID: [3, 8]}` / `{groupID: [4, 8]}`, after `CallRsp`.
- EveJS 261028.1 `src/server/src/services/character/ownerGroupManagerService.js:89-116` makes the same self-ID exception. Wiki mechanics: [EVE University, Access Lists — Roles](https://wiki.eveuniversity.org/Access_Lists).

## Verification

Verified locally on `eve_p7`:

- `Eve.AccessGroups`, `Eve.AccessGroups.Tests`, and `Eve.Host.Tests` compile successfully with zero warnings.
- Focused `AccessRulesTests` case: 1 passed, 0 skipped.
- `AccessRulesTests`, `AccessGroupStoreTests`, and `OwnerGroupManagerServiceTests`: 26 passed, 0 skipped, through `tools/dev/db-lock.sh`.
- Owner-group Host RPC summary and self-management tests: 2 passed, 0 skipped, through `tools/dev/db-lock.sh`.
- The graphical client was not used. The EveJS server package has no test script or owner-group test file; parity was checked against its `requireMemberChanges` helper and RPC handlers.

The newest completed main CI is green: run 1495 for `48178ac5bd42` completed successfully with no failed tests. Four later main commits were merged, including Everyone role/ID handling; after that merge, the focused AccessGroups/Store/Service tests passed 26/26 and the two owner-group Host PostgreSQL tests passed 2/2. The merge resolution is being committed before push; its SHA, selected Actions run, and final CI status will be added once known.
