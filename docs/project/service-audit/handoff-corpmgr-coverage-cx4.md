# corpmgr coverage handoff — cx4

## Completed

- Rows RPC-01250–RPC-01259: `GetPublicInfo`, now `IMPLEMENTED-TESTED` with `CorporationPublicInfoHostWireTests.Corporation_public_info_returns_persisted_client_fields_through_registered_host`.
- The Host test registers the real `CorpMgrService` in `ServiceRegistry`, dispatches Macho CallReq packets through `PacketDispatcher`, and reads a corporation created in PostgreSQL. It checks the public KeyVal reply fields and `None` for a missing corporation.
- Read-only RPC: no notification is expected.
- Client-wide V24.01 evidence: `corporationInfoWindow.py:48,60,75,183-188` reads corporation ID, CEO, shares and description; `bountyWindow.py:410-412`, `killRights.py:151-152`, and `warPermit.py:42-43` consume alliance ID or allow-war. Missing alliance is handled as falsey by the client callsites.
- Current EveJS reference: `/home/user/evejs-261028.1/src/server/src/services/corporation/corpmgrService.js:472-505` builds the same public KeyVal fields.
- Checks passed: Host.Tests build; the focused Host wire test (1 passed, 0 skipped); CorporationProtocolTests.Corporation_core_and_role_rpcs_preserve_public_fields_and_role_contracts (1 passed, 0 skipped). Database: `eve_p4`.

## CI follow-up

- Push `c6411f459a76` exposed `Eve.Services.Inventory.Tests.StructureControlInfoTests.Controlled_structure_item_list_and_capacity_reads_are_owner_scoped_and_the_binding_cannot_mutate_inventory` (run 41030581055). A controlled structure view containing a foreign-owner row was rejected wholesale, and the fallback hid the corporation-owned rows. `InvBrokerService` now keeps only rows owned by the controlled structure; the host read model already scopes its real result to that owner.
- V24.01 evidence: `eve/client/script/environment/invCache.py:491-497` gets the current structure item hangar through `GetInventoryFromId`; `invCache.py:1131-1165` consumes the inventory `List` reply into the client cache; `eve/client/script/ui/shared/inventory/neocomInventoryBadging.py:105` reads the current structure item hangar.
- Fix verification passed: `dotnet build` for Inventory.Tests and Host.Tests; all 83 Inventory.Tests passed; related Host inventory/structure tests passed (4, 0 skipped). Database: `eve_p4`.

## Remaining IMPLEMENTED-UNTESTED rows

- RPC-01218–RPC-01233: `GetAssetInventory` (16 rows)
- RPC-01234–RPC-01241: `GetAssetInventoryForLocation` (8 rows)
- RPC-01242–RPC-01247: `GetCorporationIDForCharacter` (6 rows)
- RPC-01248–RPC-01249: `GetCorporations` (2 rows)
- RPC-01260–RPC-01261: `SearchAssets` (2 rows)

Total remaining: 34 rows. The graphical V24.01 client was not used.

## corporationSvc medal coverage — cx4

- RPC-01262–RPC-01267, RPC-01269–RPC-01270, and RPC-01275–RPC-01278 are now marked `IMPLEMENTED-TESTED` in `EVE_RS_COVERAGE.csv` after the DB-backed registered-Host flow passed.
- The test creates corporation medals through the `corporationSvc` socket route, checks persisted medal data, the `OnCorporationMedalAdded` notification after the reply, both creation call signatures, catalogue medal/graphics rows, a persisted award and `OnMedalIssued`, received medal/graphics rows, medal details, and the employment record plus empty character-transfer list.
- eve-logic V24.01: `eve/client/script/ui/services/medals.py:31-51` checks creation roles/length and retries with confirmation; `:59-62` returns medal and graphics lists; `:70-89` filters received medals by status; `:90-91` forwards medal details; `:123-142` invalidates the corporation medal and received-medal caches after `OnCorporationMedalAdded`, `OnMedalIssued`, and `OnMedalStatusChanged`. Consumers: `eve/client/script/ui/shared/neocom/corporation/corp_ui_member_decorations.py:53-61,91,109,137`, `eve/client/script/ui/shared/neocom/charsheet/decorationsPanel.py:113-117,198-213`, `eve/client/script/ui/shared/neocom/charsheet/charSheetUtil.py:31-51`, `eve/client/script/ui/shared/info/infoContentBase.py:675,1450`, and `eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:59-71`.
- Current EveJS reference: `/home/user/evejs-261028.1/src/server/src/services/corporation/corpService.js:471-523,561-588` implements received medals, details, create and corporation-medal rows; `server/src/services/corporation/corporationNotifications.js:1429-1439` sends `OnCorporationMedalAdded` with the medal ID. The Host test exposed that eve.rs creation did not send this consumed notification; `CorporationService.CreateMedal` now schedules it after the RPC response to sessions in the corporation.
- Employment consumer: `eve/client/script/ui/shared/info/infosvc.py:2841-2886` unpacks the employment rowset and transfer list, reads `corporationID`, `deleted`, `startDate`, and `transferDate`, and renders a transfer group only when the list is nonempty. EveJS `server/src/services/corporation/corpService.js:466-469` returns employment history and an empty transfer list.
- Focused verification: `dotnet build dotnet/tests/Eve.Host.Tests --no-restore -p:UseSharedCompilation=false` passed; Host wire tests `Corporation_medal_reads_and_creation_notifications_cross_the_registered_host` and `Corporation_medal_rpcs_use_the_registered_host_service_and_preserve_wire_outcomes` passed (2 passed, 0 skipped) on `eve_p4`; all `CorporationProtocolTests` passed (51 passed, 0 skipped) on `eve_p4`. The graphical client was not used.
