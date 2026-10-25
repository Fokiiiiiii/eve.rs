# Handoff: userSvc RPC-02966–02999 Host coverage

## Outcome

These rows already had real Host registrations/providers or are a client-side analyzer false positive. The focused Host integration test added in this change exercises the registered `userSvc` through `CharacterHostRig`, checks replies and redeem state transitions, and verifies `UserLogOffCharacter`. `activateMultiTraining.py:154–170` sorts `(trainingID, trainingExpiry)` rows, displays expiries and binds each slot action to its training ID. The redeem claim result now carries the complete EveJS-compatible response shape. Its `isk_balance` comes from the character's persisted wallet.

The rows `RPC-02998` and `RPC-02999` do not call `userSvc.get`: `redeemsvc.py:127–129` calls Python `dict.get` on the `ClaimRedeemTokens` result for optional `itemsCreated` and `stationID`. Whole-tree search found no `RemoteSvc('userSvc').get(...)` call. The test checks those actual result keys; no route is added for the local dictionary method.

## V24.01 caller and consumer evidence, with EveJS comparison

| Rows / method | Whole-client caller and reply consumer | EveJS v0.12.9 behavior |
| --- | --- | --- |
| 02966, 02967 `CastUpvote` | `eve/client/script/ui/shared/systemMenu/customersatisficationcont.py:34–40` calls it and ignores the reply; sets `hasVoted` locally. | `userService.js:1104–1107` returns True; no persistence. Host returns True as recorded by GATE-C. |
| 02968, 02969 `ClaimRedeemTokens` | `eve/client/script/ui/services/redeemsvc.py:98–133` consumes `errors`, `itemsCreated`, and optional `stationID`; `eve/client/script/ui/login/charSelection/characterSlots.py:660–662` reads `isk_balance`. | `userService.js:823–1039` claims tokens; `buildRedeemResult` at 696–722 returns errors, items, activated licenses, ISK reward/balance, skillpoints, station and character IDs. |
| 02973 `GetMultiCharactersTrainingSlots` | `eve/client/script/ui/services/skillQueueSvc.py:847–851` optionally invalidates then reads slots; `IsAllCharacterTrainingSlotsUsed` consumes the mapping via `.items()` and length at `skillQueueSvc.py:858–860`. | `userService.js:752–763` returns the account's character-to-slot mapping. |
| 02974, 02975 `GetRedeemTokens` | `redeemsvc.py:50–64` caches the response; consumers include `oldRedeemPanel.py:346`, `redeemItemsContainer.py:310–311`, and `invControllers.py:1188–1191`. | `userService.js:735–750` returns pending token rows. |
| 02978, 02979 `GetUpdatedMultiCharacterTraining` | `eve/client/script/ui/shared/activateMultiTraining.py:137–138` reloads; the following consumer sorts the result and iterates `(trainingID, trainingExpiry)` pairs to update slots, or marks the state inactive when empty. | `userService.js:765–768` returns training tuples. |
| 02982, 02983 `GetUserToken` | `eve/common/script/util/urlUtil.py:16–22` inserts the returned token into a URL query. | `userService.js:1149–1153` returns a token string. |
| 02984 `InvalidateMultiCharacterTrainingCache` | `skillQueueSvc.py:847–851` calls it only on forced refresh, ignores its reply, then fetches the slot mapping. | `userService.js:818–821` returns True. eve.rs has no matching server cache; the client performs the next read directly. |
| 02987, 02988 `ReportBot` | `eve/client/script/ui/shared/info/infoContentBase.py:1684–1691` blocks self-report and ignores the reply before showing confirmation. | `userService.js:1115–1120` returns True; no report is stored, matching GATE-C. |
| 02989, 02990 `ReportISKSpammer` | `eve/client/script/ui/services/menuSvcExtras/devFunctions.py:54–70` checks self/report eligibility, updates local chat state, then ignores the reply. A second whole-tree caller is `eve/client/script/chat/client/chat_service.py:203`. | `userService.js:1122–1127` returns True without storing a report, matching GATE-C. |
| 02993, 02994 `ReverseRedeem` | `redeemsvc.py:66–75` refreshes the queue in a finally path; `menusvc.py:375–376` offers the action based on `OfferReverseRedeem`. | `userService.js:1078–1081` updates pending token state and returns True. |
| 02995, 02996 `TrashRedeemTokens` | `redeemsvc.py:156–159` invokes it and refreshes the queue. | `userService.js:1083–1095` changes token state and returns True. |
| 02997 `UserLogOffCharacter` | `eve/client/script/parklife/gameui.py:498–501` supplies the reply to session-change flow; the bool selects return to character selection. | `userService.js:1155–1176` disconnects the character and returns True. |
| 02998, 02999 `userSvc.get` | No remote caller exists. `redeemsvc.py:127–129` invokes local Python `dict.get`; optional response fields default locally. | No `userService.get` RPC exists; claim reply construction is in the redeem path above. |

## Host path and checks

The Host's registered `userSvc` is composed by `dotnet/src/Eve.Host/Rpc/RewardRpc.cs` and `HostComposition`; its redeem callbacks are `PersistenceNewEdenStoreRuntime`. Claim persists consumed tokens and created items transactionally; reverse/trash mutate persisted pending-token state. `UserSvcHostWireTests.Registered_user_service_replies_match_the_v24_consumers` checks the real registered service, response shapes, rowset, token transitions, created inventory item, reports, URL token and logoff reply.

`docs/reference/compat/DEVIATIONS.md` now marks the older votes/reports False decision superseded by owner decision GATE-C, which matches the current code and EveJS.

Local compile check: `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false -m:1` succeeded (0 errors, 3 MSBuild file-lock retry warnings from concurrent builds). No local PostgreSQL test was run. GitHub Actions focused test and push-triggered CI results will be recorded after completion.
