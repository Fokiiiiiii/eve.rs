# Skill manager Host RPC handoff

## Result

The Host composition already owns real skill behavior and registers both requested services. This batch replaces the null-dependency socket test with real Host wiring coverage and adds a socket regression for the skillMgr2 moniker. The composed-host wire test covers the remaining methods, stateful injection, injector splitting, persistence reads, client shapes, and transport machoVersion.

No handler or state owner was duplicated. GetSkillBundleInfo keeps its existing client-shaped fallback, which is recorded in docs/reference/compat/DEVIATIONS.md under Character phase S6.

## Eve-logic whole-tree call and consumer evidence

| Rows | Client call and consumer evidence |
|---|---|
| RPC-02523 GetAttributes | eve/client/script/ui/services/skillsvc.py:219-228 refreshes boosters and implants before reading the attribute map and applies each returned attribute through godma. Other consumers: eve/client/script/ui/shared/neocom/attributes.py:79; .../attributesPanel.py:48,64; eve/client/script/ui/services/skillQueueSvc.py:665. |
| RPC-02524 GetBoosters; RPC-02528 GetImplants | Calls/cache at skillsvc.py:959-966; map consumers at eve/client/script/environment/godma.py:1920-1922, eve/client/script/ui/shared/neocom/attributes.py:86-87, and eve/client/script/ui/skilltrading/skillExtractorController.py:567. The attribute page iterates booster and implant entries and looks up SDE bonuses. |
| RPC-02525 GetDiminishedSpFromInjectors | skillsvc.py:999-1002 passes type, quantity, and remaining non-diminishing count. Whole-tree UI consumers include eve/client/script/ui/shared/info/infoContentBase.py:765, .../skilltrading/skillInjectorWindow.py:154, .../dailyAlphaInjectorOverlay.py:51, and .../banner.py:61,68,95; the injector window adds the result to free SP (skillInjectorWindow.py:137). |
| RPC-02526/02527 GetFreeSkillPoints | Calls at skillsvc.py:849-851 (cached UI balance) and :988-989 (extractor threshold, alongside GetSkillPoints). Consumers include eve/client/script/ui/shared/tooltip/skillBtns.py:183,196, .../skilltrading/skillInjectorBar.py:50, .../skilltrading/skillInjectorWindow.py:137, .../skillPlan/skillQueuePanel/skillQueuePanelNew.py:141,163,535,629, and eve/client/script/ui/services/skillQueueSvc.py:913,931. |
| RPC-02529/02530 GetMySkillHandler | skillsvc.py:128-137 caches the skillMgr2 moniker and uses the bound handler for skill reads. The moniker service and bind-parameter shape matches EveJS. |
| RPC-02531 GetRespecInfo | Cached call at skillsvc.py:799-803; consumers include eve/client/script/ui/shared/neocom/attributes.py:257 and .../charsheet/attributesPanel.py:41, which read respec state for the attribute UI. |
| RPC-02532 GetSkillBundleInfo | skillsvc.py:1092-1093 forwards the result; eve/client/script/ui/crate/lootItem.py:81-87 unpacks (name, icon) to construct a bundle loot row. |
| RPC-02533 GetSkillChangesForISIS | skillsvc.py:376-388 consumes (typeID, pointChange) pairs to derive recently trained levels; eve/client/script/ui/shared/shipTree/shipTreeSvc.py:368 displays that result. |
| RPC-02534 GetSkillHistory | skillsvc.py:360-362 caches the requested history; eve/client/script/ui/shared/neocom/charsheet/skillHistoryPanel.py:53 supplies it to the character sheet. |
| RPC-02535 GetSkillPoints | skillsvc.py:988-989 adds trained and free SP before allowing extraction. |
| RPC-02536 GetSkillQueueAndFreePoints | eve/client/script/ui/services/skillQueueSvc.py:116-117 destructures the queue/free-SP pair before refreshing the queue transaction; queue state feeds queue timestamps at :119-128. |
| RPC-02537 GetSkills | Remote read/cache at skillsvc.py:133-137,214-218. Whole-tree callers include eve/client/script/dogma/clientDogmaLocation.py:443, eve/client/script/ui/shared/fittingScreen/fittingDogmaLocation.py:316, .../contracts/createContractWnd.py:1182, and .../shipTree/shipTreeUISvc.py:86. Values are skill-entry records used by the skill, fitting, contract, and ship-tree UIs. |
| RPC-02538 InjectSkillpoints | skillsvc.py:976-979 calls the mutation and then plays the injector sound and emits the local injection event. The server mutation also changes free SP and inventory. |
| RPC-02540 SplitSkillInjector | Menu action chain: eve/client/script/ui/services/menusvc.py:387-388 → .../menuSvcExtras/menuFunctions.py:498-501 → skillsvc.py:1009-1011. |
| RPC-02541/02542 skillMgr2.get | These are local Python dictionary get consumers of the GetSkills result, not remote RPC methods: skillsvc.py:243-251 and :713. The relevant wire contract is a mapping from skill type IDs to skill-entry records. |
| RPC-02543 skillMgr2.iteritems | This is local mapping iteration, not a remote method. skillsvc.py:225-228 iterates the attribute map into godma; :282 and :731 iterate skill maps. Other attribute-map iteration appears in eve/client/script/ui/shared/neocom/attributes.py:83-91. |

