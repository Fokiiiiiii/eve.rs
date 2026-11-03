# NES, PLEX, redeem tokens, and clone grade audit

## Scope and evidence

This review followed the V24.01 client consumers across the redeeming queue, NES checkout, PLEX balance, and Alpha/Omega state. Relevant client paths:

- Redeem UI sends a physical inventory `itemID` to `userSvc.ReverseRedeem`, then refetches the redeeming queue in `OnRedeemingQueueUpdated` (`eve/client/script/ui/services/redeemsvc.py:67-77,162-169`). The UI exposes reverse redeem only for configured legal item groups (`menucheckers/itemCheckers.py:1288-1289`).
- Claim UI sends token dictionaries containing `tokenID`, `massTokenID`, and `typeID`, the target `charID`, and `useHomeStation` (`eve/client/script/ui/shared/redeem/redeemItemsContainer.py:343-356`). The client surfaces UserErrors and token-specific claim errors, refreshes the queue, then reports redeemed items (`redeemsvc.py:99-131)). Redeemed items are displayed through the inventory controller (`eve/client/script/environment/invControllers.py:1187-1204`).
- NES purchase flow calls `storeManager.buy_offer` and treats a falsy result as purchase failure (`vgs/client/vgsClient.py:60-64`, `vgs/client/store.py:85-90`). PLEX balance calls `vaultManager.get_account_balance(PLX)` and defaults a falsy balance to zero (`vgs/client/vgsClient.py:29-31`).
- Alpha/Omega state is primed from `subscriptionMgr.GetCloneGrade()` (`omega/client/clone_grade_svc.py:123-128`); the subscription notification updates the cached grade (`omega/client/clone_grade_svc.py:243-245`). The PLEX panel renders a missing subscription end as Alpha and an end time as Omega, and consumes subscription-time notifications (`plexPanel.py:145-169`).

## Defect fixed: ReverseRedeem did not convert the inventory item

The Host callback used the client-supplied inventory item ID as if a redeem token already existed. It could report success without removing or converting the PLEX item. That left the item available for ordinary inventory use while the client expected it in the redeeming queue.

The current flow validates the authenticated account, item ownership, PLEX type, station hangar flag, location, and positive quantity; it then locks the item and atomically deletes it while persisting a redeem token with its source item and original station (`dotnet/src/Eve.Host/Persistence/NewEden/PersistenceNewEdenStoreRuntime.Part2.cs:387-451`). After commit, the Host queues the normal `OnItemChange` removal update for after the RPC response (`:457-471`), following the Host notification ordering convention (`dotnet/src/Eve.Host/Composition/MailComposition.cs:297-302`). The client performs its queue refresh after the RPC (`redeemsvc.py:67-77,162-169`), so the Host does not send a redundant queue update.

Claiming a reverse-redeemed token at any other station now produces a player-visible `CustomNotify` UserError before item creation; the claim retains source item identity for idempotent retries (`dotnet/src/Eve.Host/Persistence/NewEden/PersistenceNewEdenStoreRuntime.cs:358-405,440-446`). The token listing remains the existing `Ex2` containing `PyPackedRow` values, consistent with the Host wire contract (`dotnet/tests/Eve.Host.Tests/UserSvcRpcHostWireTests.cs:105-125`).

The primary reference’s current `Handle_ReverseRedeem` only logs and returns true (`src/server/src/services/user/userService.js:1096-1099`); it does not perform the player-visible transition. The EVE University [Accounts page](https://wiki.eveuniversity.org/Accounts) says reverse-redeemed PLEX can only be redeemed at the location where it was reversed. The implementation follows that mechanic rather than the stub. The difference is recorded in `docs/reference/compat/DEVIATIONS.md`.

The new PostgreSQL Host wire test exercises invalid item rejection without item loss, item deletion, no notification before the response and one notification after it, retry without a second notification, station mismatch with the token retained, successful claim with one item created, and reverse retry after claim (`dotnet/tests/Eve.Host.Tests/NewEdenStoreHostTests.cs:139-207`). I temporarily removed the transactional item deletion and ran the focused test: it failed at the item-presence assertion, then passed after restoring deletion.

The broader `userSvc` wire test also sent an item ID belonging to a fabricated QA redeem token and asserted success. That is not a physical inventory item accepted by V24.01's `ReverseRedeem(item.itemID)` call. It now checks both a player-visible UserError for an unavailable ID and a real PLEX item’s success, deletion, and token listing (`dotnet/tests/Eve.Host.Tests/UserSvcHostWireTests.cs:65-81`; client call: `eve/client/script/ui/services/redeemsvc.py:67-77`). The old test failed locally and in push CI before this correction.

## Reviewed flows with no change

- NES catalog purchase stays unavailable until a payment settlement provider is wired. The client treats a falsy result as failure, and the Host does not grant items, PLEX, Omega time, or training slots without settlement (`dotnet/src/Eve.Services.Core/StoreOffersService.cs:318-330`, `docs/reference/compat/DEVIATIONS.md` under “Store checkout remains disabled”). This matches the current fail-closed Host composition; enabling it would create charge-free entitlements. The current EveJS purchase code validates PLX and settles purchases through its fulfillment path (`server/src/services/account/storeManagerService.js:98-155`, `server/src/services/account/storeFulfillment.js:186-252`).
- PLEX balance is returned as a numeric balance and the UI treats None/zero as zero. Host scopes rows to the authenticated account’s characters and sums their PLEX balances (`dotnet/src/Eve.Host/Persistence/NewEden/PersistenceNewEdenStoreRuntime.cs:204-209`, `dotnet/src/Eve.Wallet/PgWalletStore.cs:78-127`). CCP documents the PLEX Vault as shared among characters on one account ([CCP ISK and PLEX](https://support.eveonline.com/hc/en-us/articles/14141550499612-ISK-and-PLEX)); this account scope is appropriate. EveJS reads the current character wallet (`server/src/services/account/vaultManagerService.js:20-31`).
- Subscription state is account-owned. Host reads the stored end time and clone grade (`dotnet/src/Eve.Services.Core/SubscriptionMgrService.cs:108-170`); transitions persist the new end first and then notify open account sessions (`dotnet/src/Eve.Host/Hosting/SubscriptionTransitions.cs:14-18`). EveJS returns null for no subscription time and its computed clone grade (`server/src/services/subscription/subscriptionMgrService.js:31-45`), matching the client’s Alpha/Omega rendering behavior.

## Checks and boundaries

- Passed: focused PostgreSQL Host test `Reverse_redeeming_plex_moves_the_item_to_a_station_locked_token_and_claims_it_once`.
- Passed: 17 tests across `NewEdenStoreHostTests`, `NewEdenEntitlementHostTests`, and `UserSvcRedeemTokenPersistenceTests`; 0 skipped.
- After correcting the stale wire fixture: 18 tests across those three classes plus `UserSvcHostWireTests` and `UserSvcRpcHostWireTests`; 0 skipped on `eve_p3`.
- The graphical V24.01 client was not used. Checkout behavior remains intentionally unavailable pending a settlement provider. No separate end-to-end test was added for real-money fast checkout.
