# Handoff S2 (CH-11, CH-2, CH-5, CH-7, CH-9, CH-17)

Worktree `/home/user/wt-ch-s2`, branch `ch-s2`, lane eve_p3 (live port 26102). Pushed packages go straight to `origin/main`.

## Status

| Package | State | Notes |
|---|---|---|
| CH-11 | partial (in progress) | Implants and active boosters are in the pilot's dogma world (`Eve.Ships/PilotAugmentations.cs`, `ShipSnapshot.LoadAsync`). Open: training rate uses `TrainingSettings.Attribute` (base learning attributes only), no notification path for an implant change (clone install is CH-15/16), no live walk. |
| CH-2 | done (eve_p3 live: test_alpha_injector_live.py passed) | `alphaInjectorMgr` is served by `Eve.Services.Skills/AlphaInjectorMgrService.cs` (the Core stub is removed). Daily Alpha Injector: once per 11:00 UTC downtime (EveJS `skillTradingAuthority.js`), replacing the 24-hour rule. |
| CH-5 | done (eve_p3 live: test_login_milestone_live.py passed) | `MilestoneMgrService` (Core), `LoginMilestoneStore` (Characters, migration 0008), entry hook registered in `CharacterComposition`. |
| CH-7 | done (eve_p3 live with EVE_SYSTEM_POPULATION_CAP=1: test_population_cap_live.py passed) | `SystemPopulation.TryReserve` (seat taken in the same step as the room check) used by `SelectCharacterID`. |
| CH-9 | done (eve_p3 live: test_achievement_tracker_live.py passed) | `AchievementTrackerStore` (Characters, migration 0009), `UpdateClientAchievmentsAndCounters` persists, reads of no record are empty maps (golden fixture removed). |
| CH-17 | not started | |

## Next

1. CH-11: the learning attributes behind the training rate; the implant-change notification once CH-15/CH-16 install implants; a live walk.
2. CH-17 (expert systems): not started. Catalogue source is EveJS `tools/DatabaseCreator/staticTables/expertSystems/data.json` (the pinned SDE has none).

Live runs use `EVE_RUN` with `/home/user/eve.rs/tools/headless-client/.venv/bin/python -m pytest` (the venv is not in the worktree), port 26102, the eve_p3 lock.

## Risks

- Daily Alpha timing and the ref type of the milestone ISK credit (refTypeId 0) are not in the wiki; EveJS is the source, recorded in DEVIATIONS.
- The milestone timer is in memory (as EveJS): a host restart without a character leave loses the time since the last write.
