# Eve.AccessGroups comment and dead-code sweep

Base: `main` at `50e434d4`.

## Changes

- Clarified the `AccessRules` summary and `CanSee` XML comment against the implemented role checks.
- Reworded the store's built-in corporation group comments and the per-group transaction-lock comment.
- Removed the unread local `changed` counter from `ChangeMembershipTypesAsync`; the persisted updates and returned mutation are unchanged.
- Kept the embedded SQL migration unchanged. The current migration is an existing schema artifact.
- Left private helpers intact: the `home-user-eve.rs` CBM index returned no symbols or text matches for this source directory, so there was no graph evidence for deleting private members.
- Serena `replace_content` was unavailable because the session has no language servers; the documented native patch fallback applied the edits.

## Client evidence

- V24.01 `eve/client/script/ui/structure/accessGroups/accessGroupsController.py:102-109` calls the remote `GetMyGroups`, normalizes system labels, and caches returned groups by `groupID`.
- `eve/client/script/ui/structure/accessGroups/accessGroupListCont.py:43-78` consumes `name`, `creatorID`, and `membershipType`, and handles the corporation's built-in group separately.
- Other consumers found by CBM include `eve/client/script/ui/structure/structureSettings/settingSection.py:246-267`, `eve/client/script/ui/shared/bookmarks/bookmarkContainerWindow.py:134-136`, `eve/client/script/ui/inflight/orbitalConfiguration.py:273-283`, and `parameter_types/controllers/matchertypes/parameter_acl.py:54-61`. Their group selection and filtering behavior is unchanged.

## Local verification

- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/src/Eve.AccessGroups/Eve.AccessGroups.csproj -p:UseSharedCompilation=false`: passed, 0 warnings and 0 errors.
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.AccessGroups.Tests/Eve.AccessGroups.Tests.csproj -p:UseSharedCompilation=false`: passed, 0 warnings and 0 errors.
- On database `eve_p10`, the full `Eve.AccessGroups.Tests` project passed: 24 passed, 0 failed, 0 skipped. The run used `tools/dev/db-lock.sh`.

No client-visible behavior, RPC shape, persistence, or notification order changed.