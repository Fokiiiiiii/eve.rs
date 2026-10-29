# Skill manager Host RPC handoff

## Result

The production Host composition owns real skill behavior and registers both skill services. This update adds a Host socket regression for the faction-warfare enlistment methods and records whole-tree V24.01 caller evidence for RPC-01631 through RPC-02514.

No production handler or state owner was duplicated. GetSkillBundleInfo keeps its existing client-shaped fallback, recorded in docs/reference/compat/DEVIATIONS.md under Character phase S6.

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

## Host wiring and existing skill checks

Production construction is dotnet/src/Eve.Host/Composition/CharacterComposition.cs:133-149; dotnet/src/Eve.Host/Rpc/CharacterRpc.cs:68-71 includes SkillMgr2Service and the stateful SkillHandlerService in character RPCs. Host registration consumes that service list in dotnet/src/Eve.Host/Hosting/HostComposition.cs:263-280.

- SkillMgr2HostWireTests.Skill_mgr2_returns_its_bound_skill_handler_moniker_over_the_host_route: socket Host route and machoVersion.
- SkillHandlerRpcHostTests.Skill_rpcs_accept_transport_macho_version_and_keep_the_client_wire_shapes_on_the_host: socket Host route and real SkillHandlerService instance; it uses null dependencies, so this is wire registration, argument transport and fallback-shape coverage, not authoritative skill state.
- SkillHandlerRpcHostTests.Skill_manager_reads_and_injector_mutations_use_the_composed_host_routes: calls the production-composed services through IService.CallAsync (not a socket); verifies read shapes, injector preview and mutation, notification order, persisted free SP/item changes, and split output.
- CharacterHostTests.Skill_injector_applies_sequential_awards_and_persists_points_and_inventory_through_host also calls the composed service directly and checks notification order plus restart persistence.

Whole-tree V24.01 inspection confirms rows RPC-02541 through RPC-02543 are local dictionary get/iteritems operations on GetSkills: it is keyed by type ID, returns skill entries, and callers handle missing entries as None (skillsvc.py:243-251,713); skill maps are iterated at :282 and :731, while the attribute map is iterated into godma at :225-228.

Focused Actions verification passed on earlier pushed skill revisions: runs 40098961742 and 40099326231 each reported `Skill_manager_reads_and_injector_mutations_use_the_composed_host_routes` — 1 passed, 0 failed, 0 skipped. The workflow used an isolated PostgreSQL 18 service; no local database was used. No graphical-client run was performed.

## RPC coverage update: RPC-01631 through RPC-02514

### V24.01 callers and consumers

