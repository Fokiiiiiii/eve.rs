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

- `PacketDispatcher.MaxExpensiveRpcUnitsPerMinute` now defaults to 460.
- `DispatchGoldenTests.Market_region_and_plex_browse_calls_keep_native_rows_through_a_mixed_session` dispatches 40 client-shaped market and PLEX calls through one session and checks the `GetRegionBest` native dictionary and best-order fields on every response.
- `DispatchGoldenTests.Excess_market_traffic_is_refused_before_any_trade_side_effects` accepts 46 cost-10 reads, refuses a following buy request with `SessionRpcBudgetExceeded`, and checks that the refused call did not invoke wallet, inventory, order, or success-notification operations.
- `tools/headless-client/tests/test_market.py::test_market_browse_and_history_refresh_fit_one_session` opens the market group data, reads region best and selected-type orders/history, then refreshes PLEX orders, best prices, and history three times in one client session. The path makes 36 cost-10 market requests (360 units), before any other expensive calls incurred during session setup.

## Verification

- Local: `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Services.Tests/Eve.Services.Tests.csproj -p:UseSharedCompilation=false --no-restore --filter 'FullyQualifiedName~Market_region_and_plex_browse_calls_keep_native_rows_through_a_mixed_session'` passed (1 passed, 0 failed, 0 skipped). It built the touched .NET project and dispatcher dependency.
- GitHub Actions boundary test and live headless walk are pending push.
- No PostgreSQL database was used. The graphical EVE client has not been used for this change.

The working tree also began with nine unrelated deleted Rust source files. They are untouched and must remain excluded from this task's commit.
