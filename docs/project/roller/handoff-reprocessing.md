# Reprocessing service handoff

## Client contract

The V24.01 item reprocessor groups selected items by their direct `locationID`, determines the output owner/location/flag from the selected destination, and invokes `Reprocess(itemIDs, fromLocationID, ownerID, outputLocationID, outputFlagID)` once for each source location (`/home/user/eve-logic/reprocessing/ui/itemReprocessor.py:16-39,67-68`). The returned tuple is processed item IDs plus output quantity by type. The controller computes remaining selected inputs from those IDs and forwards the output map to the results panel (`/home/user/eve-logic/reprocessing/ui/controller.py:74-80,113-117`) `reprocessingWnd.py:150-165` removes unprocessed items from the window and displays actual outputs. The context menu opens this flow at `eve/client/script/ui/services/menusvc.py:331`; quotes are read at `reprocessing/ui/quotes.py:19`.

EveJS v0.12.9 implements the bound method in `server/src/services/station/reprocessingService.js:673-764`. When the source ID is zero it batches inputs by direct location; the settlement runtime resolves accessible inputs, checks the requested source, derives yields/tax, and settles one batch transactionally (`server/src/services/reprocessing/reprocessingRuntime.js:1050-1266`). Its response is the consumed input ID list and aggregated output quantities.

## Rules and implementation

EVE University’s [Reprocessing](https://wiki.eveuniversity.org/Reprocessing) page documents whole portions, station equipment efficiency, multiplicative Reprocessing / Reprocessing Efficiency / material skill bonuses, implant yield, Scrapmetal Processing’s separate bonus, and NPC tax declining from 5% to zero at 6.67 standing. Runtime material quantities, portion sizes, skill attributes and NPC station efficiency come from the pinned SDE tables `typeMaterials`, `types`, `typeDogma` and `npcStations` through `SdeReprocessingData`.

The Host already registers `reprocessingSvc` in `dotnet/src/Eve.Host/Rpc/StationRpc.cs`. Inventory consumption, output creation and wallet tax are performed with the existing stores inside one PostgreSQL transaction; notifications are sent after commit. This change passes `fromLocationID` into the locked input plan and ignores a selected item whose direct location differs from that source, matching the client’s per-location batch contract. A zero source leaves the handler’s input unfiltered; the V24 UI normally sends one positive source location per call. The focused service tests cover location filtering, client-style multiple-location batches, retry without duplicate yield/tax, portion remainder retention, and the existing insufficient-balance rollback. A Host integration test invokes the registered service and checks persisted input/output after Host restart. EveJS batches a zero-source request by each item’s direct location; this implementation preserves the client’s positive-source batches and leaves zero-source calls unfiltered.

## Verification record

Verified locally: `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-restore -m:2 -p:UseSharedCompilation=false` succeeded with 0 warnings and 0 errors; the focused `A_reprocess_call_is_scoped_to_its_source_location` test passed (3 cases). No PostgreSQL database was used. The Host persistence/restart test and settlement tests need the Actions runner’s temporary PostgreSQL service, which is gated on clarification because this task also prohibits any PostgreSQL use. No graphical-client session was part of these checks.

Keep this note current after each push.