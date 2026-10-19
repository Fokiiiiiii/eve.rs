# EveJSにあってeve.rsにない応答 — 日本語summary

## 1. 監査で分かったことと、数字の読み方

**登録名があること、同じRPC名があることだけでは、応答互換とは判断していません。** このsummaryは672行の詳細ledgerを読む入口です。全field・状態更新・通知・実client操作まで確認できていない項目は、引き続き未確認です。

基準は `C:\Users\yutai\eve.rs`、`main`、HEAD `dda03ca0464043822e68c8348604ff34eadedcac`。元監査の開始HEADは `52b8421f` で、最終集計には親の作業中RPC宣言も含みます。参照はEveJS `828823a` とV24.01 eve-logicです。このsummary作成時も同じroot・branch・HEADを確認しました。

- **RS側coverage：181サービス名、1,280 RPC経路。** aliasを除く実装の名前は177、RPCの組合せは1,212。PostgreSQL・SDE・spaceを含むfull profileのソース集計で、実運転中の登録数を測った値ではありません。
- **詳細ledger：672行＝named出口差476行＋protobuf候補182行＋返値・通知・session・providerの14群。** 同じhandlerを継承するaliasの重複は除いています。
- **Hostの詳細source比較は8 RPC、その他1,272経路の応答bodyはこの担当では未比較。** 他担当の詳細比較を重複加算していません。実動のEveJS対RS比較・実client確認は0です。

根拠と全登録一覧は [Host監査と最終集計](../../../docs/project/service-response-audit/host-protobuf.md:432)、個々の不足・差分は [詳細ledger](../../../docs/project/service-response-audit/evejs-only-responses.md:246) を参照してください。以下の「確定」は、特記しない限り**ソース上の登録・出口・producerの差を確認した**という意味です。破損を実機で再現した意味ではありません。

### サービス11件と候補29件は別の母集団

EveJSがclientへ広告する143名のうち、RSのfull profileにない名前は**11件**です。objectCaching、structure、loadService、planetOrbitalRegistryBroker、LSC、certificateMgr、tutorialSvc、bookmarkMgr、ownerGroupManager、contractMgr、publicGatewaySvcが該当します。

一方、EveJSのconstructor・継承・re-export・bootstrap aliasまで広げた登録候補は210名で、RSにない**候補29名**があります。これは前記11件を含む数で、追加候補は18名です。29件の実運転でのconstructor成功・利用状況は未確認であり、11＋29＝40件とは数えません。[サービス境界の一覧](../../../docs/project/service-response-audit/evejs-only-responses.md:27)

特に `publicGatewaySvc` はV24ではclient-localのgRPC adapterです。MachoNet広告の名前がないだけで、remoteサービス破損とは断定できません。EveJSの `send_request` fallbackもlegacy枠です。[EJS-0355](../../../docs/project/service-response-audit/evejs-only-responses.md:606)

## 2. named出口差476行の分類と、利用者への影響

### named出口未実装448件／client-local対応3件／legacy未確認25件

**448件**をnamed出口未実装に分類しています。業務全体の未実装ではなく、残る別API代替・動的状態・実client影響は未確認です。core-a-f新handoffによって、旧不足455件からclient-local対応1件とlegacy 6件を移しました。

**client-local対応3件**は、agentMgr.GetAgentByIDとshipCosmeticsMgrの綴り違い2件です。Agent local lookupは既存GetAgentsを読み、cosmetics wrapperは既存GetLicencedSkins系へforwardします。旧RPC名は未登録でも、その通常client経路をnamed未実装には数えません。動的materialize・通知・全値の一致までは未確認です。[EJS-0015](../../../docs/project/service-response-audit/evejs-only-responses.md:266)、[EJS-0424](../../../docs/project/service-response-audit/evejs-only-responses.md:675)、[EJS-0425](../../../docs/project/service-response-audit/evejs-only-responses.md:676)

