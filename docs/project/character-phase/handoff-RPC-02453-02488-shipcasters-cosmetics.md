# Host coverage batch: RPC-02453–RPC-02488

## Status

Partial. Push bbb7b6f1b916b272859ad71047f642e4b473fe1b includes 173d933b fix: accept transport metadata on ship kill counter. The three shipcaster read methods have a Host-dispatch regression from earlier commits; kill-counter has a new Host-dispatch wire regression. The requested batch remains incomplete: production shipcaster registration has no authoritative landing-pad/faction provider, and this batch has no successful Host-wire regressions for cosmetics or stance.

## V24.01 client callers and consumers

- Shipcaster calls: eve/client/script/parklife/shipcasterSvc.py:23-30. Consumers: warzoneMapPanel.py:397,424, shipCasterVisualization.py:57,60, warzoneInfoSideCont.py:228; rows expose faction/system and built/linked/disruption state. shipcaster/landingPadData.py:9-77 defines the six-field client row.
- Cosmetics callers: eve/client/script/ui/cosmetics/ship/shipUtil.py:27-28, cosmetics/client/cosmeticsSvc.py:160-162, ui/shared/skins/devMenuFunctions.py:8-27,35-36,77-84, skinPanel.py:633,651-652. Notification consumers: nodegraph/client/events/cosmetic.py:9-13, cosmetics/client/shipSkinStateController.py:72, ui/shared/neocom/charsheet/skinsPanel.py:152-155, ui/shared/skins/controller.py:236-238, cosmeticFittingCenter.py:107, hangarView.py:769-776, shipSceneContainer.py:116-121.
- Kill counter callers: ui/services/menusvc.py:869-870, ui/view/hangarBehaviours/baseHangarBehaviours.py:94-95, ui/shared/info/shipInfoPanels/panelOverview.py:275-276. They consume a numeric count; hangar caps display at 999 and overview suppresses zero.
- Stance caller: ui/inflight/shipstance.py:30-32 ignores the RPC return. Notification consumers: ui/inflight/ship_stance_button.py:53-55, fittingController.py:429-431, hangarView.py:707-713.

## EveJS reference

- Shipcaster reads serialize in-memory list/faction state in server/src/services/shipcaster/shipcasterRuntime.js:155-188; producer evidence is limited to test seed helpers.
- Cosmetics RPCs use stateful logic in server/src/services/ship/shipCosmeticsMgrService.js:308-389 and shipCosmeticsState.js:327-384,440-460,484-556.
- Kill counter returns persisted player count in shipKillCounterService.js:19-31.
- Stance returns the previous stance (shipStanceMgrService.js:35-49) and updates/refreshes runtime ship state (shipStanceRuntime.js:424-572).

## eve.rs and checks

- Production shipcaster names are registered in dotnet/src/Eve.Host/Rpc/AgentRpc.cs:110 without state callbacks; docs/project/TODO.md retains that gap. Existing ShipcasterHostWireTests sends all three methods through real Host dispatch with test fixture providers.
- Kill-counter production registration reads persisted shipKillCounters in dotnet/src/Eve.Host/Rpc/CoreRpc.cs:147. The new wire test sends GetItemKillCountPlayer(9001) through a loopback Host and asserts persisted player count 23 as PyInt. It also accepts only the transport-added machoVersion keyword.
- Selected Actions run 40242203372 passed the kill-counter focused regression. Push-triggered CI 40242192488 failed in unrelated InsuranceRpcHostWireTests: the isolated test GameStore had no shipInsurancePrices row, so its Rifter quote list was empty at test line 140. The selected test run itself passed. No database or graphical client was used locally.

Remaining work: implement an authoritative shipcaster state provider; add successful real-Host wire coverage for cosmetics and stance; resolve the insurance fixture's missing persisted price state before treating the push CI gate as green.
