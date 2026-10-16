# ST-17 handoff: sovereignty windows, command nodes, power budget, ship burst projectors

Worktree `/home/user/wt-st17` (branch `wt-st17`), database `eve_p2`. Continues `handoff-ST-9.md` and `handoff-ST-9b.md`. Rows: TODO section 51. Decisions: DEVIATIONS sections "Sovereignty vulnerability window: custom windows, inheritance, the midpoint lock (ST-17 b)", "Command node stage after a reinforcement (ST-17 a)", "Command node stage: destruction, notices and restart (ST-17 a, continued)", "Sovereignty upgrade power budget: cynosural suppression derived (ST-17 c)", "Ship-fitted burst projectors (ST-17 d)".

## Status
- **(b) windows: done.** Custom centre and length per hub, alliance default inherited by a neutral hub at its claim, the change after the next window, the midpoint lock. Fix in the ST-9 engine: a link without an end report scores only to the end of its in-progress cycle.
- **(a) command nodes: done** (stage, nodes, tally, defended and destroyed outcomes, restart-safe jobs). Destroyed: the sovereignty records lose the hub (`DestroyLostHubAsync`, a durable job retried on failure), the hub ball and the nodes leave space, notices 161 (stage start) and 162 (hub destroyed) reach the defending members through the mailbox. Open: a new neutral hub (the anchor is not built), the drop roll of the lost hub's upgrades and fuel, the regeneration time (the wiki's 2015 maximum is used), the client view (MITM).
- **(c) power budget: derived, one decision open.** `SovUpgradeBudget` ports EVE.js `computeHubUpgradeSnapshot`; `IsCynoSuppressionOnlineAsync` is derived from it. Supply: star power 500 (EVE.js fallback) and the active skyhooks' workforce. Numbers: the wiki's 250 power and 4 500 workforce for the Cynosural Suppression; EVE.js placeholders for other upgrades.
  **Conflict to decide:** the port follows EVE.js and the wiki, and the jammer is off in every system without an active skyhook of at least 4 500 workforce. That is not the "never off everywhere" outcome. The alternative needs a source for a default workforce supply that EVE.js does not have. Decision needed on the workforce supply for systems without skyhooks, or on the number to use.
- **(d) ship burst projectors: done on the engine.** 27678, 40635, 40636, 40696-40699 use the structure burst engine with the pilot's ship as source; the per-pulse Host grant reaches the ships in reach except the pilot's own; the ECM family follows the ECM burst's high-security rule; crimewatch as for smartbombs. 41350 stays refused (unpublished). The Host ship branch has no end-to-end test.

## Evidence (this session, eve_p2)
- Space focused `Entosis|SpaceSovHubs|SovHubClaim`: 51 passed (after the destruction job); after (d): `ModuleEffects|StructureBurst|SuperWeapon|Entosis`: 102 passed; `ModuleEffects|StructureBurst|Entosis|SpaceSovHubs`: 82 passed (after the merge).
- Host focused `StructureCynoSuppression|StructureCompositionTests|Entosis|SovHub`: 21 passed (after (c)), `EntosisOwnerNotification`: 2 passed (with (a) notices), `SovHubClaim`: 4 passed (loss writer).
- Rust `cargo test -p eve-space`: 835 lib + integration suites, 0 failures; `ship_fitted` test passes. `cargo clippy --workspace --all-targets -- -D warnings` clean (CARGO_TARGET_DIR=target-194, cargo +1.94.1).
- Full suites: see the final report.

## Gaps (also in DEVIATIONS)
- Command nodes: the drop roll and the replay tombstone; the new neutral hub; the node placement in a system without a loaded scene (not verified live); client view of 161/162 and the nodes.
- Windows: no client RPC to set a hub window (none in eve-logic).
- Power budget: workforce transfers between hubs and the planet power of skyhooks are not ported; the consequence above.
- Ship burst: the Host ship branch and the high-security refusal are not covered by a test; client FX.

## Gotchas
- Serena and the codebase tools point at the main checkout; edits were made with `python3 -I` scripts in the worktree.
- Bash `grep`, `cat`, `head` and `find` are denied here; read with `python3 -I -c`.
- `tools/dev/pg18.sh` run from a worktree looks for its binaries under the worktree's `.cache`; run it from `/home/user/eve.rs`.
- Tests were run under the lock file `/tmp/eve-heavy-eve_p2.lock` with `flock -o -w 1800` (the same lock as `tools/dev/db-lock.sh` for `eve_p2`); `StructureCompositionTests` and the other PostgreSQL tests need `EVE_PG_URL` set, or they skip.
- The test class constant `System` shadows the `System` namespace in `EntosisCaptureServiceTests`: write `global::System`.