## EveJS comparison

EveJS v0.12.9 implements the same routed read and mutation families in server/src/services/skills/skillMgrService.js: history 282-297; ISIS changes 299-302; total skill points 304-307; attributes and skills 314-322; respec info 423-442; queue plus free points 444-448; boosters and implants 450-469; free skill points 471-474; injector awards 533-541; diminished preview 554-562; split 574-582. skillMgr2Service.js:10-21 returns the carbon.common.script.net.moniker.Moniker for skillHandler, binding the current character. For rows 02541-02543, EveJS returns a Python mapping for client-side access (skillMgrService.js:210-218); those names are not server RPCs. EveJS has no GetSkillBundleInfo route; the existing eve.rs empty-label/None two-value response follows the V24.01 loot-row consumer and the S6 compatibility record in DEVIATIONS.md.

## Host and verification path

Production construction is dotnet/src/Eve.Host/Composition/CharacterComposition.cs:133-149; dotnet/src/Eve.Host/Rpc/CharacterRpc.cs:68-71 includes SkillMgr2Service and the stateful SkillHandlerService in character RPCs. Host registration consumes that service list in dotnet/src/Eve.Host/Hosting/HostComposition.cs:263-280.

Tests:
- SkillMgr2HostWireTests.Skill_mgr2_returns_its_bound_skill_handler_moniker_over_the_host_route: socket Host route and machoVersion.
- SkillHandlerRpcHostTests.Skill_manager_reads_and_injector_mutations_use_the_composed_host_routes: socket calls against the production-composed services; checks read shapes, 50-result history argument, two-injector preview and mutation, notification order before the CALL_RSP, persisted free SP/item changes, and valid split output.
- Existing CharacterHostTests.Skill_injector_applies_sequential_awards_and_persists_points_and_inventory_through_host additionally checks notification order and restart persistence through the composed registry.

No handler change was needed because the current source already had these real Host routes and the composed socket regression. Whole-tree V24.01 inspection confirms rows RPC-02541 through RPC-02543 are local dictionary get/iteritems operations on GetSkills: it is keyed by type ID, returns skill entries, and callers handle missing entries as None (skillsvc.py:243-251,713); skill maps are iterated at :282 and :731, while the attribute map is iterated into godma at :225-228.

The first composed-host Actions run exposed that injector notifications arrive before the RPC reply; the socket reader now consumes and checks OnItemChange then OnFreeSkillPointsChanged before returning the reply. Host.Tests compile-check passed on merged source with 0 warnings and 0 errors. The corrected composed-host regression passed on run 40098961742 (1 passed, 0 failed, 0 skipped); another focused run on f3a971e782c9603a3343f3a3260ce4eb4d1f96c5 passed the same test. No database was used locally. No ci.yml run was available for the pushed revision when checked. No graphical-client run was performed.
