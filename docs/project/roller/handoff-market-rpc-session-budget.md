# Market RPC session budget handoff

## Scope and behavior

The V24.01 client calls `marketQuote.GetRegionBest()` from `MarketBase._GetAsksForRange` in region view. It stores the response by type ID and reads the best-order fields. EveJS v0.12.9 and 261018.1 both return a type-ID keyed summary dictionary with best-order values. The dispatcher charges each listed market read 10 units and previously allowed 160 units per rolling minute.

## Trace measurement

Using `tools/mitm/mitm show` filtered to `marketProxy`, the cost-10 market methods, and client-to-server direction, the anonymized 2026-10-23 captures contained:

| Capture | Requests | Weighted units |
|---|---:|---:|
| `eve-rs-10.0.402-client-retry.jsonl` | 34 | 340 |
| `eve-rs-10.0.402-graphical.jsonl` | 40 | 400 |
| `eve-rs-graphical.jsonl` | 46 | 460 |
| `evejs-graphical.jsonl` | 40 | 400 |

Each listed method costs 10 units. Counts include attempted requests, including requests whose server replies were errors. No packet rows, argument values, or player names were printed. The smallest ceiling that accommodates the largest measured request set at current weights is 460 units per rolling minute; this still caps traffic at 46 cost-10 requests.

## Changes

- `PacketDispatcher.MaxMarketRpcUnitsPerMinute` is 460; the general/inventory limit remains 160 and industry uses its own 160-unit limit.
- `DispatchGoldenTests.Market_region_and_plex_browse_calls_keep_native_rows_through_a_mixed_session` dispatches 40 client-shaped market and PLEX calls through one session and checks the `GetRegionBest` native dictionary and best-order fields on every response.
- `DispatchGoldenTests.Excess_market_traffic_is_refused_before_any_trade_side_effects` accepts 46 cost-10 reads, refuses a following `PlaceMultiSellOrder` request with `SessionRpcBudgetExceeded`, and checks that the refused call did not invoke wallet, inventory, order, or success-notification operations.
- `tools/headless-client/tests/test_market.py::test_market_browse_and_history_refresh_fit_one_session` opens the market group data, reads region best and selected-type orders/history, then refreshes PLEX orders, best prices, and history three times in one client session. Its rowset decoder uses the wire `columns` field because the descriptor header decodes as `ObjectEx`. The path makes 36 cost-10 market requests (360 units), before any other expensive calls incurred during session setup.

## Verification

- Local: `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Services.Tests/Eve.Services.Tests.csproj -p:UseSharedCompilation=false --no-restore --filter 'FullyQualifiedName~Market_region_and_plex_browse_calls_keep_native_rows_through_a_mixed_session'` passed (1 passed, 0 failed, 0 skipped). It built the touched .NET project and dispatcher dependency.
- Actions run `39955392053` confirmed the Host startup fix and passed the .NET solution build, both selected `Eve.Market.Tests`, the mixed browse dispatcher test, and two existing live market tests. The live walk received successful results through the new-price history call; the test then failed because its rowset helper expected `header` to be a list although the wire carries an `ObjectEx`. The helper now maps rows using the `columns` field. The abuse test exposed a test setup error: `PlaceBuyOrder` is not assigned expensive-RPC cost, so the boundary now sends the charged `PlaceMultiSellOrder` method.
- Rerun the focused Actions job on the follow-up commit. Local Host build could not complete: `project.assets.json` was missing, and restore stalled until cancelled. No PostgreSQL database was used locally. The graphical EVE client has not been used for this change.

The working tree also began with nine unrelated deleted Rust source files. They are untouched and must remain excluded from this task's commit.


## 2026-10-23 station inventory trace follow-up

- The station inventory report’s `GetInventory(10004, None)` budget refusal comes from the 05:29 UTC graphical capture, before `e1ed09a4` (06:35 UTC) separated inventory calls from unrelated expensive RPCs. It is the same shared-bucket starvation documented in `REAL_CLIENT_DIAGNOSTICS.md` (d), not a new failure against the current dispatcher.
- The current dispatcher keeps inventory bounded at 160 units per session and applies nested `MachoBindObject` costs to that bucket. `DispatchGoldenTests.Inventory_calls_keep_a_bounded_budget_separate_from_other_expensive_services` passed locally (1 passed, 0 failed, 0 skipped); it proves unrelated expensive calls do not consume the inventory allowance and that the inventory cap still refuses excess work.
- The later inventory UI scout did not inspect captures or assert a row/capacity wire mismatch. No code change or graphical-client retest was needed for this follow-up.

## Client-behavior evidence index (2026-10-24)

