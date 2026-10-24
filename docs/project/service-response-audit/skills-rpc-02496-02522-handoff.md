# Skill RPC coverage handoff (RPC-02496–RPC-02534)

## State

The initial worktree had no local edits or commits ahead of origin/main. No database was used. Host source composes `SkillMgr2Service` and `SkillHandlerService`: `dotnet/src/Eve.Host/Composition/CharacterComposition.cs:134-149`, `dotnet/src/Eve.Host/Rpc/CharacterRpc.cs:70-72`; `HostComposition.cs:256-278` registers character services. The client calls an accessor on `skillMgr2`, then invokes the returned `skillHandler` Moniker. Rows attributed to `skillMgr2` for handler methods describe the accessor hop; they are not additional handler methods to alias onto `skillMgr2`.

## Client call and consumer evidence

Whole-tree search covered Python code in `/home/user/eve-logic`. The listed calls and immediate consumers are:

- RPC-02508 and RPC-02534 `GetSkillHistory`: `eve/client/script/ui/services/skillsvc.py:362-364` reads record fields for the character sheet; `skillsvc.py:378-380` consumes (typeID, pointChange) pairs. `skillHistoryPanel.py:53` and `notifications/client/development/skillHistoryProvider.py:25` are additional consumers. A list of records has fields `logDate`, `eventTypeID`, `skillTypeID`, `absolutePoints`, `level`.
- RPC-02509 `GetSkillPoints`: `skillsvc.py:731-732`; the extraction flow adds this allocated total to free SP at `skillsvc.py:988-989`.
- RPC-02510 `GetSkillQueueAndFreePoints`: `skillQueueSvc.py:116-117` unpacks (queue, freeSP), then updates the local queue and free-point state.
- RPC-02511 `GetSkills`: `skillsvc.py:135-136`; RPC-02522 `GetAllSkills`: `skillsvc.py:141-142`. Both populate the skill caches. Other readers include `skillTreeDataProvider.py:54-55`, `nodegraph/client/conditions/skill.py:18`, `nodegraph/client/events/skill.py:12,46,50`, and `operations/common/skipconditions/skills.py:13-62`.
- RPC-02512 `InjectSkillpoints`: `skillsvc.py:976-977`; the UI refreshes free SP and skill state after the call. RPC-02519 `CheckInjectionConstraints`: `skillsvc.py:982-984`, where the error blocks the action. RPC-02525 `GetDiminishedSpFromInjectors`: `skillsvc.py:1001-1002`, consumed as injector preview SP by `skillInjectorWindow.py:137`.
- RPC-02514 `SplitSkillInjector`: `skillsvc.py:1009-1010`; RPC-02520 `CombineSkillInjector`: `skillsvc.py:1015-1017`. The client ignores their replies and refreshes inventory. Menu callers are `menuSvcExtras/menuFunctions.py:499-501,504-506`.
- RPC-02515 `AbortTraining`: `skillsvc.py:795-796`; the reply is ignored, and subsequent queue/skill notifications refresh the UI.
- RPC-02516 `ApplyFreeSkillPoints`: `skillsvc.py:867-888` consumes the returned remaining SP to update the client cache.
- RPC-02517 `CharStartTrainingSkill`: `skillsvc.py:771-772`; it updates the queue via follow-up notification/read.
- RPC-02521 `ExtractSkills`: `skillsvc.py:972-974`; RPC-02524 `GetBoosters`: `skillsvc.py:960-963`; RPC-02528 `GetImplants`: `skillsvc.py:965-968`. Extractor preview also reads totals at `skillsvc.py:988-989`. Augmentation consumers include `godma.py:1773,1840,1896`, `attributes.py:40`, `moduleButtonTooltip.py:901`, and `skillQueuePanelNew.py:141,163,535,629`.
- RPC-02523 `GetAttributes`: `skillsvc.py:223-225` applies each returned attribute to dogma state. Other consumers include `godma.py:1840,1920,1922`, `attributes.py:40`, and `attributesPanel.py:41`.
- RPC-02526 and RPC-02527 `GetFreeSkillPoints`: `skillsvc.py:851-853,988-990`; the result is cached and used in injection/extraction previews.
- RPC-02529 and RPC-02530 `GetMySkillHandler`: `skillsvc.py:129-142` obtains and caches the Moniker before calling the handler. It is an accessor used more than once, not two server methods.
- RPC-02531 `GetRespecInfo`: `skillsvc.py:799-803`; the fields are cached for remap UI and invalidated by `OnRespecInfoChanged`.
- RPC-02532 `GetSkillBundleInfo`: `skillsvc.py:1092-1094`; `eve/client/script/ui/crate/lootItem.py:84-91` unpacks (name, icon), passes name to `GetByLabel` (`lootItem.py:53-54`), and uses `icon or ICON_PATH` (`lootItem.py:14-16,80-87`). The full-tree search found no other client call. Empty name is not a valid name fallback.
- Transport adds `machoVersion=1`: `carbon/common/script/net/ServiceCallGPCS.py:221-225`, `ObjectCallGPCS.py:403-407`.

