# Skill RPC Host coverage handoff (RPC-02496–RPC-02543)

## State

Host composition registers separate `skillMgr2` and `skillHandler` services: `dotnet/src/Eve.Host/Composition/CharacterComposition.cs:134-149`, `dotnet/src/Eve.Host/Rpc/CharacterRpc.cs:70-72`, and `dotnet/src/Eve.Host/Hosting/HostComposition.cs:263-280`. The V24 client first obtains a `skillHandler` Moniker through `skillMgr2.GetMySkillHandler()`; handler RPC rows tagged `skillMgr2` are that accessor transition and must not be aliased onto a different service.

## V24 client calls and consumers

Whole-tree source search was done over `/home/user/eve-logic`. Key call/consumer evidence for this batch:

| RPC row(s) | V24 call and consumer |
| --- | --- |
| 02508, 02534 GetSkillHistory | `eve/client/script/ui/services/skillsvc.py:362-364,378-380`; history records populate the character sheet (`ui/shared/neocom/charsheet/skillHistoryPanel.py:53`, `notifications/client/development/skillHistoryProvider.py:25`). Record fields used are logDate, eventTypeID, skillTypeID, absolutePoints, level. |
| 02509 GetSkillPoints | `skillsvc.py:731-732,988-989`; extraction preview adds allocated and free SP. |
| 02510 GetSkillQueueAndFreePoints | `ui/services/skillQueueSvc.py:116-117` unpacks queue and free SP into local queue state. |
| 02511 GetSkills; 02522 GetAllSkills | `skillsvc.py:135-142` populate skill caches. Other whole-tree readers include `ui/skillTree/skillTreeDataProvider.py:54-55`, `nodegraph/client/conditions/skill.py:18`, `nodegraph/client/events/skill.py:12,46,50`, and `operations/common/skipconditions/skills.py:13-62`. |
| 02512 InjectSkillpoints | `skillsvc.py:976-977`; client refreshes points/skills after mutation. |
| 02514 SplitSkillInjector; 02520 CombineSkillInjector | `skillsvc.py:1009-1017`; replies are ignored and inventory refresh follows. Menu chain: `menusvc.py:387-390` → `menuSvcExtras/menuFunctions.py:499-506`. |
| 02515 AbortTraining | `skillsvc.py:795-796`; client observes queue and skill notifications. |
| 02516 ApplyFreeSkillPoints | `skillsvc.py:867-888` consumes the returned remaining SP. |
| 02517 CharStartTrainingSkill | `skillsvc.py:771-772`; queue is refreshed through follow-up state/notifications. |
| 02519 CheckInjectionConstraints | `skillsvc.py:982-984`; errors prevent injection. |
| 02521 ExtractSkills | `skillsvc.py:972-974`; extractor preview reads allocated/free SP at `:988-989`. |
| 02523 GetAttributes | `skillsvc.py:223-225` applies each entry to dogma. Other readers: `godma.py:1840,1920,1922`, `ui/shared/neocom/attributes.py:40,79,83-91`, `ui/shared/neocom/charsheet/attributesPanel.py:41`, and `ui/services/skillQueueSvc.py:665`. |
| 02524 GetBoosters; 02528 GetImplants | `skillsvc.py:960-968`; augmentation consumers include `godma.py:1773,1840,1896,1920-1922`, `ui/shared/neocom/attributes.py:86-87`, and `ui/skilltrading/skillExtractorController.py:567`. |
| 02525 GetDiminishedSpFromInjectors | `skillsvc.py:1001-1002` passes injector type, quantity and remaining non-diminishing count; preview consumers include `ui/skilltrading/skillInjectorWindow.py:137,154`, `ui/skilltrading/dailyAlphaInjectorOverlay.py:51`, and `ui/skilltrading/banner.py:61,68,95`. |
| 02526, 02527 GetFreeSkillPoints | `skillsvc.py:851-853,988-990`; cached balance and injector/extractor preview. Other consumers include `ui/shared/tooltip/skillBtns.py:183,196`, `ui/skilltrading/skillInjectorBar.py:50`, `ui/skillPlan/skillQueuePanel/skillQueuePanelNew.py:141,163,535,629`, and `ui/services/skillQueueSvc.py:913,931`. |
| 02529, 02530 GetMySkillHandler | `skillsvc.py:128-142` caches the accessor Moniker before handler calls. It is one accessor called more than once. |
| 02531 GetRespecInfo | `skillsvc.py:799-803` caches the fields for remap UI; invalidation is through `OnRespecInfoChanged`. Other consumers include `ui/shared/neocom/attributes.py:257` and `ui/shared/neocom/charsheet/attributesPanel.py:41`. |
| 02532 GetSkillBundleInfo | `skillsvc.py:1092-1094` forwards (name, icon). `ui/crate/lootItem.py:84-91` unpacks these into a bundle row; `lootItem.py:53-54` passes name to `GetByLabel`, while `lootItem.py:14-16,80-87` uses a default icon when icon is absent. An empty name has no fallback. |
| 02533 GetSkillChangesForISIS | `skillsvc.py:378-380` consumes (typeID, pointChange) pairs; `notifications/client/development/skillHistoryProvider.py:25` and `ui/shared/shipTree/shipTreeSvc.py:368` consume recent skill changes. |

