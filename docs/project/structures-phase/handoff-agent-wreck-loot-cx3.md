# cx3 wreck and loot handoff

## Implementation

- `Wreck.abandoned` is authoritative in the sim and appears as the fourth `lootRights` value. An abandoned container includes the rights tuple even while empty, so V24.01 can see the flag.
- `ChangeWreck` simlink protocol 84 carries tri-state `is_empty` and `abandoned` values. The host saves zeroed loot-right IDs before sending the abandonment change; refusal or link failure restores the previous record. Restored `SpaceItem` rights with zero IDs initialize the sim as abandoned.
- Corporation access compares the pilot's current corporation with the corporation stored in the wreck's rights. In-space inventory contents use the same take authorization as looting.
- Expiry/removal uses pilot, controlled-structure and bound widow observers, and clears their remembered wreck IDs.

## Client evidence (whole V24.01 source checked)

- `eve/client/script/remote/michelle.py:1440-1470` unpacks `(ownerID, corpID, fleetID, abandoned)`, returns `abandoned` from `IsAbandoned`, and grants access when it is true; otherwise corporation rights compare the session corp with both stored right IDs.
- `eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:138-173` sends `CmdAbandonLoot` for one wreck or the locally eligible wreck list. `bracketsAndTargets/bracketVarious.py:164-173` uses `IsAbandoned` for the abandoned-container indicator; `:157-161` uses `HaveLootRight` for the looting state.
- `michelle.py:1481-1493` replaces the cached slim item and scatters `OnSlimItemChange` to UI and item components, which consumes the sim observer update.
- Inventory access is through `invCache.py:533-562` (`GetInventoryFromId`), used by `menusvc.py:3368-3369` and `menuSvcExtras/invItemFunctions.py:462-467` for container contents. The broker therefore must return the member's actual contents under the same corporation loot-right rule.
- The full-client caller search found 7 `GetInventoryFromId` callers in `invCache.py`: `AcceptPossibleRemovalTax`, ship cargo/current-ship inventory accessors, structure hangar accessors, `GetStationIDOfItem`, and `StripFitting`. Loot-right consumers include `crimewatchSvc.py:529`, `menuFunctions.py:168` and `bracketVarious.py:159`.

## EveJS and mechanics comparison

EveJS 261028.1 implements the same flow: `src/server/src/services/ship/beyonceService.js:3378-3390` unwraps `CmdAbandonLoot` and calls the runtime; `src/server/src/space/runtime/spaceRuntime/pilotCommands.js:224-283` validates, marks the entity abandoned and broadcasts slim changes; `src/server/src/space/runtime/inventoryLootRights.js:261-389` durably persists the marker and rolls back failed persistence. The implementation keeps that client-visible behavior while using `SpaceItem` as eve.rs's existing durable owner.

[EVE University Salvaging](https://wiki.eveuniversity.org/Salvage) describes abandoned (blue) wrecks as freely lootable and owned (yellow) wrecks as suspect-flagged when looted. Abandonment and stored corporation rights follow those rules. EveJS 261028.1 checks space visibility/range for `GetContainerContents` but leaves contents visibility separate from the loot-right gate used for transfer (`server/src/services/inventory/invBrokerService.js:3771-3934,11827-11954`). eve.rs intentionally applies `MayTakeFromAsync` to in-space container contents too, so a pilot without loot permission sees no items; this difference is recorded in `docs/reference/compat/DEVIATIONS.md`.

## Checks

Pushed implementation commit `300534155e083e1f4cfd95dc24aebbf9e72b565f`, then CI-fix commit `f01809925b79f532996a3cb13c4093f1482545d1`, to `main`. The first push's Actions run 41022854575 found four failures: the tether policy test still expected protocol 83, the SimCodec fixture decoder did not read nullable `ChangeWreck` fields, and two `LootAllTests` changed only inventory owner while leaving stored wreck rights unchanged. Those fixes are in `f0180992`; push-triggered Actions run 41024771010 completed successfully, including Rust workspace tests, the codec fixture cases, and the PostgreSQL-backed loot tests. Locally, Rust lint and the focused tether test, C# project builds, and 260 codec fixture cases pass. PostgreSQL was not used locally, as instructed; those tests passed on CI.

Passed locally: Rust format check; `eve-space` focused abandonment, sim command and observer-expiry tests; simlink fixture tests (11); `eve-space` cargo check; workspace clippy; .NET builds for `Eve.Space`, `Eve.Services.Space`, `Eve.Services.Inventory`, `Eve.Space.Tests` and `Eve.Host.Tests`; focused sim codec and registered Host abandonment wire tests.

The graphical client was not exercised. PostgreSQL was intentionally not used for this task; persistence restore is verified by code-path inspection, not a database restart test. Existing unrelated cx3 WIP remains preserved in stash `cx3-pre-wreck-loot-wip`.
