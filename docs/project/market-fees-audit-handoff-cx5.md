# Market fee audit handoff

## Result

Fixed the three fee findings in `MarketRules` and `MarketTrading`. Zarzakh sales tax now uses Angel Cartel (500011) and Guristas (500010) and receives the character war faction plus current solar system. Broker commission uses the market solar system's faction-warfare upgrade for stations and structures. Structure broker and SCC charges each apply their own minimum, so a 1,000 ISK order at the default 1% rate costs 100 ISK broker plus 25 ISK SCC.

The Host composition wires the effective war faction callback (character enlistment, then corporation enlistment, then zero). Tests cover both pirate factions, Zarzakh vs elsewhere, non-pirate faction, structure tax, all FW discount levels, separate minimums, and wallet-cent tax settlement.

## V24.01 client evidence

- `eve/client/script/ui/services/marketsvc.py:241-242` passes `session.warfactionid` and `session.solarsystemid2` into `GetSkillLimits`; `marketutil/skilllimits.py:44-78` applies the location discount only when the current system is on the discount list and the faction is pirate FW. `eve/common/script/util/facwarCommon.py:54-58,98-103` identifies the pirate factions; their IDs are 500011 and 500010.
- `marketutil/brokerFee.py:21-40` applies the SCC minimum of 25 ISK only when SCC percentage is nonzero; `:42-61` applies the 100 ISK minimum to broker fee alone. `marketsvc.py:170-185` supplies a 0.5% SCC rate for structures and uses the venue system and session war faction. `brokerFee.py:63-70` applies the faction-warfare percentage before owner standings; `facwarCommon.py:200-202` defines the 10% per level reduction. `marketsvc.py:745-754` gets raw standings from faction/corporation owner toward character.
- The wider client caller search found the sales-tax inputs are current-session values, while broker FW uses the market provider's venue system. UI quote consumers receive raw amounts: `marketsvc.py:153-159`, `buyThisTypeWindow.py:884-894,992-993`, and `sellItemEntry.py:194-201`; `eveFormat.py:24-25,63-73` formats the value. Wallet settlement requires whole cents (`dotnet/src/Eve.Wallet/WalletTransactions.cs:100-106`), so 7.5% of 33.50 ISK displays as 2.5125 but settles as 2.51 ISK.
- Buy-order placement sends order terms, not a client-computed escrow (`marketsvc.py:256-268`); the client reads returned escrow from the server (`marketsvc.py:397-409`). eve.rs reserves `price * remaining quantity` in the same market transaction (`dotnet/src/Eve.Market/MarketTrading.cs:397-402`); fills and modifications adjust it with the remaining order. No discrepancy was found in the checked flow.
- The client gets structure broker rate at the market venue, uses 0.5% SCC, and applies a 1.0% fallback when no owner rate resolves (`marketsvc.py:170-185`; EveJS v0.12.9 fallback at `marketProxyService.js:145-146,1178-1204`). eve.rs keeps the same fallback. `StructureContext` regression continues to assert it.

## Verification

- `dotnet build dotnet/src/Eve.Host/Eve.Host.csproj -p:UseSharedCompilation=false`: passed.
- `dotnet build dotnet/tests/Eve.Market.Tests/Eve.Market.Tests.csproj -p:UseSharedCompilation=false`: passed.
- `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false`: passed.
- Focused Market tests: 21 passed, zero skipped.
- Local full Host shard without PostgreSQL: 610 passed, 232 skipped, 99 failed. The shard produced failures in DB-backed suites as well as skips; no PostgreSQL connection was attempted. CI shard result is pending.
- The first Actions run (`40826959041`, commit `57a0ca90`) found a stale Host wallet expectation: a 40 ISK structure order now charges 100 ISK broker plus 25 ISK SCC, leaving 9,875 ISK. `MarketHostTests` now asserts that result. The same run reported bookmark and repair assertion failures; main commit `e8251dbe` updated those tests. A fresh Actions run for this correction is pending.
- Graphical-client behavior has not been exercised.

## Follow-up

After pushing this correction, rerun the complete `Eve.Market.Tests` and `Eve.Host.Tests` Actions shard, then inspect the push-triggered CI result and update this note with both outcomes. This note must be updated after every push.
