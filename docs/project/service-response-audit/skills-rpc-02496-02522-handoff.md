# Skill RPC coverage handoff (RPC-02496–RPC-02522)

## State

The initial worktree had no local edits or commits ahead of origin/main. No database was used. Host source already composes `SkillMgr2Service` and `SkillHandlerService`: `dotnet/src/Eve.Host/Composition/CharacterComposition.cs:134-149`, `dotnet/src/Eve.Host/Rpc/CharacterRpc.cs:70-72`; `HostComposition.cs:256-278` registers character services. The client calls an accessor on `skillMgr2`, then invokes the returned `skillHandler` Moniker. Rows attributed to `skillMgr2` describe this accessor hop; they are not additional handler methods that should be aliased onto `skillMgr2`.

## Client call and consumer evidence

Whole-tree search covered all Python files in `/home/user/eve-logic`. RPC callers are `eve/client/script/ui/services/skillsvc.py` and `eve/client/script/ui/services/skillQueueSvc.py`:

- `skillsvc.py:127-142`: obtains the handler via `skillMgr2.GetMySkillHandler()`, then consumes `GetSkills` and `GetAllSkills` into skill caches.
- `skillsvc.py:223-225`: consumes `GetAttributes` as a mapping and applies each attribute into dogma state.
- `skillsvc.py:362-364,378-380`: consumes cached history records and (typeID, pointChange) pairs.
- `skillsvc.py:772,795-803`: invokes training and abort (reply ignored); caches respec data for remap UI.
- `skillsvc.py:849-887`: consumes free-SP count and the updated count returned by `ApplyFreeSkillPoints`.
- `skillsvc.py:909,960-968`: consumes booster and implant mappings to populate augmentation caches.
- `skillsvc.py:972-1017`: invokes extract, inject, constraint-check, split and combine; replies are ignored and UI state refreshes from subsequent reads.
- `skillsvc.py:988-1002`: consumes SP totals/free SP and diminished injector award for extractor and injector previews.
- `skillsvc.py:1093-1094`: forwards the two-value skill-bundle result to the bundle UI.
- `skillQueueSvc.py:116-117`: unpacks the queue result into (queue, freeSkillPoints), then applies both to client state.
- Other consumers found: `nodegraph/client/conditions/skill.py:18`, `nodegraph/client/events/skill.py:12,46,50`, `eve/client/script/ui/shared/neocom/attributes.py:40`, `eve/client/script/ui/shared/tooltip/skillBtns.py:183,196`, `eve/client/script/ui/skilltrading/skillInjectorWindow.py:137`, `applySkillPointsWindow.py:118`, `skillInjectorBar.py:50`, `eve/client/script/ui/inflight/shipModuleButton/moduleButtonTooltip.py:901`, `eve/client/script/ui/skillPlan/skillQueuePanel/skillQueuePanelNew.py:141,163,535,629`, `eve/client/script/environment/godma.py:1773,1840,1896`, `notifications/client/development/skillHistoryProvider.py:25`, `operations/common/skipconditions/skills.py:13-62`, and `characterskills/client/purchase.py:99`.
- Transport adds `machoVersion=1`: `carbon/common/script/net/ServiceCallGPCS.py:221-225`, `ObjectCallGPCS.py:403-407`.

## Reference comparison

EveJS v0.12.9 `server/src/services/skills/skillMgr2Service.js:5-22` returns a `skillHandler` Moniker bound to the session character. eve.rs `dotnet/src/Eve.Services.Skills/SkillMgr2Service.cs:5-17` matches it. EveJS handler RPCs and trading implementation are in `server/src/services/skills/skillMgrService.js:282-474,527-572` and `skillTradingRuntime.js:699-1034`; eve.rs equivalents are on `SkillHandlerService.cs` and partials, connected to the existing training, skills, inventory, and notification owners. Adding a `skillMgr2` alias to `skillHandler` would bypass the client-observed Moniker transition.

## Change and checks

Added `dotnet/tests/Eve.Host.Tests/SkillMgr2ReadWireTests.cs`: socket-level Host test of `skillMgr2.GetMySkillHandler`, including `machoVersion=1` and character-bound Moniker shape.

- Host test project build passed.
- Focused local test passed: 1 passed, 0 skipped.
- Actions dispatch `40095471229` for pushed commit `9aff921f7b2c` passed (1 test, 0 skipped).
- Push CI `40095465896` for `9aff921f` was still in progress at this handoff update.
- This verifies only the accessor transition. It does not wire-test all 25 listed RPC rows. Continue with Host wire checks for each listed skillHandler call and mutation effects, then run the focused Actions check and update this note with results.
