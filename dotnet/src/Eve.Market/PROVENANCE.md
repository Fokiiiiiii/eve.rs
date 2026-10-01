# Market provenance

MarketRules.cs ports the rules and arithmetic order of `Fokiiiiiii/evejs-v0.12.9`, commit `828823a75f211fdecb945b087a26c225f111491f`, `server/src/services/market/marketRules.js` (ADR 0009). `oracle/fixtures/market/rules.json` is recorded by `tools/dev/gen-market-rules-fixture.js`: 72 contexts and 504 fee cases, with the database lookups supplied as inputs. No source text is vendored.

Client contract checks use the private `Fokiiiiiii/eve-logic` snapshot; its source and schemas are never copied here. Fees retain the reference's double arithmetic, while committed balances, escrow and fills use PostgreSQL decimal cents.
