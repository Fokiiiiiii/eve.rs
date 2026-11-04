# LPStore and LPSvc deep check

Date: 2026-10-27

Client: EVE Online V24.01, build 3396210

## Name resolution and client call inventory

The client has no Host RPC service named `lpstore`. It is the local client service `LPStoreSvc` (`svc.lpstore`). The closest Host routes are `LPStoreMgr` and `LPSvc`. The client opens the local service through `sm.GetService('lpstore')` in `eve/client/script/ui/eveCommands.py:2690`; at a station it opens NPC stores at lines 2691-2707, and at a structure it reads `structureDirectory.GetStructureInfo`, checks owner and `HasServiceAccess('lpstore')`, then opens that NPC owner's store at lines 2708-2712.

The whole-client search found these direct Host calls. They all pass method arguments positionally:

| Host method | Client call site | Consuming client code |
|---|---|---|
| `LPStoreMgr.GetAvailableOffersFromCorp(corpID)` | `eve/client/script/ui/station/loyaltyPointStore/lpStoreSvc.py:96-100` | `LPStoreSvc.GetOffers` caches it; store and Paragon panels consume offers at `lpStorePanel.py:209,247` and `paragonLpStorePanel.py:71-74`. |
| `LPStoreMgr.TakeOfferForCharacter(corpID, offerID, numberOfOffers)` | `lpStoreSvc.py:246-248` | `AcceptOffer` treats the return as truthy/falsy, then clears caches and dirties the window at `lpStoreSvc.py:253-265`. |
| `LPStoreMgr.TakeOfferForCorporation(corpID, offerID, numberOfOffers)` | `lpStoreSvc.py:248-250` | Same `AcceptOffer` consumer at `lpStoreSvc.py:253-265`. |
| `LPSvc.ExchangeConcordLP(corpID, amount)` | `lpStoreSvc.py:101-103` | `ConvertConcordLP` ignores the return; the UI opens the dialog through local `lpstore.OpenConcordExchange` at `eve/client/script/ui/shared/neocom/wallet/panels/personal/lpPanel.py:228-230`. |
| `LPSvc.GetAllMyCharacterWalletLPBalances()` | `eve/client/script/ui/shared/neocom/wallet/loyaltyPointsWalletSvc.py:29-31` | The client indexes each row as `row[0]` issuer and `row[1]` balance. |
| `LPSvc.GetAllMyCorporationWalletLPBalances()` | `loyaltyPointsWalletSvc.py:35-39` | Same row consumer. |
| `LPSvc.TransferLPFromMyCorpWalletToOtherCorp(receiverCorpID, issuerCorpID, lpAmount)` | `loyaltyPointsWalletSvc.py:93-96` | Corporation donation panel calls it at `panels/corp/corpDonateLPPanel.py:45-48`; its return is ignored. |
| `LPSvc.TransferLPFromMyWalletToOtherCorp(receiverCorpID, issuerCorpID, lpAmount)` | `loyaltyPointsWalletSvc.py:97-100` | Personal donation panel calls it at `panels/personal/personalDonateLPPanel.py:44-47`; its return is ignored. |

Other local `LPStoreSvc` calls found in the whole tree include `OpenConcordExchange`, `GetConcordLPExchangeRate`, `GetOffers`, `IsRWCorp`, and the `OpenLPStore` path above. These are local client methods, not Host RPC routes. No V24.01 client call to `LPStoreMgr` or `LPSvc` uses `Moniker`, `MachoBindObject`, or a bound service object. EveJS exposes bind handlers, but they have no matching client caller.

## Registration and wire arguments

Production registration is in `dotnet/src/Eve.Host/Rpc/AgentRpc.cs:80-84`: the Host adds `LoyaltyPointsService` and `LpStoreMgrService` to its Agent RPC service set. The tests send Macho packets over the TCP Host test front and dispatch to registered `IService` instances; production composition registration was checked at that call site.

The client sends all listed method arguments as positional values. There are no method-keyword arguments in the client call sites. Macho transport metadata is the exception: `machoVersion` arrives in the outer kwargs dictionary and `PacketDispatcher.ApplicationKwargs` removes it before constructing `ServiceCall` (`dotnet/src/Eve.Services/PacketDispatcher.cs:217-223,307-317`). The wire tests include that metadata. The Host's numeric parser accepts both `PyInt` and `PyLong`; the existing station route test covers `PyInt`, and the structure route test covers `PyLong`.

