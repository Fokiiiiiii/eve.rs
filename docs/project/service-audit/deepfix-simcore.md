# Deep fix: sim core

Date: 2026-11-02

## Verified sim defect

A public solar-system `How::InSpace` entry with a missing or invalid saved pose could allocate a `Scene` before rejecting the entry. The session is registered only after a successful scene entry, so the rejected request had no leave/forget owner and the empty scene could remain in `Sim::scenes`.

The fix validates the pose after resolving the public system and before scene insertion. This preserves unknown-system error precedence and keeps the existing `SPACE_STATE_REQUIRED` and `BAD_SPACE_STATE` replies.

- `crates/eve-space/src/sim/spawn.rs:719-755`: public-system lookup and pose validation precede the `self.scenes.entry` allocation.
- `crates/eve-space/src/sim/spawn.rs:757-786`: successful entries register the session; the existing unknown-station cleanup remains unchanged.
- `crates/eve-space/src/sim/tests.rs:1804-1846`: the regression covers absent pose and NaN position, exact error replies, an empty scene map and zero ships.
- The focused regression failed before the fix at the empty-scene assertion; after the fix it passed. The adjacent undock and saved-pose restore test also passed.

No simulation protocol shape, gameplay mechanic, notification, or client-visible error changed. No SDE or wiki mechanic data was used because this change does not alter gameplay rules or static values.

## Client and Host path checked

The repeated player flow reviewed is undocking into space and restoring the saved pose at login.

- Whole-client undock flow: `/home/user/eve-logic/eve/client/script/ui/shared/dockedUI/undockingSvc.py:43-80,116-138` prepares/aborts the attempt, calls station or structure handling, and emits `OnUndockingCompleted` only on success. Station behavior is `/home/user/eve-logic/eve/client/script/ui/station/base.py:484-517`; structure behavior is `/home/user/eve-logic/eve/client/script/ui/services/structure/structureDocking.py:25-53`; the view transition consumes the event at `/home/user/eve-logic/eve/client/script/ui/view/dockingHeroNotificationService.py:125-165`.
- Host to sim wiring: `dotnet/src/Eve.Services.Space/SpaceSessions.Ships.cs:28-102` enters the sim after undocking. Login restore passes saved position and direction at `dotnet/src/Eve.Services.Space/SpaceSessions.cs:678-686`. Codec encode/decode requires those fields for `InSpace` at `dotnet/src/Eve.Space/SimCodec.Encode.cs:1068-1080` and `dotnet/src/Eve.Space/SimCodec.cs:385-391`.
- EveJS 261028.1 undock RPC is `/home/user/evejs-261028.1/src/server/src/services/ship/shipService.js:2101-2143`; scene presentation is `/home/user/evejs-261028.1/src/server/src/space/runtime/scene/presentation.js:601-703,712-752`. Its intended RPC replies and undock notifications are unchanged here.

## Host and sim protocol version

No version change was needed. C# sends and checks the version at `dotnet/src/Eve.Space/SimClient.cs:142-149`; the Rust version is 84 at `crates/eve-space/src/link/mod.rs:39`, matching C# `dotnet/src/Eve.Space/SimCodec.cs:30`. Rust rejects mismatches at `crates/eve-sim/src/lib.rs:50-58`. Reconnect, mixed-version deployment and restart behavior were not exercised.

## Red CI gate repair

The preceding main CI run 1408 failed at `Eve.Host.Tests.UserSvcHostWireTests.Registered_user_service_replies_match_the_v24_consumers`. It reproduced locally: the test passed the return value of `CreateRedeemTokenQA` (a redeem-token ID) to `ReverseRedeem` as if it were an inventory item ID.

Whole-client evidence: `/home/user/eve-logic/eve/client/script/ui/services/redeemsvc.py:67-77` passes selected `item.itemID` and refreshes the token queue in a `finally` block. Queue reads and updates are at `redeemsvc.py:51-54,83-97,162-169`. EveJS 261028.1 logs the supplied ID and returns true at `/home/user/evejs-261028.1/src/server/src/services/user/userService.js:1096-1099`. The existing persistence regression `dotnet/tests/Eve.Host.Tests/NewEdenStoreHostTests.cs:132-205` covers a Hangar PLEX being removed, converted to a station-locked redeem token, and not duplicated on retry.

The Host wire fixture now creates SDE PLEX type 44992 in the character's station Hangar, passes that item ID, and checks item removal plus one token in the queue. This retains the response assertion and gives the handler the same input shape as the client. Upstream commit `0e5d4220` also added an invalid-ID refusal assertion to this test.

CI fix commit `14592ed5` was pushed to main. Selected Agent test run 1236 and push-triggered main CI run 1409 both succeeded with no failed jobs. After merging `0e5d4220` and `061c77c3`, the same Host tests passed locally on `eve_p4` (2/2, no skips).

## Checks run after the latest merge

- Rust focus regression and adjacent undock/login restore test: each passed (1/1, no skips).
- `cargo +1.94.1 fmt --all -- --check`, `cargo +1.94.1 check -p eve-space`, and workspace clippy with `-D warnings`: passed.
- Host test project build: passed (0 warnings, 0 errors).
- Host PostgreSQL tests on `eve_p4`: registered user-service wire flow and PLEX persistence/retry flow passed (2/2, no skips).
- Space PostgreSQL tests on `eve_p4`: capsule/death transition and safe-logoff target query passed (2/2, no skips).
- These are headless tests; no graphical-client run was made.

## Remaining work

This finding fixes one scene-load leak; it is not a complete determinism or tick-performance audit. Tick ordering and repeated-run determinism were not measured. `crates/eve-space/src/scene/collision.rs:31` documents an all-pairs collision cost; no populated-system benchmark was run. The 84/84 version match is checked, but compatibility behavior across version mismatch and restart was not tested.