**legacy・未確認25件**の内訳は、ProjectDiscovery 15、authentication 2、publicGateway 1、skillMgr 1に加え、agentMgr 2、shipKillCounter 1、paperDollServer 1、planetMgr 2です。新handoffで通常client到達未確認となった6件を分離しました。legacyは不要・実装済みという意味ではありません。[core-a-f revised 44-row handoff](../../../docs/project/service-response-audit/core-a-f.md:345)

補足：この25件はledgerの分類であり、既存TODOのNative trace根拠を無効にしません。ProjectDiscovery.initialize_tutorial_statusとskillHandler.CheckAndSendNotificationsには既存のstartup/bound呼出し記録があるため、そのOPENは保持します。skillHandlerはskillMgrへの対応関係と合わせて一つの問題として追います。分類変更や全機能完了をこの文書reviewだけで宣言しません。

### 登録・boundの分類別集計

- **分類1・サービスなし：144 RPC行。** named未実装143行＋legacy 1行。11件／候補29件というサービス数とは単位が異なります。
- **分類2・export出口なし：476行全体。** 登録済みサービス側332行＋サービスなし側144行。その332行はnamed未実装305／client-local対応3／legacy 24です。
- **分類3・bound後続・binder不足：39行。** 476行の内数で、39件を追加加算しません。内訳はnamed未実装37／legacy 2です。更新したV24列ではcaller-source根拠17行、未確認22行です。[EJS-0306](../../../docs/project/service-response-audit/evejs-only-responses.md:557)、[EJS-0203](../../../docs/project/service-response-audit/evejs-only-responses.md:454)

named RPC表のV24列全体では、caller／forwarding-source根拠が40行、未確認が436行です。40行はmissing出口のcaller記録37行とclient-local代替3行で、実動比較済み件数ではありません。これはこの列に反映したsource根拠の数であり、他のNative trace・feature監査を含む「全client調査件数」ではありません。完全な引数・返値field・副作用・再接続互換を40件確認した、という意味でもありません。

### 代表的なclient操作

- **mapのpilot人数表示**：V24は3要素の結果を読みます。`machoNet.GetClusterGameStatisticsForClient` は、現在のキャラクターセッションをsystem別・dock状態別に集計し、EVEキーに対して2つのdictと除数1を返します。対象外のcluster keyにはcallerのdefaultを返します。[EJS-0220](../../../docs/project/service-response-audit/evejs-only-responses.md:471)
- **character選択画面のbiomass**：削除予約はFILETIME、削除は結果を受けて次の表示へ進みます。`PrepareCharacterForDelete`／`DeleteCharacter` の出口不足をcaller付きで記録しています。[EJS-0082](../../../docs/project/service-response-audit/evejs-only-responses.md:333)、[EJS-0076](../../../docs/project/service-response-audit/evejs-only-responses.md:327)
- **惑星施設の編集・物資操作**：惑星一覧が返っても、planetをbindして情報を読み、network変更後のcolonyを受け取る後続処理が必要です。そのbinder・後続出口の不足を別に追っています。[EJS-0306](../../../docs/project/service-response-audit/evejs-only-responses.md:557)、[EJS-0296](../../../docs/project/service-response-audit/evejs-only-responses.md:547)、[EJS-0311](../../../docs/project/service-response-audit/evejs-only-responses.md:562)
- **Agentのjournal・replay**：Agent初回bindの修正は反映済みですが、journal取得やreplay timestamp等の不足は別件です。[EJS-0020](../../../docs/project/service-response-audit/evejs-only-responses.md:271)、[EJS-0022](../../../docs/project/service-response-audit/evejs-only-responses.md:273)
- **cached objectの取得**：cache miss時のremote callerはあります。ただし、現在のRSがそのcache参照を実際に生成するかは未確認なので、サービス未登録だけで全cache機能の破損とはしません。[EJS-0262](../../../docs/project/service-response-audit/evejs-only-responses.md:513)

これらはソースから追った影響です。実client画面での再現・操作完了の確認は行っていません。

## 3. protobufの182候補は「欠落182件」ではない

