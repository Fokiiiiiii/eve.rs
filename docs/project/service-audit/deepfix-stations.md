# Station and hub deep-fix handoff

Checked 2026-11-01 in the V24.01 client tree at `/home/user/eve-logic`, the current eve.rs worktree, and EveJS 261028.1.

## Repair service flow

The inventory context menu offers the repair window (`eve/client/script/ui/services/menusvc.py:222-223`; `eve/client/script/ui/services/menusvcExtras/openFunctions.py:221-228`). The window calls `GetRepairQuotes(itemIDs)` and consumes each row's `itemID`, `damage`, `maxHealth`, and `costToRepairOneUnitOfDamage` (`eve/client/script/ui/station/repairshop/base_repairshop.py:236-252`). It uses `ceil(damage) * unitCost` for the confirmation/partial payment (`:299-331`). The wrapper calls `RepairItemsInStation(itemIDs, payment)` while docked at an NPC station and `RepairItemsInStructure(itemIDs)` while docked at a structure (`:64-72`). After the RPC it unlocks items, emits local `OnRepairDone`, and refreshes quotes (`:329-339`). The quote fields `structureTaxRate` and `baseCostToRepairOneUnitOfDamage` have no other client consumer; whole-client search found no additional repair RPC callers.

Host registers `RepairService` in `dotnet/src/Eve.Host/Rpc/StationRpc.cs:45`; `MachoNetService.cs:47` routes `repairSvc` through the station service. The bound handler validates the docked facility, reads persisted damage, commits repair state and any wallet change together, then sends `OnItemChange` (`dotnet/src/Eve.Services.Station/RepairService.cs:76-97,105-185,216-257,298-303`). NPC station prices remain based on adjusted prices. Structure repair's wire method receives only item IDs.

Current EveJS 261028.1 routes the same calls in `server/src/services/station/repairService.js:154-173,225-238`. Its `repairRuntime.js:426-469` explicitly returns zero quote price components for Upwell structures and settles structure repairs without a charge. EVE University says NPC station repairs cost ISK and friendly player structures can repair for free with the appropriate settings ([Overheating](https://wiki.eveuniversity.org/Overheating)). CCP's [Structure Management](https://support.eveonline.com/hc/en-us/articles/208289605-Structure-Management) describes configurable structure tax rates and ISK fees. The V24 repair client itself does not submit a structure fee: it passes only item IDs in `base_repairshop.py:67-68`.

### Defect and correction

Before the fix, Host read `industryTaxRate` (or generic `taxRate`) from the structure document and applied it to repair quotes and settlement. This treated industry tax as a repair fee, showed a positive client quote, and could reject a repair for insufficient ISK. A regression assertion first failed against this implementation: expected structure tax 0, received 0.15. The fix removed that unrelated tax lookup and settlement, returns a zero structure repair unit price, repairs at zero wallet balance, and leaves NPC station charging intact. Damage persistence and `OnItemChange` remain in the same transaction/after-commit flow.

Host regression evidence is in `dotnet/tests/Eve.Host.Tests/RepairStructureHostWireTests.cs:211-280`; it sets an unrelated 15% industry rate, checks zero structure quote/tax, commits repair with no balance, restarts service and reads persisted damage state, and also checks paid NPC station repair. Production construction is covered by `dotnet/src/Eve.Host/Rpc/StationRpc.cs:45`; the Host wire tests register the real service implementation and dispatch the real Macho calls.

## Related core station/hub flows checked

These selected flows were traced over the full client call/consumer paths and have more complete evidence in existing handoffs:

- Station identity, station item bits, guest rows and their display consumers: `docs/project/service-audit/DEEPCHECK_station.md` (for example `station/base.py:102-110,574-586`, `dockedUI/guests.py:93-95`).
- Corporation office quote/rent/unrent, cache invalidation, persistence and ordered notifications: `docs/project/service-audit/DEEPCHECK_station.md` (`officeManager.py:49-50,68-75,113-121`; rent commits before `OnItemsChanged`, then `OnOfficeRentalChange`).
- Agent list/location and mission offer/action paths: `docs/project/service-audit/DEEPCHECK_agentMgr.md` (`agents.py:91,131,781-782`; `agentDialogueWindow.py:427`; `missioncontroller.py:256,261`). Current EveJS 261028.1 uses `server/src/services/agent/agentMgrService.js:775-789,898-911,1158-1165` for `GetAgents`, bound `DoAction`, and offer removal.
- Daily market order and fill path: `docs/project/service-audit/deepfix-market and trade and contracts.md` (`marketsvc.py:255-278`, `sellMulti.py:461-463`); current EveJS market context and pinned SDE faction IDs are recorded there.

## Checks

All database tests used `eve_p5` through `tools/dev/db-lock.sh`.

- Before code correction, the new Host regression failed on the 15% structure tax value; 0 tests skipped.
- After correction, Host wire tests `RepairStructureHostWireTests.Registered_repair_service_quotes_free_structure_repairs_and_preserves_paid_station_repairs` and `Repair_service_binds_quotes_and_commits_station_repairs_over_host_wire`: 2 passed, 0 skipped.
- `Eve.Services.Station.Tests` `RepairServiceTests`: 8 passed, 0 skipped.
- In the same worktree after merging main, selected station read/guest Host tests: 2 passed; corporation office tests: 15 passed; market Host tests: 2 passed; market trading/matching tests: 21 passed; agent Host and mission transaction tests: 12 passed. Each selected run reported 0 skipped.
- No graphical client session was run. The wider audit is a set of selected core flows, not a proof of every station service RPC or every exceptional transition.