| Rows | Client call and whole-tree consumer evidence |
|---|---|
| RPC-01631 RemoveMyDirectEnlistment | eve/client/script/ui/services/fwEnlistmentSvc.py:96-100 checks direct membership and calls the RPC; eve/client/script/ui/shared/factionalWarfare/enlistmentFlow/enlistmentUtil.py:136-137 invokes it after confirmation. |
| RPC-01632/01633 SetMyCorpAllowedEnlistmentFactions | fwEnlistmentSvc.py:105-107 forwards the faction list and emits OnCorpAllowedEnlistmentFactionsSet. eve/client/script/ui/shared/neocom/corporation/corp_ui_direct_enlistment_mgt.py:112-113 calls it and rereads policy for the corporation. |
| RPC-02491 AbortTraining | eve/client/script/ui/services/skillsvc.py:794-796 forwards the call; eve/client/script/ui/skillPlan/skillQueuePanel/skillQueuePanelNew.py:361-363 aborts active training then applies the updated queue. The return is ignored. |
| RPC-02492 ApplyFreeSkillPoints | skillsvc.py:867-888 sends selected skill type and SP amount; eve/client/script/ui/skilltrading/applySkillPointsWindow.py:120-163 owns the user selection and submission. |
| RPC-02493 CharStartTrainingSkill | skillsvc.py:768-772 sends the selected skill item ID and location ID; the skill queue service refresh path is skillQueueSvc.py:100-128. |
| RPC-02495 CheckInjectionConstraints | skillsvc.py:981-983 forwards injector item and quantity; eve/client/script/ui/skilltrading/skillInjectorWindow.py:162-164 handles server UserError responses. |
| RPC-02496 CombineSkillInjector | skillsvc.py:1013-1017 validates minimum quantity before forwarding; menusvc.py:389-390 and menuSvcExtras/menuFunctions.py:503-505 provide the inventory menu action. |
| RPC-02497 ExtractSkills | skillsvc.py:969-973 converts the chosen skill-point map and supplies the connection token; skilltrading/skillExtractorController.py:583-585 submits the extraction. |
| RPC-02498 GetAllSkills | skillsvc.py:141-142 refreshes the all-skills handler mapping; :306-313 also builds the local published-skill catalogue used by skill UI flows. |
| RPC-02499 GetAttributes | skillsvc.py:219-228 fetches the attribute map after refreshing implants/boosters and applies each value through godma; consumers include shared/neocom/attributes.py:79 and skillQueueSvc.py:665. |
| RPC-02500 GetBoosters | skillsvc.py:959-962 caches the map; shared/neocom/attributes.py:86-91 and environment/godma.py:1920-1922 read booster entries and their bonuses. |
| RPC-02501 GetDiminishedSpFromInjectors | skillsvc.py:999-1002 supplies type, quantity and remaining non-diminishing count; consumers include shared/info/infoContentBase.py:765, skilltrading/skillInjectorWindow.py:154, dailyAlphaInjectorOverlay.py:51 and banner.py:61,68,95. |
| RPC-02502/02503 GetFreeSkillPoints | skillsvc.py:849-853 supplies the cached balance and :988-989 supplies the extraction threshold calculation. Consumers include tooltip/skillBtns.py:183,196, skillInjectorWindow.py:137, skillQueuePanelNew.py:141,163,535,629 and skillQueueSvc.py:913,931. |
| RPC-02504 GetImplants | skillsvc.py:964-968 caches the map; shared/neocom/attributes.py:86-91, environment/godma.py:1920 and skilltrading/skillExtractorController.py:567 use implant entries. |
| RPC-02505 GetRespecInfo | skillsvc.py:799-803 caches the result; shared/neocom/attributes.py:257 and charsheet/attributesPanel.py:41 read its remap fields. |
| RPC-02506 GetSkillBundleInfo | skillsvc.py:1092-1094 forwards the pair; ui/crate/lootItem.py:85-87 unpacks name and icon for a crate row. |
| RPC-02507 GetSkillChangesForISIS | skillsvc.py:376-388 reads typeID/pointChange pairs to derive recent levels; shared/shipTree/shipTreeSvc.py:368 displays the result. |
| RPC-02508 GetSkillHistory | skillsvc.py:360-364 caches the list; shared/neocom/charsheet/skillHistoryPanel.py:53 supplies it to the history panel. |
| RPC-02509 GetSkillPoints | skillsvc.py:988-989 adds trained and free SP before the extraction minimum check. |
| RPC-02510 GetSkillQueueAndFreePoints | eve/client/script/ui/services/skillQueueSvc.py:116-128 destructures the tuple and refreshes queue/free-SP state and timestamps. |
| RPC-02511 GetSkills | skillsvc.py:133-137,214-218 caches the skill-entry map; other consumers include dogma/clientDogmaLocation.py:443, fittingDogmaLocation.py:316, contracts/createContractWnd.py:1182 and shipTreeUISvc.py:86. |
| RPC-02512 InjectSkillpoints | skillsvc.py:975-979 sends item and quantity, then plays the injector sound and scatters the local injection event; the service also refreshes skill-point balance. |
| RPC-02514 SplitSkillInjector | skillsvc.py:1009-1011 forwards the item and quantity; menusvc.py:387-388 and menuSvcExtras/menuFunctions.py:498-501 drive it from the inventory menu. |

### EveJS comparison for this batch

EveJS v0.12.9 exposes the same FW methods in server/src/services/corporation/fwCharacterEnlistmentMgrService.js:92-101 (policy write) and :124-143 (direct membership removal). Skill methods are routed through server/src/services/skills/skillMgrService.js:282-327, :404-415, :423-474, :506-592 and through training/trading runtimes: skillQueueRuntime.js:1392-1437 and :1623-1667; skillTradingRuntime.js:699-1034. Its reply contracts match the client consumers above. GetSkillBundleInfo has no EveJS route; the existing V24.01 two-field response remains covered by the S6 deviation.

### Host coverage and verification for this batch

Production FW registration is in dotnet/src/Eve.Host/Rpc/CoreRpc.cs and binds callback operations to the character and corporation stores. `FwEnlistmentRpcHostTests.Faction_warfare_enlistment_methods_return_client_shapes_over_registered_host_routes` drives the actual handler over the Host socket with machoVersion, checks the three-field enlistment list and faction-policy list, then verifies removal, cooldown and the subsequent corp-only enlistment shape. The related FactionActivityRpcServicesTests.Enlistment_reads_corporation_policy_persists_direct_membership_and_changes_session covers callback mutation behavior; the production Host composition owns the PostgreSQL persistence delegates.

