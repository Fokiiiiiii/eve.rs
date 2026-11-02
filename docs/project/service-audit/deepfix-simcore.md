# Deep fix handoff

Date: 2026-11-02

## Red CI gate repair

Main CI run 1408 failed at `Eve.Host.Tests.UserSvcHostWireTests.Registered_user_service_replies_match_the_v24_consumers`. The failure reproduced locally on `eve_p4` before this test change: the wire test passed the return value of `CreateRedeemTokenQA` to `ReverseRedeem`, but that return value is a redeem-token ID, not an inventory item ID.

Whole-client evidence: `/home/user/eve-logic/eve/client/script/ui/services/redeemsvc.py:67-77` shows the player selects an inventory item and the client calls `userSvc.ReverseRedeem(item.itemID)`; its finally block refreshes the queue through `OnRedeemingQueueUpdated`. `GetRedeemTokens` and its consumers are at `redeemsvc.py:51-54,83-97,162-169`. EveJS 261028.1 logs the supplied item ID and returns true at `/home/user/evejs-261028.1/src/server/src/services/user/userService.js:1096-1099`. The eve.rs persistence behavior is covered in `dotnet/tests/Eve.Host.Tests/NewEdenStoreHostTests.cs:132-205`: a Hangar PLEX item is removed, a station-locked redeem token is added, and a repeat request does not duplicate it.

The registered Host wire test now creates SDE PLEX type 44992 in the character's station Hangar, passes that item ID, and asserts the item is removed and one redeem token appears. This preserves the wire reply assertion while making the fixture match the client call.

Checks after the fix: the selected Host database tests for `Registered_user_service_replies_match_the_v24_consumers` and `Reverse_redeeming_plex_moves_the_item_to_a_station_locked_token_and_claims_it_once` passed on `eve_p4` (2 passed, 0 skipped); `dotnet build dotnet/tests/Eve.Host.Tests -p:UseSharedCompilation=false` passed. The original test passed no tests and failed before the fixture correction.

## Sim core work in this worktree

The public-scene invalid-pose leak fix and its sim regression are present in the worktree but are not part of the red-gate repair commit. They will be recorded with their client, Host, EveJS and protocol evidence before that separate change is pushed.