This index covers the service families exercised by the market flow; it links the tests that assert client calls, response shapes, persisted effects, or notifications. “Client-contract” means a V24-shaped request/response is exercised against the Host or its headless endpoint. “Differential” means the same scenario is run against EveJS and eve.rs.

| Service family | Client-behavior tests | Evidence and remaining gap |
|---|---|---|
| `marketProxy` | `tools/headless-client/tests/test_market.py::test_a_docked_character_reads_the_market_as_the_client_does`; `test_a_seller_and_a_buyer_trade_through_the_market`; `test_market_browse_and_history_refresh_fit_one_session` | Client-shaped asks/orders/transaction rows and named refusals; the trade walk checks wallet, escrow, item delivery, cancel and modification. The new docked-read test fills the missing explicit empty-book and refusal contract. No same-scenario EveJS comparison is recorded. |
| Upwell structure market and docking | `tools/headless-client/tests/test_market.py::test_docked_corporation_members_trade_at_an_eligible_upwell_structure` | The two-pilot walk exercises V24 docking, structure market discovery, resting and matched orders, remaining stock, wallet journal, persisted item custody, and notification order against the persisted Fortizar. After the fill and cancellation, it reads buyer and seller structure hangars through the V24 global-assets moniker. No same-scenario EveJS differential or graphical-client run is recorded. |
| `invbroker` station inventory | `test_a_seller_and_a_buyer_trade_through_the_market`; `test_docked_corporation_members_trade_at_an_eligible_upwell_structure` | The station and Upwell trade walks read hangar rows through the V24 `charMgr` global-assets moniker. The Upwell case also checks those rows against persisted quantities after settlement and cancellation. General inventory rowsets and mutation methods are covered separately by the inventory Host tests. No same-scenario EveJS comparison is recorded. |
| Wallet and account notifications | `test_a_seller_and_a_buyer_trade_through_the_market`; `test_docked_corporation_members_trade_at_an_eligible_upwell_structure` | The walks reconcile balance changes with journal rows and assert account-change notifications precede order-change notices. The market-client flow has no EveJS differential. |

The two new contract cases in this handoff are the docked station read/refusal test and the two-character Upwell market walk. The first checks the basic `marketProxy` contracts on an empty book; the second covers the highest-impact cross-service market flow through docking, settlement and committed notifications. Both use the V24 headless client. The Upwell inventory helper invokes `ListStationItems` through `charMgr.MachoBindObject` with `(characterID, containerGlobal)` and checks persisted inventory quantities as well.

### Verification boundary

The service-level and Host tests listed in this handoff prove their named assertions only. The two new client walks require the live headless workflow and scratch schemas; their pass status is recorded after Actions completes. These tests do not establish native graphical-client acceptance or EveJS differential parity.

## 2026-10-24 CI run 745 notification-count follow-up

Run 745 failed two `Eve.Market.Tests.ProtocolTests` cases because immediate fills now emit the EveJS-compatible `OnOwnOrdersChanged` refresh with reason `Filled`. The native market protocol test now expects two notifications after its immediate buy and later sell order, and three after cancel. The PLEX history case expects three after its resting bid, sell order, and immediate fill. `Eve.Market.Tests` Release build passed locally with 0 warnings and 0 errors. Focused Actions results are pending; no local PostgreSQL test was run.

## 2026-10-24 headless Upwell docking setup follow-up

- The fast live E2E gate on run 747 (40046843651, 0fd2ca16) ended with MachoError: macho error 2 at call ID 525. The available CI log tail does not name the pytest case. The warp-before-dock helper was then exercised by later Actions runs.
- V24.01 eve/client/script/ui/services/structure/structureDocking.py:64-81 routes Dock through autoPilot.NavigateSystemTo(structureID, 2500, RequestDocking). The autopilot warps to an in-range item using CmdWarpToStuff before it calls Dock. EveJS server/src/services/structure/structureDockingService.js:134-179 follows a distant target and returns the approach refusal; the headless helper was repeating that RPC from 200,000 km away without the client warp.
- The helper now issues the client-shaped beyonce.CmdWarpToStuff("item", structure_id, minRange=0), waits for the warp's Stop destiny action, then requests structureDocking.Dock and verifies completion. No production service behavior changed.
- python3 -m py_compile tools/headless-client/tests/test_market.py passed. The post-change focused Actions check and push-triggered CI are pending. No local PostgreSQL test or graphical-client run was performed.


## 2026-10-24 Upwell item custody assertion follow-up

