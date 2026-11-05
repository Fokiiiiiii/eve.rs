# CX6 Dynamic Bounty Settlement Handoff

Updated: 2026-11-05

## Implementation

- `Eve.Npc.PgDynamicBountyStore` is the durable owner for SDE-eligible system state, period bounty/loss inputs, projection toward equilibrium, and idempotent loss IDs. The Host derives eligible systems from the pinned SDE.
- Host composition supplies durable reads to `DynamicBountyMgrService` and the DBS map callback. The RPC returns `(PyReal output, PyBool active)`; eligible systems without a stored row use `(1.5, true)`, while ineligible/invalid systems and an absent callback use `(1.0, false)`. The map includes all eligible systems, including those at equilibrium.
- NPC settlement calculates base bounty × NPC profile `bountyPayoutMultiplier` (default 1) × current system output and rounds the gross to cents before ESS Main/Reserve shares. Pilot share enters the bounty bucket; gross paid input and ESS credit share commit in the same transaction. The notice amount and `isModified` describe the final pilot share.
- Qualifying non-self player ship losses add an idempotent base-price input in the ship-destruction transaction. The durable daily job commits output and next period before scheduling `OnSystemDynamicBountyStateChanged` for sessions in the affected system.
- EVE University [NullSec Ratting](https://wiki.eveuniversity.org/NullSec_Ratting) states that the Bounty Risk Modifier multiplies NPC bounties, rises after player losses, falls after NPC kills, and drifts toward equilibrium. It does not state numerical control terms; those match EveJS 261028.1 `src/server/src/services/dynamic/dynamicBountyPolicy.js:10-24`. EveJS `src/server/src/services/bounty/bountyRuntime.js:448-515` computes 1,000,000 × 1 × 1.5 = 1,500,000 ISK before the ESS split; eve.rs pins the same-system scenario in a regression.
- No intentional difference from EveJS or the Wiki is recorded; `docs/reference/compat/DEVIATIONS.md` was not changed.

## V24.01 client evidence

- `/home/user/eve-logic/eve/client/script/ui/shared/dynamicResource/dynamicResourceSvc.py:58-65` calls `GetOutputForClientSolarSystem` without RPC arguments, reads output/active, and falls back to `(1.0, False)`. `:159-165` consumes `OnSystemDynamicBountyStateChanged(systemID, rate)` only for the current solar system; `:175-186` clears and reloads the cache on session changes.
- `/home/user/eve-logic/eve/client/script/ui/shared/infoPanels/infoPanelLocationInfo.py:319-323` displays output only when active.
- `/home/user/eve-logic/eve/client/script/ui/shared/mapView/filters/mapFilterDynamicBounty.py:18-39` and `/home/user/eve-logic/eve/client/script/ui/shared/mapView/mapViewColorHandler.py:210-230` consume `GetDBSMapData` as a system-ID-to-output mapping, including the equilibrium value used by the map filter.
- `/home/user/eve-logic/eve/client/script/ui/shared/notifications/client/bountyNotificationAdapter.py:8-22` consumes payout `amount` and `isModified`; the settlement test checks those values against the final pilot share.
- `dotnet/src/Eve.Services/CharacterSessions.cs:39-42` includes docked sessions in `InSolarSystem`; the client event handler above additionally checks the current system ID.

## Verification

All local test invocations used `eve_p6` through `tools/dev/db-lock.sh`; each result below reported zero skipped:

- `Eve.Npc.Tests`: `DynamicBountyTests|PgBountyStoreTests` — 23 passed.
- `Eve.Services.Space.Tests`: `DynamicBountyHostTests|NpcBountyHostTests|DynamicResourceCacheMgrTests|PlayerDeathTests|AbyssalSpaceSessionsTests` — 55 passed.
- `Eve.Services.Core.Tests`: `SessionStartupRpcServicesTests` — 7 passed.
- `Eve.Host.Tests`: `AbyssalDeathSettlementProcessorTests|Dungeon_and_dynamic_resource_cache_calls_round_trip_through_host_registration` — 2 passed.
- `Eve.Ships.Tests`: `DeathTests` — 15 passed.
- `Eve.Services.Crimewatch.Tests`: `PlayerBountyKillsTests` — 2 passed.

The Host, Npc, Space, Core and corresponding test projects compiled with zero warnings/errors. A real graphical V24.01 client session was outside this verification.

## Git and Actions

Commit: pending. Push and Actions checks: pending. Keep this section current after the main sync, push, and Actions completion.
