# Skill RPC Host coverage handoff

## Scope and result

This batch covers RPC-02523 through RPC-02543 in the skill manager list. Source inspection found these service endpoints are already present: Host registers `SkillMgr2Service` and `SkillHandlerService` in `dotnet/src/Eve.Host/Rpc/CharacterRpc.cs:69-72`; methods are attributed in `dotnet/src/Eve.Services.Skills/SkillHandlerService.cs:412-608`, `SkillHandlerService.Injection.cs:100-320`, and `SkillMgr2Service.cs:9-17`. The service and state owners already exist, so no second handler, store, or duplicate route was added.

`dotnet/tests/Eve.Host.Tests/SkillReadRpcHostTests.cs` exercises the actual Host dispatcher with the concrete service objects. It sends the transport-added `machoVersion` keyword and a normal burst of calls. The test checks the skillHandler moniker and wire responses for skills, points, attributes, boosters, implants, skill history, ISIS deltas, respec info, queue/free points, bundle info, diminished injector points, injection, and splitting. It calls the two GetFreeSkillPoints locations and GetMySkillHandler twice. It needs no database.

## Whole-client callers and consumers

| RPC row | V24.01 caller and consumer |
|---|---|
| 02523 `GetAttributes` | `eve/client/script/ui/services/skillsvc.py:223-225` applies each attribute to dogma after refreshing booster and implant maps at `:221-222`; respec attribute UI also reads those maps in `eve/client/script/ui/shared/neocom/attributes.py:86-88`. |
| 02524 `GetBoosters` | `skillsvc.py:959-962` caches the map. Consumers include attribute UI `attributes.py:86-88` and dogma state `eve/client/script/environment/godma.py:1921-1923`. |
| 02525 `GetDiminishedSpFromInjectors` | `skillsvc.py:1000-1002`; its numeric result drives the injector amount preview. |
| 02526, 02527 `GetFreeSkillPoints` | `skillsvc.py:851-852` caches free points; `:989-990` combines trained and free points for extraction. Queue initialization unpacks and applies the paired points in `skillQueueSvc.py:75-77,115-116`. Other client uses include `skillQueueSvc.py:913,931`, `skillInjectorWindow.py:137`, and `skillPlan/skillQueuePanel/skillQueuePanelNew.py:141,163,535,629`. |
| 02528 `GetImplants` | `skillsvc.py:964-967` caches implants. The map feeds attribute UI at `attributes.py:86-88`, dogma state at `godma.py:1920`, and extractor eligibility at `skillExtractorController.py:567-568`. |
| 02529, 02530 `GetMySkillHandler` | `skillsvc.py:129-130` obtains and caches the returned moniker; the client then calls methods on that bound handler, starting with `:135-136`. EveJS returns service name, node, bind parameter, and session check in the same order. |
| 02531 `GetRespecInfo` | `skillsvc.py:799-806` caches the record; the respec-change event clears it and refreshes attributes before emitting `OnRespecInfoUpdated`. |
| 02532 `GetSkillBundleInfo` | `skillsvc.py:1092-1094` forwards the tuple; `eve/client/script/ui/crate/lootItem.py:83-87` unpacks name/icon to construct the bundle loot item. |
| 02533 `GetSkillChangesForISIS` | `skillsvc.py:377-389` consumes type/point-change pairs, derives prior skill levels, and returns the recently trained skill map. |
| 02534 `GetSkillHistory` | `skillsvc.py:360-363` caches rows; `eve/client/script/ui/shared/neocom/charsheet/skillHistoryPanel.py:53-77` uses dates, event types, skill IDs, and absolute points to build the panel. |
| 02535 `GetSkillPoints` | `skillsvc.py:988-990` combines server skill points and free points for extractor eligibility; the local aggregate is also read by corporation recruitment at `corpRecruitmentContainer.py:1230`. |
| 02536 `GetSkillQueueAndFreePoints` | `skillQueueSvc.py:75-77,115-116` unpacks the tuple and applies free points to queue state. |
| 02537 `GetSkills` | `skillsvc.py:135-136,214-224` caches skill records and applies their attribute and level data; the map is read throughout requirements, skill tree and fitting UI paths. |
| 02538 `InjectSkillpoints` | `skillsvc.py:975-977` invokes the mutation, plays the injector sound and raises the local injected-points event. |
| 02540 `SplitSkillInjector` | `skillsvc.py:1009-1011` invokes the mutation; the menu route is `menuSvcExtras/menuFunctions.py:498-500`, reached from `menusvc.py:386-388`. |
| 02541, 02542 `get`; 02543 `iteritems` | These are Python mapping operations on client-local dictionaries, not remote service calls. For example, skill/attribute response maps are iterated at `skillsvc.py:224,282,403`; a skill is looked up locally with `get` at `:713`. No Host method should be registered for them. |

## EveJS comparison

In `/home/user/evejs-v0.12.9/server/src/services/skills/skillMgrService.js`, EveJS handles attributes (`:314-317`), skills (`:319-322`), skill points (`:304-307`), history (`:282-297`), ISIS deltas (`:299-302`), respec (`:423-442`), queue/free points (`:444-448`), boosters and implants (`:450-469`), free points (`:471-474`), injection (`:533-541`), diminished points (`:554-562`), and splitting (`:574-582`). `skillMgr2Service.js:10-21` returns the four-part skillHandler moniker. eve.rs reads and mutates the existing character, training, skill, inventory, and SDE owners for these operations. EveJS has no `GetSkillBundleInfo` handler; the client consumes a two-value name/icon tuple and accepts an absent icon. eve.rs preserves that wire shape with an empty name and None icon, as recorded in `docs/reference/compat/DEVIATIONS.md`.

The C# wire regression asserts no-character fallbacks and a value derived from injected TrainingSettings. It does not exercise valid-character PostgreSQL state, persistence, inventory notifications, injector transactions, or graphical-client acceptance. Existing skill service tests own the data-backed rules. Actions verification is listed below.

## Verification

- Local `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false`: passed, 0 errors; one transient MSBuild copy retry occurred while another build process held CharacterInfo output.
- Local focused test `Skill_bootstrap_and_character_reads_use_the_registered_wire_routes`: passed, 1 passed, 0 failed, 0 skipped.
- No PostgreSQL database was used.
- Actions result: pending.
