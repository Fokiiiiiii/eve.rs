# Handoff: ST-12 corporation offices in Upwell structures

Package ST-12 of the structures phase. Worktree `wt-st12` from origin/main 2600f9a (ST-7 corporation-office compression source). Database `eve_p3` (lock `tools/dev/db-lock.sh`).
Rules: `agent-rules.md`. Decisions and sources: DEVIATIONS "Structure corporation offices (ST-12, 2026-10-15)". Rows: TODO section 57.

## What was built

- **(a) Corporation Offices service.** `StructureServiceIds.Offices` (3). The service is reported online in every structure's legacy shape (`StructureStore.ToLegacy`), needs no module and no fuel, and is never taken offline by a fuel-out. Its access is profile setting 20 (`PersistenceStructureDirectoryReadModel`: `SettingIdForService(3) = 20`; before this the service had no setting and was denied to everyone). `OfficeManagerService` answers at a structure: bind, quote (`IStructureOffices.RentForAsync`, the profile rent for the pilot), the impound calls answer "no impound". Adapter: `Eve.Host/Persistence/Structures/StructureOfficeSites.cs`.
- **(b) Rent, extend, cancel at a structure.** `RentOffice` (structure branch): the RentOffice role, the service's access, the rent charged to the renter and credited to the owner, the office folder item and the office row in one transaction (`office-rent:{id}:charge` / `:credit`), the due job scheduled in the same transaction. `UnrentOffice` (structure branch): Director or CEO, the items to a corporate asset safety wrap, the office ended, its bills cancelled. Renewal: a paid bill (manual `CharPayBill`/`PayCorporationBill` or auto-pay) renews the office (`CorporationOfficeStore.PayBillAsync`); the structure's bill names its creditor (`creditor_id`, migration 010) and the payment credits it through `OfficeRentSettlement`.
- **Due job `office.rent_due`** (`Eve.Host/Persistence/Structures/StructureOfficeRentDue.cs`, key `office:{officeId}`). At the open bill's due time: not yet due → reschedule; auto-pay (corporation setting type 2 on, its division, funds) → pay and reschedule for the next bill; otherwise give the office up (items to asset safety, office ended, bill cancelled; a refused wrap is retried after an hour). A bill with no creditor (a station) is not handled.
- **Asset safety.** `PersistenceStructureAssetSafetyReadModel.TriggerAssetsAsync` body moved to `WrapItemsAsync` (unchanged behaviour), with `CreateCorporationOfficeWrapAsync` for the office path.
- **(c) Compression source.** The office row at the structure is what the ST-7 compression source reads; test `A_rented_office_hangar_at_a_structure...` runs the compression through `structureCompressionMgr` after the rent (refused before it).
- **(d) Tests.** `dotnet/tests/Eve.Host.Tests/StructureOfficeTests.cs` (8 tests, real `HostComposition`, PostgreSQL scratch schemas, restarts). Covered: the service and the quotes (owner and access group), one charge and no double charge across a restart, a refused rent with nothing written, auto-pay renewal and its reschedule, the give-up at due time after a restart, the Director un-rent and the member refusal, the compression source.

## Verified (this run)

- `Eve.Host.Tests` filter `StructureOfficeTests|StructureCompositionTests|StructureCompressionTests`: see the final run in the report (31 passed before the legacy-shape change; rerun on the final code).
- `Eve.Corporations.Tests`: 125 of 125 passed (includes the migration, the creditor column, the station rent and bill path).
- `Eve.Structures.Tests`: 105 total; the two failures seen with the store row design (`StructureFuelTests.A_short_hour...`, `StructureTimersTests.The_import...`) were caused by the offices row and are resolved by the legacy-shape design; rerun on the final code.
- `Eve.Services.Space.Tests` filter `Structure|Beacon|CynoJump|Cyno`: 122 of 122 on the row design; rerun on the final code.

## Decisions to know

- No fuel for the office service: the brief said "online/fuel via ST-7". The wiki lists Corporation offices as always available and SDE type 35893 has no fuel attribute. Not built as fuel.
- Rent of a renewal stays at the signing quote (EveJS reprices). Open row.
- Station rent: the station owner is not credited by the bill settlement (existing behaviour, a finding). Station offices have no due or impound job. Not changed.
- The DB constraints on rent and bill amount were relaxed to `>= 0` (migration 010). The station minimum stays in code.

## Open (TODO 57)

- Renewal repricing; station owner credit; station due/impound job; notices to the corporation (OnItemChange, rental change, bill refresh); a destroyed or unanchored structure with a rented office; the client acceptance (office window, structure corporate division listing, rent dialogue). No graphical client run.

## Things that will bite

- Test schemas: the corporation and wallet schemas must be migrated before the seed (the host does it at start). `members.started_ms` and `corporations.name`/`owner_names` are required by the tables.
- `Command` helper in `CorporationOfficeStore` binds null as `DBNull` (Npgsql needs a type for a null value).
- A failed `dotnet build` leaves the previous test assembly in place: check the build log before reading a test run.
