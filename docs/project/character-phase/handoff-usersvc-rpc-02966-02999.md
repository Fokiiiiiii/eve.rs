# Handoff: userSvc RPC-02966–02999 Host coverage

## Outcome

The requested rows are covered by the registered Host services and the Host wire regression `UserSvcHostWireTests.Registered_user_service_replies_match_the_v24_consumers`. It exercises userSvc registration, client-facing response types, redeem token transitions, created inventory, reports, URL token and logoff behavior.

RPC-02998 and RPC-02999 are analyzer false positives: `redeemsvc.py:127–129` calls local Python dictionary `.get` on the `ClaimRedeemTokens` result for optional `itemsCreated` and `stationID`. Whole-tree client search found no remote `userSvc.get` call, and EveJS has no `userService.get` handler. No route is needed.

## V24.01 callers, consumers and EveJS comparison

| Rows / method | Client caller and reply consumer (whole `/home/user/eve-logic` search) | EveJS v0.12.9 |
| --- | --- | --- |
| 02966, 02967 `CastUpvote` | `eve/client/script/ui/shared/systemMenu/customersatisficationcont.py:34–40` calls it; the result is ignored and `hasVoted` is set locally. | `server/src/services/user/userService.js:1104–1107` returns True; no persistence. |
| 02968, 02969 `ClaimRedeemTokens` | `eve/client/script/ui/services/redeemsvc.py:98–133` consumes `errors`, `itemsCreated`, optional `stationID`; `eve/client/script/ui/login/charSelection/characterSlots.py:660–662` reads `isk_balance`. | `userService.js:823–1039`; `buildRedeemResult` at 696–722 includes errors, created items, activated licenses, ISK reward/balance, skillpoints, station and character IDs. |
| 02973 `GetMultiCharactersTrainingSlots` | `eve/client/script/ui/services/skillQueueSvc.py:847–860` refreshes then consumes the character-to-slot mapping via `.items()` and length. | `userService.js:752–763` returns that account mapping. |
| 02974, 02975 `GetRedeemTokens` | `eve/client/script/ui/services/redeemsvc.py:50–64` caches the response; consumers include `oldRedeemPanel.py:346`, `redeemItemsContainer.py:310–311`, and `invControllers.py:1188–1191`. | `userService.js:735–750` returns pending token rows. |
| 02978, 02979 `GetUpdatedMultiCharacterTraining` | `eve/client/script/ui/shared/activateMultiTraining.py:137–170` reloads, sorts training ID/expiry pairs, updates slots, or marks training inactive when empty. | `userService.js:765–768` returns training tuples. |
| 02982, 02983 `GetUserToken` | `eve/common/script/util/urlUtil.py:16–22` inserts the returned token into a URL query. | `userService.js:1149–1153` returns a token string. |
| 02984 `InvalidateMultiCharacterTrainingCache` | `skillQueueSvc.py:847–851` invokes it on forced refresh, ignores its reply, then fetches slot state. | `userService.js:818–821` returns True. eve.rs has no server cache; the next read is direct. |
| 02987, 02988 `ReportBot` | `eve/client/script/ui/shared/info/infoContentBase.py:1684–1691` blocks self-report and ignores the reply before confirmation. | `userService.js:1115–1120` returns True; no report is stored. |
| 02989, 02990 `ReportISKSpammer` | `eve/client/script/ui/services/menuSvcExtras/devFunctions.py:54–70` checks eligibility and ignores the reply; another caller is `eve/client/script/chat/client/chat_service.py:203`. | `userService.js:1122–1127` returns True without storing a report. |
| 02993, 02994 `ReverseRedeem` | `redeemsvc.py:66–75` refreshes the queue in a finally path; `menusvc.py:375–376` offers the action based on `OfferReverseRedeem`. | `userService.js:1078–1081` updates pending token state and returns True. |
| 02995, 02996 `TrashRedeemTokens` | `redeemsvc.py:156–159` invokes it and refreshes the queue; `redeemItemsContainer.py:371` is another consumer. | `userService.js:1083–1095` changes token state and returns True. |
| 02997 `UserLogOffCharacter` | `eve/client/script/parklife/gameui.py:498–501` supplies the reply to session-change flow; the bool selects return to character selection. | `userService.js:1155–1176` disconnects the character and returns True. |
| 02998, 02999 `userSvc.get` | No remote caller exists. `redeemsvc.py:127–129` uses dictionary `.get` for optional claim result fields. | No `userService.get` RPC exists. |

## Host path and verification

The registered Host service is composed through `dotnet/src/Eve.Host/Rpc/RewardRpc.cs` and HostComposition. Redeem persistence uses `PersistenceNewEdenStoreRuntime`; claims consume tokens and create items transactionally, while reverse/trash mutate persisted pending-token state. The regression test uses `CharacterHostRig` and the real registered service.

`docs/reference/compat/DEVIATIONS.md` records the owner decision GATE-C for votes and reports, matching EveJS behavior.

Implementation history: initial focused Actions runs `40183701537` and `40184323340` failed in the Release solution build, and CI run `40183692247` found Host startup errors. Fixes were made to HostComposition and `CharacterServiceList`; commit `13d51670` passed focused run `40186802357`. A later unrelated contract wire failure on SHA `71b96203` was fixed in latest main; focused replay `40187469696` passed on `76280e64`.

On 2026-10-25, the Host test project compile check succeeded with 0 errors (three MSBuild file-lock retry warnings during concurrent builds). No local PostgreSQL test was run, and no graphical client session was used. A stale local Debug test binary failed the earlier PyLong-versus-PyInt packed-row assertion; the current Actions version checks the row's named field instead.

The userSvc focused regression passed on pushed SHA `50cac5ce` in Actions run `40188682496`; the earlier run `40186802357` also passed. Push CI `40188404827` for SHA `9de5c915` exposed a fixture error in the unrelated contract wire test: it tried to delete contract 5002, which the fixture had not created. Latest main commit `e0b238d8` fixes the fixture to delete the contract created earlier in that test. Focused replay `40191499031` passed on SHA `cb9af6cd`. No `ci.yml` run was created for that docs/test-only SHA. Push CI run `40192014272` also found the RPC registry inventory expectation one route behind after the `QuickQuery` registration; the test expectation was updated from 1399 to 1400.

The corrected RPC inventory test passed in Actions run `40194435324` on SHA `14fd06d5`. The push-triggered CI on descendant SHA `54f56fe0` passed all jobs in run `40195015477`.
