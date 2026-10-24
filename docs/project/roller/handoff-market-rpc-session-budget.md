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
| Upwell structure market and docking | `tools/headless-client/tests/test_market.py::test_docked_corporation_members_trade_at_an_eligible_upwell_structure` | The new two-pilot walk exercises V24 docking, structure market discovery, resting and matched orders, remaining stock, wallet journal, item custody, and notification ordering against the persisted Fortizar. No EveJS differential or graphical-client run is recorded. |
| `invbroker` station inventory | `test_a_seller_and_a_buyer_trade_through_the_market`; `test_docked_corporation_members_trade_at_an_eligible_upwell_structure` | The walks read the station/structure hangar before and after settlement. They cover the market custody path; general inventory rowsets and mutation methods are covered separately by the inventory Host tests. No same-scenario EveJS comparison is recorded. |
| Wallet and account notifications | `test_a_seller_and_a_buyer_trade_through_the_market`; `test_docked_corporation_members_trade_at_an_eligible_upwell_structure` | The walks reconcile balance changes with journal rows and assert account-change notifications precede order-change notices. The market-client flow has no EveJS differential. |

The two new contract cases in this handoff are the docked station read/refusal test and the two-character Upwell market walk. The first checks the basic `marketProxy` contracts on an empty book; the second covers the highest-impact cross-service market flow through docking, settlement and committed notifications. Both use the V24 headless client and assert actual Host responses rather than calling service classes directly.

### Verification boundary

The service-level and Host tests listed in this handoff prove their named assertions only. The two new client walks require the live headless workflow and scratch schemas; their pass status is recorded after Actions completes. These tests do not establish native graphical-client acceptance or EveJS differential parity.

## 2026-10-24 CI run 745 notification-count follow-up

Run 745 failed two `Eve.Market.Tests.ProtocolTests` cases because immediate fills now emit the EveJS-compatible `OnOwnOrdersChanged` refresh with reason `Filled`. The native market protocol test now expects two notifications after its immediate buy and later sell order, and three after cancel. The PLEX history case expects three after its resting bid, sell order, and immediate fill. `Eve.Market.Tests` Release build passed locally with 0 warnings and 0 errors. Focused Actions results are pending; no local PostgreSQL test was run.

## 2026-10-24 headless Upwell docking setup follow-up

- The fast live E2E gate on run 747 (40046843651, 0fd2ca16) ended with MachoError: macho error 2 at call ID 525. The available CI log tail does not name the pytest case. Focused test_market.py Actions run 252 (40048337454) is still running against the earlier helper.
- V24.01 eve/client/script/ui/services/structure/structureDocking.py:64-81 routes Dock through autoPilot.NavigateSystemTo(structureID, 2500, RequestDocking). The autopilot warps to an in-range item using CmdWarpToStuff before it calls Dock. EveJS server/src/services/structure/structureDockingService.js:134-179 follows a distant target and returns the approach refusal; the headless helper was repeating that RPC from 200,000 km away without the client warp.
- The helper now issues the client-shaped beyonce.CmdWarpToStuff("item", structure_id, minRange=0), waits for the warp's Stop destiny action, then requests structureDocking.Dock and verifies completion. No production service behavior changed.
- python3 -m py_compile tools/headless-client/tests/test_market.py passed. The post-change focused Actions check and push-triggered CI are pending. No local PostgreSQL test or graphical-client run was performed.
