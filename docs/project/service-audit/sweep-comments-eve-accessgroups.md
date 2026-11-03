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

## Post-push CI status

- Commit `1b70f525e3f8` was pushed to `main`. The selected `Eve.AccessGroups.Tests` Actions run (`41642172186`) passed.
- The push-triggered CI run (`41642142196`) failed in `Eve.Host.Tests.RpcFuzzTests.Registered_rpcs_refuse_seeded_hostile_arguments_through_dispatcher` while dropping scratch schemas; Npgsql reported a read timeout. Host test code was outside the AccessGroups change at the time; the follow-up is documented below.
- Reproduced the test locally on `eve_p10` after loading the pinned SDE build `3396210` and mission tables from build `3405148`; the pinned archive does not include `missions.jsonl`. It passed: 1 passed, 0 skipped. The database lock wrapper was used.
- A separate Actions run of that exact Host test (`41647928146`) passed. The full push-triggered CI run also logged a skip for `Eve.Host.Tests.TournamentMatchPersistenceTests.Match_creation_uses_real_scene_is_idempotent_and_last_referee_resets_it`; this is outside the slice.
- The AccessGroups sweep changed no Host runtime or test files. The follow-up for its red Host shard is recorded below.

## CI run 1472 Host RPC fuzz follow-up

- Run `1472` at `1b70f525e3f8` failed in `Eve.Host.Tests.RpcFuzzTests.Registered_rpcs_refuse_seeded_hostile_arguments_through_dispatcher` while dropping scratch schemas (`RpcFuzzTests.cs:176` before this fix). Npgsql reported `Exception while reading from stream` and `Timeout during reading attempt`; no dispatch assertion was reported as failing.
- The fuzz harness changes process-wide schema and Host settings (`RpcFuzzTests.cs:59-63`). `EventLogHostTests` also changes `EVE_PG_URL`, `EVE_XMPP_ENABLED`, and `EVE_SIM` outside the `host-composition` collection (`EventLogHostTests.cs:8,13,18`). The fuzz test now uses its own nonparallel collection (`RpcFuzzTests.cs:13`), restores those settings after Host shutdown and before DDL teardown (`:182-184`), and gives each scratch schema drop a 120-second command timeout (`:193`). The RPC dispatch, packet-count, and persistent-data assertions are unchanged.
- The whole V24.01 client search found the packet contracts at `home-user-eve-logic/carbon/common/script/net/machoNetPacket.py:270-299`: `ErrorResponse(originalCommand, code, payload)` and `CallRsp(payload)`. `home-user-eve-logic/carbon/common/script/net/machoNet.py:3658-3668` handles an error response. This cleanup fix changes no client wire shape.
- After the change, the Host test project build passed with 0 warnings and 0 errors. On `eve_p10`, `RpcFuzzTests` passed 2/2 in 20 seconds; together with `EventLogHostTests` and `ServiceNotificationHostWireTests`, the selected tests passed 4/4 in 21 seconds, all through `tools/dev/db-lock.sh`. One earlier post-change isolated run reached the 120-second cleanup timeout; subsequent standalone and neighbor runs passed. PostgreSQL showed no active blocker after that timeout.
- The same CI shard was also run locally to check load: 177 passed, 5 failed, and 3 skipped. The five failures were outside this fix: `AbyssalHostFlowTests.Rig.Create` rejects `eve_p10` because its allowlist omits that lane (`AbyssalHostFlowTests.cs:1113`). The RPC fuzz case passed in that shard. These totals are not a clean shard pass.
- Push-triggered CI verification is pending.