For the client-used shapes:
- `GetAvailableOffersFromCorp` takes one positional corporation ID. Missing, `None`, or nonpositive IDs become an empty list without reading offers; `None` is not sent by the client.
- Both purchase calls take the store corporation ID, offer ID, and quantity positionally. The Host defaults a missing quantity to 1; the client explicitly sends the quantity. A `None` quantity becomes zero and the purchase validator rejects it as `INVALID_QUANTITY`.
- Balance reads take no method arguments.
- Exchange and donation calls take the positional values shown above. The V24.01 client does not send them as method kwargs.

## Method results

### `LPStoreMgr.GetAvailableOffersFromCorp`

The Host returns a `PyList` of `util.KeyVal` rows with ten keys: `typeID`, `iskCost`, `akCost`, `reqItems`, `offerID`, `qty`, `requiredStandings`, `corpID`, `lootItems`, and `lpCost` (`LpStoreMgrService.cs:140-150)). Numeric fields are `PyInt`; `reqItems` is a list of two-element tuples of `PyInt`; `requiredStandings` is `PyNone); `lootItems` is an empty `PyList`. The client reads `qty`, `reqItems`, `lootItems`, and optional `requiredStandings` in `lpOfferEntry.py:42-59` and `lpOfferEntry2.py:44-48`; `LPStoreSvc._SetOfferDataSortOrder` also treats missing standings as none at `lpStoreSvc.py:464-483`, and its standings helper treats None/false as no restriction at `lpStoreSvc.py:520-525`.

The Host reads the corporation's offers from `LpStoreOfferCache` and returns these rows at `LpStoreMgrService.cs:51-60). The structure path now calls the persisted structure authority before reading offers. A structure session must pass `CharacterHasServiceAsync` for service ID 11; otherwise the route returns a wrapped `UserError`, code `CustomNotify`, with `notify = "That loyalty store service is not available."`.

EveJS 261028.1 checks structure access in `server/src/services/evermarks/lpStoreMgrService.js:103-122,360-381`. It uses structure service ID 11 and setting ID 19 (`structureConstants.js:105-138`, `structureServiceAuthority.js:130-158`). eve.rs now uses the same service ID and setting mapping (`StructureFuel.cs:5-20`, `PersistenceStructureDirectoryReadModel.Part2.cs:39-51`).

### `LPStoreMgr.TakeOfferForCharacter`

The Host builds the purchase request from the authenticated character, positional store and offer IDs, quantity, and current location (`LpStoreMgrService.cs:62-69)). The location is `structureid` when present, otherwise `stationid`/`stationID` (`LpStoreMgrService.cs:118-137`). This matches EveJS `getDockedLocationID` (`src/server/src/services/structure/structureLocation.js:52-58)). A successful purchase returns `PyBool.True`, which is the truthy result consumed by `AcceptOffer`.

The purchase provider validates character, store, location, quantity, offer, supported costs, required inventory and wallet balances. It settles LP, ISK, required items, granted inventory, and an `lp_store_purchases` row in one PostgreSQL transaction (`LpStorePurchases.cs:98-145,233-254)). A refusal rolls back and is converted to `CustomNotify` by `LpStoreMgrService.Refusal` (`LpStoreMgrService.cs:96-108`). The exact current message table is in `LpStoreMgrService.cs:15-31`; examples include invalid store, invalid quantity, missing offer, unsupported AK/requirements, insufficient LP/ISK/items, and delivery failure.

After commit, the purchase provider announces LP and ISK changes (`LpStorePurchases.cs:254-260)). The LP announcer sends `OnLPChange("set", issuerCorpID, before, after)` to the character session or `OnCorpLPChange(reason, issuerCorpID)` to sessions of the corporation (`LoyaltyPointsService.cs:84-106)). The Host then sends one `OnItemChange` per receipt item before the purchase reply (`LpStoreMgrService.cs:96-108)); the wire test checks the item notification precedes the `CallRsp`. The client handles LP cache invalidation at `loyaltyPointsWalletSvc.py:58-67` and inventory invalidation at `lpStoreSvc.py:255-265).

EveJS routes this method through `Handle_TakeOfferForCharacter` and the same static-offer transaction path (`lpStoreMgrService.js:383-387,223-343)). Its success is truthy and its client-visible refusals are `CustomNotify`.

### `LPStoreMgr.TakeOfferForCorporation`

