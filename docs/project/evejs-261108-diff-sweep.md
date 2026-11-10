# EveJS 261028.1 → 261108.1 差分調査

## 対象範囲とソース

`/home/user/evejs-261028.1` と `/home/user/evejs-261108.1` の `server/`、`config/`、`content-packs/`、`tools/` を比較した。両ソースディレクトリに Git メタデータがないため、各ツリーの `server/package.json:3` でバージョンを確認した。対象クライアントは V24.01 build 3396210、checkout `1dbb5711a072a146e1d228365668ac05655eefe1` の `/home/user/eve-logic`。

変更パスは各1回だけ掲載する。変更されたテキストファイルは旧版・新版それぞれの変更箇所行を記し、追加・削除ファイルは全行を示す。多くの content-pack JSON は1行に圧縮されているため、参照行は1。`tools/DatabaseCreator/staticTables/dungeonAuthority/data.json` は約70 MB。差分ツールがこれと他の静的テーブルJSON 4件をバイナリ/大型ファイルとして扱ったため、該当行には旧版・新版の全行範囲を記載した。`server/src/edge/chat/chatEdgeRuntime.js` と `server/src/services/chat/commands/crimewatch.js` はバイト列のみの変更で、論理行の内容は同一。

| ルート | 変更 | 追加 | 削除 | 合計 |
| --- | ---: | ---: | ---: | ---: |
| `server/` | 381 | 189 | 0 | 570 |
| `config/` | 5 | 0 | 0 | 5 |
| `content-packs/` | 0 | 11 | 5 | 16 |
| `tools/` | 19 | 5 | 0 | 24 |
| **合計** | **405** | **205** | **5** | **615** |

分類件数: combat 160、exploration 27、industry 28、market 19、character 116、UI 16、GM command 21、data 75、other 153。

## 分類と監査キー

分類は指定された9領域（`combat`、`exploration`、`industry`、`market`、`character`、`UI`、`GM command`、`data`、`other`）を使う。`H-*` は提示された261108.1のリリース見出しに含まれる変更、`N-*` は見出し外で下表に監査結果を記した変更を示す。`H / N-*` は1ファイルに両方の変更があることを示す。`PD` はProject Discoveryの変更で、分類上はcharacterに含めたが、別ジョブのため挙動監査対象から除外した。

クライアントの呼び出し箇所から確認できるのは、クライアントが使う要求・ペイロード・通知までである。隠れたサーバーポリシー、作成されたコンテンツ、スポーン確率、永続化の意味までは証明しない。「部分的」「未確認」は同等性を主張しない保守的な判定である。

## リリース見出し外の挙動監査

| キー | 提示見出し外の変更 | 現行eve.rs | V24.01クライアント根拠と判定 |
| --- | --- | --- | --- |
| `N-MISSION` | セキュリティミッションの候補提示・投影・レベル別パック（追加パックはクライアント dungeon build 3579973 を記載）、キャリアエージェントの連鎖・進捗・報酬、Fractured Legacy / Visions の物語、ダンジョン参加・結果、ミッション貨物・採掘・サルベージ等。 | eve.rs は `agentMgr` のRPC面を登録している（`dotnet/src/Eve.Services.Core/AgentMissionService.cs:74-104`）が、実際のオファー生成経路はない（`docs/project/TODO.md:1175`）。コミット済みミッションデータは build 3405148 で、対象 build 3396210 と異なる。候補一覧、ダンジョンの不足、報酬入力も未解決（`TODO.md:1270-1276`）。**RPC面は一部あり。候補・コンテンツ・報酬の同等性は未確認。** | V24は `GetAgents`、完了済みキャリアエージェント、無効化されたミッション、agent-system系メソッドを呼ぶ（`/home/user/eve-logic/eve/client/script/ui/station/agents/agents.py:89-98,216-226,356-363,799-802`）。この呼び出し箇所では候補選定や作成シーン・報酬値は決まらない。パックのbuild情報も対象build 3396210との一致を証明しない。**呼び出し面は確認。コンテンツ同等性は未確認。** |
| `N-INCURSION` | インカージョンのライフサイクル、遭遇サイト、参加条件、補給、報酬、tale/mapへの投影。リリース見出しのSleeper NPC修正とは別の変更。 | eve.rs はフェーズ・サイトを永続化し、ライフサイクル処理をスケジュールする（`dotnet/src/Eve.Services.Space/Incursions/IncursionDirector.cs:14-60`）。map/tale/anomaly読み取りも提供する（`dotnet/src/Eve.Services.Core/IncursionAnomalyService.cs:71-85,97-110,285-296`）。貢献度報酬と遅延LPは未対応項目（`docs/project/TODO.md:62,1479-1488`）。**一部実装。精算・報酬は新しいEveJSランタイムと同等でない。** | V24はtaleデータを読み、遅延報酬を要求する（`/home/user/eve-logic/eve/client/script/ui/shared/incursionSvc.py:68-82,254-260`）。mapとjournalも `GetIncursionGlobalReport` を呼ぶ（`/home/user/eve-logic/eve/client/script/ui/shared/agencyNew/contentProviders/contentProviderIncursions.py:42`; `/home/user/eve-logic/eve/client/script/ui/shared/neocom/journal.py:781`）。表示・読み取り面の根拠にはなるが、EveJSの参加計算や報酬値の根拠にはならない。**クライアントの面とeve.rsの遅延精算ギャップを確認。新ポリシーは未確認。** |
| `N-MAP` | 太陽系干渉状態、ローカル/星図上のバンド、減衰投影。 | eve.rs は `solarsystemInterferenceMgr` を登録し、6フィールドのEx1ペイロードと減衰計算を伴う両メソッドを提供する（`dotnet/src/Eve.Services.Core/CoreServices.cs:90`; `dotnet/src/Eve.Services.Core/GameRpcServices.cs:1429-1471,1480-1521`）。Hostは永続テーブルを読むが、状態を書き込む処理との同等性は調べていない。**読み取り面と投影は一致。状態の作成元・永続化は未確認。** | V24はローカル状態/全バンドを取得し、時刻補正した状態を計算する（`/home/user/eve-logic/eve/client/script/ui/services/solarsystemInterferenceSvc.py:32-56`; `/home/user/eve-logic/solarsysteminterference/solarsystemInterferenceState.py:91-113`）。星図とシステム情報欄でそのバンドを使う（`/home/user/eve-logic/eve/client/script/ui/shared/maps/starModeHandler.py:1012-1025`; `/home/user/eve-logic/eve/client/script/ui/shared/info/infoContentBase.py:1572-1577`）。**RPCとペイロードの利用を確認。状態の作成元・永続化はクライアントから確認できない。** |
| `N-HOMEFRONT` | Homefrontのクライアント/ワールド更新のうち、提示見出し外のもの。 | eve.rs はサイト一覧の発見面を実装する（`dotnet/src/Eve.Services.Core/DungeonRpcServices.cs:150-180`）が、EveJSの目標進行やジョブ状態までは示さない。**サイト発見のみ。ランタイム同等性は未確認。** | V24のAgency providerは `DUNGEON_TRACKER_ID = 'homefront_operations'` を設定する（`/home/user/eve-logic/eve/client/script/ui/shared/agencyNew/contentProviders/contentProviderHomefrontSites.py:12-18`）。Job Boardは各ダンジョンインスタンスに対するジョブを引く（`/home/user/eve-logic/eve/client/script/ui/shared/agencyNew/ui/contentPageInfoConts/homefrontSiteInfoContainer.py:61-65`; `/home/user/eve-logic/jobboard/client/features/dungeons/homefront_operations/provider.py:13-31`）。**サイト発見とジョブ検索面は確認。変更された進行ルールは確認できない。** |
| `N-AIR` | AIR NPEのランタイム/効果とtutorial pack 1.0.11（manifestは `nativeClientVerified:false`、提供元graph build 3487903と記載）。 | eve.rs はAIRの有効状態・状態・skipを永続化/提供する（`dotnet/src/Eve.Services.Core/AirNpeService.cs:17-60`）が、EveJSのcheckpoint/graph実行ランタイムではない。 | V24 AIRコードは有効状態・状態を読み、skipを送る（`/home/user/eve-logic/storylines/client/airnpe.py:58-109`）。この呼び出し箇所では追加graphコンテンツや効果は確認できない。**基本的な状態/skip面は一致。追加graphコンテンツ/効果は未確認。** |
| `N-NODEGRAPH` | NodeGraph compiler、効果/ランタイム、native atom署名データ。 | eve.rs は任意callbackを登録する（`dotnet/src/Eve.Services.Core/CoreServices.cs:22,73`）。Host側callbackがない場合、`NodeGraphService` は既定値を返し、graphを実行・永続化しない（`UnportedFeatureRpcServices.cs:197-209,239-247`）。**同等ではない。** | V24はgraphメッセージとblackboard読み取りを送る（`/home/user/eve-logic/nodegraph/client/graph.py:13-26`）ほか、QA用graph操作がある（`/home/user/eve-logic/nodegraph/client/log_graph.py:45-77,114-127`）。**実際のサービス利用を確認。eve.rsの実行処理はない。** |
| `N-VOID` | 通常のexit/jump処理を超える、過去のVoidSpaceを再生する非公開room、参加条件、天候。 | eve.rs は `voidSpaceMgr` の基本メソッドのみを公開する。汎用サービスのexit/jumpメソッドは明示的に拒否し（`dotnet/src/Eve.Services.Core/LoginInitializationRpcServices.cs:774-801`）、過去のreplay roomはない。 | V24はexit gateと `VoidSpacePlayerJump` を呼ぶ（`/home/user/eve-logic/eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:588-590`; `/home/user/eve-logic/voidspace/client/void_space_service.py:20-46`）。**基本RPCは確認。replay sceneはこの呼び出し箇所では確認できず、eve.rsにもない。** |
| `N-EVENTS` | Warpathミッション/報酬、daily goals、login campaign、event schedule、season報酬。 | eve.rs はseason系メソッドを提供するが、稼働中seasonの定義・進捗・報酬は利用できず既定値が残る（`docs/reference/compat/DEVIATIONS.md:4596-4597`; `docs/project/TODO.md:2699`）。**EveJSのcatalog/scheduleとの一致は未確認。** | V24は `seasonManager` を初期化しseason/character情報を取得する（`/home/user/eve-logic/seasons/client/seasonservice.py:74-80,145-167,243-259`）。achievement handlerは進捗を送る（`/home/user/eve-logic/achievements/client/eventHandler.py:112-120`）。新しいWarpath scheduleや報酬表を確定するV24ソースは確認できなかった。**API面は確認。追加コンテンツは未確認。** |
| `N-GM` | `/sov`、`/war start`、`/extraction advance`、ネイティブクライアントmenu操作以外のGM command。 | eve.rs は個別に登録したQA/GM一式を提供する（`docs/project/GM_QA_DESIGN.md:49-71`）。新しいEveJS command modules全体との同等性はない。 | V24には汎用slash dispatchがあり、menuから `/unspawn`、`/unload`、`/heal`、`/online`、`/skyhook state` などを呼ぶ（`/home/user/eve-logic/eve/client/script/ui/services/menusvc.py:781-804,834,885,890,1061,1241`）。**menuからの直接呼び出しのみ確認。他の追加commandはクライアント根拠なし。** |
| `N-CORPORATION` | 提示されたGM command以外のcorporation/alliance/war/sovereignty状態。secondary mercenary-den/insurgency gateway状態とSkyhook raid lifecycleを含む。 | eve.rs は `warRegistry` 系を提供する（`dotnet/src/Eve.Services.Corporations/WarRegistryService.cs:12-27`）が、変更されたEveJS gatewayやSkyhook raid状態との同等性は示せていない。structure/sovereigntyにも未完了項目がある（`docs/project/TODO.md:64,2589`）。**一部service群が重なるが、新状態ポリシーは未確認。** | V24の `linkWithShip` 呼び出しを確認した（`/home/user/eve-logic/spacecomponents/client/components/linkWithShip.py:24-25,237-245`）。eve.rsは `linkWithShip.InitiateLink` を設定済みCRAB provider経由に限定し（`dotnet/src/Eve.Services.Space/BeyonceService.Components.cs:9-29`）、Skyhook raid providerはない。通常のcorporation/war RPCは新lifecycleの証拠にならない。**Skyhook raidは未対応。その他のポリシーも未確認。** |
| `N-STRUCTURE` | moon-drill各phase効果と `/extraction advance` 以外のdeployable、dock/delivery/asset-safety、structure変更。 | eve.rs はstructure記録/timer、dock/deploy、moon extraction記録を持つが、extraction変更と他のstructure処理は未完了（`docs/project/TODO.md:64`）。**一部実装。全体の同等性は主張しない。** | V24にはdock/undockとdeploymentの呼び出しがある（`/home/user/eve-logic/eve/client/script/ui/services/structure/structureDocking.py:26-81`; `/home/user/eve-logic/eve/client/script/ui/services/structure/structureDeployment.py:65-69,173`）。変更された精算/回収ルールの根拠ではない。**クライアントの呼び出し面のみ。** |
| `N-INDUSTRY` | moon-drill phase効果以外のmining cargo/精算、planetary/customs、reprocessing/compression。 | eve.rs は共有mining/industryルールを実装する（`dotnet/src/Eve.Services.Core/IndustryRules.cs:73-89`）。mining-scanとstructure extractionには未完了項目がある（`docs/project/TODO.md:47,64`）。**基本処理は重なるが、変更されたポリシーの一致は未確認。** | V24のindustry helperはblueprint素材を再帰的に価格計算し、adjusted averageへfallbackする（`/home/user/eve-logic/eve/common/script/util/industryCommon.py:521-543`）。新しい配置/精算の権威データは確認できない。**新しい分布/修正値はクライアントから未確認。** |
| `N-EXPLORATION` | 自然beltの形状/構成、K-space gas配置、hacking、名前付きwormhole変更以外のprivate mission anomaly。 | eve.rsにはscan/site面がある。今回、自然belt/gas配置テーブルを対象buildの権威データに結び付ける根拠は見つからなかった。**同等性は未確認。** | V24のscan処理はscan要求、probe回収、probe活動状態変更を行う（`/home/user/eve-logic/eve/client/script/parklife/scanSvc.py:193-197,324-342,423-435`）。world-generation policyの根拠にはならない。**新belt/gasルールはクライアントから確認できない。** |
| `N-MARKET` | 価格/時刻順matching、NPC限定seed v4、private-contract broker fee見出し以外のcontract auction/runtimeとsecondary gateway変更。 | eve.rsにはmarket（`dotnet/src/Eve.Services.Market/MarketProxyService.cs:14-40`）とcontract service/storeがあるが、変更されたauction遷移の一致はこの静的調査で確認していない。**未確認。** | V24のmarket/contract呼び出し（`/home/user/eve-logic/eve/client/script/ui/shared/market/quote.py:95-103`; `/home/user/eve-logic/eve/client/script/ui/shared/market/marketbase.py:2766-2774`）はmarket RPC境界を示すが、サーバー側matchingやauction永続化は示さない。**呼び出し面は確認。内部ポリシーは未確認。** |
| `N-ECONOMY` | 名指しリリース項目以外のaccount、bill、wallet、bounty、store変更。 | eve.rsにはwallet/account serviceがある（`dotnet/src/Eve.Services.Wallet/AccountService.cs:1-45`）が、receipt、projection、retry各経路の旧新同等性は確認していない。**包括的な同等性は未確認。** | V24のwallet送金画面は応答を使う（`/home/user/eve-logic/eve/client/script/ui/shared/neocom/wallet/panels/personal/giveIskPanel.py:103-107`; `/home/user/eve-logic/eve/client/script/ui/shared/neocom/wallet/transferMoneyWnd.py:147-152`）が、内部の耐久性/retryを規定しない。**wire面は確認。精算内部は未確認。** |
| `N-CHARACTER` | MCT、jump clone、SKINR、PD以外のcharacter/access group、bookmark、skill、chat、inventory、fitting、reward状態。 | eve.rsにはskill（`dotnet/src/Eve.Services.Skills/SkillMgr2Service.cs:5-17`）やinventory（`dotnet/src/Eve.Services.Inventory/InvBrokerService.cs:17-26,526-583`）を含む対応Host service/永続化がある。変更パスは多数のowner/notificationにまたがる。**個々の状態変更の同等性は未確認。** | V24のskill queue操作（`/home/user/eve-logic/eve/client/script/ui/services/skillQueueSvc.py:247-283`）やfitting保存（`/home/user/eve-logic/eve/client/script/environment/fittingSvc.py:1150-1151`）は関連RPCを制約するが、private store/retryの挙動は示さない。**個々のwire挙動のみ確認。包括的な同等性なし。** なおresculpt差分は261108の変更ではない。両EveJS版の `characterResculpt.js` は同一で、両certificate typeにstate 1を設定する（`261028.1/server/src/services/character/characterResculpt.js:15-18,92-97`; `261108.1/server/src/services/character/characterResculpt.js:15-18,92-97`）。eve.rsはtype 34132をpaper-doll state 3、89956を1に対応させる（`dotnet/src/Eve.Host/Rpc/RewardRpc.cs:64-76`）。V24はstate値を定義し（`/home/user/eve-logic/paperdoll/common/state.py:8-13`）、editor modeを選ぶ（`/home/user/eve-logic/eve/client/script/ui/login/characterCreationContainer_new.py:275-284`）。**これは既存の差で、本差分には含まれない。** |
| `N-COMBAT` | empire sentry、CONCORD timer/penalty、fighter、cloak/D-scan、doomsday warp停止、warp中launch禁止の見出し外にあるNPC/module/combat変更。 | eve.rsにはRustのspace/NPC ownerがあるが、release全体を通したEveJSとの同等性比較は行っていない。**見出し外combatポリシーは未確認。** | V24のmodule buttonはactivation actionを呼ぶ（`/home/user/eve-logic/eve/client/script/ui/inflight/shipModuleButton/shipmodulebutton.py:960-963`）。クライアントソースからNPC AI、spawn選択、非公開timer計算は確定できない。**protocol/presentation面のみ。内部ポリシーは未確認。** |
| `N-DATA` / `N-TOOLS` | より具体的なgameplay領域に属さないconfig、static table、setup tool、作成データ。 | eve.rsは独自のpinned SDE/data pipelineを使う。EveJS出力だけを権威データとは扱わない。 | V24 checkoutは上記のとおりbuild 3396210。調査したcallsiteは改訂static tableや提供元buildのデータがこの対象と同等であることを示さない。**データ全体の同等性は未確認。** |
| `N-OTHER` | Network/game-store、identity、dynamic-resource、scheduler、chat transportなど、他の特定gameplay ownerに該当しないサーバー変更。 | eve.rsはHost所有のPostgreSQL `GameStore`を通じて永続化し（`dotnet/src/Eve.GameStore/PersistenceStore.Tables.cs:9-21,29-63`）、`ClientSession`からsession notificationを送る（`dotnet/src/Eve.Services/ClientSession.cs:313-323`）。変更された各分岐の結果同等性はこの静的調査で確認していない。**内部同等性は未確認。** | V24クライアントソースからEveJS SQLite/game-store内部、identity割当、transaction順序、retry挙動は見えない。上記のgameplay callsiteは各公開契約を示すが、これら内部実装の同等性は示さない。**内部同等性を裏付けるクライアント根拠なし。** |
| `PD` | Project Discoveryのcorpus、sample、reward、runtime、service。 | 本ジョブの対象外。同等性の判定なし。 | 本ジョブの対象外。クライアント確認の判定なし。 |

## リリース見出しとの対応

台帳の見出し対象は次のとおり。combat: armed empire sentry、CONCORD timer/penalty、fighter、cloak/D-scan、doomsday warp停止、warp中のdrone/fighter launch禁止。exploration: K-space wandering wormhole、Thera static、Sleeper site NPC修正。industry: moon extraction各phase効果。market: Market Seed v4、価格/時刻順matching、private-contract broker fee。character/UI: jump clone implant/cooldown、MCT queue、Paragon SKINR import、Navy Quartermaster SKIN、ScoopToSMA。GM command/UI: `/sov`、`/war start`、`/extraction advance`、native GM menu操作。複合ファイルは `H / N-*` と記し、見出し外の部分は上表で監査した。

提示された見出しは対象範囲を示すもので、gameplayの正しさを証明するものではない。ルールとクライアント挙動は引き続き対象V24.01/SDEの根拠が必要。

## 確認済みの範囲と未確認事項

- **静的に確認済み:** source path/status一覧、変更箇所の行番号、package version、client checkout ID、引用したeve.rs/V24 callsite、content packのprovenance。
- **実行していない:** EveJS runtime flow、build、test、DB-backed test、グラフィカルクライアント接続、performance比較。本件はドキュメントのみの変更。
- **同等性を確定していない:** corporation、structure、industry、market、economy、character、combat、otherに分類した内部処理すべて。該当行は未確認と記した。全件のpath/line一覧は、全分岐をruntimeで検証したという主張ではない。

## 変更パスの全件台帳

`M` は変更、`A` は追加、`D` は削除。変更行には両版の変更hunk行を示し、追加/削除にはファイル全体の行範囲を示す。`バイトのみ` は論理行の内容が同一であることを示す。

