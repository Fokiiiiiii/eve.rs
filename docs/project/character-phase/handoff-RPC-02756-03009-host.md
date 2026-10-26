# RPC-02756–03009 Host coverage handoff

## Scope and evidence

The task rows collapse to service/method signatures; duplicate audit IDs use the same client method and wire contract.

| Rows | V24.01 callsites and consumed result |
|---|---|
| 02756 | `eve/client/script/ui/services/sovSvc.py:89-95` calls the remote method. Whole-tree callers consume structure rows at `eve/client/script/ui/shared/info/panels/panelSov.py:28-29`, `PanelSovConstellation.py:40-41`, `eve/client/script/ui/shared/skyhook/uiController.py:124-125`, and `eve/client/script/ui/shared/infoPanels/infoPanelLocationInfo.py:347-348`. |
| 02757–02758 | `sovSvc.py:71-81` calls and caches the claim response; `sovSvc.py:310-311` consumes the result while deriving sovereignty multipliers. |
| 02759 | `sovSvc.py:387-389`; the menu permission gate is `eve/client/script/ui/services/menuSvcExtras/openFunctions.py:173-174`. |
| 02760 | `sovSvc.py:383-385`; the hub controller sends the chosen ACL at `eve/client/script/ui/shared/sov/sovHub/hubController.py:382-384`. |
| 02761 | `sovSvc.py:249-250` calls `remove_from_cache` on the local memoized Python method after a session change. The helper is defined in `brennivin/threadutils.py:321-334`; this is not an RPC and has no server route. |
| 02762–02765 | `eve/client/script/ui/services/standingsvc.py:113-123,281-282` calls the NPC-to-NPC, character, and composition reads. The service filters the NPC rowset by `toID` at :115 and stores character rows for standings calculations at :118-124. The standings panel forwards compositions at `eve/client/script/ui/shared/neocom/charsheet/standingsPanel/standingsPanel.py:109-110`. |
| 02766–02767 | `standingsvc.py:173-178` calls and caches transaction rows; `eve/client/script/ui/shared/neocom/charsheet/standingsPanel/standingsPanel.py:106-107` forwards the result. `resourcewars/client/firstcompletiontracker.py:35` also iterates standing transactions. |
| 02901–02902 | `eve/client/script/ui/inflight/positionalControl.py:112-120` sends (moduleID, effectID, destination) or (moduleID, startPoint, endPoint); the caller ignores the reply and proceeds only after the remote call returns. |
| 02903–02904 | `eve/client/script/ui/services/surveySvc.py:42-48` claims rewards after the dialog; it ignores the reply. |
| 02905–02906 | `surveySvc.py:67-85` stores the returned active survey ID; `eve/client/script/ui/login/charSelection/characterSelection.py:217-218` branches on its truthiness. |
| 02907–02908 | `characterSelection.py:138-139` calls the survey check; `surveySvc.py:75-78` only sends it when local active or grace-period surveys exist. |
| 02939 | `eve/client/script/ui/extras/tourneyUISvc.py:29-31` calls QueryShipList(tourneyID) and indexes `allowedShipList` in the response. |
| 03006 | `eve/client/script/ui/services/corporation/voting.py:262-264` invokes the action and ignores the reply. |
| 03007 | `voting.py:189-195` caches the boolean; `eve/client/script/ui/shared/neocom/corporation/corp_ui_votes.py:793,1024-1025` gates CEO candidacy and proposing. |
| 03008–03009 | `voting.py:151-159` caches the local-corporation boolean and forwards checks for other corporations. Consumers include `eve/client/script/ui/shared/neocom/corporation/corporationWindow.py:307`, `corp_ui_votes.py:880-881`, and `corp_ui_sanctionableactions.py:89-90,157-158`. |

## EveJS comparison

- Sov reads and fuel group handlers: `server/src/services/map/sovMgrService.js:92-124`; structure and claim values are shaped from sovereignty read data.
- Standing methods: `server/src/services/character/standingMgrService.js:194-241`; transactions are projected by `standingRuntime.js:1027-1053`, compositions by :472-512.
- Superweapon activations: `server/src/services/ship/superWeaponMgrService.js:74-125`; valid activations return integer 1 and refusals return null.
- Survey: `server/src/services/support/surveyService.js:50-63`; the active ID comes from configured survey state and claim/check calls return null.
- Tournament: `server/src/services/tournament/tourneyMgrService.js:114-119` returns a dict with an empty `allowedShipList`.
- Votes: `server/src/services/corporation/voteManagerService.js:213-273`; authority and sanctions live in `voteRuntimeState.js:753-912`.

## eve.rs status

- Sov methods are implemented in `dotnet/src/Eve.Services.Core/SovFwService.cs` and production callbacks use sovereignty persistence read/write operations. `SovMgrHostWireTests.cs` exercises registered structure/claim/fuel group reads and mutation, including absent state. Local focused result: 1 passed, 0 failed, 0 skipped.
- RPC-02761 is client-local cache invalidation and has no server method.
- `TourneyMgrService.QueryShipList` now falls back to a dict containing `allowedShipList: []`, matching EveJS. `TourneyMgrHostWireTests.cs` uses standard CoreServices registration and Host dispatch to check the field. Local focused result: 1 passed, 0 failed, 0 skipped.
- `SurveyHostWireTests.cs` checks active ID, None results for checks/claims, and callback event sequence through Host. `SuperWeaponHostWireTests.cs` checks both calls and the integer success result when registered activation callbacks accept.
- Production Host composition lacks superweapon activation callbacks; the route remains fail-closed rather than fabricating success.
- Standing reads use `Eve.ReadSide.ReadSide` SQL over pinned SDE and persisted relations. Vote methods use corporation persistence. This task forbids database access, so these providers have no data-backed Host wire regression.
- Host test project build succeeded with 0 warnings and 0 errors. Actions selected Host job `40283833977` on `03a91013` completed successfully; the four selected tests are ordinary `[Fact]` tests and the job had no failures. `ci-status.py failures 03a91013` found no push-triggered `ci.yml` run.
- All target client callsites and consumers were searched across the whole `/home/user/eve-logic` tree; file:line evidence is listed above.

## Checkpoint after push

Implementation commit `913059d7` was pushed after merging `origin/main`; merge checkpoint `2a527df7` added Survey and Superweapon regressions. Handoff checkpoint `dd80c61b` was then pushed. A newer main merge checkpoint `03a91013` included additional Host wire tests and passed the focused Actions job.

## Checks

No database or graphical client was used. Host wire checks do not constitute graphical-client acceptance.