EveJS public gatewayのrequest selectorを、namespace aliasや重複fallbackを整理すると**182候補**になります。これは**全nested DTO比較済みではありません。完全比較済み0／未完了182**です。request・responseの必須／任意field、repeated・oneof、権限、実状態、通知順まで揃ったと判断した件数ではありません。

RSのproduction compositionにpublic gateway producerがないことはソース確認済みです。ただし、182候補には、現行clientが使うもの、管理用、旧経路、空の互換payload、意図的404も混ざっています。**182候補を一律missingや破損の確定数へ足していません。**

- daily goals：clientがgoals一覧を読む経路。[EJS-0569](../../../docs/project/service-response-audit/evejs-only-responses.md:846)
- saved SKIN design容量：clientはcapacityを読む一方、EveJS側はcompatibility fallbackを含みます。[EJS-0553](../../../docs/project/service-response-audit/evejs-only-responses.md:830)
- map tags：GetUpdateからcheckpointを受ける経路。[EJS-0654](../../../docs/project/service-response-audit/evejs-only-responses.md:931)

内部Rust↔HostLinkは独自binary通信で、public protobuf gatewayを代替しません。`publicGatewaySvc` の登録名追加だけでも対応できません。過去の「gatewayを作らない」という意図境界と、現行startup TODOの再開を併記しており、policy判断と未実装producerを区別しています。[protobuf namespace・DTO・producer監査](../../../docs/project/service-response-audit/host-protobuf.md:191)

## 4. 返値・通知・session・providerの14群

この14群は、named出口差476行とは別の問題です。**13群は具体的なソース差を確認、1群は現行sequence未確認**です。分類タグが重複するため、次の7＋3＋2＋6を足して18群とは数えません。

### 分類4・返値容器／field／値：7群でソース差を確認

人数値が固定1になる応答、CacheOK分岐、facility一覧・税・activity field、共有bookmarkのACL field、war negotiationの結果、handshake ackのmaxSessionTimeを含みます。

特に人数値は時刻やIDの揺れとは別の差です。一方、CacheOKなしでも新しい結果を読める場合があり、maxSessionTime省略のV24利用影響も未確認です。**7群とも実clientへの影響は未確認**で、すべてを破損とは呼びません。[EJS-0659](../../../docs/project/service-response-audit/evejs-only-responses.md:945)、[EJS-0660](../../../docs/project/service-response-audit/evejs-only-responses.md:946)、[EJS-0662](../../../docs/project/service-response-audit/evejs-only-responses.md:948)、[EJS-0672](../../../docs/project/service-response-audit/evejs-only-responses.md:958)

### 分類5・server→client通知：3群でproducer不足を確認

calendar、共有bookmark、office rentalの更新通知です。保存・操作が成功しても、別の閲覧者やclient cacheへ更新を知らせる経路が必要です。producer不足は確認しましたが、**3群とも実配送・画面反映・正確な通知順は未確認**です。Officeのexport修正とは別件です。[EJS-0663](../../../docs/project/service-response-audit/evejs-only-responses.md:949)、[EJS-0664](../../../docs/project/service-response-audit/evejs-only-responses.md:950)、[EJS-0666](../../../docs/project/service-response-audit/evejs-only-responses.md:952)

### 分類6・session属性／order：確定field差1群、現行sequence未確認1群

handshake ackのmaxSessionTime省略はソース差です。もう1群のSkills mutationとcorporation kick/leaveは、親の修正が進んでいたため、現行sequenceを未確認として保留しています。**両群の実clientでのorderは未確認**です。[EJS-0672](../../../docs/project/service-response-audit/evejs-only-responses.md:958)、[EJS-0671](../../../docs/project/service-response-audit/evejs-only-responses.md:957)

### 分類7・mutation／provider接続：6群でソース差を確認

facility、共有bookmark ACL、個人bill支払い、war negotiation、mutual-war invite、community fittingsです。出口が存在しても、状態を読む／保存する担当処理へつながっていないケースがあります。

