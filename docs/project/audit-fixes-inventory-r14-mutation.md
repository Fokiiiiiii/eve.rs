# Inventory audit findings and fixes

Checked the read-only report `90-audit-eve-inventory-r14.md` against this worktree, the pinned V24.01 client (`/home/user/eve-logic`), and EveJS v0.12.9 (`/home/user/evejs-261028.1`). The report's old `wt-cx9` paths identify the same files; all four findings reproduce against the `cx3` baseline. **No finding was false positive.**

## P1 — dynamic item mutation reach and placement

**Confirmed.** Before the fix, `DynamicItemRpcService` passed the source item's location as the requested location, while `ItemMutationService` only compared both rows with that value. That allowed an RPC against two owned items in a remote station. It also required both rows to share a location, despite the client checking each input separately with `IsInPilotLocation()` (`eve/client/script/ui/shared/dynamicItem/craftingWindow.py:204-230,322-327`; `menucheckers/itemCheckers.py:1050-1106`). The client result drag data uses the mutator's location and flag (`craftingWindow.py:329-331`); EveJS creates the result there (`server/src/services/dogma/dynamicItemService.js:472-481`).

**Fix.** The Host now passes the session's current docked station, active ship and solar system. The inventory authority checks both stored input locations and flags against that snapshot, including the current station hangar, active ship cargo and owned local container chains, while locking the rows it follows. It accepts separately reachable inputs and creates the result in the mutator's location and flag. It refuses remote pairs before consuming or notifying.

**Regression coverage.** `MutationHarnessTests.The_host_route_refuses_a_pair_in_a_remote_station_without_consuming_or_notifying` failed before the fix because the remote pair was accepted. `MutationHarnessTests.The_host_route_mutates_between_a_local_container_and_the_station_hangar_into_the_mutator_location` failed before the fix because the local pair was rejected. Both passed after the fix (2 passed, 0 skipped). They use the registered Host service, PostgreSQL-backed stores, and assert persistence and notifications.

## P2 — missing V24 packaged type volumes

**Confirmed.** V24 `inventorycommon/const.py:2806-3069` defines 264 packaged type overrides. The eve.rs table had only 19. Type 37289 (`typeQuad800mmRepeatingCannonI`, client `const.py:1904,2828`) has SDE volume 4000 m³ and packaged volume 1000 m³. EveJS has the type overrides in `server/src/services/inventory/itemStore.js:232-497`, but its table omits V24's station vault and station warehouse type entries (17367 and 17368). The complete eve.rs table now matches all 264 V24 type/value pairs; this comparison was checked with a script against the client source and EveJS.

**Regression coverage.** Added `SpaceCargoTests.A_packaged_capital_turret_uses_its_client_volume_when_moved_into_a_container`. It moves type 37289 into the SDE Huge Secure Container (capacity 1950 m³). Before the fix the SDE's 4000 m³ value caused a refusal; after the fix the move succeeds and the stored container usage is exactly 1000 m³ (1 passed, 0 skipped).

## P2 — singleton plastic wrap volume encoding

**Confirmed.** V24 `inventorycommon/const.py:1660` identifies type 3468; `inventorycommon/util.py:43-59` computes singleton wrap volume as `-quantity / 100` and raises when that is nonpositive. EveJS decodes negative singleton-wrap quantities in `itemStore.js:2508-2523`; for invalid nonnegative quantities it falls back to metadata, while eve.rs follows V24's explicit error. Before the fix, an encoded quantity of -1000 was treated as the static 0.01 m³ SDE volume rather than 10 m³.

**Fix and regression coverage.** `ItemVolumes.Of` now decodes singleton plastic wrap quantities and rejects a nonpositive decoded volume. Cargo and container incoming-volume calculations pass the requested stack quantity through the same item-volume path. The DB-backed `SpaceCargoTests.A_singleton_plastic_wrap_uses_its_encoded_volume_when_calculating_container_capacity` failed before the fix (expected 10, got 0.01); it passes after. A direct boundary test now checks both zero and positive stored quantities raise, matching the client condition.

## P3 — `GetDynamicItemInfo.modifiers` values

**Confirmed.** eve.rs exposed the calculated multiplier as `modifiers`. EveJS `dynamicItemService.js:343-352` returns the original uniform fractions, and V24 `tooltip/dynamicItem.py:45-61` reads only the keys. The values are not currently consumed by the tooltip, but the RPC shape and semantics should match the reference.

**Fix and regression coverage.** Mutation records now keep the applied multipliers in the existing `rolls` field and persist original fractions separately in nullable `roll_fractions` (`0006_mutation_roll_fractions`). The Host returns fractions rounded to six decimal places in `modifiers`, matching EveJS `dynamicItemService.js:343-352`; the exact fraction remains stored for reproducibility. Old rows recover the fraction from the stored multiplier and pinned range when possible. Equal-ended ranges cannot reveal a historical draw and return 0 for that key. The new assertions in `DynamicItemRpcServiceTests.GetDynamicItemInfo_answers_from_the_stored_record_and_is_none_for_an_item_that_was_not_mutated` failed before the fix (expected fraction 0.5, got multiplier 1.0). A second Host test checks EveJS's six-decimal wire precision while the stored fraction remains exact. The added legacy-row test clears `roll_fractions` and verifies all five fractions are recovered from multipliers. Persistence across a store restart is also covered by `ItemMutationStoreTests.A_stored_record_reads_back_bit_for_bit_after_a_restart`.

## Verification status

- **Verified against sources:** the whole V24 callsites for mutation input reach, result location/flag, plastic wrap volume and dynamic-item tooltip modifiers; EveJS mutation, volume and packaged-volume behavior; V24's full 264-entry packaged type table.
- **Executed before/after fix:** P1 remote and local-container Host cases, capital turret container move, plastic wrap container usage. The pre-fix failures and post-fix passes are recorded above.
- **Verified after the origin/main merge:** Release builds for `Eve.Inventory`, `Eve.Ships`, `Eve.Host`, `Eve.Inventory.Tests`, `Eve.Ships.Tests`, `Eve.Host.Tests` and `Eve.Services.Space.Tests` each completed with 0 warnings / 0 errors. Selected affected classes on `eve_p3`: Inventory 29 passed, Host 10 passed, Space 32 passed, Ships 73 passed; 0 skipped in each project. The final Host build after the six-decimal wire adjustment also completed with 0 warnings / 0 errors.
- **Executed against EveJS v0.12.9:** its `itemStore` helper returned 1000 for packaged type 37289 and 10 for singleton plastic wrap quantity -1000, matching the client/SDE and the eve.rs regression values.
- **Not checked:** the graphical V24 client session, packet capture from a live client, and the CI run after push.
- **False positives:** none among the four report findings. EveJS's two missing station-container entries are a separate reference discrepancy; the implementation follows the higher-priority V24 client table.
- The required Serena/CBM MCP search tools were unavailable in this environment; source review used `rg`, `sed`, the SDE and direct client/reference reads instead.
