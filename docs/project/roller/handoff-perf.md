# Performance pass handoff

The source-level audit is recorded in `docs/project/roller/perf.md`.

This worktree began with nine unrelated tracked source deletions and was ten commits behind `origin/main`. The deletions were restored as unrelated interrupted-run residue; `origin/main` was fetched and merged.

The task instruction explicitly prohibited PostgreSQL use. Therefore this pass did not run seeded-database timings or EXPLAIN, add a speculative index, change production code, or run tests/builds. Source inspection identified:
- `MarketStore.BookAsync`: two SQL reads (asks and bids) under one repeatable-read transaction; the order indexes include station before id while the SQL additionally orders by issue time.
- `GetMarketOrderHistory`: two owner-order reads (personal and corporation) and a read-triggered expiry sweep.
- Character selection has an account index; inventory has owner and location indexes.

Next step to complete the requested measured optimization is a run on the task's authorized seeded lane: collect EXPLAIN (ANALYZE, BUFFERS), row counts, and repeated p50/p95 for the listed query shapes, identify the highest-cost path, make one evidence-backed query/index change, and record before/after results and regression checks in `perf.md`.

No database was accessed, and no tests/builds were run.