The wire arguments and success result match the character purchase. The payer is the member corporation and current wallet division; the granted items still go to the buying character's hangar. Before spending, Host checks director authorization from persisted membership/roles rather than trusting cached session roles (`LpStoreMgrService.cs:72-88`, `AgentRpc.cs:39-45`). The test checks the exact denial: wrapped `UserError`, code `CustomNotify`, `notify = "You do not have permission to spend your corporation's funds."`.

The same transaction, commit ordering, and notifications as the character purchase apply. EveJS performs the corporation take through `Handle_TakeOfferForCorporation` (`lpStoreMgrService.js:389-421`) and checks corporation authorization in its purchase flow.

### `LPSvc.GetAllMyCharacterWalletLPBalances`

The Host reads the authenticated character's persisted wallet and returns `eve.common.script.sys.rowset.Rowset`: header/columns are `issuerCorpID, loyaltyPoints`, `RowClass` is `util.Row`, and each line is a two-value tuple of `PyInt` issuer ID and `PyInt` balance (`LoyaltyPointsService.cs:38-63`). The client builds its map from `row[0]` and `row[1]`, and treats absent balances as zero (`loyaltyPointsWalletSvc.py:29-31,69-71)). This is a read only; it sends no notifications.

EveJS uses the same method name and a wallet Rowset at `src/server/src/services/corporation/lpService.js:95-100).

### `LPSvc.GetAllMyCorporationWalletLPBalances`

The Host resolves the corporation from the authenticated character's persisted record, not stale `corporationID`/`corpid` session fields (`AgentRpc.cs:80-81`), reads that corporation's wallet, and returns the same Rowset shape (`LoyaltyPointsService.cs:45-63`). The client consumes it identically at `loyaltyPointsWalletSvc.py:35-39`. Balance changes notify every session of that corporation through `OnCorpLPChange` after commit (`LoyaltyPointsService.cs:99-106`).

EveJS reads the corporation from the session and returns the same named route at `lpService.js:102-109`; eve.rs deliberately uses persisted affiliation to avoid a stale-session read.

### `LPSvc.ExchangeConcordLP`

The client calls it positionally through `LPStoreSvc.ConvertConcordLP` and discards the return (`lpStoreSvc.py:101-103`). Host returns a wrapped `UserError`, code `CustomInfo`, with `info = "This loyalty point operation is not available on this server yet."`; it changes no persisted balance and sends no notification (`LoyaltyPointsService.cs:24-35`). The wire test asserts that exact error.

EveJS 261028.1's `Handle_ExchangeConcordLP` returns an empty dictionary without a balance mutation (`server/src/services/corporation/lpService.js:150-153`). The EVE University LP page states CONCORD exchange is station-only, requires an existing destination balance, gives 1:0.8/1:0.4 rates, and excludes several corporation groups (`https://wiki.eveuniversity.org/Loyalty_Points`). The current Host keeps a refusal rather than returning a success-shaped empty value. The full eligibility and rate calculation remains open.

### `LPSvc.TransferLPFromMyWalletToOtherCorp`

The personal wallet panel calls the route positionally with destination corporation, issuer corporation, and amount (`loyaltyPointsWalletSvc.py:97-100`, `personalDonateLPPanel.py:44-47`). The client ignores the return. Host now returns None on success, debits the character and credits the destination corporation in one wallet transaction, then sends `OnLPChange` followed by `OnCorpLPChange`. Destination eligibility and available balance failures return `CustomNotify` messages matching EveJS.

EveJS 261028.1 performs the transfer and returns None on success (`server/src/services/corporation/lpService.js:116-131`, `lpWalletState.js:615-688`); it checks destination corporation eligibility and sufficient balance, debits the character, credits the corporation, and emits both balance changes. EVE University says LP can be donated to a player corporation (`Loyalty_Points`); CCP's 2023 Viridian notes also establish corporation LP donations. The eve.rs path uses the persistent `LoyaltyPointStore` and verifies an eligible nonempty player corporation before committing.

### `LPSvc.TransferLPFromMyCorpWalletToOtherCorp`

The corporation wallet panel calls it positionally with the same three values (`loyaltyPointsWalletSvc.py:93-96`, `corpDonateLPPanel.py:44-48`). The client ignores the return. Host now returns None on success, checks director authority from persisted corporation membership, and transfers points from the source corporation to the destination in one transaction. The source wallet notification is sent before the destination wallet notification.

