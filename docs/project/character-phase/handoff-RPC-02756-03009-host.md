# Host coverage handoff: RPC-02756–03009

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

- Sov reads and fuel group handlers: `server/src/services/map/sovMgrService.js:92-124`; structure and claim values are shaped from the sovereignty read data.
- Standing methods: `server/src/services/character/standingMgrService.js:194-241`; transaction data is projected by `standingRuntime.js:1027-1053`, compositions by :472-512.
- Superweapon activations: `server/src/services/ship/superWeaponMgrService.js:74-125`; valid module activations return integer 1 and refused activations return null.
- Survey: `server/src/services/support/surveyService.js:50-63`; active ID comes from the configured survey value and claim/check calls return null.
- Tournament: `server/src/services/tournament/tourneyMgrService.js:114-119` returns a dict with an empty `allowedShipList`.
- Votes: `server/src/services/corporation/voteManagerService.js:213-273`; authority and sanctions live in `voteRuntimeState.js:753-912`.

## eve.rs status

- Sov methods are implemented in `dotnet/src/Eve.Services.Core/SovFwService.cs` and production callbacks use the sovereignty persistence read/write operations. The focused socket regression is `dotnet/tests/Eve.Host.Tests/SovMgrHostWireTests.cs`: it exercises structure/claim/fuel group reads and mutation, including absent state. Local result: 1 passed, 0 failed, 0 skipped.
- RPC-02761 is a client-local cache invalidation, so no server method should be registered.
- `TourneyMgrService.QueryShipList` previously fell back to a bare list, which does not satisfy the client dictionary access. It now falls back to a dict containing `allowedShipList: []`, matching EveJS. `TourneyMgrHostWireTests.cs` exercises the registered service through Host and checks this field. Focused local Host socket test passed: 1 passed, 0 failed, 0 skipped.
- The remaining standing, superweapon, survey, and vote rows still need Host socket regressions and final wire/provider review before this batch is complete. Superweapon callbacks must remain fail-closed when no authoritative activation callback exists.

## Checks

No database or graphical client was used. The real Host socket check does not constitute graphical-client acceptance.
