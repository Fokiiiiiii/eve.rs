# EveJS 261108.1 ship SKIN port review

## Result

No ship-SKIN code or data was ported. The two release labels do not identify a verified V24.01 ship-SKIN gap:

- EveJS adds an operator-only importer for local SKINR JSON files into its Paragon catalogue. V24.01 shares designs as client links; the file importer is not a client operation. eve.rs has no Paragon listing handlers, so importing rows alone would not create a client-visible flow.
- The new EveJS LP-offer rows associated with navy hulls are corporation and alliance emblems in Paragon's LP store, not paint SKIN licences. They do not belong in the classic SKIN catalogue.

No EveJS design files, LP-offer snapshots, or other third-party content were copied.

## Existing eve.rs behavior

### First-party SKINs and catalogue

`ShipCosmeticsCatalog.LoadAsync` imports `skinMaterials`, `skins`, and `skinLicenses` from the configured SDE. It resolves a skin's eligible hulls from the material rows when needed and retains the SDE license type, material, duration, and single-use flag. See [`ShipCosmeticsCatalog.cs`](../../dotnet/src/Eve.Host/ShipCosmeticsCatalog.cs#L17) lines 17–53.

The actual `shipCosmeticsMgr` Host service exposes V24's licensed-skin reads and first-party skin data. `ApplySkinToShip` checks ship ownership or active-pilot status, verifies the character's license and hull compatibility, persists the selected skin under `/ships/{shipId}`, and invokes the Host callback. `ActivateSkinLicense` validates and consumes the inventory item, persists its character license, and emits the inventory and license notifications. See [`ShipCosmeticsMgrRpcService.cs`](../../dotnet/src/Eve.Host/ShipCosmeticsMgrRpcService.cs#L84) lines 84–116, 172–199, and 202–293.

License consumption and the grant are bridged across PostgreSQL and GameStore with a persisted activation intent. The inventory transaction records the intent; Host startup recovers pending grants before accepting character RPCs. See [`SkinLicenseActivationIntents.cs`](../../dotnet/src/Eve.Host/SkinLicenseActivationIntents.cs#L12) lines 12–98 and [`CharacterMigrations.cs`](../../dotnet/src/Eve.Host/Composition/Migrations/CharacterMigrations.cs#L50) lines 50–51. This already covers the retry/restart concern addressed by EveJS 261108's `server/src/services/ship/shipCosmeticsState.js:235–268, 370–389` change using the PostgreSQL intent owner rather than copying EveJS's GameStore receipt implementation.

Host notification wiring sends `OnCurrentShipSkinChange` after applying a skin, `OnSkinLicenseActivated` after activation, and `OnItemChange` after license-item consumption. See [`InventoryRpc.cs`](../../dotnet/src/Eve.Host/Rpc/InventoryRpc.cs#L62) lines 62–82.

### SKINR designs and ship visuals

The SKINR catalogue is also SDE-derived: `ShipSkinrCatalog.LoadAsync` reads component, slot, hull-configuration, and tier data. Host startup registers draft handlers and the sequencing/license/data gateway; sequencing completion grants a persisted license and notifies the client. See [`ShipSkinrCatalog.cs`](../../dotnet/src/Eve.Host/ShipSkinrCatalog.cs#L63), [`HostComposition.cs`](../../dotnet/src/Eve.Host/Hosting/HostComposition.cs#L282) lines 282–304, [`ShipSkinrSequencingGateway.cs`](../../dotnet/src/Eve.Host/PublicGateway/ShipSkinrSequencingGateway.cs#L55) lines 55–76, and [`ShipSkinrSequenceJobs.cs`](../../dotnet/src/Eve.Host/PublicGateway/ShipSkinrSequenceJobs.cs#L29) lines 29–83. The current migration persists drafts, component licenses, sequencing jobs, ship licenses, and request receipts; it has no Paragon market-listing table ([`0001_ship_skinr.sql`](../../dotnet/src/Eve.Host/SkinrMigrations/0001_ship_skinr.sql#L1) lines 1–76).

V24.01's classic-SKIN client calls `ActivateSkinLicense`, `GetLicencedSkins`, `GetLicencedSkinsForShipType`, `GetFirstPartySkinData`, and `ApplySkinToShip` from `/home/user/eve-logic/cosmetics/client/cosmeticsSvc.py:90–168`. `/home/user/eve-logic/cosmetics/client/shipSkinApplicationSvc.py:44–51, 102–115` buffers first-party and third-party changes and applies them through their respective services. The client turns current ship skin changes into `OnCurrentShipSkinChange` (`/home/user/eve-logic/cosmetics/client/shipSkinStateController.py:59–75`); the in-scene SKINR renderer applies slot components and pattern projection to the ship model (`/home/user/eve-logic/eve/client/script/environment/spaceObject/cosmeticsManager.py:42–52, 311–383`). CCP's [Ship SKINs support article](https://support.eveonline.com/hc/en-us/articles/203280701-Ship-SKINs) describes activating a tradable license and selecting the unlocked SKIN in the fitting window.

The Host's `GetEnabledCosmetics` path is separate: it reads `shipLogoFittings` (`ShipCosmeticsMgrRpcService.cs:69–81`). Emblem offers must not be treated as entries in the paint-SKIN licence catalogue.

## EveJS 261108.1 comparison

### Paragon SKINR file import

The new `server/src/services/ship/shipSkinDesignFiles.js:91–118` reads and validates an `eveoffline.skinr-design` v1 file from the server's local SKINR directory, then calls `importShipSkinStoreDesign`. The new `/skinr store import` branch in `server/src/services/chat/commands/skinrDrafts.js:72–93` requires a staff/debug role and accepts listing price, currency, and quantity. `server/src/services/ship/shipSkinThirdPartyRuntime.js:221–268` creates a public catalogue listing and persists it in EveJS's runtime table. This is a working server-operator content workflow, not a V24 RPC or client file-import contract.

The V24 client instead registers `ShipSkinDesignLinkValidator` and resolves a shared design through `GetSharedRequest` (`/home/user/eve-logic/cosmetics/client/shipSkinDesignSvc.py:33–65`, `/home/user/eve-logic/cosmetics/client/ships/link/validators/paste.py:20–32`, and `/home/user/eve-logic/cosmetics/client/messengers/cosmetics/ship/shipSkinDesignRequestMessenger.py:55–68`). CCP's [SKINR guide](https://support.eveonline.com/hc/en-us/articles/14280655446300-Using-SKINR-to-Create-Ship-SKINs) likewise describes sharing a design by dragging its card into chat, mail, or notes.

The current eve.rs gateway registers draft, sequencing, license, and design-data requests, but no `eve_public.cosmetic.market.skin.*` listing or purchase requests (`ShipSkinrGateway.cs:11–21`, `ShipSkinrSequencingGateway.cs:55–76`). Therefore, porting only EveJS's importer would write records that the client cannot discover or buy. The imported recipes and terms also come from operator-supplied files, which are not project-owned data. This operator extension is rejected for this client-compatibility port; the separate Paragon marketplace flow remains outside this release delta.

The 261108.1 and 261028.1 `content-packs/` trees were compared. The changed files are career-agent 1.4.4, fractured-legacy 1.0.2, incursions 1.0.0, security-mission packs 1.1.9/1.1.10, visions-of-greatness 1.0.4, and wormhole-sites 1.2.1. Neither feature below is a content pack: SKINR store import is server code, and the Quartermaster recipe is a server-side authority file.

### Navy Quartermaster recipe

EveJS 261108.1 adds `/home/user/evejs-261108.1/server/src/services/spaceComponents/nativeItemTraderAuthority.json:1–28`, sourced from build 3396210 component and recipe resource hashes. It admits trader type 93547 in Amarr and Mehatoor, and only dungeon object 1492060. Recipe 405 consumes 250 type 93592 Warpath Operational Intel and returns one type 93729 SKIN license. `/home/user/evejs-261108.1/server/src/services/spaceComponents/nativeItemTraderRuntime.js:78–151` validates the active ship, admitted target, range, cargo and input stacks before the inventory exchange; it records a durable pending intent and receipt around the transaction. `/home/user/evejs-261108.1/server/src/services/ship/beyonceService.js:3079–3086` dispatches the client's `itemTrader.ProcessTrade` component call to this runtime.

V24.01 has the matching generic component call: `/home/user/eve-logic/spacecomponents/client/components/itemTraderNew.py:24–36` reads the component recipes and calls `CallComponentFromClient`; `/home/user/eve-logic/eve/client/script/ui/inflight/itemTrader/itemTraderController.py:127–145` resolves recipe fields from the client FSD loader; `/home/user/eve-logic/eve/client/script/ui/inflight/itemTrader/confirmWnd.py:84–94` sends the recipe ID, multiplier, and ISK amount. The pinned build 3396210 SDE identifies type 93547 as Imperial Quartermaster Trader (`.cache/sde/eve-online-static-data-3396210-jsonl/types.jsonl:49648`), type 93592 as Warpath Operational Intel (`types.jsonl:49666`), and license 93729 as the Augoror Navy Issue Capsuleer Day XXIII SKIN for hull 29337 (`skinLicenses.jsonl:11737`, `skins.jsonl:6908`, `types.jsonl:49762`). The existing eve.rs `ShipCosmeticsCatalog` already imports this license and its skin from those SDE tables.

CCP's [Version 23.02 patch notes](https://www.eveonline.com/news/view/patch-notes-version-23-02) say Capsuleer Day XXIII: Warpath concluded on June 2, 2026, and Quartermaster Traders remained only until June 9. This review is dated November 10, 2026. The client FSD and the EveJS whitelist of two captured targets do not establish that the seasonal trader still exists on TQ. I therefore reject resurrecting this exchange in eve.rs: its input is Warpath event currency, its output is the event SKIN, and CCP's published availability window has ended. The unsupported `beyonce.CallComponentFromClient` branch in eve.rs currently returns `None` for non-CRAB component calls ([`BeyonceService.Components.cs`](../../dotnet/src/Eve.Services.Space/BeyonceService.Components.cs#L13), lines 13–28); this review does not claim generic item-trader support is implemented. If CCP reintroduces a live Quartermaster recipe, it needs fresh target and offer evidence before a port.

### Paragon EverMarks emblem offers

The relevant 261108.1 data addition is `/home/user/evejs-261108.1/tools/DatabaseCreator/staticTables/evermarksCatalog/data.json`, whose description is “Cached EverMarks heraldry emblem offers and ship-logo metadata” (`:2–24`). The old/new JSON diff shows six new entries in its `licensesByTypeID` and six offers, IDs 19536–19539 and 19542–19543, in `offersByOfferID` (`:7371–7438, 16443–16508`). The offer rows point to type IDs 91863–91866 and 91869–91870, corporation ID 1000419, and 8,000 LP each. The metadata records the Paragon corporation LP-store ESI endpoint and retrieval date 2026-11-05 (`:12–24`); the matching pinned SDE identifies 1000419 as Paragon (`.cache/sde/eve-online-static-data-3396210-jsonl/npcCorporations.jsonl:268`).

Those six new type IDs are explicitly Dragoon Navy Issue (corporation/alliance), Corax Navy Issue (corporation/alliance), and Talwar Fleet Issue (corporation/alliance) emblems in the EveJS catalogue (`/home/user/evejs-261108.1/tools/DatabaseCreator/staticTables/evermarksCatalog/data.json:7371–7438`). The matching build 3396210 SDE independently names all eight IDs 91863–91870 as corporation/alliance emblems, including two Algos Navy Issue emblems without new LP offers in this snapshot (`.cache/sde/eve-online-static-data-3396210-jsonl/types.jsonl:48715–48722`). None is a row in the SDE `skinLicenses` or `skins` tables read by `ShipCosmeticsCatalog`.

The Paragon emblem offers are separate from the expired Quartermaster SKIN recipe. No emblem rows were added to `ShipCosmeticsCatalog`; these EverMarks entries are rejected for the paint-SKIN port.

## Verification record

Verified by source inspection:

- The first-party catalogue, license activation, persistence/recovery, Host notifications, SKINR draft/sequencing/license routes, client application calls, and render path described above.
- The exact EveJS 261108.1 import command and its filesystem/runtime behavior.
- The added Quartermaster recipe, its V24 item-trader callsite, its SDE input/license mapping, EveJS's target/system whitelist, and CCP's published event/trader end date.
- The type names and source metadata for the new navy-hull emblem offer rows, including their Paragon LP-store association.
- No SKINR or Quartermaster-specific EveJS content pack is part of the 261108.1 pack changes.

Local checks on the merged `origin/main` source:

- `/root/.dotnet/dotnet build dotnet/src/Eve.Host/Eve.Host.csproj -c Release -m:1`: succeeded, 0 warnings and 0 errors.
- Focused `Eve.Host.Tests` coverage for classic SKINs, SKINR dispatch/catalogue/notices, compatibility service calls, Host wire reads/GM operations, and registered Host activation: 34 passed, 0 failed, 0 skipped on `eve_p5` after loading SDE build 3396210 and mission data.
- The first run on the newly created but unseeded `eve_p5` failed 13 tests because SDE queries returned no build and `sde.mapSolarSystems` was absent. After the official SDE and mission import, the same focused run passed as above.

Not verified in this review:

- The native graphical V24.01 client was not launched.
- The ESI LP-store endpoint was not independently queried live; offer availability and terms beyond the captured EveJS data are not asserted.
- EveJS's local-file store import was not executed. Its server package declares no project-level test script, and the import depends on server-local design files and its runtime database.
- EveJS's item-trader flow was source-traced, not executed. The EveJS checkout has no focused native-item-trader test file, and its server runtime requires a live space scene and persistent inventory/game store.
- No eve.rs code or content data changed, so no new end-to-end regression was added. Existing relevant Host tests are run separately and reported with this change.
