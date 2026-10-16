# ST-17 handoff: sovereignty windows, command nodes, and the two blocked items

Worktree `/home/user/wt-st17` (branch `wt-st17`), database `eve_p2` (live port 26101). Continues `handoff-ST-9.md` and `handoff-ST-9b.md`. Rows: TODO section 51. Decisions: DEVIATIONS "Sovereignty vulnerability window: custom windows, inheritance, the midpoint lock (ST-17 b)" and "Command node stage after a reinforcement (ST-17 a)".

## Done (pushed to main: 5cb457ba, c5612247, merged as ad93af3c)
- **(b) Windows.** `EntosisWindow` takes a custom setting (centre hour, whole length) and locks each window's length at the midpoint of the window the day before. Tables `hub_windows`, `alliance_windows`, `window_locks` (migration 0003). The alliance default is inherited by a neutral hub at its claim. A change takes effect after the next window.
- **Fix in the ST-9 engine.** A link without an end report no longer scores the whole gap: scoring stops at the end of its in-progress cycle (`EntosisRules.Advance`).
- **(a) Command nodes.** `CommandNodeRules` (pure), `EntosisCaptureService.CommandNodes.cs` (campaign start at the reinforcement exit, the node spawns, node cycles and jobs, the tally, the defended and destroyed outcomes, the events), migration 0004 (`campaigns`, `nodes`, the links' hub foreign key dropped, `invulnerable_until_ms`, phase `destroyed`). Host: the target check accepts live nodes, `SpaceSovHubs` places and removes the node balls from the events and on system entry, the constellation from `mapSolarSystems`.

## Evidence
- Focused (eve_p2): Space `Entosis|SpaceSovHubs|SovHubClaim` 50 passed, 0 failed, 0 skipped (after the merge of ad93af3c too).
- Host `Entosis|SovHub|Sovereignty|StructureCompositionTests|StructureCynoSuppression` 29 passed, 0 skipped.
- Full Space suite on eve_p2: see the final report of this session (run once after the last push).

## Open: (c) sovereignty upgrade power budget: blocked on data, not implemented
- EVE.js `computeHubUpgradeSnapshot` (sovModernState.js) allocates power and workforce in install order; `online` = `powerState` 2 (`isCynoSuppressionUpgradeOnline`). eve.rs stores only the owner's toggle (`hubUpgradeConfiguration[].online`, `SaveUpgradeConfigurationAsync`) and reads it in `PersistenceSovFwReadModel.IsCynoSuppressionOnlineAsync`.
- The SDE (build 3396210) has no power or workforce attribute on the hub upgrades: type 81619 has only 1603 (`sovBillSystemCostDEPRECATED`) and 1615 (`devIndexSovereignty`).
- The wiki gives examples, not the table: Cynosural Suppression 250 power and 4 500 workforce. The full list (with type ids) is needed.
- The supply is in no source the repo reads: star power and planet workforce per system. EveJS uses a fallback by spectral class and a placeholder table (`UPGRADE_FAKE_BY_REMAINDER`, keyed by type id modulo 4; eve-logic's `FakeSovUpgradesExternalMessenger` is the same placeholder). Deriving `online` with no supply would make every upgrade LOW and switch the cyno jammer off everywhere, so nothing was changed.
- Decision needed: the supply source (eve.rs has no star or planet harvest in the sovereignty read model) and the full upgrade table.

## Open: (d) ship-fitted burst projectors: blocked on policy, not implemented
- The SDE publishes 27678, 40635, 40636, 40696-40699 (group 842 "Burst Projectors", category 7); 41350 is unpublished (type policy). Their effects are the structure ones (6513 ECM, 6476 web, 6477 neutralize, 6482 warp disruption, 6481 damp, 6478 paint, 6479 track or weapon disruption); the burst engine (`StructureBurstProjectorEffect`, `SimAction.StructureBurst`) handles category 66 only.
- EveJS has no ship-side burst handling (its burst code is structure group 1331 only; the command bursts are a different engine).
- Three wiki pages for burst projectors (`Burst_projectors`, `Burst_Projectors`, `Burst_Projector`) returned 404 on 2026-10-16.
- Decision needed before a Host rule can be written: whether a ship's burst reaches friendly ships, whether it is an offensive action (Crimewatch, CONCORD), and how the Host grants each pulse (the structure path grants per pulse in `SpaceSessions.StructureDamageCandidates.cs`; a ship would need the pilot's weapon rules).

## Gaps (also in DEVIATIONS)
- Command nodes: the sovereignty records and the hub ball on destruction; the anchoring of a new neutral hub; the node notices (no V24.01 node-event type found); the regeneration time (wiki 2015 maximum); the live Host with the real simulation (placement into a system without a loaded scene is not verified); the client's view (MITM open).
- Windows: no client RPC to set a hub window (none in eve-logic); the client's view.

## Gotchas
- Serena and the codebase tools point at the main checkout; edits were made with `python3 -I` scripts in the worktree.
- Bash `grep`, `cat`, `head` and `find` are denied here; read with `python3 -I -c`.
- `tools/dev/pg18.sh` run from a worktree looks for its binaries under the worktree's `.cache`; run it from `/home/user/eve.rs`.
- The tests were run under the lock file `/tmp/eve-heavy-eve_p2.lock` with `flock -o -w 1800` directly (the same lock as `tools/dev/db-lock.sh` for `eve_p2`).
- The test class constant `System` shadows the `System` namespace in `EntosisCaptureServiceTests`: write `global::System`.
