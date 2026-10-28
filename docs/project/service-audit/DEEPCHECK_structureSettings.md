# structureSettings 全クライアント呼び出し・Host深掘り監査

対象は eve-logic V24.01 / build 3396210 全ツリーとCSVの14行。unique methodは5。

## 登録・authority source

StructureComposition.cs:496はPersistenceStructureDirectoryReadModelを使用し、CharacterServicesCompositionでStructureSettingsServiceを合成する。StructureDirectoryService.cs:450-521は登録されたstructureSettings methodsをpersisted structure service/profile stateから読む。read-only RPCで、service自体はstate mutationやnotificationを行わない。clientは全てpositional args、machoVersion=1はtransport kwargs。IDはclientが使用するstructure/service/setting IDs。

## 全caller・reply consumer

| メソッド | 全client call / consume | Host reply / error |
|---|---|---|
| CharacterCheckService | marketMenu.py:19。callerはaccess boolがfalseならmarket access errorを画面に出す (:18-21)。contracts.py:900, quote.py:295, structureEntryMyCorp.py:356, eveCommands.py:2520でも同serviceを呼ぶ。 | access時True。拒否はserviceID 5 StructureMarketDenied、1 StructureDockingDenied、15 ActivateJumpBridgeDenied、16 ConnectToCynoBeaconDenied、それ以外StructureGenericSettingDenied。 |
| CharacterGetService | marketsvc.py:239。replyはstructure market taxとしてcallerへ戻る。 | profile settingの値またはNone。権限なしはNone。 |
| CharacterGetServices | structureServices.py:35,56。structure別 serviceID -> online-state mappingを受けて serviceID in map / availabilityを判定 (:25-64)。infosvc.py:841にはstructureServices service alias経由の参照もある。 | PyDict。不存在/非認可structureのmapはempty dict。 |
| CharacterHasService | structureSettings.py:18、memoize 2秒。UI/cmd callersはeveCommands.py:2520, contracts.py:900, quote.py:295, structureEntryMyCorp.py:356。 | PyBool。session reset時client cache clear (:24-27)。 |
| CharacterHasSetting | structureSettings.py:22、memoize 2秒。undockQuestions.py:56, structureControl.py:95, moonminingExtraHudBtns.py:78, auto_moon_miner_window.py:47がbool gateに使う。 | PyBool。session reset時cache clear。 |

Client values are returned, not inferred from static type records: production read model enforces structure-specific settings. EveJS v0.12.9 structureSettingsService.js reads structure profile and uses structureServiceAuthority.js:159-175,335-339 to select denial labels. The four specialized Host labels match EveJS mapping; unknown access uses StructureGenericSettingDenied. No mutation/notification is expected.

## Wire tests

StructureSettingsAndTravelHostWireTests.csのStructure_settings_jump_bridge_and_reinforcement_calls_use_registered_host_routesはactual Host registrationとservice callsをcoverする。今回のfocused runでは7件のHost wire tests中1件がpass (0 failures, 0 skips)。persisted deployed structure/profile restartとGUI access dialogsは実行していない。


## 引数型
CharacterCheckService, CharacterGetService, CharacterGetServices, CharacterHasService, CharacterHasSettingは全てstructureIDと必要なserviceID/settingIDをpositionalで送る。IDsはintegerでNone optionなし。service settingsの値自体はprofileからPyValueとして返し、bool checkはPyBool。machoVersion=1はtransport kwargs。
