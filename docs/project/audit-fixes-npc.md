# NPC audit fixes (R14)

This log records findings verified against `/home/user/reports/90-audit-eve-npc-r14.md`, the V24.01 client source in `/home/user/eve-logic`, EveJS in `/home/user/evejs-261028.1`, and the local `eve_p7` database. All three audit findings were real; none were skipped as false positives.

## P2: NPC module drops were marked as singleton items

### Verification

- `NpcLoot.Roll` and `NpcLoot.RollRules` derived the item `Singleton` flag from whether its type could be stacked. Category 7 modules are non-stackable, so generated modules were marked singleton even though they had no fitted state.
- The Host path passes the drop's flag into `WreckContent` in `NpcWrecks.LeaveAsync`; `SpaceSessions.LeaveNpcWreckAsync` makes the wreck, places it in space, and keeps its contents in the item store.
- EveJS `server/src/space/npc/npcLoot.js:279-290` resolves an unspecified generated module to `singleton=false`; rule-generated entries use that result at lines 633-658.
- V24.01 `eve/client/script/ui/shared/inventory/test/test_neocomInventoryBadging.py:138-142` uses an ordinary module with `singleton=0`; lines 332-336 represent a fitted module with `singleton=1`.
- The new Host E2E test first failed against the old code with the wreck content set to `Singleton=true`; after the fix it passed with the content row, quantity, owner and `Singleton=false` verified.

### Fix

Default generated modules to packaged state (`Singleton=false`) independently of their quantity. A loot selector's explicit singleton value overrides the default. Other item categories retain their prior default.

## P2: bounty tax terms changed between failed payout attempts

### Verification

- `PgBountyStore.PayAsync` previously read corporation and tax terms inside the wallet transaction. A failure rolled back that transaction and left the bucket pending, so a retry could read a different corporation or rate.
- EveJS `server/src/services/bounty/bountyRuntime.js:930-960` persists corporation ID, NPC-corporation status, tax rate, gross, tax, net and credit flag before wallet adjustment; lines 1001-1041 reuse that persisted split when retrying.
- V24.01 `eve/client/script/ui/services/wallet/transactionsUtil.py:23-41,49-73,87-103,148-153` builds the corporation-tax journal view from the saved wallet transaction description. The existing bounty description and tax journal shape remain in use.
- The new PostgreSQL test first failed after a no-wallet payout attempt: changing the corporation and rate changed the expected member net from 108,000 ISK to 90,000 ISK. After the fix, a restarted store paid the original 108,000 net, credited the original corporation 12,000, preserved the original journal description, and left the new corporation untouched.

### Fix

Persist the initial `BountyTaxSplit` on the pending row in a separate transaction before wallet writes. Retries use that snapshot. Adding a later kill to the bucket clears the snapshot so the enlarged bucket gets one new split at its next payout attempt.

## P3: rare asteroid-belt NPC spawn groups were missing

### Verification

- Before the change, `BeltRats.Decide` planned only normal faction/hull groups. The audit finding was confirmed.
- EveJS `server/src/space/npc/beltRatRuntime.js:51-89` maps faction hauler, commander, officer and capital pools and officer home regions; lines 774-895 define eligibility and chance order; lines 899-1045 build special plans; lines 1048-1109 fall back to normal belt groups; lines 1260-1295 enforce the active capital group limit and bypass the ordinary chance for selected specials.
- EveJS default values and environment settings are in `server/src/config/schema/npc.js:146-298`. Capital profiles, fittings, loot and pool weights are authored in `tools/DatabaseCreator/staticTables/capitalNpcAuthority/data.json` and mapped to runtime rows by `server/src/space/npc/capitals/capitalNpcCatalog.js:36-89,122-205`. A direct comparison of the embedded C# capital records with the 18 source profiles/loadouts/loot rows and 10 generated pools found no mismatches in the mapped fields.
- The V24.01 client action is `eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:452-457`: it issues warp, records the warp destination, and emits `OnWarpToAsteroidBelt` for asteroid-belt targets. `eve/client/script/parklife/spaceMgr.py:182-200` creates a space object from the received slim item's group/category; `eve/client/script/environment/spaceObject/repository.py:49-57` maps category `Entity` to `EntityShip`; `entityShip.py:37-45,82-103` reads the hull's dogma turret/missile type and fits its hardpoints. These client paths consume the normal ship/entity and item state, with no client-specific capital-spawn RPC or special label required.
- `eve_p7` contains SDE build 3396210 (`sde._sde.doc.buildNumber`). The DB-backed capital loot test checks all 18 authored capital profiles against SDE item types, including both ends of each authored guaranteed-charge quantity range. The Host capital test additionally loads the selected hull and weapon from SDE and verifies the actual `SimSpawn` plus the system-wide one-group limit.
- The normal belt plan continues to use the faction base pool and chooses by hull class, matching EveJS `beltRatRuntime.js:33-40,643-712`. EveJS `npcData.js:219-256` merges generated capital catalog rows into NPC profiles, loot and spawn pools, and lines 328-373 resolve those pools by exact or unique partial name; `npcSelection.js:533-557` selects a successful pool when a profile match is ambiguous. The NPC selector test checks exact `Capital Supercarriers`, alias `supers` and partial `arri` pool searches. The existing spawn-plan fixture's generator explicitly disables generated catalogs (`tools/dev/gen-npc-selection-fixture.js:15-30`); its test now loads only the embedded authored table so it continues to compare the same scope.
- The new commander Host E2E test first failed with `ChanceMiss` when only the ordinary chance was applied; with the special roll and pool wired, it passed and spawned the selected commander profile despite ordinary chance 0. The capital Host E2E test passed with the SDE hull/weapon and blocked a second belt's capital group.

