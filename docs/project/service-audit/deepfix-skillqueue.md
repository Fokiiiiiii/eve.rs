# Skill queue deep correctness fix

## Finding and correction

Starting two characters' queues concurrently could leave both active when an account had only one training slot. `CharacterTraining.SaveQueueAsync` locked each character separately, but counted the other running characters before saving, without an account-wide lock. Both transactions could therefore count zero and commit. The saved queue is account-wide behavior: the client invokes `SaveNewQueue` with a position-to-`(typeID, toLevel)` map from `eve/client/script/ui/services/skillQueueSvc.py:141-160`; it passes the current account's character set through the Host handler at `dotnet/src/Eve.Services.Skills/SkillHandlerService.Training.cs:14-24`.

The fix takes a transaction-scoped advisory lock keyed by account before reading the active count, then checks available slots and commits the queue within that transaction (`dotnet/src/Eve.Skills/CharacterTraining.cs:428-486`). Existing active queues do not consume another slot when edited; paused queues do not consume a slot. The Host maps the no-slot result to the client's `UserAlreadyHasSkillInTraining` error with `charactersTrainingCount` (`dotnet/src/Eve.Services.Skills/SkillErrors.cs:23-24`).

## Client and reference evidence

- The whole V24.01 client search found `SaveNewQueue` called from `skillQueueSvc.py:141-160`. The client builds the indexed queue mapping, sends `activate` (defaulting to true at the service call), handles success by updating queue caches, and special-cases `UserAlreadyHasSkillInTraining`, `SkillInQueueRequiresOmegaCloneState`, and `SkillInQueueOverAlphaSpTrainingSize` (`skillQueueSvc.py:152-160`). The skill queue panel handles the busy error at `skillQueuePanelNew.py:321-328`, showing the multi-training overlay unless suppressed (`skillQueuePanelNew.py:55-61)); the overlay is displayed by `skillPlanPanel.py:240-243`. Saved-queue and pause notifications are consumed at `skillQueueSvc.py:748-764`; the client then emits its queue-changed event from the updated cache.
- Skill changes flow through `skillsvc.py:158-176`: the client consumes `OnServerSkillsChanged`, updates skills/history, delegates the queue update, and scatters the skill-change UI events. Multiple-character training slot updates are consumed at `skillQueueSvc.py:840-843`.
- The custom fallback message box is registered for `UserAlreadyHasSkillInTraining` at `messageboxCustom.py:9`; it reads `charactersTrainingCount`, `charName1`, and `charName2` at `canNotStartTrainingWindow.py:20-24`. The normal queue-panel path presents the overlay directly. The fallback's account-name payload is a separate compatibility concern and was not changed here.
- EVE University describes Multiple Character Training as granting additional simultaneous queues in [Accounts](https://wiki.eveuniversity.org/Accounts) and [Alternate characters](https://wiki.eveuniversity.org/Alternate_characters). This supports enforcing the account slot limit atomically; the concurrency defect does not change training rates or SDE-derived character statistics.
- EveJS 261028.1 checks the active character count against the account's available training slots before saving in `server/src/services/skills/training/skillQueueRuntime.js:1305-1429`. Its single-threaded event loop serializes those operations. The Rust/.NET implementation has concurrent DB callers, so it needs the account transaction lock to preserve the same intended behavior.

## Adjacent flows reviewed

The whole-client search also traced injector preflight/use (`skillsvc.py:975-982,1000-1001`; `skillInjectorWindow.py:162-166`), extractor queue read and execution (`skillExtractorController.py:487-489,583-584,649-651`; `skillsvc.py:969-973)), remap state and action (`skillsvc.py:799-802`; `attributes.py:256-267`; `attributesPanel.py:41-43`), and skill/attribute refresh consumers (`skillsvc.py:158-176,219-228`; `attributes.py:80,98-103,192,238-247`). Existing service handler coverage for these paths passed in the local `SkillHandlerTests` class. This change is limited to the demonstrated active-slot race; no injector, extractor, remap, attribute formula, or notification behavior was changed.

## Regression proof and verification

Added `Concurrent_starts_for_different_characters_cannot_exceed_the_account_training_slots` to `dotnet/tests/Eve.Skills.Tests/CharacterTrainingTests.cs:280-294`. It starts two different characters concurrently, asserts one saved result and one busy result, and verifies the persisted running count is one.

- Before the fix, the focused PostgreSQL-backed test failed: both calls returned `QueueSaved`.
- After the fix, the focused test passed; the full `CharacterTrainingTests` class passed 20 tests, and `SkillServiceTests.SkillHandlerTests` passed 49 tests. Each DB test used the dedicated `eve_p4` database through `tools/dev/db-lock.sh`.
- The touched `Eve.Skills` and `Eve.Skills.Tests` projects built successfully with shared compilation disabled after merging main.
- After merging main, the first `HostServerTests` run had 388 passes and 11 setup failures because the local database lacked `sde_mission.skinrComponentPointValues` (PostgreSQL 42P01). Loaded the mission schema using the pinned mission SDE build 3405148 (`sde/lock.json`, via `eve-sde ensure --missions`) and reran the full class: 399 passed, 0 failed, 0 skipped. The standalone `Skill_rpcs_accept_transport_macho_version_and_keep_the_client_wire_shapes_on_the_host` test also passed. The Host test file has no net change. Graphical-client behavior was not exercised.
