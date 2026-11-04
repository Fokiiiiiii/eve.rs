# Character attributes and remap deep correctness handoff

## Status

Fixed one client-visible defect in the registered `skillHandler.GetAttributes` route: active attribute augmentation was omitted from the Host reply. The root change and exact wire regression are in `dotnet/src/Eve.Services.Skills/SkillHandlerService.cs:501-507` and `dotnet/tests/Eve.Host.Tests/SkillHandlerRpcHostTests.cs:13-92`.

## Client evidence and decision

A whole-client search for `GetCharacterAttributes`, `GetCharacterAttribute`, `GetAttributes`, `GetRespecInfo`, `RespecCharacter`, training calculator, and remap notifications found these consumers:

- `eve/client/script/ui/services/skillsvc.py:219-227` calls `GetBoosters(True)`, `GetImplants(True)`, then `skillHandler.GetAttributes()`, caches the returned map, and applies every value to dogma. Lines 229-240 use the same values for training attributes, applying alpha scaling and subtracting accelerator bonuses only when requested; `:703-704` passes these values into skill training estimates.
- `eve/client/script/ui/shared/neocom/attributes.py:75-104` uses `GetCharacterAttributes()` for the remap UI and subtracts implant and active accelerator bonuses locally before showing the remappable base. Returning base attributes from the server therefore caused the UI to subtract bonuses twice.
- `eve/client/script/ui/shared/neocom/attributes.py:235-267` validates 17–27 and 99 total points, reads `GetRespecInfo`, asks for confirmation, raises `RespecTooSoon` with `nextTime` when no remap is available, then calls `RespecCharacter`.
- `eve/client/script/ui/services/skillsvc.py:799-807` invalidates respec info, refreshes the attribute cache on `OnRespecInfoChanged`, then scatters `OnRespecInfoUpdated`. The character sheet loads attributes and respec info at `eve/client/script/ui/shared/neocom/charsheet/attributesPanel.py:16-21,37-48` and listens for the update at `:85-88`.
- Skill queue estimates call the client calculator from `eve/client/script/ui/services/skillQueueSvc.py:534-544,665-696`; the applied skill points window also calls it at `eve/client/script/ui/skilltrading/applySkillPointsWindow.py:375-377`. `characterskills/client/skill_training.py:33-39,53-174` reads the character attribute at training-time offsets and calculates primary plus half secondary, including accelerator expiry boundaries.

Thus the Host must send the effective values consumed by client dogma and the training calculator, including active learning/augmentation bonuses. eve.rs now serializes `(Attribute + LearningBonus) * Speed` at `SkillHandlerService.cs:501-507`. This matches EveJS 261028.1 `server/src/services/skills/skillMgrService.js:308-339`, which adds active implant/booster bonuses before applying its configured client training-speed scale. Its training calculation uses primary + secondary/2 and floors duration in `server/src/services/skills/training/skillTrainingMath.js:139-173`. EVE University describes attribute implants as +1 through +5 in [Implants](https://wiki.eveuniversity.org/Implant) and the training formula in [Skills and learning](https://wiki.eveuniversity.org/Skills_and_learning).

The augmentation cache is refreshed during the character training view on selection: `dotnet/src/Eve.Services.Skills/SkillsLoginHook.cs:16-20`, `dotnet/src/Eve.Skills/CharacterTraining.cs:68-72`. The settings owner defines the learning bonus as attribute bonuses from implants and active boosters in `dotnet/src/Eve.Skills/TrainingSettings.cs:22-28`. This keeps the RPC sourced from authoritative, refreshed state rather than reconstructing or hardcoding packet values.

## Remap state and notification path reviewed

eve.rs persists the remap and cooldown/free-remap counters in `dotnet/src/Eve.Skills/PgSkillStore.cs:21-38,813-855`. Defaults are 19 charisma and 20 for the other attributes (99 total), with two bonus remaps; validation is 17–27 and exactly 99 points. `SkillHandlerService.RespecCharacter` validates the full five-attribute map, returns the client error codes `RespecAttributesMisallocated`, `RespecAttributesTooLow`, and `RespecAttributesTooHigh` for invalid input, maps cooldown to `RespecTooSoon(nextTime)`, commits through `CharacterTraining.RespecAsync`, then awaits `TrainingAnnouncer.AnnounceRemappedAsync` before sending `OnRespecInfoChanged` (`SkillHandlerService.cs:617-660`; transaction and remap update at `CharacterTraining.cs:570-627`). The client handler refreshes attributes before `OnRespecInfoUpdated`, as cited above. Existing tests cover persistence and Host notification ordering; no additional defect was found in this path during this job.

## Regression proof and checks

The Host wire test was run against the previous implementation and failed on the expected value: attribute 164 returned 246 instead of 252. With the fix, it passed and asserts all five effective attribute values (252, 255, 258, 261, 264) using a nontrivial speed and learning-bonus fixture.

Local checks on database `eve_p5`:
- `dotnet build dotnet/src/Eve.Services.Skills --no-restore -p:UseSharedCompilation=false`: passed.
- `dotnet build dotnet/tests/Eve.Host.Tests --no-restore -p:UseSharedCompilation=false`: passed.
- Host tests (two focused registered-route/database classes): 2 passed, 0 failed, 0 skipped.
- `Eve.Services.Skills.Tests` handler and notification classes: 51 passed, 0 failed, 0 skipped.
- `Eve.Skills.Tests` character learning tests: 3 passed, 0 failed, 0 skipped.
- `Eve.Ships.Tests` pilot learning bonus tests: 2 passed, 0 failed, 0 skipped.

The fix commit is `ee69a91f` (`Fix training attribute augmentation reply`); it was merged with current main and pushed as `a78bef65`. Push-triggered CI run 1435 completed successfully with no failed tests or jobs. The CI headless end-to-end check passed. No interactive graphical V24.01 client session was run for this change.