Other whole-tree consumers include `ui/shared/tooltip/skillBtns.py:183,196`, `ui/skilltrading/skillInjectorWindow.py:137`, `ui/skilltrading/applySkillPointsWindow.py:118,120-122,161,163`, `ui/inflight/shipModuleButton/moduleButtonTooltip.py:901`, `ui/skillPlan/skillQueuePanel/skillQueuePanelNew.py:141,163,535,629`, and `characterskills/client/purchase.py:99`.

Transport supplies `machoVersion=1`: `carbon/common/script/net/ServiceCallGPCS.py:221-225`, `ObjectCallGPCS.py:403-407`.

## EveJS v0.12.9 comparison

| RPC row(s) | EveJS v0.12.9 source |
| --- | --- |
| 02508, 02534 GetSkillHistory | `server/src/services/skills/skillMgrService.js:282-297` |
| 02509 GetSkillPoints | `skillMgrService.js:304-307` |
| 02510 GetSkillQueueAndFreePoints | `skillMgrService.js:444-448` |
| 02511 GetSkills | `skillMgrService.js:319-322` |
| 02512 InjectSkillpoints | `skillMgrService.js:533-541` |
| 02514 SplitSkillInjector | `skillMgrService.js:574-582` |
| 02515 AbortTraining | `skillMgrService.js:527-531` |
| 02516 ApplyFreeSkillPoints | `skillMgrService.js:506-513` |
| 02517 CharStartTrainingSkill | `skillMgrService.js:404-415` |
| 02519 CheckInjectionConstraints | `skillMgrService.js:543-552` |
| 02520 CombineSkillInjector | `skillMgrService.js:584-592` |
| 02521 ExtractSkills | `skillMgrService.js:564-572` |
| 02522 GetAllSkills | `skillMgrService.js:324-327` |
| 02523 GetAttributes | `skillMgrService.js:314-317` |
| 02524 GetBoosters | `skillMgrService.js:450-453` |
| 02525 GetDiminishedSpFromInjectors | `skillMgrService.js:554-562` |
| 02526, 02527 GetFreeSkillPoints | `skillMgrService.js:471-474` |
| 02528 GetImplants | `skillMgrService.js:455-469` |
| 02529, 02530 GetMySkillHandler | `server/src/services/skills/skillMgr2Service.js:10-21` |
| 02531 GetRespecInfo | `skillMgrService.js:423-442` |
| 02533 GetSkillChangesForISIS | `skillMgrService.js:299-302` |

EveJS has no `GetSkillBundleInfo` handler. Its crate `normalizeSkillBundleEntry` (`server/src/services/campaign/crateService.js:152-188`) retains only bundle ID, free SP and skill grants, with no display name or icon. eve.rs currently returns empty name and `None` icon in `SkillHandlerService.cs`. The V24 client passes the name to `GetByLabel`, so that response remains functionally incomplete until an authoritative name source is found and connected; no display string was invented.

eve.rs has implementations for the other listed methods in `SkillHandlerService.cs`, `SkillHandlerService.Injection.cs`, and `SkillHandlerService.Training.cs`. They use the existing skills/training/inventory/dogma/character/notification owners. Relevant existing persistence coverage includes `dotnet/tests/Eve.Host.Tests/CharacterHostTests.cs` for injection, notifications, and restart state.

## Host tests and checks

- `SkillMgr2HostWireTests.Skill_mgr2_returns_its_bound_skill_handler_moniker_over_the_host_route` (from origin/main) checks accessor Moniker shape through a socket.
- `SkillHandlerRpcHostTests.Skill_manager_reads_and_injector_mutations_use_the_composed_host_routes` (from origin/main) checks production-composed Host reads, diminishing preview, injection, split, persisted SP and inventory. It uses the PostgreSQL test fixture and was not executed in this no-database task.
- `SkillHandlerRpcHostTests.Skill_rpcs_accept_transport_macho_version_and_keep_the_client_wire_shapes_on_the_host` checks all 25 requested row occurrences via strict real socket routing with `machoVersion=1`, Moniker transitions, response types and rejection preconditions on no-character state. No database is used.
- Focused local no-database run: 1 passed, 0 failed, 0 skipped. The first run exposed that decoded Moniker text is `PyBytes`; the test uses the existing conversion helper.
- The no-character wire test verifies route reachability and those no-character shapes. It does not replace character-state persistence, successful action mutations, notification ordering, retry/concurrency/restart or graphical-client verification.
- No database was opened. Graphical-client verification was not performed.

- Focused implementation push SHA: `4a8c2199cf8676b5091dac8f5df9d22d54c52588`.
- GitHub Actions focused dispatch `40098930677` completed successfully for the focused no-database Host test (0 failed jobs/tests reported).
- Documentation and concurrent-main merge push SHA: `d1e6ba00f6bc7f65c3b39947f61f42e8bfdc7117`.
- Push-triggered `ci.yml` run `40098860277` for the implementation SHA remained in progress at the last status check; check the final pushed SHA with `ci-status.py failures`.