| 状態 | 分類 | 監査キー | パスと変更行 |
| --- | --- | --- | --- |
| M | other | N-OTHER | `server/bootstrap.js` — 261028.1:316; 261108.1:316 |
| M | other | N-OTHER | `server/index.js` — 261028.1:61, 63–64; 261108.1:61–64, 66 |
| M | data | N-DATA | `server/node_modules/.package-lock.json` — 261028.1:3; 261108.1:3 |
| M | data | N-DATA | `server/package-lock.json` — 261028.1:3, 9; 261108.1:3, 9 |
| M | data | N-DATA | `server/package.json` — 261028.1:3; 261108.1:3 |
| M | UI | N-CHARACTER / N-OTHER | `server/src/_secondary/chat/chatRuntime.js` — 261028.1:770, 778, 869, 872–885, 1014, 1016, 1020, 2023; 261108.1:770–783, 791, 882, 885, 1013, 1014–1020, 1025–1030, 2034–2035 |
| M | UI | N-CHARACTER / N-OTHER | `server/src/_secondary/chat/chatStore.js` — 261028.1:378, 387; 261108.1:378, 388–389 |
| M | character | N-EVENTS | `server/src/_secondary/express/gatewayServices/careerGoalGatewayService.js` — 261028.1:14, 23–24, 29, 35, 48–59, 86; 261108.1:14–18, 27–28, 33, 39–65, 79–80, 107–110 |
| M | other | N-CORPORATION | `server/src/_secondary/express/gatewayServices/corpGoalsGatewayService.js` — 261028.1:243, 283; 261108.1:243, 283 |
| M | other | N-CORPORATION | `server/src/_secondary/express/gatewayServices/corpGoalsProto.js` — 261028.1:44–45, 239–246; 261108.1:44–45, 239–245 |
| M | character | N-EVENTS | `server/src/_secondary/express/gatewayServices/dailyGoalsGatewayService.js` — 261028.1:91, 185, 189, 193; 261108.1:91, 185, 190, 194–213 |
| M | other | N-CORPORATION | `server/src/_secondary/express/gatewayServices/insurgencyGatewayService.js` — 261028.1:49, 51, 57; 261108.1:49–58, 61, 67 |
| M | other | N-CORPORATION | `server/src/_secondary/express/gatewayServices/mercenaryDenGatewayService.js` — 261028.1:386; 261108.1:386 |
| M | market | H-MARKET / N-MARKET | `server/src/_secondary/express/gatewayServices/shipSkinMarketGatewayService.js` — 261028.1:118–119; 261108.1:118–121 |
| M | other | N-CORPORATION | `server/src/_secondary/express/gatewayServices/sovereigntyGatewayService.js` — 261028.1:114, 687, 1003; 261108.1:114–143, 717, 1034–1041 |
| M | other | N-OTHER | `server/src/_secondary/express/server.js` — 261028.1:1025–1026; 261108.1:1025–1026 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/abyssal/runtime/abyssalManagerRuntimeGroup12.js` — 261028.1:143; 261108.1:143–144 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/abyssal/runtime/abyssalNpcWatchdog.js` — 261028.1:39, 41, 47, 52, 63, 67, 72, 287, 293, 391, 432, 446, 458, 526, 528, 677; 261108.1:39–48, 51, 57, 62–63, 74, 79, 84, 300, 306, 404, 445, 459, 471, 539, 541–542, 692 |
| A | combat | N-COMBAT | `server/src/common/dogmaAttributeBounds.js` — 261108.1:1–48 (追加) |
| M | other | N-OTHER | `server/src/common/random.js` — 261028.1:102; 261108.1:102 |
| M | data | N-EVENTS | `server/src/config/eventSchedules.js` — 261028.1:5, 9, 27, 37; 261108.1:5–6, 11, 30–35, 45 |
| M | data | N-DATA | `server/src/config/manager.js` — 261028.1:6, 459, 476, 501, 503; 261108.1:6, 459, 476, 501, 503 |
| M | data | N-MISSION | `server/src/config/productionMissionPolicy.js` — 261028.1:288, 289; 261108.1:288–289, 291–297 |
| M | data | N-DATA | `server/src/config/schema/gameplay.js` — 261028.1:182; 261108.1:182–199 |
| M | data | H-INDUSTRY | `server/src/config/schema/mining.js` — 261028.1:765, 771; 261108.1:765, 771 |
| M | data | H-COMBAT / N-COMBAT | `server/src/config/schema/npc.js` — 261028.1:24, 82; 261108.1:24, 82 |
| M | data | N-DATA | `server/src/config/schema/server.js` — 261028.1:38; 261108.1:38–51 |
| M | data | N-DATA | `server/src/config/schema/world.js` — 261028.1:4; 261108.1:4–56 |
| A | data | N-MISSION | `server/src/config/securityMissionChains.js` — 261108.1:1–17 (追加) |
| A | data | N-MISSION | `server/src/config/securityMissionChains.json` — 261108.1:1–6979 (追加) |
| A | data | N-MISSION | `server/src/config/securityMissionCorporationStanding.json` — 261108.1:1–217 (追加) |
| A | data | N-MISSION | `server/src/config/securityMissionDeclineBaseline.js` — 261108.1:1–36 (追加) |
| A | data | N-MISSION | `server/src/config/securityMissionDeclineBaseline.json` — 261108.1:1–24945 (追加) |
| A | data | N-MISSION | `server/src/config/securityMissionDeclineMeasured.json` — 261108.1:1–517 (追加) |
| A | data | N-MISSION | `server/src/config/securityMissionDestinationMeasured.json` — 261108.1:1–115 (追加) |
| A | data | N-MISSION | `server/src/config/securityMissionDestinations.js` — 261108.1:1–85 (追加) |
| A | data | N-MISSION | `server/src/config/securityMissionDestinations.json` — 261108.1:1–20794 (追加) |
| A | data | N-MISSION | `server/src/config/securityMissionOfferEvidence.js` — 261108.1:1–33 (追加) |
| A | data | N-MISSION | `server/src/config/securityMissionOfferExceptions.json` — 261108.1:1–39 (追加) |
| A | data | N-MISSION | `server/src/config/securityMissionOfferObservations.json` — 261108.1:1–64930 (追加) |
| A | data | N-MISSION | `server/src/config/securityMissionOfferPolicy.json` — 261108.1:1–18 (追加) |
| A | data | N-MISSION | `server/src/config/securityMissionOfferProjection.js` — 261108.1:1–54 (追加) |
| A | data | N-MISSION | `server/src/config/securityMissionOfferProjection.json` — 261108.1:1–4046 (追加) |
| A | data | N-MISSION | `server/src/config/securityMissionRewardBaseline.js` — 261108.1:1–24 (追加) |
| A | data | N-MISSION | `server/src/config/securityMissionRewardBaseline.json` — 261108.1:1–3511 (追加) |
| A | data | N-MISSION | `server/src/config/securityMissionStandingBaseline.js` — 261108.1:1–36 (追加) |
| A | data | N-MISSION | `server/src/config/securityMissionStandingBaseline.json` — 261108.1:1–328 (追加) |
| A | data | N-MISSION | `server/src/config/securityMissionStandingMeasured.json` — 261108.1:1–114 (追加) |
| M | data | N-OTHER | `server/src/contentPacks/autoInstallContentPacks.js` — 261028.1:42, 161, 187–193, 197–213; 261108.1:42, 160–172, 198–200, 204 |
| A | data | N-MISSION | `server/src/contentPacks/careerMissionContent.js` — 261108.1:1–57 (追加) |
| M | data | N-OTHER | `server/src/contentPacks/contentPackManager.js` — 261028.1:14, 29, 50, 75, 88, 236, 266, 276, 418, 818, 866, 1160–1162, 1194, 1252, 1255, 1261, 1268–1270, 1273, 1283, 1290, 1302; 261108.1:14–15, 31–45, 67–69, 95–97, 111–112, 260–271, 302, 313–322, 465, 865–867, 916–982, 1277–1281, 1313, 1371–1415, 1419, 1426–1428, 1435–1436, 1439–1442, 1453, 1460, 1472–1477 |
| A | data | N-MISSION | `server/src/contentPacks/securityMissionBinding.js` — 261108.1:1–241 (追加) |
| M | UI | N-CHARACTER / N-OTHER | `server/src/edge/chat/chatEdgeRuntime.js` — 261028.1:1 (バイトのみ; テキスト差分なし); 261108.1:1 (バイトのみ; テキスト差分なし) **バイトのみ** |
| A | data | N-DATA | `server/src/gameStore/data/agent-spawn-report.json` — 261108.1:1–658 (追加) |
| M | other | N-OTHER | `server/src/gameStore/index.js` — 261028.1:286, 300, 744, 1233, 1711, 1713, 1716–1726, 1734, 1739, 1747, 1799, 2180–2190, 2194, 2197, 2211, 2234, 2238, 2245, 2339; 261108.1:286–287, 302, 747, 1237–1245, 1724, 1726–1744, 1748, 1756–1777, 1783–1786, 1795, 1848–1850, 2232–2235, 2239, 2242, 2256–2286, 2310–2312, 2316, 2323, 2417–2422 |
| M | other | N-OTHER | `server/src/gameStore/sqliteStore.js` — 261028.1:72, 92, 106, 132, 146, 149, 856, 1486, 1503, 1526, 1641; 261108.1:72–73, 93–94, 109, 135, 149–153, 157–228, 936–960, 1591, 1608–1613, 1636–1768, 1884–1890 |
| M | other | N-OTHER | `server/src/gameStore/tableOwnership.js` — 261028.1:50, 163; 261108.1:50–55, 169–180 |
| M | other | N-OTHER | `server/src/gameStore/tableRepository.js` — 261028.1:134; 261108.1:134–137 |
| M | other | N-OTHER | `server/src/network/clientSession.js` — 261028.1:14, 671, 673–694; 261108.1:14, 672, 675–683 |
| A | other | N-OTHER | `server/src/network/gameFrame.js` — 261108.1:1–31 (追加) |
| M | other | N-OTHER | `server/src/network/tcp/directGameSocketTransport.js` — 261028.1:3; 261108.1:3–5 |
| M | other | N-OTHER | `server/src/network/tcp/handshake.js` — 261028.1:23, 735, 833, 865, 887–889, 891; 261108.1:23, 736–738, 836–839, 872–874, 897, 899 |
| M | other | N-OTHER | `server/src/network/tcp/index.js` — 261028.1:227; 261108.1:227–232 |
| M | other | N-OTHER | `server/src/network/tcp/utils/decode/index.js` — 261028.1:2, 16; 261108.1:2–3, 18 |
| M | other | N-OTHER | `server/src/network/tcp/utils/marshal.js` — 261028.1:2, 2003–2005; 261108.1:2–3, 2005 |
| M | market | H-MARKET / N-MARKET | `server/src/services/_other/contractProxyService.js` — 261028.1:962, 964; 261108.1:962, 964–966 |
| M | other | N-OTHER | `server/src/services/_shared/identityAllocator.js` — 261028.1:599, 639; 261108.1:599–620, 661 |
| M | other | N-OTHER | `server/src/services/_shared/referenceData.js` — 261028.1:2, 162; 261108.1:2–3, 164–167 |
| M | other | N-ECONOMY | `server/src/services/account/accountRoleProfiles.js` — 261028.1:245; 261108.1:245 |
| M | other | N-ECONOMY | `server/src/services/account/accountService.js` — 261028.1:83–148, 769–770; 261108.1:83, 704–705 |
| A | other | N-ECONOMY | `server/src/services/account/accountingEntryTypes.json` — 261108.1:1–220 (追加) |
| M | other | N-ECONOMY | `server/src/services/account/billRuntimeState.js` — 261028.1:84, 88, 341, 395–396, 401, 409–410, 490, 498; 261108.1:84, 88–89, 342, 397–402, 407, 415–416, 496–498, 507–510 |
| M | other | N-ECONOMY | `server/src/services/account/walletAuthorityOwnerState.js` — 261028.1:45, 48; 261108.1:45, 49 |
| M | other | N-ECONOMY | `server/src/services/account/walletAuthorityProcess.worker.js` — 261028.1:189, 209–212, 231–234; 261108.1:189–207, 228, 247 |
| M | other | N-ECONOMY | `server/src/services/account/walletAuthorityProjection.js` — 261028.1:130, 149; 261108.1:130–139, 159 |
| M | other | N-ECONOMY | `server/src/services/account/walletAuthorityReceiptState.js` — 261028.1:29, 32, 208; 261108.1:29, 33, 210–216 |
| M | other | N-ECONOMY | `server/src/services/account/walletState.js` — 261028.1:48, 60, 64, 163, 175, 749, 764, 769, 813; 261108.1:48, 61–62, 67–68, 168–169, 182–189, 764–773, 788–790, 796–798, 843 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/achievement/achievementState.js` — 261028.1:66–93, 187–188, 196–197, 201–202; 261108.1:66–124, 218–219, 227–229, 233 |
| M | exploration | N-EXPLORATION | `server/src/services/activity/filamentAdmission.js` — 261028.1:46; 261108.1:46–47 |
| A | exploration | N-VOID | `server/src/services/activity/voidReplayAdmission.js` — 261108.1:1–103 (追加) |
| A | exploration | N-VOID | `server/src/services/activity/voidReplayRoom.js` — 261108.1:1–123 (追加) |
| A | exploration | N-VOID | `server/src/services/activity/voidReplayRuntime.js` — 261108.1:1–394 (追加) |
| A | exploration | N-VOID | `server/src/services/activity/voidReplayWeather.js` — 261108.1:1–39 (追加) |
| M | exploration | N-VOID | `server/src/services/activity/voidSpaceMgrService.js` — 261028.1:12–15, 75, 83, 91; 261108.1:12–13, 73–78, 87–91, 100–102 |
| M | GM command | N-GM | `server/src/services/admin/slashService.js` — 261028.1:243; 261108.1:243–244 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/agent/agentAuthority.js` — 261028.1:146; 261108.1:146–148 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/agent/agentMgrService.js` — 261028.1:35, 112, 451, 628, 633, 903, 957, 985, 1000, 1074, 1101, 1117–1119, 1239, 1240, 1254, 1272, 1277, 1280, 1285, 1293, 1297, 1300; 261108.1:35–43, 121–127, 467–641, 819–822, 827–830, 1101–1111, 1166, 1194–1216, 1232, 1307, 1334, 1351, 1471–1476, 1478–1529, 1544–1553, 1572, 1577, 1579, 1584, 1591, 1595, 1597 |
| M | character | N-MISSION | `server/src/services/agent/agentMissionRuntime.js` — 261028.1:4, 28, 75, 105, 159, 192, 439, 483, 491, 675, 687, 694, 702, 860–871, 873, 931–932, 955, 1054, 1057, 1059, 1063, 1064, 1134, 1146, 1152–1162, 1164, 1167, 1171–1172, 1204–1207, 1210–1211, 1229, 1237, 1255–1262, 1266, 1274, 1305, 1312, 1319–1323, 1326–1328, 1330, 1332–1338, 1340, 1342, 1391–1392, 2115, 2366, 2507, 2510, 2873–2894, 3153, 3261, 3290, 3371, 3386, 3426, 3739–3741, 3804, 3856, 3901–3904, 3978, 3984, 3986–3987, 3995, 4000, 4004, 4145, 4149, 4154, 4179, 4182, 4238, 4248, 4250, 4258, 4467, 4469, 4480, 4577, 4623–4625, 4953, 5085, 5087, 5142, 5152, 5159, 5652, 5966, 6071–6081, 6112, 6218, 6237, 6264, 6290, 6310, 6312, 6313, 6334, 6342, 6363, 6377, 6379, 6380, 6395, 6396, 6397–6401, 6405–6408, 6414–6417, 6419, 6425, 6435, 6442, 6496, 6503, 6511, 6535, 6769, 6802, 6814, 6941, 6979–6981, 6986, 6990, 7027, 7095–7096, 7102, 7117, 7147, 7166, 7168, 7242, 7254, 7256, 7455–7457, 7518, 7629, 7637, 7648, 7701, 7708, 7714, 7940, 7954, 7966–7979, 7988–7990, 8026–8042, 8045–8051, 8086–8092, 8114, 8170, 8206, 8211, 8224–8228, 8232, 8283–8298, 8336–8358, 8443, 8447, 8455, 8481; 261108.1:4–10, 35, 83, 112–116, 171, 203–204, 452–460, 505–513, 522–529, 714–718, 731–734, 741, 749, 907–917, 919, 977–978, 1001, 1100, 1104, 1106–1110, 1115, 1117, 1187–1214, 1227–1246, 1253–1271, 1273, 1276, 1280–1281, 1313–1318, 1321–1329, 1347, 1355, 1373–1374, 1378, 1386–1439, 1471–1512, 1520–1524, 1532, 1535, 1537–1538, 1541–1543, 1545, 1547, 1597–1598, 2321–2327, 2578–2600, 2742–2745, 2749–2758, 3121, 3379, 3488, 3517–3532, 3614–3616, 3632, 3672, 3986, 4049–4058, 4111–4119, 4165–4173, 4247–4258, 4264, 4266–4270, 4278–4377, 4383–4386, 4391, 4532, 4537, 4542, 4568–4574, 4578–4580, 4637–4638, 4648, 4650, 4658–4660, 4870, 4873–4966, 4978–4985, 5083–5086, 5133–5135, 5463–5464, 5596–5604, 5607–5608, 5663, 5673–5674, 5681–5684, 6178, 6492, 6597–6601, 6632, 6738–6768, 6788–6798, 6826–6834, 6861–6884, 6905–6916, 6919, 6921–6923, 6945–6961, 6970, 6991, 7006–7009, 7012, 7014–7016, 7032–7054, 7056, 7058–7075, 7079, 7084–7087, 7089–7091, 7098–7121, 7132–7158, 7165, 7220–7224, 7232, 7240, 7265–7267, 7502, 7536–7538, 7551, 7679–7682, 7721–7722, 7727, 7732–7827, 7865–7868, 7936–7937, 7943, 7958–7959, 7990–7992, 8011–8014, 8017, 8092–8097, 8110–8111, 8113, 8313–8317, 8378–8380, 8492–8496, 8504–8510, 8522–8525, 8578–8584, 8592–8599, 8606–8610, 8836–9139, 9154–9159, 9172–9183, 9192, 9228–9264, 9267, 9301, 9322, 9379–9380, 9417, 9422–9431, 9444, 9447–9450, 9502, 9539–9551, 9636–9638, 9643, 9652, 9679–9692 |
| A | character | N-MISSION | `server/src/services/agent/careerAgentChains.json` — 261108.1:1–416 (追加) |
| A | character | N-MISSION | `server/src/services/agent/careerAgentMissions.js` — 261108.1:1–237 (追加) |
| A | character | N-MISSION | `server/src/services/agent/careerRetailMissionData.js` — 261108.1:1–58 (追加) |
| A | character | N-MISSION | `server/src/services/agent/careerRetailMissionData.json` — 261108.1:1–43463 (追加) |
| M | character | N-MISSION | `server/src/services/agent/epicArcStatusService.js` — 261028.1:10, 223; 261108.1:10–11, 225–232 |
| A | character | N-MISSION | `server/src/services/agent/fracturedLegacy.data.json` — 261108.1:1–60651 (追加) |
| A | character | N-MISSION | `server/src/services/agent/fracturedLegacy.js` — 261108.1:1–70 (追加) |
| M | character | N-MISSION | `server/src/services/agent/missionAuthority.js` — 261028.1:14, 18, 19, 30, 31, 208, 278, 430, 431, 512, 515, 565, 571, 587–588, 593, 595, 739, 758, 766, 772, 786–790, 795, 800, 899, 903, 924, 927, 932–936, 939, 973, 997; 261108.1:14, 19, 21, 32–38, 40, 218, 289–292, 445–449, 451, 532, 536–539, 590–591, 598, 615–618, 623–646, 649–650, 795–801, 821–882, 891–893, 900, 914–1009, 1014, 1019–1036, 1135–1136, 1140–1146, 1167, 1170, 1175–1179, 1182–1194, 1229–1230, 1255 |
| M | character | N-MISSION | `server/src/services/agent/missionRewardSettlementRecovery.js` — 261028.1:138, 173, 174–178, 180–188, 190, 192–193, 200, 205, 207, 209–210, 218, 223–225, 228–229, 231–233, 237, 242, 248, 251–252, 255, 257, 262, 267, 297, 340–341, 451, 457; 261108.1:138–144, 180–184, 186–190, 192–205, 207, 209–210, 217, 222–227, 229, 231–232, 240, 245–258, 261–262, 264–270, 274, 279, 285, 288–290, 293, 295, 300, 305–337, 368, 411–415, 525–527, 533–538 |
| M | character | N-MISSION | `server/src/services/agent/missionRewardSettlementState.js` — 261028.1:21, 22, 34, 57, 72, 102–106, 111, 113, 114, 136, 142, 156, 162, 173, 241, 247, 263, 290, 389, 398–400, 409, 417, 421–423, 449, 455, 462, 472–473, 478, 487, 493, 512, 520, 538, 539, 541, 554–555, 557–567, 570–574, 587, 589, 604, 608, 610–612, 614–615, 617–619, 621–624, 626–627, 749, 1012, 1152, 1171, 1172–1334, 1618, 1627; 261108.1:21, 23–26, 39–42, 66–101, 117–123, 154–167, 172–179, 182–184, 186, 208, 215–216, 231, 238–239, 251, 319–322, 329–331, 348–350, 378–379, 479, 488–490, 499, 507, 512–514, 540, 547–788, 796–797, 808, 813, 821–822, 829–840, 860, 869, 888–889, 891, 893, 906–939, 941–947, 950, 962, 965, 981–984, 989–990, 992, 994, 996, 998–999, 1001, 1123–1125, 1389–1667, 1808–1809, 1829–1836, 1838–1841, 2125, 2135 |
| M | character | N-MISSION | `server/src/services/agent/missionRuntimeState.js` — 261028.1:21, 234, 262, 296, 333, 336, 1179, 1404–1405; 261108.1:21–22, 236–245, 274–276, 311–318, 356, 360, 1204–1221, 1447–1450 |
| M | character | N-MISSION | `server/src/services/agent/missionTrackerMgrService.js` — 261028.1:66–75, 77; 261108.1:66–81, 83 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/agent/researchRuntime.js` — 261028.1:384, 435, 607, 757, 930; 261108.1:384, 436–445, 618–691, 842–850, 1024–1025 |
| A | character | N-CHARACTER / N-OTHER | `server/src/services/agent/visionsCrates.js` — 261108.1:1–133 (追加) |
| A | character | N-MISSION | `server/src/services/agent/visionsOfGreatness.data.json` — 261108.1:1–3115 (追加) |
| A | character | N-MISSION | `server/src/services/agent/visionsOfGreatness.js` — 261108.1:1–102 (追加) |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/bookmark/bookmarkNotifications.js` — 261028.1:7, 8, 25, 221, 227, 235; 261108.1:7, 9, 27, 224–323, 330, 339 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/bookmark/bookmarkRuntimeState.js` — 261028.1:46, 758, 791, 815, 832, 841, 1123; 261108.1:46–90, 803, 836, 860, 877, 886–904, 1187–1197 |
| M | other | N-ECONOMY | `server/src/services/bounty/bountyProxyService.js` — 261028.1:30, 553; 261108.1:30, 554–557 |
| M | other | N-ECONOMY | `server/src/services/bounty/bountyRuntime.js` — 261028.1:6, 43, 60, 65, 67, 89, 94, 492, 504, 646, 660, 667, 668, 670–673, 679–685, 697, 710, 714–721, 725, 728, 750, 754, 763, 769, 791–798, 803, 826–833, 835–840, 842–850, 876, 880, 955, 1014–1016, 1024, 1042, 1044–1046, 1064, 1075, 1088, 1094–1096, 1178, 1185, 1189, 1204, 1292; 261108.1:6, 44, 60, 66–68, 71–75, 98–104, 110–117, 516–517, 529, 672, 687–690, 698–701, 703–706, 708–727, 733–823, 835, 849–854, 858–966, 970–971, 974–978, 1000–1002, 1007, 1016–1020, 1026–1027, 1050, 1054, 1077–1131, 1133, 1134–1148, 1174, 1178–1183, 1259, 1318–1322, 1330–1338, 1357–1359, 1361–1377, 1395, 1405–1408, 1422, 1427, 1508–1509, 1517, 1522, 1538–1540, 1629 |
| M | character | N-ECONOMY | `server/src/services/bounty/killRightNotifications.js` — 261028.1:131, 133; 261108.1:131–158, 161 |
| M | character | N-EVENTS | `server/src/services/campaign/crateService.js` — 261028.1:54, 433, 568, 573–576, 730, 739, 740, 775, 780, 793, 821, 822, 827, 828, 873; 261108.1:54–55, 435–444, 580, 585, 738–741, 751–765, 767–771, 806, 812–817, 830–831, 860–862, 864–869, 875–887, 889–900, 946–949 |
| M | character | N-EVENTS | `server/src/services/campaign/loginCampaignCatalog.js` — 261028.1:8, 41, 62–63; 261108.1:8, 42–51, 73–76 |
| M | character | N-EVENTS | `server/src/services/campaign/loginCampaignRedemption.js` — 261028.1:9, 18, 33, 35, 47, 101, 109, 115, 118, 119–120, 124, 125; 261108.1:9, 19–21, 37–41, 44–73, 86–88, 142, 150, 156, 159–161, 163–164, 168–177, 179 |
| M | character | N-EVENTS | `server/src/services/campaign/loginCampaignRuntime.js` — 261028.1:6, 68, 100, 103–105, 120; 261108.1:6, 69–73, 106–115, 119–123, 138 |
| M | character | N-EVENTS | `server/src/services/campaign/loginCampaignService.js` — 261028.1:84–85, 89–91; 261108.1:84–86, 90–95 |
| A | character | N-EVENTS | `server/src/services/campaign/warpathLoginRewards.js` — 261108.1:1–40 (追加) |
| A | character | N-EVENTS | `server/src/services/career/airCareerCrates.js` — 261108.1:1–199 (追加) |
| M | character | N-EVENTS | `server/src/services/career/careerAbyssalSources.js` — 261028.1:10–11, 13, 106, 110; 261108.1:10–11, 13, 106–110, 114 |
| M | data | N-EVENTS | `server/src/services/career/careerCatalog.data.json` — 261028.1:3–5, 9, 14, 17, 21, 30–36, 41, 46, 49, 53, 62–68, 73, 78, 81, 85, 94–100, 105, 110, 113, 117, 126–132, 137, 142, 145, 149, 156–162, 167, 172, 175, 179, 186–192, 197, 202, 205, 209, 216–222, 227, 232, 235, 239, 246–252, 257, 262, 265, 269, 276–282, 287, 292, 295, 308–315, 319, 324, 327, 340–347, 351, 356, 359, 375–379, 383, 391, 404–411, 415, 420, 423, 436–443, 447, 452, 455, 468–474, 479, 487, 500–506, 511, 516, 519, 532–538, 543, 548, 551, 555, 563–570, 574, 579, 582, 586, 594–601, 605, 610, 613, 617, 625–632, 636, 641, 644, 648, 656–663, 667, 675, 679, 687–694, 698, 703, 706, 719–726, 730, 735, 738, 751–758, 762, 767, 770, 783–790, 794, 802, 815–822, 826, 831, 834, 846–852, 857, 865, 877–883, 888, 893, 896, 908–914, 919, 924, 927, 931, 938–944, 949, 954, 957, 961, 969–974, 979, 984, 987, 991, 998–1005, 1009, 1014, 1017, 1021, 1028–1035, 1039, 1044, 1047, 1051, 1058–1065, 1069, 1074, 1077, 1090–1097, 1101, 1106, 1109, 1122–1129, 1133, 1138, 1141, 1154–1161, 1165, 1170, 1173, 1186–1193, 1197, 1202, 1205, 1209, 1218–1224, 1229, 1237, 1241, 1250–1256, 1261, 1269, 1273, 1282–1288, 1293, 1298, 1301, 1305, 1314–1320, 1325, 1330, 1333, 1346–1352, 1357, 1362, 1365, 1378–1384, 1389, 1394, 1397, 1410–1416, 1421, 1426, 1429, 1442–1448, 1453, 1458, 1461, 1474–1480, 1485, 1490, 1493, 1506–1512, 1517, 1522, 1525, 1539–1544, 1549, 1554, 1557, 1570–1576, 1581, 1599, 1604–1606, 1610, 1628, 1633–1641, 1645, 1663, 1668–1670, 1674, 1692, 1697–1699, 1703, 1721, 1726–1728, 1732, 1750, 1755–1757, 1761, 1779, 1784–1786, 1790, 1808, 1813–1814, 1819, 1824, 1827, 1831, 1839–1847, 1851, 1856, 1859, 1863, 1871–1879, 1888, 1891, 1895, 1903–1908, 1915, 1920, 1923, 1927, 1935–1943, 1947, 1952, 1955, 1959, 1967–1975, 1979, 1984, 1987, 1991, 2000–2007, 2011, 2016, 2019, 2023, 2031–2039, 2043, 2048, 2051, 2055, 2063–2071, 2075, 2080, 2083, 2087, 2095–2103, 2107, 2112, 2115, 2119, 2128–2135, 2139, 2144, 2147, 2151, 2159–2167, 2171, 2176, 2179, 2183, 2191–2199, 2203, 2208, 2211, 2215, 2223–2231, 2235, 2240, 2243, 2247, 2256–2263, 2267, 2272, 2275, 2279, 2287–2295, 2299, 2304, 2307, 2319–2327, 2331, 2336, 2339, 2352–2359, 2363, 2368, 2371, 2383–2391, 2395, 2400, 2403, 2415–2423, 2427, 2432, 2435, 2447–2455, 2459, 2464, 2467, 2479–2487, 2491, 2496, 2499, 2503, 2511–2519, 2523, 2528, 2531, 2535, 2543–2551, 2555, 2560, 2563, 2567, 2575–2583, 2587, 2592, 2595, 2599, 2607–2615, 2619, 2627, 2631, 2639–2647, 2651, 2656, 2659, 2671–2679, 2683, 2688, 2691, 2695, 2703–2711, 2715, 2720, 2723, 2727, 2735–2743, 2747, 2752, 2755, 2759, 2770–2775, 2779, 2787, 2791, 2799–2807, 2811, 2816, 2819, 2823, 2831–2839, 2843, 2848, 2851, 2855, 2863–2871, 2875, 2880, 2883, 2887, 2895–2903, 2907, 2912, 2915, 2919, 2930–2935, 2939, 2947, 2951, 2959–2967, 2971, 2976, 2979, 2983, 2991–2999, 3003, 3008, 3011, 3015, 3023–3031, 3035, 3040, 3043, 3047, 3055–3063, 3067, 3072, 3075, 3079, 3090–3095, 3099, 3107, 3111, 3119–3127, 3131, 3136, 3139, 3143, 3151–3159, 3163, 3168, 3171, 3175, 3183–3191, 3195, 3200, 3203, 3207, 3218–3223, 3227, 3232, 3235, 3239, 3247–3255, 3264, 3267, 3271, 3279–3284, 3291, 3296, 3299, 3303, 3311–3319, 3323, 3328, 3331, 3335, 3343–3351, 3355, 3360, 3363, 3367, 3375–3383, 3387, 3392, 3395, 3399, 3408–3415, 3419, 3424, 3427, 3431, 3439–3447, 3451, 3456, 3459, 3463, 3471–3479, 3483, 3491, 3495, 3503–3511, 3515, 3520, 3523, 3527, 3535–3543, 3547, 3552, 3555, 3559, 3567–3575, 3579, 3584, 3587, 3591, 3599–3607, 3611, 3616, 3619, 3623, 3634–3639, 3643, 3651, 3655, 3663–3671, 3675, 3683, 3695–3703, 3707, 3712, 3715, 3727–3735, 3739, 3747, 3751, 3759–3767, 3771, 3776, 3779, 3783, 3791–3799, 3803, 3808, 3811, 3822–3830, 3834, 3839, 3842, 3854–3862, 3866, 3871, 3874, 3886–3894, 3898, 3903, 3906, 3910, 3918–3926, 3930, 3935, 3938, 3942, 3953–3958, 3962, 3970, 3974, 3982–3990, 3994, 3999, 4002, 4006, 4014–4022, 4026, 4031, 4034, 4038, 4050–4055, 4059, 4064, 4067, 4071, 4080–4088, 4092, 4097, 4100, 4104, 4113–4121, 4125, 4130, 4133, 4137, 4148–4153, 4157, 4162, 4165, 4169, 4177–4185, 4189, 4194, 4197, 4201, 4209–4217, 4221, 4226, 4229, 4233, 4241–4249, 4258, 4261, 4265, 4272–4277, 4292, 4296, 4305–4310, 4317, 4322, 4325, 4340–4345, 4349, 4354, 4357, 4372–4377, 4381, 4386, 4389, 4404–4409, 4413, 4418, 4421, 4436–4441, 4445, 4450, 4453, 4468–4473, 4477, 4482, 4485, 4497–4505, 4509, 4514, 4517, 4530–4537, 4541, 4546, 4549, 4561–4569, 4573, 4578, 4581, 4593–4601, 4605, 4610, 4613, 4628–4633, 4637, 4642, 4645, 4657–4665, 4669, 4674, 4677, 4689–4697, 4701, 4706, 4709, 4721–4729, 4733, 4741, 4753–4761, 4765, 4770, 4773, 4785–4793, 4797, 4802, 4805, 4817–4825, 4829, 4837, 4849, 4850–4852, 4857, 4860; 261108.1:3–5, 9, 14, 17, 21–22, 32–37, 42, 47, 50, 54–55, 65–70, 75, 80, 83, 87–88, 98–135, 140, 145, 148, 152–153, 163–200, 205, 210, 213, 217–218, 226–231, 236, 241, 244, 248–249, 257–262, 267, 272, 275, 279–280, 288–299, 304, 309, 312, 316–317, 325–330, 335, 340, 343, 347–348, 356–367, 372, 377, 380, 393–399, 403, 408, 411, 424–430, 434, 439, 442, 458–467, 471, 479, 492–498, 502, 507, 510, 523–535, 539, 544, 547, 560–565, 570, 578, 591–596, 601, 606, 609, 622–633, 638, 643, 646, 650–651, 660–698, 702, 707, 710, 714–715, 724–730, 734, 739, 742, 746–747, 756–768, 772, 777, 780, 784–785, 794–800, 804, 812, 816–817, 826–838, 842, 847, 850, 863–869, 873, 878, 881, 894–906, 910, 915, 918, 931–937, 941, 949, 962–974, 978, 983, 986, 998–1003, 1008, 1016, 1028–1039, 1044, 1049, 1052, 1064–1075, 1080, 1085, 1088, 1092–1093, 1101–1112, 1117, 1122, 1125, 1129–1130, 1139–1143, 1148, 1153, 1156, 1160–1161, 1169–1181, 1185, 1190, 1193, 1197–1198, 1206–1212, 1216, 1221, 1224, 1228–1229, 1237–1275, 1279, 1284, 1287, 1300–1306, 1310, 1315, 1318, 1331–1337, 1341, 1346, 1349, 1362–1368, 1372, 1377, 1380, 1393–1431, 1435, 1440, 1443, 1447–1448, 1458–1469, 1474, 1482, 1486–1487, 1497–1502, 1507, 1515, 1519–1520, 1530–1541, 1546, 1551, 1554, 1558–1559, 1569–1606, 1611, 1616, 1619, 1632–1637, 1642, 1647, 1650, 1663–1668, 1673, 1678, 1681, 1694–1731, 1736, 1741, 1744, 1757–1762, 1767, 1772, 1775, 1788–1825, 1830, 1835, 1838, 1851–1862, 1867, 1872, 1875, 1889–1893, 1898, 1903, 1906, 1919–1930, 1935, 1953–1983, 1988–2000, 2004, 2022–2046, 2051–2063, 2067, 2085–2109, 2114–2126, 2130, 2148–2172, 2177–2197, 2201, 2219, 2224–2238, 2242, 2260, 2265–2280, 2284, 2302, 2307–2321, 2325, 2343, 2348–2353, 2358, 2363, 2366, 2370–2371, 2380–2393, 2397, 2402, 2405, 2409–2410, 2419–2432, 2441, 2444, 2448–2449, 2458–2464, 2471, 2476, 2479, 2483–2484, 2493–2500, 2504, 2509, 2512, 2516–2517, 2526–2539, 2543, 2548, 2551, 2555–2556, 2566–2572, 2576, 2581, 2584, 2588–2589, 2598–2611, 2615, 2620, 2623, 2627–2628, 2637–2644, 2648, 2653, 2656, 2660–2661, 2670–2677, 2681, 2686, 2689, 2693–2694, 2704–2710, 2714, 2719, 2722, 2726–2727, 2736–2749, 2753, 2758, 2761, 2765–2766, 2775–2782, 2786, 2791, 2794, 2798–2799, 2808–2821, 2825, 2830, 2833, 2837–2838, 2848–2854, 2858, 2863, 2866, 2870–2871, 2880–2893, 2897, 2902, 2905, 2917–2924, 2928, 2933, 2936, 2949–2955, 2959, 2964, 2967, 2979–2992, 2996, 3001, 3004, 3016–3023, 3027, 3032, 3035, 3047–3060, 3064, 3069, 3072, 3084–3091, 3095, 3100, 3103, 3107–3108, 3117–3124, 3128, 3133, 3136, 3140–3141, 3150–3201, 3205, 3210, 3213, 3217–3218, 3227–3278, 3282, 3287, 3290, 3294–3295, 3304–3355, 3359, 3367, 3371–3372, 3381–3388, 3392, 3397, 3400, 3412–3419, 3423, 3428, 3431, 3435–3436, 3445–3452, 3456, 3461, 3464, 3468–3469, 3478–3485, 3489, 3494, 3497, 3501–3502, 3514–3524, 3528, 3536, 3540–3541, 3550–3557, 3561, 3566, 3569, 3573–3574, 3583–3634, 3638, 3643, 3646, 3650–3651, 3660–3673, 3677, 3682, 3685, 3689–3690, 3699–3706, 3710, 3715, 3718, 3722–3723, 3735–3745, 3749, 3757, 3761–3762, 3771–3778, 3782, 3787, 3790, 3794–3795, 3804–3843, 3847, 3852, 3855, 3859–3860, 3869–3882, 3886, 3891, 3894, 3898–3899, 3908–3915, 3919, 3924, 3927, 3931–3932, 3944–3954, 3958, 3966, 3970–3971, 3980–3987, 3991, 3996, 3999, 4003–4004, 4013–4026, 4030, 4035, 4038, 4042–4043, 4052–4059, 4063, 4068, 4071, 4075–4076, 4088–4124, 4128, 4133, 4136, 4140–4141, 4150–4157, 4166, 4169, 4173–4174, 4183–4215, 4222, 4227, 4230, 4234–4235, 4244–4251, 4255, 4260, 4263, 4267–4268, 4277–4284, 4288, 4293, 4296, 4300–4301, 4310–4323, 4327, 4332, 4335, 4339–4340, 4350–4356, 4360, 4365, 4368, 4372–4373, 4382–4395, 4399, 4404, 4407, 4411–4412, 4421–4428, 4432, 4440, 4444–4445, 4454–4467, 4471, 4476, 4479, 4483–4484, 4493–4500, 4504, 4509, 4512, 4516–4517, 4526–4533, 4537, 4542, 4545, 4549–4550, 4559–4566, 4570, 4575, 4578, 4582–4583, 4595–4605, 4609, 4617, 4621–4622, 4631–4638, 4642, 4650, 4662–4675, 4679, 4684, 4687, 4699–4706, 4710, 4718, 4722–4723, 4732–4745, 4749, 4754, 4757, 4761–4762, 4771–4784, 4788, 4793, 4796, 4807–4814, 4818, 4823, 4826, 4838–4845, 4849, 4854, 4857, 4869–4876, 4880, 4885, 4888, 4892–4893, 4902–4909, 4913, 4918, 4921, 4925–4926, 4938–5038, 5042, 5050, 5054–5055, 5064–5071, 5075, 5080, 5083, 5087–5088, 5097–5200, 5204, 5209, 5212, 5216–5217, 5230–5240, 5244, 5249, 5252, 5256–5257, 5267–5280, 5284, 5289, 5292, 5296–5297, 5307–5320, 5324, 5329, 5332, 5336–5337, 5349–5359, 5363, 5368, 5371, 5375–5376, 5385–5398, 5402, 5407, 5410, 5414–5415, 5424–5431, 5435, 5440, 5443, 5447–5448, 5457–5470, 5479, 5482, 5486–5487, 5495, 5510, 5514–5515, 5525–5531, 5538, 5543, 5546, 5561–5571, 5575, 5580, 5583, 5598–5608, 5612, 5617, 5620, 5635–5671, 5675, 5680, 5683, 5698–5708, 5712, 5717, 5720, 5735–5745, 5749, 5754, 5757, 5769–5776, 5780, 5785, 5788, 5801–5807, 5811, 5816, 5819, 5831–5844, 5848, 5853, 5856, 5868–5875, 5879, 5884, 5887, 5902–5912, 5916, 5921, 5924, 5936–5943, 5947, 5952, 5955, 5967–5974, 5978, 5983, 5986, 5998–6005, 6009, 6017, 6029–6042, 6046, 6051, 6054, 6066–6073, 6077, 6082, 6085, 6097–6104, 6108, 6116, 6128–6339, 6341–6371, 6376–7483, 7486 |
| M | character | N-EVENTS | `server/src/services/career/careerCatalog.js` — 261028.1:4, 12, 42, 51, 82, 91, 97–98, 101, 110; 261108.1:4, 13–21, 52, 62–70, 101–106, 116–117, 123, 125–137, 146–167 |
| M | character | N-EVENTS | `server/src/services/career/careerCombatSources.js` — 261028.1:7, 8, 9–14, 27, 31, 41–60, 62, 65, 103; 261108.1:7, 9, 11, 23–91, 96, 106–108, 110, 113–114, 152 |
| M | character | N-EVENTS | `server/src/services/career/careerEventLedger.js` — 261028.1:47; 261108.1:47–48 |
| M | exploration | N-EVENTS | `server/src/services/career/careerExplorationSources.js` — 261028.1:6, 24–26, 181, 188, 192, 193; 261108.1:6, 25–27, 182–183, 190, 194–208, 210 |
| M | character | N-EVENTS | `server/src/services/career/careerGameplaySources.js` — 261028.1:6, 36, 65; 261108.1:6–7, 38–55, 85–92 |
| M | exploration | N-EVENTS | `server/src/services/career/careerHackingEvidence.js` — 261028.1:47, 59, 61–65, 69–70, 72–73; 261108.1:47–52, 65–66, 68, 71–77, 79–93 |
| A | character | N-EVENTS | `server/src/services/career/careerIdentityMigration.js` — 261108.1:1–64 (追加) |
| M | character | N-MISSION | `server/src/services/career/careerMissionSources.js` — 261028.1:5, 7, 9–11, 32, 42, 66–72, 74; 261108.1:5–8, 10, 12–13, 34–37, 47–49, 74–75, 77–79 |
| A | character | N-EVENTS | `server/src/services/career/careerOwnerReads.js` — 261108.1:1–20 (追加) |
| M | character | N-EVENTS | `server/src/services/career/careerOwnerSources.js` — 261028.1:6–13, 22, 39–42, 44–45, 48–57; 261108.1:6, 14–35, 53–55, 57–65, 68 |
| M | character | N-EVENTS | `server/src/services/career/careerProgress.js` — 261028.1:12, 21–22, 30, 64, 66, 71, 84, 104, 107, 120; 261108.1:12, 22–27, 35–36, 70–72, 75, 80, 93, 114, 117, 130–131 |
| A | character | N-EVENTS | `server/src/services/career/careerRetailIdentities.json` — 261108.1:1–10298 (追加) |
| M | character | N-EVENTS | `server/src/services/career/careerRewardRuntime.js` — 261028.1:9, 18, 25, 27–29, 45–46, 49–50, 53, 57, 60–61, 64–72, 74, 76–78, 82–89, 98, 101–102, 108, 110, 112, 119, 122–128, 131–133, 136–137, 140, 142; 261108.1:9, 18, 26, 28–59, 75–83, 86–87, 90, 94, 96–97, 100, 102–103, 106–112, 116, 124, 127–128, 134, 137, 139, 146, 149–156, 159, 162–163, 166, 168 |
| A | character | N-EVENTS | `server/src/services/career/careerRewardSettlement.js` — 261108.1:1–162 (追加) |
| M | character | N-EVENTS | `server/src/services/career/careerSocialSources.js` — 261028.1:2, 5–10; 261108.1:2, 5–6 |
| M | character | N-EVENTS | `server/src/services/career/careerStandingSources.js` — 261028.1:2, 6–11, 14; 261108.1:2, 6, 9 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/character/accessGroupStore.js` — 261028.1:35, 45, 50, 57, 61; 261108.1:35–47, 58, 64–67, 74, 79 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/character/charMgrService.js` — 261028.1:602, 1077; 261108.1:602–636, 1112 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/character/charService.js` — 261028.1:35, 39, 50, 415, 428, 1178, 1202, 1634, 1829, 1888; 261108.1:35, 40, 52, 418–457, 470, 1220, 1245, 1678–1684, 1880–1887, 1947–1951 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/character/characterEnergyMgrService.js` — 261028.1:7, 13, 23–24, 39–42, 106, 108–111, 197, 215, 264–266, 332; 261108.1:7, 14, 25–26, 41, 105–107, 109–112, 198, 216–263, 313, 379–380 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/character/characterEnergyState.js` — 261028.1:45, 49, 88, 91, 98–99, 101, 146, 149, 151; 261108.1:45, 49, 88, 91, 98–101, 103–111, 156–169, 173, 176–177 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/character/characterState.js` — 261028.1:4316; 261108.1:4316–4317 |
| M | combat | N-CHARACTER / N-OTHER | `server/src/services/character/dogmaInvalidationVersion.js` — 261028.1:6, 15; 261108.1:6, 15–16 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/character/ownerGroupManagerService.js` — 261028.1:10, 12, 20, 166, 311, 313, 335, 348, 386, 389, 404, 407, 427, 434; 261108.1:10–11, 14, 23–26, 173–235, 381–382, 385–386, 409, 423–427, 466, 470–476, 492–494, 498–499, 520, 528–534 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/character/paperDollPayloads.js` — 261028.1:368, 379; 261108.1:368–376, 388–392 |
| A | character | PD (separate job; excluded) | `server/src/services/character/projectDiscoveryCorpus.js` — 261108.1:1–221 (追加) |
| A | character | PD (separate job; excluded) | `server/src/services/character/projectDiscoveryRewards.js` — 261108.1:1–346 (追加) |
| M | character | PD (separate job; excluded) | `server/src/services/character/projectDiscoveryRuntime.js` — 261028.1:9, 14, 29, 36, 42, 58, 62–63, 66, 70, 82, 84, 99, 106, 108–109, 111–113, 115, 116, 118–120, 126, 127, 149, 166, 170; 261108.1:9–10, 15–30, 46, 53–60, 66–68, 84, 88–95, 98–107, 112, 124, 125–128, 143, 151–153, 155–175, 177–180, 182, 184–185, 187–189, 195–198, 200–235, 258–260, 278–280, 284–285 |
| M | character | PD (separate job; excluded) | `server/src/services/character/projectDiscoverySamples.js` — 261028.1:131; 261108.1:131–135 |
| M | character | PD (separate job; excluded) | `server/src/services/character/projectDiscoveryService.js` — 261028.1:8, 17, 18–28, 33, 47–48, 52–53, 65–68; 261108.1:8–10, 20–31, 33, 38–64, 79–80, 84–85, 97–106 |
| M | character | N-EVENTS | `server/src/services/character/seasonManagerService.js` — 261028.1:28, 134, 145, 161, 174, 202, 209, 229, 241, 247, 256, 265; 261108.1:28–33, 140–141, 153, 170–174, 188–190, 219–220, 227–228, 249–251, 263–267, 274, 284, 294 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/character/standingRuntime.js` — 261028.1:9, 107, 585, 753, 825–828, 837–840, 850, 855–860, 864–865, 1148, 1170, 1208, 1211, 1262–1291, 1374, 1608, 1756, 1759, 1763–1766, 1771, 1796, 1865, 1872; 261108.1:9, 108–110, 589, 757, 829–857, 866–867, 877–880, 886–894, 898–912, 1195–1206, 1229, 1267, 1270–1315, 1367, 1449, 1683–1690, 1838–1858, 1861–1877, 1881–1951, 1956, 1982–1989, 2058, 2066 |
| M | GM command | N-GM | `server/src/services/chat/chatCommands.js` — 261028.1:45, 47, 81, 181, 246, 301, 327, 328, 337, 383, 501, 508, 524, 525, 532, 534, 1086, 1087–1089, 1120, 1144, 1174, 1175, 1179, 1192, 1657, 1707, 1723, 1760, 1777, 2398; 261108.1:45–49, 52–84, 119–120, 221, 287–288, 344, 371, 373–374, 384–386, 433–442, 561–563, 571–573, 590, 592, 600–601, 603, 1156–1158, 1160–1162, 1193–1196, 1221–1223, 1254–1257, 1259–1308, 1313–1316, 1330–1350, 1816–1824, 1874, 1891, 1929, 1947, 2569–2600 |
| A | GM command | N-GM | `server/src/services/chat/commands/changeOwner.js` — 261108.1:1–66 (追加) |
| A | GM command | N-GM | `server/src/services/chat/commands/copyShip.js` — 261108.1:1–33 (追加) |
| M | combat | N-COMBAT | `server/src/services/chat/commands/crimewatch.js` — 261028.1:1 (バイトのみ; テキスト差分なし); 261108.1:1 (バイトのみ; テキスト差分なし) **バイトのみ** |
| A | GM command | N-GM | `server/src/services/chat/commands/dogma.js` — 261108.1:1–85 (追加) |
| M | GM command | N-GM | `server/src/services/chat/commands/fitModule.js` — 261028.1:10, 18, 19, 24, 46; 261108.1:10, 19, 21, 26–155, 178–181 |
| A | GM command | H-GM (native menu) | `server/src/services/chat/commands/nativeCompanionRetirement.js` — 261108.1:1–18 (追加) |
| A | GM command | H-GM (native menu) | `server/src/services/chat/commands/nativeDestroy.js` — 261108.1:1–32 (追加) |
| A | GM command | H-GM (native menu) | `server/src/services/chat/commands/nativeSpawns.js` — 261108.1:1–228 (追加) |
| A | GM command | N-GM | `server/src/services/chat/commands/onlineModules.js` — 261108.1:1–49 (追加) |
| A | GM command | N-GM | `server/src/services/chat/commands/reportDesync.js` — 261108.1:1–28 (追加) |
| M | GM command | N-GM | `server/src/services/chat/commands/shipHealth.js` — 261028.1:20, 358, 457; 261108.1:20, 359–443, 543–544 |
| A | GM command | N-GM | `server/src/services/chat/commands/shipSetup.js` — 261108.1:1–41 (追加) |
| M | GM command | N-GM | `server/src/services/chat/commands/shipSkins.js` — 261028.1:61; 261108.1:61 |
| M | GM command | N-GM | `server/src/services/chat/commands/skinrDrafts.js` — 261028.1:6, 9, 10, 21, 23, 37, 41, 43–44, 47, 52, 56, 70–96; 261108.1:6–9, 13–15, 17–18, 29, 31–34, 49–71, 76–95, 97–103, 106, 111–112, 116–134, 149–155 |
| M | GM command | N-GM | `server/src/services/chat/commands/transport.js` — 261028.1:1326, 1530; 261108.1:1326–1338, 1543 |
| M | GM command | N-GM | `server/src/services/chat/commands/unfitModule.js` — 261028.1:64, 76; 261108.1:64–71, 84–99 |
| A | GM command | H-GM (native menu) | `server/src/services/chat/commands/unspawn.js` — 261108.1:1–84 (追加) |
| A | exploration | N-VOID | `server/src/services/chat/commands/voidReplay.js` — 261108.1:1–35 (追加) |
| M | UI | N-CHARACTER / N-OTHER | `server/src/services/chat/sessionChatSync.js` — 261028.1:17, 18; 261108.1:17–26, 28–30 |
| M | UI | N-CHARACTER / N-OTHER | `server/src/services/chat/sessionRegistry.js` — 261028.1:122, 123, 138; 261108.1:122–127, 129, 145 |
| M | UI | N-CHARACTER / N-OTHER | `server/src/services/chat/xmppChatMgrService.js` — 261028.1:262; 261108.1:262 |
| M | UI | N-CHARACTER / N-OTHER | `server/src/services/chat/xmppStubServer.js` — 261028.1:1039, 1051, 1166, 1168, 1448, 1728, 1738, 1740, 1743–1745, 1765, 1774, 2663, 2821, 2834, 2968, 2969–2970, 2974, 2977, 3002, 3005, 3026, 3065, 3142, 3148, 3151, 3152, 3184; 261108.1:1039, 1051–1061, 1176, 1178–1180, 1460–1461, 1741, 1751–1763, 1765, 1768–1770, 1790–1791, 1801, 2690, 2848, 2862, 2996–3042, 3044–3046, 3050–3053, 3057–3060, 3086–3088, 3091–3092, 3114–3118, 3158–3162, 3240–3241, 3248–3252, 3256–3286, 3288–3291, 3324 |
| M | data | N-DATA | `server/src/services/config/configService.js` — 261028.1:408, 411, 423, 531, 817, 841; 261108.1:408–414, 418–423, 436, 544, 830, 854 |
| M | market | N-MARKET | `server/src/services/contracts/contractAuctionRuntime.js` — 261028.1:3, 44, 62, 68, 125, 128, 132; 261108.1:3–6, 48, 66–68, 74–76, 134–138, 142, 146 |
| M | market | N-MARKET | `server/src/services/contracts/contractRuntimeState.js` — 261028.1:45, 59, 199, 725, 735, 738, 1319, 1378, 1380–1383, 1935, 1941, 1947, 1963, 2513, 2521, 2528, 2530, 2531, 2562, 4455, 4458, 4461, 4467, 4560; 261108.1:45–46, 61, 202, 729, 740, 743, 1324–1326, 1385–1407, 1410, 1962–1968, 1974–1978, 1984, 2001–2033, 2584, 2593, 2600–2627, 2629–2631, 2633–2637, 2669, 4563, 4567, 4570, 4576–4577, 4670 |
| A | other | N-CORPORATION | `server/src/services/corporation/allianceAffiliationSettlement.js` — 261108.1:1–152 (追加) |
| M | other | N-CORPORATION | `server/src/services/corporation/allianceRegistryRuntime.js` — 261028.1:7, 48, 52, 57, 62–63, 76, 87, 89, 126, 131, 189–191, 1222–1244; 261108.1:7, 48, 51, 55, 59, 71, 81, 82, 118, 122, 179, 1209–1210 |
| M | character | N-CORPORATION | `server/src/services/corporation/corpApplicationNotifications.js` — 261028.1:197–200, 207; 261108.1:197–205, 212–214 |
| M | other | N-CORPORATION | `server/src/services/corporation/corpGoalsState.js` — 261028.1:166–172, 174, 181–186, 404, 428–429, 431, 435, 482; 261108.1:166–168, 170, 177, 395–402, 427–429, 431, 435, 482–485 |
| M | other | N-CORPORATION | `server/src/services/corporation/corpRecProxyService.js` — 261028.1:14, 116, 129, 134, 191, 197, 311, 314, 325–326; 261108.1:14, 117–123, 137, 142, 199, 205, 319, 322–324, 336–337 |
| M | other | N-CORPORATION | `server/src/services/corporation/corpRegistryRuntime.js` — 261028.1:19, 41, 43, 50, 80, 83–84, 91–92, 96–98, 154–157, 185–186, 213–236, 258–270, 305–314, 377–378, 380, 758, 849, 1980–1983, 2566, 2702, 2737, 3547, 3553–3557, 3559, 3628, 3870–3892; 261108.1:19, 40, 41, 47–50, 81, 83, 89, 93, 148, 175, 201, 222, 256, 318, 319, 696–698, 789, 1920, 2502–2514, 2651–2652, 2688–2692, 3502–3509, 3516–3534, 3536, 3605, 3847–3848 |
| M | other | N-CORPORATION | `server/src/services/corporation/corpService.js` — 261028.1:33, 440; 261108.1:33, 441 |
| M | other | N-CORPORATION | `server/src/services/corporation/corpWalletState.js` — 261028.1:221, 489–491, 556; 261108.1:221–227, 495, 559 |
| M | other | N-CORPORATION | `server/src/services/corporation/corpmgrService.js` — 261028.1:375–376, 493; 261108.1:375–376, 493 |
| M | other | N-CORPORATION | `server/src/services/corporation/corporationLiquidationState.js` — 261028.1:26, 325; 261108.1:26, 325 |
| A | other | N-CORPORATION | `server/src/services/corporation/corporationMemberLimit.js` — 261108.1:1–82 (追加) |
| M | character | N-CORPORATION | `server/src/services/corporation/corporationNotifications.js` — 261028.1:1083, 1518; 261108.1:1083–1114, 1550–1552 |
| M | other | N-CORPORATION | `server/src/services/corporation/corporationRuntimeState.js` — 261028.1:41, 1388, 1522, 1909; 261108.1:41–43, 1391–1418, 1553–1556, 1943 |
| M | other | N-CORPORATION | `server/src/services/corporation/corporationState.js` — 261028.1:270, 552, 717, 727, 729, 753, 767, 793, 1151, 1363, 1428, 1433–1528, 1565, 1625, 1629, 1631, 1826, 1846–1855, 1901; 261108.1:270–272, 555–593, 759–771, 782–784, 787, 811, 825, 851, 1210, 1422–1432, 1498–1504, 1510, 1547–1557, 1618, 1622, 1623–1626, 1821–1822, 1843, 1888–1890 |
| M | other | N-CORPORATION | `server/src/services/corporation/officeRentalBilling.js` — 261028.1:166; 261108.1:166 |
| M | other | N-CORPORATION | `server/src/services/corporation/voteManagerService.js` — 261028.1:43, 174, 295, 302, 321, 330 (EOF insertion point); 261108.1:43–104, 236–238, 360–365, 373–377, 396–399, 409 |
| A | other | N-CORPORATION | `server/src/services/corporation/warDeclarationState.js` — 261108.1:1–231 (追加) |
| A | GM command | H-GM | `server/src/services/corporation/warGmCommands.js` — 261108.1:1–44 (追加) |
| M | other | N-CORPORATION | `server/src/services/corporation/warNegotiationRuntimeState.js` — 261028.1:6, 107, 132, 240–250, 267, 498, 504, 591, 603, 608, 621, 633, 637, 779; 261108.1:6–7, 109, 134, 242–298, 315, 546, 553, 641, 654, 659–661, 675, 688, 692–694, 837 |
| M | character | N-CORPORATION | `server/src/services/corporation/warNotificationCenter.js` — 261028.1:90, 109, 164, 181; 261108.1:90, 110, 166–167, 185–186 |
| M | other | N-CORPORATION | `server/src/services/corporation/warRegistryService.js` — 261028.1:35, 69, 164, 167–174, 184, 215, 217–225, 241, 257–265, 313–321, 353; 261108.1:35, 70, 166, 169–172, 182, 213, 215–222, 238, 254–256, 304–306, 338–340 |
| M | other | N-CORPORATION | `server/src/services/corporation/warRuntimeState.js` — 261028.1:4, 11, 160, 410, 429, 433, 441, 442, 448, 451–452, 459–460, 465, 474, 479, 483, 490, 491, 497, 512, 527, 534, 536, 547, 571, 573–580, 1024, 1660, 1668, 1776; 261108.1:4, 12, 162, 412, 430, 434, 442, 444, 450, 454–456, 463–464, 469, 479, 484, 488, 495, 497, 503–529, 544–592, 608–609, 617–635, 638, 650, 675–678, 681, 1125–1128, 1765–1775, 1784, 1892 |
| M | character | N-EVENTS | `server/src/services/dailyGoals/dailyGoalsCatalog.js` — 261028.1:26–28, 32–33; 261108.1:26–27, 31–32 |
| M | character | N-EVENTS | `server/src/services/dailyGoals/dailyGoalsProto.js` — 261028.1:103, 106; 261108.1:103, 105–106 |
| M | character | N-EVENTS | `server/src/services/dailyGoals/dailyGoalsState.js` — 261028.1:72, 1291; 261108.1:72–134, 1354–1355 |
| M | combat | N-COMBAT | `server/src/services/dogma/dogmaService.js` — 261028.1:1161, 1164, 1170, 1412, 1429, 1530, 1873; 261108.1:1161–1162, 1165, 1171, 1413, 1430–1431, 1533, 1876 |
| M | combat | N-COMBAT | `server/src/services/dogma/dogmaService/implantsAndBoosters.js` — 261028.1:225; 261108.1:225 |
| M | combat | N-COMBAT | `server/src/services/dogma/dogmaService/itemInfo.js` — 261028.1:48, 1385, 1730, 1738–1740; 261108.1:48–53, 1391, 1736–1740, 1749–1750 |
| M | combat | N-COMBAT | `server/src/services/dogma/dogmaService/moduleOperation.js` — 261028.1:62, 2061, 2298, 2614; 261108.1:62, 2062–2075, 2313–2321, 2638–2647 |
| A | combat | N-COMBAT | `server/src/services/dogma/gmDogmaOverrides.js` — 261108.1:1–91 (追加) |
| M | combat | H-COMBAT / N-COMBAT | `server/src/services/drone/droneDogma.js` — 261028.1:10, 81, 399, 675, 678–683, 685, 846; 261108.1:10, 82, 401–402, 679–713, 717–725, 727, 888–892 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/services/drone/droneRuntime.js` — 261028.1:36, 99, 130, 1394, 1421–1422, 1424–1427, 3365, 3377, 3723, 4855, 4906–4909, 7205, 7362–7369, 7377, 7397; 261108.1:36, 100, 132–134, 1399–1406, 1434–1440, 1442–1446, 3384–3403, 3415–3416, 3762, 4894–4906, 4958–4963, 7259–7265, 7423–7449, 7457–7462, 7483 |
| A | other | N-MISSION | `server/src/services/dungeon/capturedSecurityLegacyScenes.js` — 261108.1:1–35 (追加) |
| A | other | N-MISSION | `server/src/services/dungeon/capturedSecurityLegacyScenes.json` — 261108.1:1 (追加) |
| A | character | N-MISSION | `server/src/services/dungeon/careerLessonRuntime.js` — 261108.1:1–293 (追加) |
| M | other | N-MISSION | `server/src/services/dungeon/dungeonAuthority.js` — 261028.1:8, 255, 272, 288, 291, 408, 473, 595; 261108.1:8–9, 257, 275–297, 314–318, 322, 439, 505–514, 637–638 |
| M | other | N-MISSION | `server/src/services/dungeon/dungeonInstanceCacheMgrService.js` — 261028.1:65, 626, 661, 663; 261108.1:65, 627–636, 672, 674–690 |
| A | other | N-MISSION | `server/src/services/dungeon/dungeonInstanceTable.js` — 261108.1:1–186 (追加) |
| M | other | N-MISSION | `server/src/services/dungeon/dungeonRuntime.js` — 261028.1:288, 304, 437, 461, 463, 474, 558, 572, 576, 630, 639, 650, 883, 929, 938, 953, 956, 970–977, 1015, 1098, 1100, 1105, 1223, 1666, 1710, 1712, 2237; 261108.1:288–292, 308, 441–443, 468–470, 472, 484–486, 571–767, 782–787, 792, 846, 855, 867, 1100–1109, 1156, 1165–1193, 1209, 1212–1213, 1227–1230, 1268, 1352–1355, 1358, 1364–1372, 1491–1493, 1937, 1981–2045, 2048–2056, 2582–2583 |
| A | other | N-MISSION | `server/src/services/dungeon/dungeonRuntimeIndexes.js` — 261108.1:1–221 (追加) |
| M | other | N-MISSION | `server/src/services/dungeon/dungeonRuntimeState.js` — 261028.1:17, 18, 32, 363, 533, 614, 630, 657, 754, 782, 790–791, 965–991, 993–1046, 1048–1064, 1066–1069, 1076, 1079–1080, 1095–1097, 1109, 1123, 1364, 1378, 1442, 1444; 261108.1:17, 19–26, 41–42, 374, 544–546, 628, 644–652, 680–708, 806, 834–843, 851–855, 1029–1072, 1074–1083, 1085–1147, 1149–1155, 1162–1163, 1167–1169, 1184–1189, 1201, 1215–1227, 1468–1493, 1508–1509, 1574, 1577–1578 |
| M | other | N-MISSION | `server/src/services/dungeon/dungeonSiteSpawnPolicy.js` — 261028.1:18–19, 23, 74, 487, 1030, 1145, 1304, 1306; 261108.1:18–20, 24, 76–116, 530–533, 1076–1099, 1215–1219, 1379–1380, 1383 |
| M | other | N-MISSION | `server/src/services/dungeon/dungeonTrackingRuntime.js` — 261028.1:16, 31, 166, 191, 226, 389, 431, 435, 447, 448, 526, 527, 554, 788, 943–946, 950, 961, 972; 261108.1:16–17, 32–35, 171–176, 202, 237–239, 403, 445–658, 662, 674–676, 678–686, 765, 767–779, 807, 1042–1043, 1198–1404, 1408–1410, 1422–1425, 1437–1438 |
| M | other | N-MISSION | `server/src/services/dungeon/dungeonUniverseRuntime.js` — 261028.1:22, 1642, 1986, 1987, 1990, 2058, 2108, 2112, 2131, 2198, 2200, 3050, 3245, 3268, 3299, 3301, 3921, 3928–3931, 3939, 3943, 3951, 3955, 3959–3968, 3974–3977, 4015, 4124, 4135–4140, 4144, 5549, 5554, 5561, 5597; 261108.1:22–25, 1646–1648, 1993–1996, 1998, 2001–2002, 2071–2076, 2127, 2131, 2150–2154, 2221–2228, 2231–2233, 3084, 3280–3334, 3358, 3390–3391, 3393, 4013, 4020–4022, 4030, 4034, 4043, 4047–4048, 4052–4058, 4064–4065, 4103–4118, 4228, 4238–4241, 4245–4247, 5652–5653, 5659, 5667, 5704 |
| M | other | N-MISSION | `server/src/services/dungeon/dungeonUniverseSiteService.js` — 261028.1:20, 26, 81, 109–110, 1492, 1884–1890, 1963–1981, 1983, 2547, 2548, 2561, 2571, 2626, 2630, 3011, 3025, 3085, 3144, 3275, 3300, 3312, 3326–3330, 3333–3335, 3542, 3651, 3678, 3707–3708, 3782, 3788, 3836, 4041, 4045, 4055, 4160, 4165, 4255, 4261, 4286, 4313, 4318, 4340, 4344, 4357, 4367, 4368, 4377, 4379, 4383, 4388–4389, 4395, 4411, 4414, 4437, 4442, 4445, 4450, 4491, 4496, 4505, 4520, 4545, 4555, 4564, 4565, 4765, 4949, 5251, 5326, 5350, 5365, 5412, 5435, 5600, 5743, 5843, 5896, 5897, 6005, 6063, 6076, 6078, 6121, 6158, 6160, 6173, 6181, 6189, 6193, 6470, 6497, 6681, 6684, 6876, 6878, 6908, 6909, 6943, 6959, 6980–6981, 6984, 6987, 6996, 6997, 7000, 7014, 7017, 7025, 7027–7028, 7030, 7031, 7039–7045, 7084–7090, 7122, 7165, 7246, 7248–7254, 7257, 7266, 7272, 7289, 7300, 7387, 7435, 7460, 7471, 7585, 7590, 7766, 7885, 7889, 7899, 7979, 8138–8141, 8248, 8349, 8376, 8388, 8429, 8482, 8483, 8488, 8495, 8497, 8521, 8538, 8539, 8592, 8704, 8736, 8836, 8881, 9085, 9104, 9161–9164, 9166, 9170–9182, 9189, 9199, 9202, 9204–9205, 9208, 9219, 9381, 9456, 9459, 9460, 9462, 9470, 9489, 9491–9492, 9504, 9511, 9519, 10737, 10772, 10783, 10860, 10917, 11049, 11146, 11189, 11194, 11206, 11215, 11217, 11218, 11224, 11225, 11238, 11239, 11240, 11245, 11248, 11250, 11279, 11308, 11309–11310, 11324, 11326–11330, 11333, 11401, 11441, 11456, 11470, 11473, 11474; 261108.1:20–24, 31, 87–91, 120–121, 1503–1505, 1898–1901, 1974–1983, 1985, 2550–2554, 2556, 2569, 2579–2585, 2641–2642, 2647–2650, 3032–3034, 3049, 3110–3112, 3172, 3304–3341, 3367–3370, 3383–3458, 3473–3507, 3510–3521, 3728–3734, 3844–3845, 3872–3891, 3921–3924, 3998, 4004–4016, 4065–4066, 4272, 4277, 4288, 4394, 4399–4400, 4491–4540, 4546, 4572, 4599–4601, 4607–4609, 4632, 4636–4637, 4650–4652, 4662–4663, 4665, 4674–4676, 4679, 4683, 4688–4689, 4695, 4712, 4715–4717, 4740, 4745, 4748–4749, 4755, 4796, 4802, 4812, 4828–4829, 4855, 4866, 4876–4878, 4880–4882, 5082, 5267–5274, 5577, 5653, 5678, 5693–5706, 5754, 5778, 5944–5945, 6088–6090, 6190, 6244–6278, 6280, 6389–6394, 6453, 6467, 6470–6473, 6516–6517, 6555, 6558–6559, 6572–6573, 6582, 6590–6669, 6673–6678, 6956–6961, 6988–6996, 7181, 7185, 7378, 7380–7381, 7411–7421, 7423–7424, 7458, 7474, 7494, 7497–7505, 7509, 7517–7518, 7520–7555, 7559–7564, 7579–7581, 7584, 7592–7600, 7603–7613, 7615–7628, 7630–7665, 7673, 7712, 7744–7748, 7792, 7873–7876, 7879–7885, 7888–7889, 7898–7906, 7912–8005, 8023–8025, 8037–8044, 8132–8134, 8183, 8209–8210, 8221, 8336–8340, 8346–8357, 8534–8705, 8825–8828, 8832–8839, 8850–8853, 8933, 9092–9182, 9289–9319, 9421–9441, 9469–9581, 9594, 9636–9703, 9757–9758, 9760–9774, 9780–9788, 9796–9798, 9801–9816, 9841–9845, 9863–9873, 9875–9876, 9930–9931, 10044–10059, 10092–10093, 10194–10199, 10245–10284, 10489–10496, 10515, 10573–10596, 10598–10599, 10604, 10611, 10621–10633, 10636–10640, 10642–10645, 10648–10649, 10660, 10823–10824, 10899–10902, 10905, 10907, 10909, 10917–10918, 10938, 10940–10941, 10953, 10960, 10969, 12188, 12223, 12234–12237, 12314–12318, 12376–12382, 12515–12516, 12613–12618, 12661, 12667–12669, 12682–12683, 12693–12694, 12697, 12699, 12706–12775, 12777–12781, 12794–12799, 12801, 12803, 12808–12815, 12818–12826, 12828, 12857, 12886–12892, 12894–12895, 12909–12910, 12913–12915, 12918–12997, 13066–13084, 13125–13130, 13146, 13161, 13165, 13167 |
| A | other | N-MISSION | `server/src/services/dungeon/epicSceneActions.js` — 261108.1:1–274 (追加) |
| A | other | N-MISSION | `server/src/services/dungeon/fracturedLegacyRuntime.js` — 261108.1:1–243 (追加) |
| A | other | N-MISSION | `server/src/services/dungeon/fracturedLegacySceneActions.js` — 261108.1:1–160 (追加) |
| M | other | N-MISSION | `server/src/services/dungeon/globalSiteSchedulerRuntime.js` — 261028.1:61, 96, 107–109, 121, 127, 146, 183, 198, 218, 299–302, 305, 328, 351, 366, 414, 436; 261108.1:61–90, 126–127, 139–175, 187–188, 195, 215, 253, 269, 290–296, 378–380, 383–385, 409, 433, 449, 498, 521 |
| M | other | N-MISSION | `server/src/services/dungeon/keeperService.js` — 261028.1:711, 772, 848; 261108.1:711–720, 782–785, 862–865 |
| A | character | N-MISSION | `server/src/services/dungeon/missionAccessGrants.js` — 261108.1:1–55 (追加) |
| A | industry | N-MISSION | `server/src/services/dungeon/missionMiningSettlement.js` — 261108.1:1–217 (追加) |
| A | industry | N-MISSION | `server/src/services/dungeon/missionSalvage.js` — 261108.1:1–132 (追加) |
| M | other | N-MISSION | `server/src/services/dungeon/operationSiteRuntime.js` — 261028.1:117, 122; 261108.1:117–138, 144 |
| A | other | N-MISSION | `server/src/services/dungeon/requiredActorPolicy.js` — 261108.1:1–132 (追加) |
| A | character | N-MISSION | `server/src/services/dungeon/securityMissionReconHazards.js` — 261108.1:1–185 (追加) |
| A | character | N-MISSION | `server/src/services/dungeon/securityMissionWeather.js` — 261108.1:1–115 (追加) |
| M | other | N-MISSION | `server/src/services/dungeon/siteHazardOutcomes.js` — 261028.1:6–7, 10, 12, 33, 34, 39–40, 77–78, 84–92, 94–95, 99, 112–113, 118; 261108.1:6, 8, 11–12, 33, 35–36, 41–44, 81–82, 88, 89–90, 94, 107–108, 113 |
| A | other | N-MISSION | `server/src/services/dungeon/sovProspectingNameRepair.js` — 261108.1:1–142 (追加) |
| A | other | N-MISSION | `server/src/services/dungeon/visionsCaldariCapture.json` — 261108.1:1–24 (追加) |
| A | other | N-MISSION | `server/src/services/dungeon/visionsCaldariNative.json` — 261108.1:1–1484 (追加) |
| A | other | N-MISSION | `server/src/services/dungeon/visionsCaldariProgress.js` — 261108.1:1–267 (追加) |
| A | other | N-MISSION | `server/src/services/dungeon/visionsCaldariRuntime.js` — 261108.1:1–204 (追加) |
| A | other | N-MISSION | `server/src/services/dungeon/visionsSceneRuntime.js` — 261108.1:1–141 (追加) |
| M | other | N-OTHER | `server/src/services/dynamic/dynamicResourceState.js` — 261028.1:9, 49–106, 130, 198, 202, 205–210, 296–298, 300, 313, 315, 329, 337; 261108.1:9, 50–52, 76, 145, 149, 152–155, 241–242, 244–255, 268–269, 272, 286–297, 306–307 |
| A | combat | H-COMBAT / N-COMBAT | `server/src/services/dynamic/essAccessRuntime.js` — 261108.1:1–127 (追加) |
| A | combat | H-COMBAT / N-COMBAT | `server/src/services/dynamic/essFieldRuntime.js` — 261108.1:1–179 (追加) |
| A | combat | H-COMBAT / N-COMBAT | `server/src/services/dynamic/essPresentationRuntime.js` — 261108.1:1–62 (追加) |
| A | combat | H-COMBAT / N-COMBAT | `server/src/services/dynamic/essReserveKeyAuthority.json` — 261108.1:1–153 (追加) |
| M | combat | H-COMBAT / N-COMBAT | `server/src/services/dynamic/essReserveRuntime.js` — 261028.1:19, 63, 171; 261108.1:19–39, 84, 193 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/services/dynamic/essSettlementRuntime.js` — 261028.1:32, 36–37, 39; 261108.1:32–34, 38, 40–43 |
| M | other | N-OTHER | `server/src/services/eveOffline/transportLocationResolver.js` — 261028.1:56; 261108.1:56–66 |
| A | exploration | N-EXPLORATION | `server/src/services/exploration/gas/kspaceGasPlacementAuthority.js` — 261108.1:1–414 (追加) |
| M | exploration | N-EXPLORATION | `server/src/services/exploration/hackingBoardEngine.js` — 261028.1:68, 168, 292, 296–298, 322, 331, 333; 261108.1:68–77, 178–179, 303–304, 308–310, 334, 343, 345 |
| M | exploration | N-EXPLORATION | `server/src/services/exploration/hackingContainerState.js` — 261028.1:130, 153; 261108.1:130–133, 157 |
| M | exploration | N-EXPLORATION | `server/src/services/exploration/hackingMgrService.js` — 261028.1:214, 612, 619, 678, 700, 779, 827, 835; 261108.1:214, 613, 621–624, 684, 707, 787, 835–837, 846 |
| M | exploration | N-EXPLORATION | `server/src/services/exploration/scanMgrService.js` — 261028.1:72, 517, 705, 711–714, 723, 806, 825, 831, 1619, 2355, 2357, 3054; 261108.1:72, 518–520, 709–733, 739–745, 754–824, 907, 925, 931–933, 1722, 2458, 2460–2462, 3160–3187 |
| A | exploration | N-MISSION | `server/src/services/exploration/signatures/privateMissionAnomalies.js` — 261108.1:1–99 (追加) |
| M | exploration | N-EXPLORATION | `server/src/services/exploration/signatures/signatureRuntime.js` — 261028.1:78–79, 87, 91–92, 100, 382; 261108.1:78–79, 87–91, 95–96, 104–120, 402–403 |
| A | data | H-EXPLORATION / N-EXPLORATION | `server/src/services/exploration/wormholes/kspaceWanderingProfiles.json` — 261108.1:1–26 (追加) |
| A | exploration | H-EXPLORATION / N-EXPLORATION | `server/src/services/exploration/wormholes/systemWideEffectsSync.js` — 261108.1:1–102 (追加) |
| M | exploration | H-EXPLORATION / N-EXPLORATION | `server/src/services/exploration/wormholes/wormholeAuthority.js` — 261028.1:13, 47, 52, 53–56, 70, 134, 136–138, 140, 158, 226–229, 278; 261108.1:13, 48–76, 81, 83, 97, 161–163, 166, 168–170, 189, 258–272, 321 |
| M | exploration | H-EXPLORATION / N-EXPLORATION | `server/src/services/exploration/wormholes/wormholeMgrService.js` — 261028.1:24–27, 71–83, 139, 325; 261108.1:24, 67, 122–124, 310 |
| M | exploration | H-EXPLORATION / N-EXPLORATION | `server/src/services/exploration/wormholes/wormholeRuntime.js` — 261028.1:28, 38, 332–337, 780, 784, 787–788, 790, 793–794, 803–805, 815, 819, 821, 885, 1242, 1243, 1250, 1659, 1668, 1670, 1677–1678, 1694–1730, 1756, 1758–1759, 1765, 1769–1773, 1836, 1840, 1856, 1874, 1878, 1879, 1891, 1893, 1895, 1900, 1911, 1913–1916, 2531–2532; 261108.1:28, 39–42, 337, 780, 784, 787, 789, 792–793, 802–810, 820, 824, 826, 891–896, 1254–1256, 1258–1259, 1267, 1677, 1686–1687, 1689–1690, 1697, 1713–1729, 1755, 1757–1760, 1766, 1771–1772, 1835, 1839–1844, 1860, 1877, 1881–1885, 1887, 1899–1944, 1946, 1948, 1952, 1963, 1965–1966, 2581 |
| M | exploration | H-EXPLORATION / N-EXPLORATION | `server/src/services/exploration/wormholes/wormholeRuntimeState.js` — 261028.1:11, 25, 152, 156, 163, 210–211, 266, 270, 307, 311, 357, 358, 423, 586, 649, 650, 653, 663, 666, 672, 689, 785, 808, 814, 816; 261108.1:11, 26–27, 155–157, 162–163, 171–177, 225–226, 281–289, 293–302, 340, 344–357, 404, 406–408, 474–497, 661–667, 731, 733, 736, 746, 750, 757, 775–778, 874, 898, 905–906, 909 |
| A | combat | H-COMBAT / N-COMBAT | `server/src/services/fighter/fighterDamageApplication.js` — 261108.1:1–43 (追加) |
| M | combat | H-COMBAT / N-COMBAT | `server/src/services/fighter/fighterDogma.js` — 261028.1:15, 44, 127, 145, 540, 656–659, 801, 814; 261108.1:15, 45, 129–130, 149–152, 548–556, 672–677, 819–829, 843–847 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/services/fighter/fighterInventory.js` — 261028.1:12, 243, 252, 258, 263, 268, 269, 273, 341, 347; 261108.1:12, 244–261, 270, 276, 281, 286–289, 291, 295–301, 369, 376 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/services/fighter/fighterMgrService.js` — 261028.1:7, 27, 29, 31, 132–153, 195, 200, 204, 208, 325; 261108.1:7, 28, 30, 33, 135, 176, 181, 185, 189–200, 318 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/services/fighter/fighterRuntime.js` — 261028.1:9, 47, 71, 73, 97, 136, 137, 142, 147, 163, 1197, 1394, 1412, 1813, 1889–1891, 2025, 2215–2236, 2280, 2284–2285, 2288, 2459, 2565, 2566, 2568–2569, 2842–2855, 2920–2930, 3037–3074, 3076–3155, 3234, 3246, 3303, 3498, 3515, 3529, 3531, 3554, 3632, 3730, 3770, 3775, 4067, 4076, 4120, 4138, 4214, 4217, 4248, 4307, 4337, 4338, 4345–4351, 4355, 4484, 4544, 4555, 4578, 4689, 4715, 4820, 5063, 5137, 5173, 5180, 5211, 5307–5314, 5402, 5403–5411; 261108.1:9, 48, 73, 76, 101–104, 144–148, 150, 156, 162, 179–195, 1230, 1428–1430, 1449–1454, 1856, 1932–1934, 2068–2070, 2261, 2304, 2308–2309, 2312–2368, 2540–2559, 2666, 2668, 2670, 2943–2947, 3012–3025, 3132–3162, 3164–3186, 3265–3268, 3280–3295, 3353–3359, 3555–3557, 3574–3575, 3589–3592, 3594, 3617–3619, 3698–3705, 3804–3813, 3854–3859, 3865, 4157, 4166–4187, 4232, 4251–4252, 4328, 4332–4339, 4370–4373, 4433–4439, 4470–4474, 4476–4495, 4503, 4507, 4637–4640, 4701–4722, 4734, 4758–4764, 4876–4888, 4915–4918, 5024–5028, 5272–5279, 5353, 5390–5418, 5426, 5458, 5555, 5643–5646, 5648–5650 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/fitting/liveFittingState.js` — 261028.1:22, 52, 111, 599, 617, 655, 676, 677–679, 1019, 1041, 1153, 1154, 1158, 1241, 1243–1245, 1247, 2662, 2880, 2897, 2909–2926, 2940, 2942–2945, 2949–2950, 2972, 2998–3000, 3002–3007, 3015, 3017, 3033, 3117, 3118, 3253, 3626, 3722, 3731, 3791, 3793–3795, 3797, 3816, 3821, 3849, 3887, 3925, 4002, 4013, 4045, 4047, 4050, 4061, 4063, 4066, 4105, 4313, 4356; 261108.1:22, 53, 113, 602–611, 630, 668–672, 694–696, 698–710, 1050–1064, 1087, 1198, 1200, 1204, 1287, 1289–1291, 1293, 2708, 2926, 2943, 2955, 2969–2971, 2973–2976, 2980, 3001–3026, 3052–3054, 3056, 3063, 3066, 3082–3083, 3167–3191, 3193, 3329–3330, 3704, 3800–3805, 3815–3822, 3883, 3885–3887, 3889, 3908–3909, 3915, 3944–3948, 3987–3989, 4028–4035, 4112–4116, 4128, 4160, 4162, 4165, 4176, 4178, 4181, 4220–4224, 4433–4434, 4478–4479 |
| A | other | N-CORPORATION | `server/src/services/fleetShare/fleetShare.js` — 261108.1:1–133 (追加) |
| A | other | N-CORPORATION | `server/src/services/fleets/fleetIdentityAllocator.js` — 261108.1:1–64 (追加) |
| M | other | N-CORPORATION | `server/src/services/fleets/fleetRuntime.js` — 261028.1:295–296, 532, 3009; 261108.1:295–296, 532–548, 3026 |
| A | other | N-CORPORATION | `server/src/services/fleets/fleetWarpFollowers.js` — 261108.1:1–379 (追加) |
| M | combat | N-HOMEFRONT | `server/src/services/homefront/homefrontClient.js` — 261028.1:3, 8; 261108.1:3–7, 12–14 |
| M | combat | N-HOMEFRONT | `server/src/services/homefront/homefrontWorld.js` — 261028.1:249, 253, 304; 261108.1:249–268, 273, 324 |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionActorCleanup.js` — 261108.1:1–52 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionAdmission.js` — 261108.1:1–82 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionCargo.js` — 261108.1:1–318 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionCivilianDrops.js` — 261108.1:1–172 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionClock.js` — 261108.1:1–11 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionEncounterModel.js` — 261108.1:1–125 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionEncounterRuntime.js` — 261108.1:1–444 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionFactory.js` — 261108.1:1–198 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionFinalSiteRuntime.js` — 261108.1:1–84 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionFinalSiteSlots.js` — 261108.1:1–148 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionFocusPlacement.js` — 261108.1:1–138 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionFocusRuntime.js` — 261108.1:1–187 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionFocusState.js` — 261108.1:1–105 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionFriendlyActors.js` — 261108.1:1–150 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionHooks.js` — 261108.1:1–26 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionLifecycle.js` — 261108.1:1–262 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionLifecycleContract.js` — 261108.1:1–248 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionLifecycleScheduler.js` — 261108.1:1–105 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionLifecycleStore.js` — 261108.1:1–49 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionLogistics.js` — 261108.1:1–269 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionLogisticsSupport.js` — 261108.1:1–190 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionMining.js` — 261108.1:1–91 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionParticipation.js` — 261108.1:1–69 (追加) |
| M | combat | N-INCURSION | `server/src/services/incursion/incursionPayloads.js` — 261028.1:2, 10, 75; 261108.1:2, 11, 77–81 |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionPresentation.js` — 261108.1:1–55 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionProjectionRuntime.js` — 261108.1:1–100 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionReinforcements.js` — 261108.1:1–160 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionRepairPresentation.js` — 261108.1:1–30 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionRewardReports.js` — 261108.1:1–32 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionRewardScheduler.js` — 261108.1:1–101 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionRewardStore.js` — 261108.1:1–57 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionRewards.js` — 261108.1:1–405 (追加) |
| M | combat | N-INCURSION | `server/src/services/incursion/incursionRuntime.js` — 261028.1:3, 10, 14, 18; 261108.1:3–5, 12–14, 18–19, 23 |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionSiteAdmission.js` — 261108.1:1–72 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionSiteMechanics.js` — 261108.1:1–187 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionSiteModel.js` — 261108.1:1–271 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionSiteRuntime.js` — 261108.1:1–144 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionSiteSlots.js` — 261108.1:1–248 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionStructureRepair.js` — 261108.1:1–78 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionStructures.js` — 261108.1:1–436 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionSystemEffectContract.js` — 261108.1:1–32 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionSystemEffects.js` — 261108.1:1–160 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionTerminalContract.js` — 261108.1:1–120 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/incursionTerminalRuntime.js` — 261108.1:1–51 (追加) |
| A | combat | N-INCURSION | `server/src/services/incursion/installIncursions.js` — 261108.1:1–7 (追加) |
| M | industry | N-INDUSTRY | `server/src/services/industry/blueprintManagerService.js` — 261028.1:18, 34; 261108.1:18, 35–41 |
| M | industry | N-INDUSTRY | `server/src/services/industry/industryFacilityModifiers.js` — 261028.1:13, 18, 24, 27–145, 148–338, 475–501, 503–530, 575–576, 612–613, 623, 627; 261108.1:13, 19, 24, 28–75, 78, 214–219, 221, 265–266, 302, 312, 317 |
| A | data | N-INDUSTRY | `server/src/services/industry/industryModifierAuthority.json` — 261108.1:1–260 (追加) |
| M | industry | N-INDUSTRY | `server/src/services/industry/industryRuntimeState.js` — 261028.1:2145, 3638, 3643, 3651; 261108.1:2145, 3638–3640, 3646, 3655 |
| M | data | N-INDUSTRY | `server/src/services/industry/starbaseAssemblyProfiles.json` — 261028.1:3–8, 12, 14, 15, 17, 21, 23–24, 26–28; 261108.1:3–15, 19, 22, 24, 26, 31, 34, 35 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/inventory/careerCombatOutcomes.js` — 261028.1:11, 46, 89, 121; 261108.1:11–43, 79–96, 140–141, 173 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/inventory/deliveryItemCustody.js` — 261028.1:139; 261108.1:139 |
| A | character | N-MISSION | `server/src/services/inventory/fracturedLegacyCargo.js` — 261108.1:1–193 (追加) |
| A | combat | N-INCURSION | `server/src/services/inventory/incursionCombatOutcomes.js` — 261108.1:1–190 (追加) |
| A | combat | N-INCURSION | `server/src/services/inventory/incursionFactoryCustody.js` — 261108.1:1–63 (追加) |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/inventory/invBroker/fitRefusalNotice.js` — 261028.1:24; 261108.1:24 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/inventory/invBroker/fittingOperations.js` — 261028.1:21, 28, 74; 261108.1:21–22, 30, 77–128 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/inventory/invBrokerService.js` — 261028.1:70, 111, 158, 169, 1223, 1328, 2741, 2747–2757, 3142, 3145, 3791, 3831, 3870, 5353, 5356, 5358, 5725–5728, 6638, 7487, 7802–7830, 7930–7934, 8062–8066, 8754, 8892, 9024, 9069, 9139, 9320, 10019, 10394, 10944, 10983, 11004, 11676, 12631, 12678, 13090, 13165, 13216, 13269, 13457, 13924, 14028, 14333; 261108.1:70–73, 115–116, 164, 176, 1231–1234, 1340–1346, 2760, 2766–2772, 3157–3161, 3165, 3811–3814, 3855–3856, 3895–3896, 5379–5380, 5383, 5385, 5752–5753, 6663–6666, 7515–7533, 7849, 7948, 8076, 8764–8770, 8909–8913, 9046–9049, 9095–9106, 9177–9179, 9361–9364, 10064–10068, 10444–10457, 11008–11019, 11059–11073, 11095–11101, 11774–11781, 12737, 12784–12796, 13209, 13285, 13336, 13390–13398, 13586–13587, 14055, 14160, 14465–14470 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/inventory/itemCustody.js` — 261028.1:17, 87, 228, 994, 1118, 1142, 1152, 1239, 1289, 1322, 1332, 1384, 1493, 1567, 1608, 1839, 1867, 1882, 2778, 2801, 2807; 261108.1:17–18, 89, 231, 998, 1123, 1147–1153, 1164–1172, 1260–1263, 1314–1321, 1355, 1366, 1419–1422, 1532, 1607, 1649–1666, 1898, 1927–1931, 1946, 2842, 2866–2901, 2908–2909 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/inventory/itemStore.js` — 261028.1:8, 30, 89, 90, 862, 864–867, 990, 992, 1050, 1051, 1078, 1086, 1096, 1100, 1236, 1244, 1373, 1671, 1703, 2026, 2048, 2050, 2386, 2391, 2554, 2605, 2766, 2961, 3009, 3762, 3997, 4511, 4530, 4573, 4834, 5633, 5696, 5703, 5707, 5836, 6132, 6206, 6942, 6968, 7223, 7261, 7313, 7337, 7431, 7511, 7534, 8461, 8463, 8480, 8481, 8529, 8540, 8549, 8552; 261108.1:8–9, 32, 92, 94, 867–901, 904, 1027–1110, 1113–1115, 1174–1176, 1178–1210, 1238, 1247, 1258, 1263, 1400–1421, 1430, 1559–1573, 1872, 1905–1908, 2232, 2255–2257, 2260–2262, 2599, 2605–2606, 2769, 2821–2822, 2984, 3180, 3229, 3983, 4219, 4734–4792, 4812–4830, 4873–4878, 5140, 5939–5940, 6004, 6012–6014, 6018, 6147–6153, 6450–6463, 6537–6561, 7298–7322, 7349–7352, 7608–7613, 7652–7665, 7718–7719, 7744–7746, 7841–7843, 7924, 7948–8016, 8944, 8946, 8963–9088, 9090–9094, 9143, 9155, 9165, 9169–9170 |
| A | character | N-CHARACTER / N-OTHER | `server/src/services/inventory/lootOwnerTransferMetadata.js` — 261108.1:1–38 (追加) |
| A | character | N-MISSION | `server/src/services/inventory/materializedMissionCargoContainer.js` — 261108.1:1–14 (追加) |
| A | industry | N-MISSION | `server/src/services/inventory/missionMiningItemCustody.js` — 261108.1:1–38 (追加) |
| A | character | N-MISSION | `server/src/services/inventory/missionRescueCargo.js` — 261108.1:1–38 (追加) |
| A | industry | N-MISSION | `server/src/services/inventory/missionSalvageItemCustody.js` — 261108.1:1–60 (追加) |
| M | character | H-OTHER / N-STRUCTURE | `server/src/services/inventory/mobileItemCustody.js` — 261028.1:37, 51, 162; 261108.1:37–41, 56, 168 |
| M | industry | N-INDUSTRY | `server/src/services/inventory/reprocessingItemCustody.js` — 261028.1:189, 193; 261108.1:189–196, 201–202 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/killmail/killmailState.js` — 261028.1:281–285, 288–295; 261108.1:281–290, 293 |
| M | other | N-OTHER | `server/src/services/machoNet/machoNetService.js` — 261028.1:8, 162, 164–173, 328–331; 261108.1:8–10, 164, 166–170, 325–327 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/mail/mailNotifications.js` — 261028.1:9, 52; 261108.1:9, 53 |
| M | exploration | N-INCURSION | `server/src/services/map/mapService.js` — 261028.1:571; 261108.1:571–572 |
| M | other | N-MAP | `server/src/services/map/solarSystemInterferenceState.js` — 261028.1:201, 204, 211–214, 257, 273, 275; 261108.1:201, 204, 211–224, 267–310, 327, 330–333 |
| M | other | N-MAP | `server/src/services/map/solarsystemInterferenceMgrService.js` — 261028.1:7, 10, 11, 17, 22–30, 33, 36, 39, 41–44, 46, 57, 71, 90; 261108.1:7–8, 12, 14, 19–23, 29–40, 43, 46, 49–50, 52–55, 57–106, 118, 132, 151–152 |
| M | other | N-CORPORATION | `server/src/services/map/sovMgrService.js` — 261028.1:20–22, 24, 28, 50–70, 126, 127–129, 132–135; 261108.1:20, 21, 24, 47–49, 105–106, 108, 111–113 |
| M | market | H-MARKET / N-MARKET | `server/src/services/market/marketProxyService.js` — 261028.1:104, 181, 1150, 1152–1153, 1158, 2909, 3045, 5186, 5220, 5294–5298, 5300–5333, 5399–5404, 5406–5428; 261108.1:104, 182–184, 1153–1154, 1156–1160, 1165–1183, 2935, 3071–3073, 5214, 5248–5252, 5327–5371, 5373, 5438, 5440–5471 |
| M | market | H-MARKET / N-MARKET | `server/src/services/market/marketTopology.js` — 261028.1:9, 208–215, 259–266; 261108.1:9, 208, 251 |
| M | industry | N-INDUSTRY | `server/src/services/mining/airMiningSettlement.js` — 261028.1:17; 261108.1:17–18 |
| M | industry | N-INDUSTRY | `server/src/services/mining/miningCargoCommits.js` — 261028.1:3, 49, 55, 82, 85, 99, 101, 108, 115; 261108.1:3, 49–58, 65, 92, 95, 109, 111–113, 120–122, 129–135 |
| M | industry | N-INDUSTRY | `server/src/services/mining/miningResourceSiteService.js` — 261028.1:1131; 261108.1:1131–1144 |
| M | industry | N-INDUSTRY | `server/src/services/mining/miningRuntime.js` — 261028.1:853, 927, 984, 1006, 1008, 1018–1019, 1025, 1048; 261108.1:853–859, 934–936, 994, 1017–1023, 1025–1030, 1041–1042, 1048–1054, 1078 |
| M | industry | N-INDUSTRY | `server/src/services/mining/miningRuntimeState.js` — 261028.1:559–579, 583–584, 653, 690, 695, 974, 1010–1014, 1258, 1577, 1579, 1604, 1687–1688, 1712, 1878, 1888; 261108.1:559–584, 588–590, 659–708, 746, 751, 1031–1117, 1154–1162, 1406, 1725, 1727, 1753–1754, 1838–1839, 1863–1868, 2035–2036, 2047–2049 |
| A | industry | N-INDUSTRY | `server/src/services/mining/playerMiningCargo.js` — 261108.1:1–39 (追加) |
| A | character | H-CHARACTER / N-CHARACTER | `server/src/services/newEdenStore/rewardRedeemTokens.js` — 261108.1:1–81 (追加) |
| M | character | H-CHARACTER / N-CHARACTER | `server/src/services/newEdenStore/storeState.js` — 261028.1:193, 240, 244, 245; 261108.1:193, 241, 246, 248–249 |
| A | data | N-NODEGRAPH | `server/src/services/nodegraph/nativeAtomSignatures.json` — 261108.1:1–49 (追加) |
| M | other | N-NODEGRAPH | `server/src/services/nodegraph/nodeGraphCompiler.js` — 261028.1:6, 16, 23, 150, 191; 261108.1:6, 17, 24–51, 179–189, 231 |
| M | other | N-NODEGRAPH | `server/src/services/nodegraph/nodeGraphEffects.js` — 261028.1:9, 85, 101; 261108.1:9, 85–89, 106–114 |
| M | other | N-NODEGRAPH | `server/src/services/nodegraph/nodeGraphRuntime.js` — 261028.1:6, 89, 366, 368–369; 261108.1:6, 89, 366, 368–389 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/notifications/notificationConstants.js` — 261028.1:37, 41; 261108.1:37–40, 45–47 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/notifications/notificationState.js` — 261028.1:4, 25, 517, 547, 564–566, 570, 593–594; 261108.1:4, 26, 517–544, 575–578, 595–597, 601, 624–627 |
| M | other | N-AIR | `server/src/services/npe/airCombatRuntime.js` — 261028.1:14, 23–24, 29, 34, 38, 43, 71–72, 93–94, 97–100, 125, 130, 141, 142, 149–160, 164–165, 167, 179, 182, 186, 189, 200, 206; 261108.1:14, 24–49, 54–59, 64–66, 71–93, 99–104, 132–136, 157–160, 163–170, 195–200, 206–210, 221–234, 236–254, 261, 265–267, 269–270, 282, 286, 290–300, 304–308, 320–321, 328 |
| M | other | N-AIR | `server/src/services/npe/airEconomyRuntime.js` — 261028.1:13, 24, 25, 37, 53, 54, 106; 261108.1:13–14, 26–67, 69–72, 84, 101–103, 105, 157 |
| M | other | N-AIR | `server/src/services/npe/airEncounterRuntime.js` — 261028.1:32–33, 130, 162, 166, 173, 202–205, 267, 273, 276, 284, 290, 307–308; 261108.1:32–38, 135, 167–169, 173–176, 183–196, 226–236, 298–305, 312–320, 324, 333, 340–369, 387–388 |
| A | other | N-AIR | `server/src/services/npe/airFleetBattle.js` — 261108.1:1–144 (追加) |
| M | other | N-AIR | `server/src/services/npe/airGuidanceRuntime.js` — 261028.1:21, 40; 261108.1:21–44, 64 |
| M | industry | N-AIR | `server/src/services/npe/airMiningRuntime.js` — 261028.1:12, 28, 53, 67, 69, 70, 114, 119, 125, 129, 132, 149, 171–172, 175, 191; 261108.1:12–19, 36–59, 85–87, 101, 103–105, 107, 151–152, 158–160, 167–170, 174–176, 179, 196, 218–225, 228, 244 |
| M | other | N-AIR | `server/src/services/npe/airNpeRuntime.js` — 261028.1:66, 68, 70, 73–78, 84–89, 95, 96, 99–102, 112, 121, 142, 160, 180, 187, 189, 201, 231, 279; 261108.1:66–68, 71–72, 74–79, 83–95, 101, 107–115, 117–118, 121–149, 159, 168–170, 191–201, 219–225, 245–248, 256, 259–266, 278–282, 312, 360–366 |
| A | other | N-AIR | `server/src/services/npe/airOpportunityRuntime.js` — 261108.1:1–90 (追加) |
| M | other | N-AIR | `server/src/services/npe/nesIntroService.js` — 261028.1:40; 261108.1:40 |
| M | industry | N-INDUSTRY | `server/src/services/planet/planetMgrService.js` — 261028.1:25, 27, 73, 717, 726, 965, 1439; 261108.1:25–26, 29–35, 82, 726, 735, 974–1003, 1478–1489 |
| A | industry | N-INDUSTRY | `server/src/services/planet/planetOrbitalReinforcement.js` — 261108.1:1–389 (追加) |
| M | industry | N-INDUSTRY | `server/src/services/planet/planetOrbitalState.js` — 261028.1:7, 66, 572, 667, 675, 761, 776, 786, 841, 859, 877, 1075, 1130, 1145, 1160, 1198–1199, 1327–1333, 1335–1341, 1344, 1346, 1347, 1358, 1363, 1367, 1369, 1394, 1397, 1399, 1582, 1586; 261108.1:7, 67–75, 582–584, 680–700, 709–710, 796–799, 815–826, 837–841, 897–899, 918–926, 945–948, 1147–1152, 1207, 1222, 1237, 1275–1277, 1405–1408, 1410–1420, 1423–1424, 1426, 1428–1513, 1525, 1530–1548, 1553, 1555, 1581, 1584–1586, 1589–1711, 1895, 1900–1904 |
| A | industry | N-INDUSTRY | `server/src/services/planet/planetResourceDistribution.json` — 261108.1:1–148 (追加) |
| M | industry | N-INDUSTRY | `server/src/services/planet/planetRuntimeStore.js` — 261028.1:50, 1469, 3611, 3622, 3784; 261108.1:50–52, 1472–1484, 3627–3635, 3647–3662, 3825–3832 |
| M | industry | N-INDUSTRY | `server/src/services/planet/planetStaticData.js` — 261028.1:9, 441, 442, 569; 261108.1:9, 442–447, 449–453, 581 |
| M | industry | N-INDUSTRY | `server/src/services/planet/planetaryCustomsSettlementState.js` — 261028.1:991; 261108.1:991–1042 |
| M | industry | N-INDUSTRY | `server/src/services/planet/skyhookOfficeReplacement.js` — 261028.1:195, 288, 322; 261108.1:195–196, 289–316, 351 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/raffles/raffleInventory.js` — 261028.1:106, 115, 139; 261108.1:106, 115–117, 141 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/raffles/raffleNotifications.js` — 261028.1:12, 27; 261108.1:12, 27 |
| A | industry | N-INDUSTRY | `server/src/services/reprocessing/compressionAuthority.js` — 261108.1:1–26 (追加) |
| A | data | N-INDUSTRY | `server/src/services/reprocessing/compressionAuthority.json` — 261108.1:1–224 (追加) |
| M | industry | N-INDUSTRY | `server/src/services/reprocessing/reprocessingSettlementState.js` — 261028.1:599–601, 650, 654, 722, 1120, 1248, 1261; 261108.1:599–603, 652–654, 659, 728–796, 1195, 1324, 1338 |
| M | industry | N-INDUSTRY | `server/src/services/reprocessing/reprocessingStaticData.js` — 261028.1:8, 301, 303–306, 317–320; 261108.1:8, 302–304, 307, 318 |
| A | character | N-ECONOMY | `server/src/services/rewards/groupRewardCatalog.js` — 261108.1:1–35 (追加) |
| A | data | N-ECONOMY | `server/src/services/rewards/groupRewardCatalog.json` — 261108.1:1–2665 (追加) |
| A | character | N-ECONOMY | `server/src/services/rewards/groupRewardEvaluator.js` — 261108.1:1–81 (追加) |
| M | character | N-ECONOMY | `server/src/services/rewards/rewardMgrService.js` — 261028.1:10, 13, 20, 26, 43, 47, 49, 52–55; 261108.1:10–11, 15–16, 24, 30, 47–49, 53, 55, 58–67 |
| M | other | N-INDUSTRY | `server/src/services/scheduler/productionSchedulerRuntime.js` — 261028.1:9, 24, 38, 39, 206, 213, 222, 223, 306, 311, 319, 320, 374, 380, 381, 404, 597, 605, 613, 614, 662, 666, 667, 671, 713, 751, 793, 816, 1018; 261108.1:9–10, 26, 41, 43–44, 212–213, 221, 231, 233–234, 318–319, 325, 334, 336–337, 392–393, 400, 402–403, 427–430, 624–625, 634, 643, 645–646, 695–696, 701, 703–704, 709–710, 753, 792, 835–836, 860, 1063–1064 |
| M | character | N-EVENTS | `server/src/services/seasons/eventLifecycle.js` — 261028.1:27, 64, 118, 119, 131; 261108.1:27–34, 72, 126, 128, 140 |
| M | character | N-EVENTS | `server/src/services/seasons/eventSchedule.js` — 261028.1:4, 9–10, 12, 15, 21, 27; 261108.1:4, 9–14, 16–19, 23, 29–34, 41 |
| M | character | N-EVENTS | `server/src/services/seasons/installEventRuntime.js` — 261028.1:6, 13, 16; 261108.1:6, 14–17, 20 |
| A | character | N-MISSION | `server/src/services/seasons/warpathMissionCatalog.js` — 261108.1:1–28 (追加) |
| A | character | N-MISSION | `server/src/services/seasons/warpathMissionPayloads.js` — 261108.1:1–52 (追加) |
| A | character | N-MISSION | `server/src/services/seasons/warpathMissionRuntime.js` — 261108.1:1–137 (追加) |
| M | combat | H-COMBAT / N-COMBAT | `server/src/services/security/crimewatchService.js` — 261028.1:134; 261108.1:134 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/services/security/crimewatchState.js` — 261028.1:56, 58, 60, 63, 82, 84, 90–92, 96, 121, 307, 775, 1032, 1054, 1062, 1063, 1085, 1271–1290, 1383, 1402, 1414, 1497, 1499, 1505, 1937, 2330, 2345, 2382, 2410, 2498, 2515, 2523, 2531, 2540, 2645, 2736, 2744, 3008, 3016, 3041, 3073, 3198, 3587, 3880, 3900, 3902, 4072, 4084, 4319, 4421, 4549, 4566, 4610, 4676–4679, 4685, 4704, 4795, 4797, 4819, 4821, 4823, 4826, 4837, 4848–4849, 4859; 261108.1:56, 59, 62–63, 67, 87–90, 93–101, 108–113, 117–129, 155–165, 352, 820, 1078–1086, 1109, 1117–1126, 1128–1217, 1240, 1426–1433, 1526–1538, 1558–1576, 1589–1590, 1673–1677, 1680, 1686–1694, 2126–2129, 2523, 2538, 2575–2577, 2605–2607, 2695–2704, 2721, 2730, 2739, 2749–2756, 2862–2867, 2958–2965, 2974–2975, 3239–3252, 3261, 3287, 3319, 3444–3453, 3842–3984, 4278–4321, 4342–4344, 4347–4351, 4522, 4534–4542, 4778, 4880–4925, 5054, 5071, 5116–5140, 5207, 5213–5227, 5247, 5339–5340, 5343, 5366, 5369, 5372–5373, 5377, 5389–5391, 5403, 5413 |
| A | combat | H-COMBAT / N-COMBAT | `server/src/services/security/securityTax.js` — 261108.1:1–75 (追加) |
| M | other | N-OTHER | `server/src/services/ship/beyonceService.js` — 261028.1:7, 30, 95–98, 133, 135, 140, 152, 160–161, 1921, 1968–2001, 2273, 2423–2464, 3136, 3151, 3186, 3439, 3669, 3835, 3843–4140; 261108.1:7, 29–31, 97–98, 133, 134, 138–143, 156, 163, 1922, 1970, 2241–2257, 2408, 3080–3082, 3097–3105, 3141–3143, 3396, 3627–3635, 3802, 3810 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/ship/controlTowerRuntime.js` — 261028.1:561, 589–596, 605, 609; 261108.1:561–568, 596–600, 609, 613 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/ship/deployableCynoRuntime.js` — 261028.1:702, 704, 745; 261108.1:702, 705, 747 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/ship/ejectRuntime.js` — 261028.1:1324, 1332; 261108.1:1324–1398, 1407 |
| A | other | N-CORPORATION | `server/src/services/ship/mercenaryDenRuntime.js` — 261108.1:1–1141 (追加) |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/ship/mobileAnalysisBeaconRuntime.js` — 261028.1:28, 62, 63, 92, 159, 226, 256, 638, 648, 671–672, 675, 714–719, 721, 753–757, 759, 763, 774–778, 783, 801, 806, 811, 818, 828, 830, 837, 871, 885, 951, 966, 992, 998, 1070–1098, 1100, 1104, 1115–1116, 1118–1130, 1135–1136, 1146, 1162, 1178, 1219, 1221, 1230–1257, 1266–1267, 1277, 1305, 1306, 1314; 261108.1:28–34, 69, 71–77, 106, 174–246, 314–317, 348–351, 734–826, 837, 861–863, 866–882, 922–936, 938–942, 975–989, 991–996, 1001–1287, 1299–1311, 1316–1321, 1340–1341, 1347, 1353, 1360, 1370, 1372–1384, 1392–1413, 1447, 1462–1467, 1534–1535, 1550, 1576, 1582–1601, 1674, 1675, 1679, 1689–1692, 1694–1738, 1743–1744, 1754, 1770–1777, 1794–1796, 1838–1852, 1855, 1864–1883, 1892, 1902–1903, 1931, 1933–1936, 1945–1949 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/ship/mobileCynoInhibitorRuntime.js` — 261028.1:881; 261108.1:881 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/ship/mobileDepotRuntime.js` — 261028.1:1579, 1619; 261108.1:1579–1582, 1623 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/ship/mobileMicroJumpUnitRuntime.js` — 261028.1:851; 261108.1:851 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/ship/mobileObservatoryRuntime.js` — 261028.1:987; 261108.1:987 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/ship/mobilePhaseAnchorRuntime.js` — 261028.1:407; 261108.1:407 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/ship/mobileScanInhibitorRuntime.js` — 261028.1:841; 261108.1:841 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/ship/mobileSiphonUnitRuntime.js` — 261028.1:600; 261108.1:600 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/ship/mobileTractorUnitRuntime.js` — 261028.1:1320; 261108.1:1320 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/ship/mobileWarpDisruptorRuntime.js` — 261028.1:750; 261108.1:750 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/ship/orbitalLaunchRuntime.js` — 261028.1:42, 56, 111, 384, 647, 664–668; 261108.1:42, 57–63, 119–121, 395, 658–659, 677–691 |
| M | other | N-OTHER | `server/src/services/ship/safeLogoffRuntime.js` — 261028.1:14, 16–29, 113; 261108.1:14–17, 19–45, 129 |
| M | UI | H-UI / N-CHARACTER | `server/src/services/ship/shipCosmeticsMgrService.js` — 261028.1:34, 183, 187, 191, 376, 390, 406; 261108.1:34, 184–185, 190, 194–224, 410–412, 427–429, 446–448 |
| M | UI | H-UI / N-CHARACTER | `server/src/services/ship/shipCosmeticsState.js` — 261028.1:238, 245, 247, 257, 378; 261108.1:238, 245, 247–257, 268, 389 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/ship/shipService.js` — 261028.1:78, 97, 578, 1411, 1540, 1541, 1544–1546, 2139; 261108.1:78, 98, 580–583, 1417–1446, 1576–1581, 1583–1585, 1589–1591, 2184–2198 |
| M | UI | H-UI / N-CHARACTER | `server/src/services/ship/shipSkinDesignFiles.js` — 261028.1:91; 261108.1:91–118 |
| A | UI | H-UI / N-CHARACTER | `server/src/services/ship/shipSkinDesignValidation.js` — 261108.1:1–33 (追加) |
| M | UI | H-UI / N-CHARACTER | `server/src/services/ship/shipSkinDraftRuntime.js` — 261028.1:6–7, 29, 35–61, 102; 261108.1:6, 28–43, 49, 89 |
| M | UI | H-UI / N-CHARACTER | `server/src/services/ship/shipSkinState.js` — 261028.1:6, 12, 15, 16, 31; 261108.1:6–8, 14, 18–24, 26, 42 |
| M | UI | H-UI / N-CHARACTER | `server/src/services/ship/shipSkinStoreAuthority.js` — 261028.1:14, 20; 261108.1:14–23, 30 |
| A | UI | H-UI / N-CHARACTER | `server/src/services/ship/shipSkinStoreTerms.js` — 261108.1:1–50 (追加) |
| M | UI | H-UI / N-CHARACTER | `server/src/services/ship/shipSkinThirdPartyRuntime.js` — 261028.1:3, 23, 25, 155, 379; 261108.1:3, 23–26, 29–90, 221–271, 496–506 |
| A | other | N-OTHER | `server/src/services/ship/warpFailureUserError.js` — 261108.1:1–63 (追加) |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/skills/boosters/boosterRuntime.js` — 261028.1:462, 501, 502, 506–508, 527, 533–547, 551, 559, 564, 571–576, 583–589, 591–592, 605, 629, 638; 261108.1:462, 501, 503–510, 515, 534, 539, 543, 551, 556, 563, 570, 571–572, 585–607, 632–676, 686–688 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/skills/skillMgrService.js` — 261028.1:559, 569; 261108.1:559, 569 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/skills/skillState.js` — 261028.1:1127, 1129, 1131, 1145, 1146; 261108.1:1127, 1130, 1133–1136, 1150–1154, 1156–1187 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/skills/trading/skillTradingRuntime.js` — 261028.1:26, 880, 905–907; 261108.1:26, 881–892, 917 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/skills/training/skillQueueNotifications.js` — 261028.1:109–187, 325–331, 352–365, 484, 510; 261108.1:109, 246, 266–269, 388–489, 516 |
| M | character | N-CHARACTER / N-OTHER | `server/src/services/skills/training/skillQueueRuntime.js` — 261028.1:94, 327, 631, 633, 651, 658, 842, 848, 954–962, 967, 977, 1078, 1334–1342, 1381, 1427, 1431, 1433, 1438, 1441, 1460, 1480, 1482, 1512, 1553; 261108.1:94–96, 330–432, 737–739, 741–743, 762–764, 772, 956–961, 967, 1074–1084, 1089, 1099, 1200, 1457, 1496–1525, 1572–1576, 1580–1581, 1584–1588, 1594–1597, 1600, 1619, 1640–1646, 1648–1650, 1681–1683, 1724–1728 |
| M | other | N-CORPORATION | `server/src/services/sovereignty/mercenaryDenState.js` — 261028.1:8, 106, 109–116, 119–126, 152–161, 422, 441, 491, 502, 529, 573, 706, 711; 261108.1:8, 107–138, 141–142, 145–151, 177–188, 449–471, 491–492, 543, 555–557, 585, 630, 763–1126, 1132–1150 |
| A | other | N-CORPORATION | `server/src/services/sovereignty/skyhookCleanup.js` — 261108.1:1–191 (追加) |
| M | other | N-CORPORATION | `server/src/services/sovereignty/skyhookRaidRules.js` — 261028.1:26; 261108.1:26–28 |
| M | other | N-CORPORATION | `server/src/services/sovereignty/skyhookRaidRuntime.js` — 261028.1:20, 212, 216, 228, 573, 575, 577; 261108.1:20, 212–226, 231, 243–246, 591–606, 609, 611 |
| M | character | N-EVENTS | `server/src/services/sovereignty/sovCampaignNotifications.js` — 261028.1:16, 90–92, 199, 201; 261108.1:16, 91–93, 200–315, 318–322 |
| M | character | N-EVENTS | `server/src/services/sovereignty/sovCampaignState.js` — 261028.1:39, 40, 42, 1286, 1289, 1290, 1901–1904, 2208, 2516; 261108.1:39–43, 45, 47, 1291–1305, 1309, 1311, 1922–1923, 2227–2235, 2544 |
| M | other | N-CORPORATION | `server/src/services/sovereignty/sovCaptureState.js` — 261028.1:27, 431; 261108.1:27–30, 435–443 |
| M | GM command | H-GM | `server/src/services/sovereignty/sovChatCommands.js` — 261028.1:72, 679; 261108.1:72–75, 683–746 |
| M | other | N-CORPORATION | `server/src/services/sovereignty/sovFlexStructures.js` — 261028.1:244, 289, 292–293, 295, 299, 313, 444–449, 497, 502, 504; 261108.1:244–246, 291–292, 295, 297, 301–303, 317–343, 475–477, 525–526, 532–555, 558 |
| M | other | N-CORPORATION | `server/src/services/sovereignty/sovHubFuelBurn.js` — 261028.1:21, 22, 119; 261108.1:21–24, 26–42, 140 |
| M | other | N-CORPORATION | `server/src/services/sovereignty/sovModernState.js` — 261028.1:1056, 1179–1180, 1390, 2274–2353, 2481, 2484; 261108.1:1056–1062, 1185–1188, 1398–1403, 2288, 2415, 2417 |
| M | character | N-CORPORATION | `server/src/services/sovereignty/sovNotifications.js` — 261028.1:251, 327, 328; 261108.1:251–283, 360–394, 396 |
| M | other | N-CORPORATION | `server/src/services/sovereignty/sovPlayerDeployment.js` — 261028.1:800–801, 913, 957, 1074; 261108.1:800–810, 922–935, 980–984, 1102–1105 |
| M | other | N-CORPORATION | `server/src/services/sovereignty/sovSpaceInterop.js` — 261028.1:10, 278, 287, 300–301, 311; 261108.1:10, 279–283, 293, 306–313, 323–329 |
| M | other | N-CORPORATION | `server/src/services/sovereignty/sovState.js` — 261028.1:51, 662, 696, 698, 1567; 261108.1:51, 663–700, 735, 738–742, 1612 |
| A | data | N-OTHER | `server/src/services/spaceComponents/appliedProximityEffectAuthority.json` — 261108.1:1–22 (追加) |
| A | other | N-OTHER | `server/src/services/spaceComponents/appliedProximityEffectsRuntime.js` — 261108.1:1–137 (追加) |
| A | market | N-MARKET | `server/src/services/spaceComponents/nativeItemTraderAuthority.json` — 261108.1:1–28 (追加) |
| A | market | N-MARKET | `server/src/services/spaceComponents/nativeItemTraderRuntime.js` — 261108.1:1–173 (追加) |
| A | data | H-OTHER / N-STRUCTURE | `server/src/services/starbase/controlTowerResourceAuthority.json` — 261108.1:1–56 (追加) |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/starbase/starbaseReinforcementRuntime.js` — 261028.1:57–58, 60, 62–63; 261108.1:57–60, 62, 64 |
| M | character | H-CHARACTER / N-CHARACTER | `server/src/services/station/jumpCloneRuntime.js` — 261028.1:41, 152, 168, 1857–1861, 1895, 1918–1920, 2260, 2413, 2425, 2781, 2835; 261108.1:41, 151, 167, 1856, 1889–1890, 1913–1916, 2256, 2410–2412, 2425, 2781, 2836–2838 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/station/repairRuntime.js` — 261028.1:36, 39, 580, 618, 899, 1294, 1327, 1333–1334, 1339, 1347; 261108.1:36, 40, 582–585, 623–627, 909, 1304–1309, 1342, 1348–1349, 1354, 1362 |
| M | industry | H-INDUSTRY | `server/src/services/structure/moonExtractionsService.js` — 261028.1:1081, 1265; 261108.1:1081–1088, 1273–1342 |
| M | industry | H-INDUSTRY | `server/src/services/structure/moonMiningSpaceRuntime.js` — 261028.1:28, 38, 101, 191, 192, 223, 225–226, 228, 230, 236–237, 240, 243–246, 248, 255, 520, 527; 261108.1:28–30, 41–45, 109–112, 203, 205–345, 377–407, 409, 411, 413, 419–420, 423–428, 431–436, 438–445, 453–459, 725, 733 |
| A | character | H-OTHER / N-STRUCTURE | `server/src/services/structure/structureAccessNotifications.js` — 261108.1:1–26 (追加) |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/structure/structureAssetSafetyState.js` — 261028.1:2741, 2745, 3044; 261108.1:2741–2743, 2748, 3047–3048 |
| M | GM command | H-GM / N-GM | `server/src/services/structure/structureChatCommands.js` — 261028.1:382, 941; 261108.1:382–412, 972 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/structure/structureConstants.js` — 261028.1:196; 261108.1:196 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/structure/structureCynoBeaconMgrService.js` — 261028.1:15, 19, 31, 488, 603, 614, 616–620, 649, 656–663; 261108.1:15, 19–20, 33, 490–501, 617, 628, 629, 657, 664–700 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/structure/structureDeliveriesService.js` — 261028.1:156; 261108.1:156 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/structure/structureDeliverySaga.js` — 261028.1:276; 261108.1:276–296 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/structure/structureDestructionLootState.js` — 261028.1:14, 227, 228, 294, 296, 304; 261108.1:14, 228–249, 251–252, 318–361, 363, 371 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/structure/structureDockedRecoveryState.js` — 261028.1:119–131; 261108.1:119–134 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/structure/structureFlexFuelRuntime.js` — 261028.1:38, 342, 400, 445, 464, 573, 605, 682, 705, 738, 772, 844, 856, 865, 888, 907, 918; 261108.1:38–40, 345, 403, 448, 467, 576, 608, 685, 708, 741, 775, 847, 859, 868, 891, 910, 921 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/structure/structureProfilesState.js` — 261028.1:31, 603, 615, 651, 655, 671, 685, 740, 786, 933, 974, 1026, 1097; 261108.1:31, 604–606, 619, 656–658, 663, 680–682, 697–701, 756, 802, 949–975, 1017, 1069–1072, 1143–1144 |
| A | other | H-OTHER / N-STRUCTURE | `server/src/services/structure/structureRemovedFromSpaceNotice.js` — 261108.1:1–61 (追加) |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/structure/structureSelfDestructRuntime.js` — 261028.1:29, 102, 113, 114, 155, 193; 261108.1:29–46, 120, 132, 134–144, 186, 225 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/structure/structureServiceModules.js` — 261028.1:780, 861, 900, 907, 909, 941; 261108.1:780–854, 936–943, 983–987, 995, 998–1000, 1033 |
| M | other | H-OTHER / N-STRUCTURE | `server/src/services/structure/structureState.js` — 261028.1:841, 888, 2321, 2347–2348, 2438, 2464, 2564, 2823, 3113, 3114, 3156, 3284, 3287, 3302, 3315, 3332, 3438, 3486, 3542, 3587, 3904, 3948, 4019, 4180, 4458, 4632, 4635, 4652, 4779; 261108.1:841–842, 890, 2324–2326, 2353–2355, 2445–2464, 2491, 2591, 2850–2851, 3142–3176, 3178–3181, 3224–3235, 3364–3365, 3369–3370, 3386–3391, 3405, 3423–3430, 3537, 3586–3596, 3653, 3698–3727, 4045–4048, 4093–4105, 4177–4180, 4342, 4621–4625, 4800–4817, 4821, 4839, 4966 |
| A | character | H-OTHER / N-STRUCTURE | `server/src/services/structure/structureTimerInventoryFanout.js` — 261108.1:1–211 (追加) |
| M | other | N-OTHER | `server/src/services/support/publicQaToolsServerService.js` — 261028.1:50; 261108.1:50–59 |
| M | other | N-INCURSION | `server/src/services/tale/taleMgrService.js` — 261028.1:9–11, 47; 261108.1:9, 44 |
| M | other | N-INCURSION | `server/src/services/tale/taleSessionNotices.js` — 261028.1:23, 24; 261108.1:23–24, 26–30 |
| A | character | H-CHARACTER / N-CHARACTER | `server/src/services/user/mctCertificateActivation.js` — 261108.1:1–214 (追加) |
| M | character | H-CHARACTER / N-CHARACTER | `server/src/services/user/userService.js` — 261028.1:62, 193, 273, 303, 307, 515, 516, 518, 744, 755, 768–771, 773–774, 776–777, 824, 837–840, 848–849, 852, 867, 1037–1043; 261108.1:62, 194–221, 302, 333, 337, 545, 547, 549–557, 784, 796, 809–812, 814–818, 820–822, 869–898, 911, 918, 920, 935, 1105 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/abyssalEngineAdapter.js` — 261028.1:145; 261108.1:145–146 |
| M | exploration | N-EXPLORATION | `server/src/space/asteroids/asteroidService.js` — 261028.1:15, 1095, 1101–1104, 1108, 1109, 1117, 1129–1131, 1137–1138, 1150–1151, 1188, 1194; 261108.1:15, 1096–1130, 1136–1148, 1152, 1154–1160, 1168, 1180–1202, 1208–1211, 1223–1224, 1261, 1266 |
| A | exploration | N-EXPLORATION | `server/src/space/asteroids/beltV1Geometry.json` — 261108.1:1 (追加) |
| A | exploration | N-EXPLORATION | `server/src/space/asteroids/beltV1GeometryMigration.js` — 261108.1:1–37 (追加) |
| A | exploration | N-EXPLORATION | `server/src/space/asteroids/naturalBeltAuthority.js` — 261108.1:1–20 (追加) |
| A | exploration | N-EXPLORATION | `server/src/space/asteroids/naturalBeltComposition.json` — 261108.1:1–40942 (追加) |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/combat/airCombatPolicy.js` — 261028.1:19, 24; 261108.1:19–43, 49 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/combat/damage.js` — 261028.1:144, 263, 268, 344; 261108.1:144–145, 265–266, 272, 348–357 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/combat/killmailTracker.js` — 261028.1:508; 261108.1:508–509 |
| A | combat | H-COMBAT / N-COMBAT | `server/src/space/combat/visionsCombatPolicy.js` — 261108.1:1–54 (追加) |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/combat/weaponDogma.js` — 261028.1:613, 962, 1507; 261108.1:613, 962, 1507 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/defaultEmpireScenery.js` — 261028.1:308, 349, 393; 261108.1:308–310, 352–354, 399–401 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/destiny/commands/drone.js` — 261028.1:2, 38; 261108.1:2–10, 47 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/destiny/commands/warpCommands.js` — 261028.1:33, 34, 453, 479, 749, 1045, 1051, 1440, 1445, 1455; 261108.1:33, 35–47, 467, 494, 765, 1062–1076, 1083, 1472, 1478, 1488 |
| A | combat | N-MISSION | `server/src/space/destiny/commands/warpTransmissionReceipt.js` — 261108.1:1–5 (追加) |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/destiny/index.js` — 261028.1:173–174, 392, 395, 426, 636, 769, 881, 899; 261108.1:173–177, 395–402, 406–409, 441–451, 662–666, 800–802, 914–918, 937–950 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/destiny/presentation/activeSpecialFxReplay.js` — 261028.1:93, 186, 187, 189, 284, 285, 287; 261108.1:93–101, 195–199, 201–203, 205, 300–304, 306–308, 310 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/destiny/simulation/evolve.js` — 261028.1:931, 1224; 261108.1:931–936, 1230 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/destiny/simulation/warpJitter.js` — 261028.1:19, 43, 60, 63; 261108.1:19–20, 45–49, 67, 71 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/destiny/stream/ballEncoding.js` — 261028.1:444, 541, 663, 725; 261108.1:444–445, 542, 664, 725 |
| A | combat | H-COMBAT / N-COMBAT | `server/src/space/encounterSurveillanceSystem.js` — 261108.1:1–278 (追加) |
| M | combat | H-UI / N-CHARACTER | `server/src/space/gateSkinCommand.js` — 261028.1:69–74, 204–210, 218–219, 225, 239; 261108.1:69–76, 206–207, 215–216, 222, 236 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/modules/assistanceModuleRuntime.js` — 261028.1:48, 126, 175, 1356, 1406, 1425, 1468, 1536, 1537, 1604, 1753, 1772, 1881, 1944, 1976, 1977; 261108.1:48, 127–131, 181–189, 1371–1376, 1427, 1447, 1490–1491, 1560, 1562–1596, 1663–1665, 1815, 1835–1839, 1948, 2012–2014, 2047–2152, 2154–2155 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/modules/hostileModuleRuntime.js` — 261028.1:124, 132, 375, 810, 830, 890, 1427, 1470, 1536, 1786, 1789, 1835; 261108.1:124, 133–141, 385, 821–822, 842, 902–903, 1440, 1483, 1549–1551, 1801–1807, 1811, 1856 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/modules/hudIconRuntime.js` — 261028.1:15, 18, 173, 184, 201, 261, 269; 261108.1:15, 19–23, 179, 191, 209–235, 296, 305 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/modules/jammerModuleRuntime.js` — 261028.1:28, 630, 674, 691, 739, 783, 907, 914, 1197; 261108.1:28, 631, 675–677, 695, 743, 787, 912–913, 921, 1205–1221 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/modules/salvagerRuntime.js` — 261028.1:1451, 1477; 261108.1:1451–1458, 1485–1495 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/npc/beltRatRuntime.js` — 261028.1:32, 255, 523, 557, 1124, 1349, 1371, 1373, 1394; 261108.1:32, 256, 525–542, 576, 1143–1144, 1369, 1391–1427, 1430, 1452 |
| A | combat | H-COMBAT / N-COMBAT | `server/src/space/npc/empireSecurity/empireSentryRuntime.js` — 261108.1:1–125 (追加) |
| A | combat | N-INCURSION | `server/src/space/npc/incursionDeathWrecks.js` — 261108.1:1–119 (追加) |
| A | combat | H-COMBAT / N-COMBAT | `server/src/space/npc/nativeNpcAbilities.js` — 261108.1:1–109 (追加) |
| A | combat | H-COMBAT / N-COMBAT | `server/src/space/npc/nativeNpcDeathWrecks.js` — 261108.1:1–155 (追加) |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/npc/nativeNpcService.js` — 261028.1:56, 63, 658, 673–676, 683, 1292, 1340, 1476, 1482, 1534, 1556, 1562, 1565, 1586, 1591, 1610, 1612, 1618, 1665, 1667–1668, 1674, 1677, 1682, 1779, 1797, 1801, 1814, 1820, 1855, 1879–1881, 1912, 1925, 1968, 2019–2025, 2028, 2031, 2057, 2293, 2316, 2377, 2435, 2436; 261108.1:56–58, 66–68, 664, 678–679, 686, 1294, 1343, 1480–1516, 1523, 1575–1578, 1601–1607, 1614, 1617–1626, 1647–1677, 1683, 1703, 1704–1709, 1716–1723, 1771–1781, 1784–1785, 1791, 1794, 1799, 1896–2219, 2238, 2243, 2256, 2262–2265, 2301–2313, 2338–2340, 2371–2373, 2386, 2430–2437, 2488–2507, 2510–2517, 2521–2525, 2552–2613, 2850–2855, 2879–2902, 2964–2965, 3023–3030, 3032–3034 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/npc/nativeNpcStore.js` — 261028.1:136, 484, 791, 862, 865, 1014, 1018, 1020, 1027, 1034–1035, 1292; 261108.1:136, 485–486, 794–797, 869–870, 873–874, 1024–1060, 1065–1105, 1108–1109, 1116, 1123–1124, 1381–1382 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/npc/nativeNpcWreckService.js` — 261028.1:442, 525, 526, 527, 529, 533, 625, 862, 873, 991–1073, 1075–1107, 1176, 1181; 261108.1:442–447, 531, 533, 535, 536–538, 542, 634–652, 890–1077, 1089–1125, 1244–1253, 1255–1257, 1326–1327, 1333 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/npc/npcBehaviorLoop.js` — 261028.1:13, 87, 1110, 1573, 1698, 1726, 2189, 3088, 3089, 3091, 3093, 3414, 3486, 3654, 3656, 3681, 3692, 3694, 3729, 3731, 3733, 4748, 5090, 5111, 5116, 5124, 5242, 5531, 5807, 5808, 5828; 261108.1:13–15, 90–92, 1116, 1580–1584, 1710, 1739, 2203–2208, 3107–3108, 3110, 3112, 3114, 3435–3440, 3513–3516, 3685, 3687–3690, 3715–3720, 3732–3736, 3739, 3774–3776, 3779, 3781–3782, 4798–4869, 5212–5214, 5236–5306, 5312–5321, 5330–5345, 5464–5471, 5760–5762, 6039–6040, 6042, 6063–6065 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/npc/npcEquipment.js` — 261028.1:56, 436, 495, 845, 966, 972, 1179; 261108.1:56, 437–443, 502–506, 857, 979, 985–986, 1194 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/npc/npcLoot.js` — 261028.1:304; 261108.1:304–309 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/npc/npcRuntime.js` — 261028.1:15, 99, 101, 763; 261108.1:15–18, 103–112, 115, 777 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/npc/npcSentryPolicy.js` — 261028.1:4, 85, 112, 209; 261108.1:4, 86–90, 118, 215–216 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime.js` — 261028.1:1044; 261108.1:1044–1047 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/activationSpaceRestrictions.js` — 261028.1:193; 261108.1:193–196 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/activeShipModifiers.js` — 261028.1:305; 261108.1:305–308 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/attributeChangeNotification.js` — 261028.1:122, 219, 558–561, 576–579, 594–597, 687–691; 261108.1:122, 220–221, 561–563, 578–580, 595–597, 687–689 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/combatDamageMessages.js` — 261028.1:178; 261108.1:178–189 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/combatDestruction.js` — 261028.1:237, 259, 337, 338, 339, 342, 343, 520, 521, 532, 535, 580, 617, 630, 663; 261108.1:237–246, 268, 347–349, 351, 353–358, 361, 363–368, 545–546, 548, 559, 563, 608, 646, 659–666, 700–708 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/commandBurst.js` — 261028.1:326, 330; 261108.1:326, 330–332 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/crimewatchAdapter.js` — 261028.1:39, 45; 261108.1:39, 45–54 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/entityScopeTags.js` — 261028.1:120, 131, 156, 324, 335, 358; 261108.1:120, 132, 158–161, 330, 342, 366–369 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/entitySpacePersistence.js` — 261028.1:391; 261108.1:391–393 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/hudIconStates.js` — 261028.1:6, 23, 59, 132, 265, 266, 269, 518, 521; 261108.1:6, 24, 61–107, 181–226, 360, 362, 365, 615, 618–624 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/inventoryLootRights.js` — 261028.1:98, 101, 104, 107; 261108.1:98, 101, 104, 107 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/inventorySpaceEntity.js` — 261028.1:26, 58, 308–312, 346, 376, 953; 261108.1:26, 59, 310–318, 352, 383–385, 963 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/microJumpDriveEligibility.js` — 261028.1:139; 261108.1:139 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/missileEntity.js` — 261028.1:421, 565; 261108.1:421–422, 566 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/moduleAttributes.js` — 261028.1:7, 273, 397, 406, 434, 446, 455, 464, 471, 525, 530; 261108.1:7, 274–292, 417, 426, 454, 466, 475, 484, 491, 545, 550 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/moduleEffectShutdown.js` — 261028.1:361, 362, 365, 399, 400, 403; 261108.1:361, 363, 366, 401, 403, 406 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/moduleHeat.js` — 261028.1:984, 985; 261108.1:984, 986 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/moduleRuntimeCallbacks.js` — 261028.1:346, 384, 437; 261108.1:346, 384–389, 442 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/moduleSpecialFx.js` — 261028.1:136; 261108.1:136–138 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/nativeRecordSceneOwnership.js` — 261028.1:77; 261108.1:77–78 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/propulsionModule.js` — 261028.1:52, 364–368, 386–389; 261108.1:52–53, 366, 383–386 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/scene/abyssalTrace.js` — 261028.1:620–621, 644; 261108.1:620–621, 644–654 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/scene/cloaking.js` — 261028.1:256; 261108.1:256–260 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/scene/destinyHistory.js` — 261028.1:141, 145–147, 151, 153–159; 261108.1:141–143, 148–149, 153–177, 180 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/scene/dynamicEntities.js` — 261028.1:30, 612, 647; 261108.1:30, 613–614, 650 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/scene/evolve.js` — 261028.1:2, 173, 194, 401, 419, 439, 456, 471; 261108.1:2–3, 175–176, 198–212, 420–445, 464–484, 505, 523–525, 541–546 |
| M | combat | N-MISSION | `server/src/space/runtime/scene/missionAccess.js` — 261028.1:2, 56, 165, 175, 178–179, 202, 250, 253, 335–343, 352, 399; 261108.1:2–3, 58–61, 171–173, 184, 187–188, 211–214, 263, 267–268, 351–354, 363–377, 424–429 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/scene/modules.js` — 261028.1:149, 222, 388, 673, 734, 1133, 1364, 1475–1486, 1488–1506, 1568, 2394, 2395, 2710, 3095, 3177, 3343, 3428, 3566, 3672, 3673, 3676, 3710, 3711, 3714; 261108.1:149, 221, 388, 674–677, 739–740, 1139–1142, 1374–1377, 1489–1507, 1509–1515, 1577–1584, 2411, 2413, 2728–2732, 3118, 3201, 3368–3377, 3463–3474, 3613, 3720, 3722, 3725, 3760, 3762, 3765 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/scene/pilotCommands.js` — 261028.1:123, 362, 382–387, 454, 536; 261108.1:123–124, 364–368, 388–395, 462–474, 557 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/scene/presentation.js` — 261028.1:402; 261108.1:402–419 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/scene/sceneFeatureHooks.js` — 261028.1:66, 89, 235, 279; 261108.1:66–68, 92–97, 244–252, 297–302 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/scene/sessions.js` — 261028.1:138–142, 150, 155–162, 184–185, 187–212, 239, 260, 264–271, 300–306, 308, 326–331, 334, 338, 341, 347–348, 352, 1002, 1043, 1047, 1268; 261108.1:138–139, 147–150, 156, 178, 180–188, 215, 236–239, 244, 273–274, 276, 295–296, 299, 303, 306, 312–313, 317, 967, 1009–1011, 1016–1018, 1240 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/scene/targeting.js` — 261028.1:365, 368; 261108.1:365–367, 371 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/scene/tick.js` — 261028.1:183, 334, 397, 571, 1808, 1891, 2429, 2452; 261108.1:183, 335, 399, 574–576, 1814, 1898, 2437–2443, 2467–2471 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/shipCloak.js` — 261028.1:29, 582–585; 261108.1:29, 583–596 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/shipEntity.js` — 261028.1:394; 261108.1:394–395 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/shipHealthState.js` — 261028.1:214, 382, 384, 388–389; 261108.1:214–216, 385, 387, 391–394 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/siteHazardDamage.js` — 261028.1:115; 261108.1:115–119 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/solarSystemScene.js` — 261028.1:26, 177, 322; 261108.1:26, 178–184, 330 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/spaceRuntime/sessions.js` — 261028.1:128, 271, 483; 261108.1:128–140, 284, 497 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/staticSceneEntity.js` — 261028.1:38, 96, 434, 436–437, 523, 531, 534, 579, 663, 665; 261108.1:38, 97–98, 437–503, 506–510, 596–597, 606, 610, 655–656, 741–742, 744–745 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/structureLifecycleFanout.js` — 261028.1:39, 63, 68, 69, 470, 477–512; 261108.1:39, 64–65, 71, 73, 475, 482–484 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/targetDamageApplication.js` — 261028.1:29, 34, 94, 137, 149, 271, 285, 290, 315, 318–321, 347, 371, 378–380, 382, 507, 524–526, 530, 536, 553; 261108.1:29, 35, 95–96, 140–143, 156–158, 281–290, 305–311, 317–320, 346–347, 350, 376–382, 407–413, 421, 423–428, 554–555, 573–575, 579–605, 612–615, 633–653 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/targeting.js` — 261028.1:38; 261108.1:38 |
| A | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/visionsEwarNotice.js` — 261108.1:1–85 (追加) |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/weaponCycle.js` — 261028.1:351, 903–910; 261108.1:351–352, 904–905 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/runtime/weaponSnapshot.js` — 261028.1:244; 261108.1:244–251 |
| M | combat | H-COMBAT / N-COMBAT | `server/src/space/shipDestruction.js` — 261028.1:80, 82, 179, 183, 204, 744, 969, 1834, 1837; 261108.1:80, 82, 179–182, 187, 208, 748–756, 982–984, 1850–1857, 1861–1862 |
| M | other | N-VOID / N-OTHER | `server/src/space/transitions.js` — 261028.1:2875, 3105, 3109, 3497, 4641, 4760, 4774, 4837, 4854; 261108.1:2875–2878, 3109–3111, 3116, 3505, 4650–4657, 4777, 4791–4794, 4857, 4874–4876 |
| M | data | N-DATA | `config/gameplay.json` — 261028.1:25, 47; 261108.1:25, 47 |
| M | data | H-INDUSTRY | `config/mining.json` — 261028.1:68; 261108.1:68 |
| M | data | N-DATA | `config/server.json` — 261028.1:6; 261108.1:6 |
| M | data | N-DATA | `config/version.json` — 261028.1:2; 261108.1:2 |
| M | data | N-DATA | `config/world.json` — 261028.1:2, 24; 261108.1:2–4, 27–29 |
| D | data | N-AIR | `content-packs/evejs-air-tutorial-1.0.0.json` — 261028.1:1–3383 (削除) |
| D | data | N-AIR | `content-packs/evejs-air-tutorial-1.0.1.json` — 261028.1:1–3605 (削除) |
| A | data | N-AIR | `content-packs/evejs-air-tutorial-1.0.11.json` — 261108.1:1–3892 (追加) |
| D | data | N-AIR | `content-packs/evejs-air-tutorial-1.0.2.json` — 261028.1:1–3751 (削除) |
| D | data | N-AIR | `content-packs/evejs-air-tutorial-1.0.3.json` — 261028.1:1–3898 (削除) |
| A | data | N-DATA | `content-packs/evejs-career-agents-1.4.4.json` — 261108.1:1–174347 (追加) |
| A | data | N-DATA | `content-packs/evejs-fractured-legacy-1.0.2.json` — 261108.1:1–6619 (追加) |
| A | combat | N-INCURSION | `content-packs/evejs-incursions-1.0.0.json` — 261108.1:1–44916 (追加) |
| A | data | N-MISSION | `content-packs/evejs-security-missions-level-1-1.1.10.json` — 261108.1:1 (追加) |
| A | data | N-MISSION | `content-packs/evejs-security-missions-level-2-1.1.9.json` — 261108.1:1 (追加) |
| A | data | N-MISSION | `content-packs/evejs-security-missions-level-3-1.1.9.json` — 261108.1:1 (追加) |
| A | data | N-MISSION | `content-packs/evejs-security-missions-level-4-1.1.9.json` — 261108.1:1 (追加) |
| A | data | N-MISSION | `content-packs/evejs-security-missions-level-5-1.1.9.json` — 261108.1:1 (追加) |
| A | data | N-DATA | `content-packs/evejs-visions-of-greatness-1.0.4.json` — 261108.1:1–35568 (追加) |
| D | data | H-EXPLORATION / N-EXPLORATION | `content-packs/evejs-wormhole-sites-1.2.0.json` — 261028.1:1–816901 (削除) |
| A | data | H-EXPLORATION / N-EXPLORATION | `content-packs/evejs-wormhole-sites-1.2.1.json` — 261108.1:1–821791 (追加) |
| M | other | N-TOOLS | `tools/ClientSETUP/scripts/Test-EvEJSResources.ps1` — 261028.1:15–16, 20–39, 41–44; 261108.1:15, 19, 21 |
| M | other | N-TOOLS | `tools/DatabaseCreator/database-creator.js` — 261028.1:8, 1791, 1932–1961, 2439, 2440–2447, 3009, 3096, 3363; 261108.1:8, 1792–1795, 1937, 2414–2420, 2422–2429, 2991, 3078, 3345 |
| A | data | N-DATA | `tools/DatabaseCreator/staticTables/characterCreationSchools/data.json` — 261108.1:1–89 (追加) |
| M | data | N-DATA | `tools/DatabaseCreator/staticTables/dungeonAuthority/data.json` — 261028.1:1–2314801; 261108.1:1–2314837 (大型JSON; 全行参照; 差分ツールはバイナリと判定) |
| M | data | H-UI / N-CHARACTER | `tools/DatabaseCreator/staticTables/evermarksCatalog/data.json` — 261028.1:11, 7358, 7374, 16371, 17194, 17195; 261108.1:11–24, 7371–7406, 7423–7440, 16438–16503, 17327–17330, 17332–17334 |
| M | data | N-EXPLORATION | `tools/DatabaseCreator/staticTables/explorationAuthority/data.json` — 261028.1:1–208081; 261108.1:1–208202 (大型JSON; 全行参照; 差分ツールはバイナリと判定) |
| M | data | H-EXPLORATION / N-EXPLORATION | `tools/DatabaseCreator/staticTables/explorationWormholeStatic/data.json` — 261028.1:1–206889; 261108.1:1–207010 (大型JSON; 全行参照; 差分ツールはバイナリと判定) |
| M | data | H-COMBAT / N-COMBAT | `tools/DatabaseCreator/staticTables/npcBehaviorProfiles/data.json` — 261028.1:1–185038; 261108.1:1–185166 (大型JSON; 全行参照; 差分ツールはバイナリと判定) |
| M | data | H-COMBAT / N-COMBAT | `tools/DatabaseCreator/staticTables/npcLootTables/data.json` — 261028.1:125; 261108.1:125–163 |
| M | data | H-COMBAT / N-COMBAT | `tools/DatabaseCreator/staticTables/npcProfiles/data.json` — 261028.1:1–147308; 261108.1:1–148038 (大型JSON; 全行参照; 差分ツールはバイナリと判定) |
| M | data | H-COMBAT / N-COMBAT | `tools/DatabaseCreator/staticTables/npcSpawnGroups/data.json` — 261028.1:192–204, 207, 211–270; 261108.1:192–196, 199, 203 |
| A | other | N-TOOLS | `tools/Maintenance/repair-sov-prospecting-names.js` — 261108.1:1–184 (追加) |
| M | other | N-TOOLS | `tools/Setup/Setup-EvEJS.ps1` — 261028.1:90, 93, 785–787, 790, 799, 820, 834–836, 849, 855; 261108.1:90, 93, 786–789, 792, 801–804, 826–827, 842–845, 858, 864–868 |
| M | market | H-MARKET / N-MARKET | `tools/market-seed/BuildMarketSeed.bat` — 261028.1:48; 261108.1:48–50 |
| M | market | H-MARKET / N-MARKET | `tools/market-seed/MarketSeedGui.ps1` — 261028.1:208, 458; 261108.1:208, 458 |
| M | market | H-MARKET / N-MARKET | `tools/market-seed/README.md` — 261028.1:20; 261108.1:20 |
| M | market | H-MARKET / N-MARKET | `tools/market-seed/src/main.rs` — 261028.1:2254; 261108.1:2254 |
| M | market | H-MARKET / N-MARKET | `tools/market-seederv2/BuildMarketSeedV2.bat` — 261028.1:8, 38, 41, 45, 60, 69, 76, 83; 261108.1:8–16, 47, 50–58, 62, 77, 86, 93, 100 |
| M | market | H-MARKET / N-MARKET | `tools/market-seederv2/README.md` — 261028.1:73; 261108.1:73–75 |
| M | market | H-MARKET / N-MARKET | `tools/market-seederv2/src/main.rs` — 261028.1:148, 225, 235, 559–561, 1144, 1212, 1534, 2012; 261108.1:148–149, 227, 238, 563–568, 1151–1158, 1227, 1549, 2027–2180 |
| M | market | H-MARKET / N-MARKET | `tools/market-seederv3/README.md` — 261028.1:498–499; 261108.1:498 |
| A | market | H-MARKET / N-MARKET | `tools/market-seederv4/BuildMarketSeedV4.bat` — 261108.1:1–4 (追加) |
| A | market | H-MARKET / N-MARKET | `tools/market-seederv4/README.md` — 261108.1:1–50 (追加) |
| A | market | H-MARKET / N-MARKET | `tools/market-seederv4/config/market-seederv4.local.toml` — 261108.1:1–21 (追加) |