EveJS 261028.1 returns None after a successful corporation-to-corporation transfer and enforces director authorization, destination eligibility, and sufficient balance (`server/src/services/corporation/lpService.js:133-148`, `lpWalletState.js:690-769`).

Both transfer methods reject Evermark issuer `1000419`. The V24.01 generic wallet balance lists omit `corpHeraldry` (`loyaltyPointsWalletSvc.py:80,91`; constant `appConst.py:637`), and the dedicated Evermark panels inherit the base donation stub (`panels/donateLPPanel.py:382-384`). EveJS 261028.1 shares its normal transfer RPCs with Evermarks and enforces additional brand-manager and destination rules (`lpService.js:116-148`, `lpWalletState.js:615-769`). The V24.01 path exposes no Evermark donation caller, so eve.rs refuses that issuer at the ordinary LP boundary until its dedicated rules and client path are implemented. This compatibility choice is recorded in `docs/reference/compat/DEVIATIONS.md`.

## Pochven NPC station standing gate (2026-11-04)

V24.01 calls `GetAvailableOffersFromCorp` for listing (`eve/client/script/ui/station/loyaltyPointStore/lpStoreSvc.py:96-104`) and calls `TakeOfferForCharacter` / `TakeOfferForCorporation` in `AcceptOffer` (`lpStoreSvc.py:240-266`). The panels consume cached offer rows (`lpStorePanel.py:209,247`; `paragonLpStorePanel.py:72`; `lpOfferEntry.py:42-59`; `lpOfferEntry2.py:44-48`). A successful purchase invalidates the local offer cache, and `OnItemChange` refreshes inventory state (`lpStoreSvc.py:253-265,424-432`).

The production callback resolves the station in `StationCatalog`, loaded from pinned SDE `npcStations` and `mapSolarSystems` (`dotnet/src/Eve.Host/Composition/CharacterStores.cs:78`; `dotnet/src/Eve.Universe/StationCatalog.cs:37-83`). For region `10000070`, it reads the personal Triglavian standing from `foundations.Relations` and compares it with `-0.01` (`dotnet/src/Eve.Host/Rpc/AgentRpc.cs:63-78`; `dotnet/src/Eve.Relations/RelationStore.cs:300-301`; `dotnet/src/Eve.Services.Space/Pochven/PochvenRules.cs`). The service checks before it reads offers or invokes either purchase provider. A refusal is a `CustomNotify`; the DB regression confirmed unchanged LP/ISK, items, both journals and purchase rows, with no item notifications. Standing at the threshold and above remains eligible. The existing structure-service check runs first, and structure calls bypass this NPC-station check. The pinned station region also leaves other-region NPC station behavior unchanged.

EveJS 261028.1 checks structure service access (`server/src/services/evermarks/lpStoreMgrService.js:103-122,360-421`) but does not enforce this NPC-station threshold. eve.rs follows the published EVE University [Pochven rule](https://wiki.eveuniversity.org/Pochven) for all three client RPCs.

Local checks on `eve_p4`: `LpStoreMgrServiceTests` passed 6/6, including below/equal/above standing on listing and both purchases; the two LP Store Host wire tests passed 2/2; `PochvenRulesTests` passed 6/6. The graphical EVE client was not exercised.

## Checks and limits

Verified by source and Host wire tests:
- Production route registration for both services; packet dispatch and `machoVersion` removal.
- Station offers, character/corporation purchase success, offer and balance reply shapes, and exact mutation-route errors.
- Structure offer access, structure-based purchase location, `PyInt`/`PyLong` inputs, `machoVersion`, `None` handling, director denial, structure denial, purchase quantity denial, and item-notification-before-reply order.

Source-reviewed only:
- Purchase persistence across a server restart; this task's focused `eve_p4` run covers the Pochven gate and its database mutation boundary, not restart recovery.
- Real graphical-client behavior. The wire tests use the Host TCP test front, not the graphical EVE client.

The focused Host wire run passed 3/3 tests with no skips. On the required local PostgreSQL lane `eve_p6`, `LoyaltyPointStoreTests` passed 8/8 and `LoyaltyPointsServiceTests|AnnouncementsTests` passed 8/8, with no skips. These cover atomic balance updates, replay, insufficient funds, concurrent overspending, notification order, and the LPSvc DB path. The Host wire route test also rejects Evermark issuer `1000419` with `CustomNotify` before invoking the transfer provider. A graphical-client run was not performed.
