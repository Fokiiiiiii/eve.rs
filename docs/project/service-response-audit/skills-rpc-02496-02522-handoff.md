# Skill RPC coverage handoff (RPC-02496–RPC-02522)

## State

The initial worktree had no local edits or commits ahead of origin/main. No database was used. Host source already composes `SkillMgr2Service` and `SkillHandlerService`: `dotnet/src/Eve.Host/Composition/CharacterComposition.cs:134-149`, `dotnet/src/Eve.Host/Rpc/CharacterRpc.cs:70-72`; `HostComposition.cs:256-278` registers character services. The client calls an accessor on `skillMgr2`, then invokes the returned `skillHandler` Moniker. The rows classified MISSING as `skillMgr2` methods describe this accessor hop; they are not additional handler methods that should be aliased onto `skillMgr2`.

## Client call and consumer evidence

Whole-tree search covered all Python files in `/home/user/eve-logic`. The RPC callers are `eve/client/script/ui/services/skillsvc.py` and `eve/client/script/ui/services/skillQueueSvc.py`:

- `skillsvc.py:127-142`: obtains the handler via `skillMgr2.GetMySkillHandler()`, then consumes `GetSkills` and `GetAllSkills` into skill caches.
- `skillsvc.py:223-225`: consumes `GetAttributes` as a mapping and applies each attribute into dogma state.
- `skillsvc.py:362-364,378-380`: consumes cached history records and (typeID, pointChange) pairs.
- `skillsvc.py:772,795-803`: invokes training and abort (reply ignored); caches respec data for remap UI.
- `skillsvc.py:849-887`: consumes free-SP count and the updated count returned by `ApplyFreeSkillPoints`.
- `skillsvc.py:909,960-968`: consumes booster and implant mappings to populate augmentation caches.
- `skillsvc.py:972-1017`: invokes extract, inject, constraint-check, split and combine; replies are ignored and UI state refreshes from subsequent skill/inventory reads.
- `skillsvc.py:988-1002`: consumes SP totals/free SP and diminished injector award for extractor and injector previews.
- `skillsvc.py:1093-1094`: forwards the two-value skill-bundle result to the bundle UI.
- `skillQueueSvc.py:116-117`: unpacks the queue result into (queue, freeSkillPoints), then applies both to client state.
- Other searched consumers include `nodegraph/client/conditions/skill.py:18`, `nodegraph/client/events/skill.py:12,46,50`, `eve/client/script/ui/shared/neocom/attributes.py:40`, `eve/client/script/ui/shared/tooltip/skillBtns.py:183,196`, `eve/client/script/ui/skilltrading/skillInjectorWindow.py:137`, `applySkillPointsWindow.py:118`, `skillInjectorBar.py:50`, `eve/client/script/ui/inflight/shipModuleButton/moduleButtonTooltip.py:901`, `eve/client/script/ui/skillPlan/skillQueuePanel/skillQueuePanelNew.py:141,163,535,629`, `eve/client/script/environment/godma.py:1773,1840,1896`, `notifications/client/development/skillHistoryProvider.py:25`, `operations/common/skipconditions/skills.py:13-62`, and `characterskills/client/purchase.py:99`.
- Transport adds `machoVersion=1`: `carbon/common/script/net/ServiceCallGPCS.py:221-225` and `ObjectCallGPCS.py:403-407`.

## Reference comparison

EveJS v0.12.9 `server/src/services/skills/skillMgr2Service.js:5-22` returns a `skillHandler` Moniker bound to the session character. eve.rs `dotnet/src/Eve.Services.Skills/SkillMgr2Service.cs:5-17` matches it. EveJS handler RPCs and trading implementation are in `server/src/services/skills/skillMgrService.js:282-474,527-572` and `skillTradingRuntime.js:699-1034`; eve.rs equivalents are implemented in `SkillHandlerService.cs` and its partials, connected to the existing training, skill, inventory, and notification owners. Adding a `skillMgr2` alias to `skillHandler` would bypass the client-observed Moniker transition.

## Change and checks

Added `dotnet/tests/Eve.Host.Tests/SkillMgr2ReadWireTests.cs`: socket-level Host registration check for `skillMgr2.GetMySkillHandler`, including `machoVersion=1` and the returned character-bound Moniker.

- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --no-restore`: passed.
- Focused `Skill_mgr2_returns_the_character_scoped_skill_handler_moniker_over_the_host`: passed, 1 passed, 0 skipped.
- This test verifies the accessor transition; it does not by itself wire-test all 25 listed RPC rows. Continue by adding Host wire checks for each listed skillHandler call and mutation effects, then run the focused Actions check and update this note with the run result.
