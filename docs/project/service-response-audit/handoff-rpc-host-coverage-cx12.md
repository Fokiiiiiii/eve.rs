# Host RPC coverage handoff: CX12

Scope: RPC-01767–RPC-01768, RPC-01839–RPC-01845, RPC-02109–RPC-02110, RPC-02302–RPC-02303, RPC-02400–RPC-02401, and RPC-00668–RPC-00678.

## V24.01 client trace and EveJS comparison

| Rows | Client call and consumer evidence | EveJS v0.12.9 |
|---|---|---|
| RPC-01767, RPC-01768 | `characterSelectionUtils.py:71-75` returns the remote boolean. All consumers are in `characterSelection.py:353,931` and `characterSlots.py:161,456,574,967`; they branch on that boolean to style the selection screen and slots. | `server/src/services/newEdenStore/kiringMgrService.js:152-156` returns the configured boolean. |
| RPC-01839, RPC-01840, RPC-01842–RPC-01845 | The two character-creation containers call `machoNet.GetGlobalConfig().get(...)` at `characterCreationContainer.py:2684-2685` and `characterCreationContainer_new.py:1536-1537`. `ccUtil.py:76-84` and `charcreation_new/ccUtil.py:77-85` read the same dictionary for disabled factions and schools. A whole-tree search also found dictionary consumers in `eveCommands.py:3570-3622`, `evePhotosvc.py:430,714`, `flightControls.py:47,376`, and other settings gates. | `server/src/services/machoNet/machoNetService.js:340-343` returns the global-config dictionary. |
| RPC-02109, RPC-02110 | `characterSelection.py:620-625` sends the computer hash; its callers at lines 615 and 634 stop login and open the multi-login warning when the result is false. | `server/src/services/login/multiLoginBlockerService.js:163-206` checks and records the hash. |
| RPC-02302, RPC-02303 | `characterSelection.py:742-760` reads a mapping of solar-system ID to slot token, offers alternatives when there is more than one, and echoes the chosen system/token before entering the character. | `server/src/services/populationCap/populationCapService.js:178-189` returns the single-entry mapping. The V24 move call has no same-named method in the v0.12.9 source; eve.rs validates the offered system/token and persists the selected character location. |
| RPC-02400, RPC-02401 | `searchUtil.py:272-281` consumes Query's dictionary keyed by result type and sorts/primes its ID lists. Lines 296-301 return QuickQuery's flat ID list; callers across `searchOld.py:21-51,79-124`, `killRights.py:352`, `bountyWindow.py:284`, `contractsearch.py:193,1002`, `mapSvc.py:283`, and `eveEditPlainText.py:289` consume those IDs. | `server/src/services/_other/searchService.js:415-429` returns the category map; lines 431-452 flatten and de-duplicate IDs. |
| RPC-00668, RPC-00669 | `lpStoreSvc.py:95-99` caches the returned offers. `lpStorePanel.py:209,247`, `paragonLpStorePanel.py:72`, and `lpStoreSvc.py:352` feed them into the offer list. The UI reads offer IDs, LP/ISK costs, required items and type/quantity fields. | `server/src/services/evermarks/lpStoreMgrService.js:360-381` returns offers. |
| RPC-00670–RPC-00673 | `lpStoreSvc.py:239-266` chooses the character or corporation purchase RPC and uses its boolean to show success. Callers include `lpOfferEntry.py:131,381` and `lpOfferEntry2.py:182`. | `lpStoreMgrService.js:383-421` routes both purchases through the offer purchase logic. |
| RPC-00674 | `lpStoreSvc.py:101-103` forwards the selected corporation and amount; the UI action is `lpExchangeDialog.py:100`. Wallet state is refreshed through LP change notifications. | `server/src/services/corporation/lpService.js:166-169` is a stub. eve.rs refuses this unsupported operation with a client error, as documented in DEVIATIONS.md, Agents slice AG-1. |
| RPC-00675, RPC-00676 | `loyaltyPointsWalletSvc.py:29-39` converts each two-column row into an issuer-to-balance map. Wallet panels, LP store affordability checks and donation panels consume the maps; `OnLPChange`/`OnCorpLPChange` invalidate them at lines 53-63. | `lpService.js:95-109` returns the character and corporation balance rowsets. |
| RPC-00677, RPC-00678 | `loyaltyPointsWalletSvc.py:93-100` forwards receiver corporation, issuer corporation and amount from `corpDonateLPPanel.py:46` and `personalDonateLPPanel.py:45`. The callers refresh from notifications; they do not consume a success payload. | `lpService.js:122-154` contains the matching methods. eve.rs currently returns an explicit error because destination, Evermark, director and membership checks are not implemented; this is recorded in the AG-1 deviation. |

## Change and regression coverage

- `SearchQueryService` now registers `QuickQuery`, returns a de-duplicated flat ID list, supports the client’s NPC and alternate-name flags, preserves Query ranking, and caps each result group at 500.
- `SearchQueryServiceTests.QuickQuery_returns_a_unique_flat_list_and_honors_alias_and_npc_filters` checks list shape, duplicate IDs, ticker aliases with the V24 `exact=-1`/`onlyAltName=True` request, and NPC filtering.
- `SearchQueryHostTests.Host_registers_search_and_returns_the_client_query_wire_shape` builds the real Host composition and checks registration and the empty-list wire shape, including an extra `machoVersion` keyword.
- The actual network walk `test_search_query_and_quickquery_use_the_registered_host_route` sends both search methods through the live Host and compares their Rifter type IDs. `test_login_configuration_and_loyalty_rpc_routes_keep_their_wire_contracts` covers the Kiring, machoNet, multi-login and LP methods with `machoVersion`; the population-cap walk checks a valid move and invalid choices through the bound Host route.
- The contract batch wire test now correlates each received response with the request call ID from the returned client address. The full CI shard had consumed an unrelated Host send before the `DeleteContract` response; the focused reproduction passed, and the response correlation removes that ordering assumption.
- Registering `QuickQuery` raises the Host RPC inventory by one method; `RpcSecurityInventoryTests` now expects 1,400 registered rows.
- Locally: `Eve.Services.Core` and `Eve.Host.Tests` builds succeeded; the focused QuickQuery test passed (1 passed, 0 skipped). No database was used locally and no graphical-client test was performed.

## Actions status

- On `63a64d23`, the focused Core and Host Actions runs succeeded (40190235392, 40190236662). Push-gate CI (40190222770) failed in the contract batch wire helper at `DeleteContract`; its end-to-end job succeeded. The selected full `test_login_to_character.py` run (40190235213) is still being followed.
- On `5dbc0a2c`, the V24 alias-mode Core test passed on Actions (40191564033), and the isolated contract batch Host test passed (40191989482). Push-gate CI (40191553927) remains in progress.
- Re-run the Host shard and inspect both push-gate CI runs after the response-correlation fix is pushed. Record final run results here before handoff.