`CharPayBill` は未接続callbackのため支払不能を返します。mutual-war writesは現在、未対応として明示拒否します。古い「何も保存せず成功Noneを返す」という指摘を現在の挙動に流用していません。**6群とも対象データを用いた実動・永続化・通知の総合確認は未実施**です。[EJS-0661](../../../docs/project/service-response-audit/evejs-only-responses.md:947)、[EJS-0665](../../../docs/project/service-response-audit/evejs-only-responses.md:951)、[EJS-0667](../../../docs/project/service-response-audit/evejs-only-responses.md:953)、[EJS-0668](../../../docs/project/service-response-audit/evejs-only-responses.md:954)、[EJS-0669](../../../docs/project/service-response-audit/evejs-only-responses.md:955)、[EJS-0670](../../../docs/project/service-response-audit/evejs-only-responses.md:956)

## 5. 欠落集計から外した修正・改善・policy差

**対応済みとして欠落から除外したもの**は、station／skillMgr／tradeMgr／standing2 aliases、Allianceファイル内のCorpRegistry partial所属、Agent初回bind・resolve、Crateのshared fitting保存先、dungeonのTriglavian出口、親追加のbeyonce.CmdAbandonLootです。ルート修正の反映確認と、機能全体の互換確認は別です。[撤回・訂正一覧](../../../docs/project/service-response-audit/evejs-only-responses.md:729)

EveJSの生配列はmarshal後にPython tupleになるため、PaperDoll portraitやIndustryMonitor.ConnectJobの「生配列対Py.Tuple」も容器欠落には数えません。ほかのfield・権限・provider差は別に残ります。

**CCP callsite／契約に根拠のある改善**は、global config書込みのROLE_ADMIN／ROLE_SERVICE gateと、LoginSsoが読むaccess_token／computer_hashキーです。SKIN activation・dynamic-item mutationのtransaction atomicityも、意図した改善として分離しています。[role・transaction改善の説明](../../../docs/project/service-response-audit/evejs-only-responses.md:739)、[EJS-0672](../../../docs/project/service-response-audit/evejs-only-responses.md:958)

**policy・意図差分**には、保護config keyの明示拒否、未対応書込みの拒否、unknown RPCをNone成功扱いせずerrorにする方針、gatewayの過去の意図境界があります。EveJSとの差があることと、RSを欠落実装として修正すべきことを同一視しません。[configの意図差分](../../../docs/project/service-response-audit/host-protobuf.md:98)、[EJS-0669](../../../docs/project/service-response-audit/evejs-only-responses.md:955)

## 6. このsummaryの確認範囲

672行の通し番号、476＝448＋3＋25、476＝332＋144、182候補、14群と分類タグを詳細ledgerから照合しました。summaryの代表例は詳細行へ直接リンクしており、全RPC表を複製していません。

source実装・test・build・Git mutation・子agentは引き続き行っていません。今回のhandoff反映はsummary・ledger・Host監査・TODO・READMEの文書更新だけです。次に機能完了を判断する際は、caller未確認の行、全nested DTO、通知・session order、実データでの失敗／再接続／再起動を含む比較が必要です。未確認は一致へ繰り上げていません。


### core-a-f新handoffとの照合（2026-10-13）

旧44件は**未実装33／client-local対応3／legacy 6／現行同service実装済み2**、現在の未登録名は42です。agentMgr.MachoResolveObjectとdungeonInstanceCacheMgr.GetTriglavianSiteInstancesは、既存ledger・TODOでも既に欠落から除外済みでした。2件を再び減算せず、core-a-fの分類変更7件を反映しています。**全体の672行、物理名差476、サービス11／候補29、protobuf候補182、14群、Host181／1280は維持**します。named未実装のTODOは448件・63groupへ更新しました。[core-a-f revised 44-row handoff](../../../docs/project/service-response-audit/core-a-f.md:345)

Source snapshotは引き続きmain/dda03ca0（親dirty宣言を含む）です。文書読戻し時のmain HEADはc114dc0dへ進んでいましたが、その新commitのsource再監査は今回行っていません。この文書更新で動的受入を追加確認したものはありません。