Other whole-tree consumers found include `skillBtns.py:183,196`, `skillInjectorBar.py:50`, `applySkillPointsWindow.py:118`, `moduleButtonTooltip.py:901`, `skillQueuePanelNew.py:141,163,535,629`, `skillPlanUtil.py:54,58`, `skillsCataloguePanel.py:75,82,88,98`, `skillRequirementDialog.py:42`, `panelSkills.py:293,305`, and `characterskills/client/purchase.py:99`.

## EveJS v0.12.9 comparison

The executable reference handlers are in `server/src/services/skills/skillMgrService.js`:

| RPC row(s) | EveJS v0.12.9 |
| --- | --- |
| 02508, 02534 GetSkillHistory | 282-297 |
| 02509 GetSkillPoints | 304-307 |
| 02510 GetSkillQueueAndFreePoints | 444-448 |
| 02511 GetSkills | 319-322 |
| 02512 InjectSkillpoints | 533-541 |
| 02514 SplitSkillInjector | 574-582 |
| 02515 AbortTraining | 527-531 |
| 02516 ApplyFreeSkillPoints | 506-513 |
| 02517 CharStartTrainingSkill | 404-415 |
| 02519 CheckInjectionConstraints | 543-552 |
| 02520 CombineSkillInjector | 584-592 |
| 02521 ExtractSkills | 564-572 |
| 02522 GetAllSkills | 324-327 |
| 02523 GetAttributes | 314-317 |
| 02524 GetBoosters | 450-453 |
| 02525 GetDiminishedSpFromInjectors | 554-562 |
| 02526, 02527 GetFreeSkillPoints | 471-474 |
| 02528 GetImplants | 455-469 |
| 02531 GetRespecInfo | 423-442 |
| 02533 GetSkillChangesForISIS | 299-302 |

The accessor is EveJS `server/src/services/skills/skillMgr2Service.js:10-21`. The eve.rs counterpart is `dotnet/src/Eve.Services.Skills/SkillMgr2Service.cs:5-17`. EveJS does not provide `GetSkillBundleInfo` in its skill handler. Its crate `normalizeSkillBundleEntry` (`server/src/services/campaign/crateService.js:152-188`) stores only bundle ID, SP, and skill grants; it carries no display label or icon. The current eve.rs handler still returns an empty label and `None` icon (`SkillHandlerService.cs`); because the V24 client passes the label to `GetByLabel`, this row remains functionally incomplete until the authoritative name/icon owner is connected. No label or icon was fabricated.

Existing eve.rs implementations use skill, training, inventory, dogma, character, and notification owners. `SkillHandlerService.cs`, `SkillHandlerService.Injection.cs`, and `SkillHandlerService.Training.cs` implement the listed handlers; `CharacterComposition.cs:134-149` constructs the persistent providers. Host registration composes `skillMgr2` and `skillHandler` as separate routes, preserving the Moniker transition.

## Change and checks

Added a socket-level Host regression in `dotnet/tests/Eve.Host.Tests/SkillHandlerRpcHostTests.cs`. It registers the actual `SkillMgr2Service` and `SkillHandlerService` with strict RPC routing, sends the V24 transport `machoVersion=1` kwarg, calls each of the 25 listed row occurrences, checks the Moniker and read response shapes, and checks errors for invalid extraction/injection preconditions. It exercises the no-character paths and does not use PostgreSQL. Existing stateful Host persistence coverage remains in `CharacterHostTests.cs` (for example, `Skill_injector_applies_sequential_awards_and_persists_points_and_inventory_through_host`), but that database test was not run in this task.

- First focused run found the wire decoder represents Moniker names as `PyBytes`; the test was corrected to use the existing text helper.
- Focused local run: 1 passed, 0 failed, 0 skipped.
- The test verifies route reachability and no-character wire contracts; it does not replace character-state persistence, notification, retry, concurrency, restart, or graphical-client checks.
- GetSkillBundleInfo's display name remains a known gap as described above.
- No database was opened. Graphical-client verification was not performed.