- Push CI run 749 (40049260674, 30dcbf9d) passed its Rust, .NET, and Windows/headless jobs but failed the Python parser test (fixed in 3ac28805) and the live Upwell walk at its inventory assertion. The client RPC `invbroker.ListStationItems(structure_id)` returned an empty list because that RPC lists station locations and the structure ID is not a station. This was a test assumption, not a failed market settlement: the walk had already read successful transaction rows for buyer and seller.
- The Upwell walk now checks the committed item quantities in the inventory store, keeping its V24 market order and transaction assertions while avoiding a station-only inventory RPC. The station walk continues to prove `ListStationItems`.
- Focused Actions run 255 (40049274673, 30dcbf9d) failed during the same pre-correction version. A rerun against the corrected commit is pending. The separate focused run 40050216216 targets the prior commit and does not validate this correction.
- No local database or graphical client was used. The structure inventory client presentation now has a client-shaped assertion; its focused Actions result is pending.

## 2026-10-24 V24 global-assets inventory moniker follow-up

- V24 `invCache.GetInventory(const.containerGlobal)` creates a `charMgr` moniker bound to `(session.charid, 10002)` and reads `ListStationItems(stationID)` through it. A direct `invbroker.ListStationItems` call does not carry the global-assets context and returns no rows for a structure ID.
- The shared hangar helper now follows that wire route. The Upwell market walk checks the buyer's and seller's visible structure rows after the fill, then checks the seller's rows after cancelling the remaining order; the existing persisted-quantity checks remain.
- The Host binds `charMgr.MachoBindObject` to the global-assets inventory provider. `InvBrokerService.ListStationItems` permits a structure location in `GlobalAssets` context after validating it as a dockable asset location, then reads the character's items at that location.
- Local `python3 -m py_compile tools/headless-client/tests/test_market.py` passed. Focused Actions validation is pending. No local database or graphical-client run was used.


## 2026-10-24 CI run 757 order escrow follow-up

- Push CI run 757 (40050603403, 676f26f6) passed Rust checks/tests, Python client tests, the .NET build and shards, fixtures, and the Windows matrix. Its live Upwell market walk failed because it expected the seller's two unfilled units in the structure hangar before cancelling the open ask.
- The order retains the unfilled quantity in escrow until cancellation. The walk now checks buyer delivery after the fill and seller return after cancellation; it does not count escrowed stock as hangar inventory before cancel.
- The structure inventory client path was subsequently added via the V24 global-assets charMgr moniker, and persisted inventory checks remain. Focused Actions runs against the pre-escrow-assertion fix do not establish the corrected walk. No local PostgreSQL or graphical-client run was used.


## 2026-10-24 CI run 762 wallet journal and RPC inventory follow-up

- Push CI run 762 (40052033248, b5850087) passed Rust checks/tests, Python client tests, .NET build and most .NET shards, plus the Windows matrix. Its fast live Upwell walk showed a buyer journal of `(46, -99.5), (149, -0.5), (42, -100.0)` after filling a standing bid; the buyer's 100 ISK had already been paid into escrow, so the fill uses journal ref 42 rather than a second ordinary purchase ref 2. The client test now asserts the escrow settlement row.
- The same push exposed the RPC inventory snapshot count changing from 1382 to 1383 after the concurrent host registration change. The registry test's expected count is updated to 1383.
- Focused Actions validation for the journal assertion and registry count is pending. The local Host test-project build was cancelled after it remained silent while compiling its dependency graph; no local database or graphical client was used.

## 2026-10-24 market global-assets walk Actions follow-up

- Push CI run 769 (`40053234501`) on `4307511c` includes the global-assets inventory test (`d3d45b18`) and escrow-custody correction (`b5850087`). The full required gate passed, including End-to-end in 11.4 minutes.
- Focused Actions run 266 (`40051416649`) on `d3d45b18` failed after 41.8 minutes: the live Upwell test expected the seller's unfilled two units in the hangar before cancellation, while the order held them in escrow. The test now checks zero before cancellation and two after; run 769 passed the corrected flow.
- Focused market run 292 (`40054489401`) on `4619fa62`, selected with `--projects none --live test_market.py`, succeeded in 8.1 minutes with no failed tests. The success summary does not report a skip count.
- Full CI runs 772 (`ef6286e9`) and 773 (`21744fd5`) passed every selected job and the required gate. The market and InvBroker paths did not change after run 773 through current main `8e9dbd0c); both fix commits are ancestors of main.
- Run 774 (`40054348279`, `4619fa62`) passed End-to-end in 14.0 minutes, Windows in 14.2 minutes, and every job except `.NET (space tests 2/2)`. That test shard was cancelled after 15.3 minutes, before a test result summary appeared; the required gate failed because the job was cancelled. No market or E2E failure was reported.
- Local `python3 -m py_compile tools/headless-client/tests/test_market.py` passed. No local database, graphical-client run, or EveJS differential was performed.