### Fix

Add EveJS-authored capital NPC profiles, loadouts, loot and special pools to the existing `NpcAuthority`; retain the existing SDE-backed hull and weapon construction path. Keep guaranteed loot quantity ranges and exact selector type IDs from the authored capital table. Add deterministic special chance, faction-pool selection and eligibility for haulers, commanders, officers and capitals. Count active capital groups across the solar system under the Host's belt-arrival gate. Keep generated capital pools searchable like the reference NPC catalog.

### Evidence links

- CCP, [Introduction to Mining](https://support.eveonline.com/hc/en-us/articles/23655785488668-Introduction-to-Mining).
- CCP, [Patch Notes Version 20.07](https://www.eveonline.com/news/view/patch-notes-version-20-07).

## Verification actually run

- `Eve.Npc.Tests`: 121 selected tests passed, 0 failed, 0 skipped. Included `BeltRatsTests`, `NpcLootTests`, DB-backed `NpcLootSdeTests`, `NpcSelectorTests`, `NpcSecurityStatusRecoveryTests`, `SpawnPlanFixtureTests`, DB-backed `PgBountyStoreTests`, and `DynamicBountyTests`.
- `Eve.Services.Space.Tests`: 266 selected Host tests passed, 0 failed, 0 skipped. Included `BeltRatsHostTests`, `NpcLootHostTests`, `AnalysisBeaconHostTests`, `InsurgencyDirectorTests`, `SiteNpcTests`, `SpaceServiceTests`, `DungeonWaveSpaceTests`, `NpcBountyHostTests`, `WormholeSiteTemplatesTests`, `IncursionDirectorTests`, `DungeonWaveTests`, `NpcSpawnTests` and `PgEssStoreTests`.
- After converting the new special-spawn tests to start from the Host's `SimArrived` event, DB-backed `BeltRatsHostTests` passed 9/9 with commander and capital spawns observed through the arrival handler.
- `Eve.Host.Tests` `NeedlejackActivationHostTests` passed 9/9. All were Release runs using `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p7` when applicable.
- The loot and bounty regressions were also exercised as failing tests before their respective fixes. No assertion was weakened.

## Not checked

- EveJS's planner could not be executed in this checkout: its local game store lacks the required static tables, and the attempted module load stopped at `ITEM_RESOLVE_FAILED:52182`. Behavior was compared against the source and authored tables cited above, not a live EveJS run.
- The pinned `.cache/sde` directory is absent in this worktree; SDE-backed verification used the `eve_p7` database, whose metadata reports build 3396210.
- The graphical V24.01 client was not launched. Client source paths were inspected; Host and SDE tests are headless checks.
- The full local test suite and live headless suite were not run. Release `-warnaserror` builds passed for `Eve.Npc`, `Eve.Services.Space`, `Eve.Npc.Tests` and `Eve.Services.Space.Tests`, each with 0 warnings. The required fetch/merge/push/CI gate is pending in this work session. Latest main CI for SHA `b6f5dc1` completed successfully.