Local verification before merge: the Eve.Host.Tests project built with 0 warnings and 0 errors, and the focused Host socket test passed (1 passed, 0 failed, 0 skipped). After merging origin/main, the same focused test passed again with its updated transport dispatcher. No database was used. A graphical-client run was not performed.

### CI follow-up for skill rules push 64cc75992fc3adfe4c8eb7ba802fa73dcc58f08a

Selected Actions run 40827781042 passed all 96 SP-threshold cases but failed the alpha service integration case because it used a fixed character ID instead of the created character row; the service consequently had no persisted race ID. The test now uses `Rig.CharacterId`. Push CI run 40827765713 reported EveJS starter skill-point totals of 384036 while the client-ceiling implementation returns 384047; the starter and login oracle expectations now use 384047, which follows the client formula. That CI run also exposed an outdated structure-market balance expectation (the code charges the 100 ISK broker minimum plus 25 ISK SCC minimum) and a bookmark Host test that did not explicitly activate its personal folder; both tests now state their intended conditions. The pure alpha unit test now requests a level allowed to Alpha when testing the SP cap. After merge, all three touched test projects built; 99 focused skill cases passed and the Host wire test passed 1/1. Database-backed alpha, market and bookmark cases need Actions verification. No database was used locally and no graphical-client run was made.


### GitHub Actions status for 06550ad4ded496f93f9501c47599dfa99334dde6

Selected agent-test runs 40225507669, 40225724141 and 40225951161 all failed in actions/setup-dotnet before test execution. The .NET runtime 10.0.14 and SDK 10.0.403 downloads from dotnetorigin.blob.core.windows.net returned HTTP 409 on each attempt. Push-triggered ci.yml run 40225494897 also failed at .NET setup on Linux/Windows and its required gate failed. The selected test did not run in Actions; no GitHub test pass is claimed.

## Skill rules audit update

- V24.01 `characterskills/util.py:39-46` computes every level threshold with `ceil`. Its full client use includes queue entries (`characterskills/queue.py:22-35`), training (`characterskills/skill_training.py:77-107`), applying SP (`eve/client/script/ui/services/skillsvc.py:709-715`) and extraction (`eve/client/script/ui/skilltrading/skillExtractorController.py:180-205,317,386-387`). The old EveJS v0.12.9 helper rounds instead (`server/src/services/skills/skillState.js:126-136`). `SkillPoints.At` now uses the client threshold; the table regression covers ranks 1-16 and levels 0-5, including the point below each threshold.
- The client respec panel reads the attribute map via `skillsvc.py:219-228`; `attributes.py:80,98-103,192,238-247` subtracts the five values from the 99-point pool and refuses a non-99 allocation. `attributes.py:257-266` prefers a ready timed remap before spending `freeRespecs`. EVE University says base Charisma is 19, other base attributes 20, the sum is 99, and the character gets one normal plus two bonus remaps (`Skills_and_learning`, https://wiki.eveuniversity.org/Skills_and_learning, lines 260-261, 366-373, 410). Thus the persisted/default map is 19/20/20/20/20 and `freeRespecs` starts at 2. EveJS's 20 across and default `freeRespecs=3` are recorded as deviations in `docs/reference/compat/DEVIATIONS.md`.
- Queue wire flow: `skillQueueSvc.py:141-155` packs the position-to-`(typeID, level)` map and calls `SaveNewQueue`; `:392-403` retries alpha-SP refusals as paused queues. `skillQueuePanelNew.py:318-345` consumes both alpha error names and `:409-440` calculates the cap and per-race level disablement. `clonegrade/const.py:34` sets 5,000,000; `characterskills/queue.py:10-11` sets the 10-year duration and 150-entry limit. `SaveNewQueue` now forwards account clone state and character race to `CharacterTraining.SaveQueueAsync`; active alpha queues return the two expected `UserError`s, while paused queue plans can be saved. A focused Host/service-path regression covers the two failures and the paused retry.
- Local checks: builds passed for `Eve.Services.Skills.Tests` and `Eve.Skills.Tests` (0 warnings/errors) and `Eve.Host.Tests` (0 warnings/errors); the all-ranks/all-levels threshold test passed 96/96 cases; the non-database Host wire-route test passed 1/1. The two PostgreSQL-backed skill RPC regressions were not run locally because this task forbids database use; run them through Actions after pushing. No graphical-client run has been made.
