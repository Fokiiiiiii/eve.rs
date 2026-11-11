# PI・主権・戦争システム深掘り監査

監査日: 2026-11-11
対象クライアント: V24.01 build 3396210 (`/home/user/eve-logic`)
実行参照: `/home/user/evejs-261108.1`

## 結果

この作業で確定した PI のクライアント可視差異を2件修正した。いずれも Host に登録した bound RPC を経由する PostgreSQL 回帰テストを、修正前に失敗させてから直した。

主権、開発指数、戦争適格性、FW enlistment/VP、insurgency については既存実装と監査証拠を現行コード・クライアント全体の呼び出し箇所・現在の EveJS checkout と照合した。この作業では追加のコード差異を確認しなかったため、それらのランタイムコードは変更していない。旧 war audit と TODO に「war negotiation 未実装」と残る記述は現状と異なる。現行 Host には PostgreSQL 保存、wallet/relation transaction、通知まで実装されており、登録済み Host wire regression もある。

## 修正した差異

### 1. PI resource list は planet type ごとの実データを返す

- V24.01 の `resourceController.CreateLayout` は bound object の `GetPlanetResourceInfo()` を呼び、返された各 `typeID` と quality を一覧に表示する (`/home/user/eve-logic/eve/client/script/ui/shared/planet/resourceController.py:41-50`)。惑星の RPC wrapper は `clientPlanet.py:644` にある。
- EveJS は planet type から resource ID を選び、クライアントの native distribution では quality `> 0` のエントリだけを採用する (`/home/user/evejs-261108.1/server/src/services/planet/planetStaticData.js:441-449`)。その native table は build 3396210 の `globaltypes.fsdbinary` と SHA-256 を `planetResourceDistribution.json:1-6` に記録している。`planetMgrService.js:414-427,1143-1154` がその ID 群と runtime quality を返す。
- 旧 eve.rs は全 raw resource group から4種を planet ID 由来で選んでいたため、同じ planet type でも client distribution と異なるセットを返し、dogma provider がない構成では空 dict を返した。新しい `PlanetResourceTypes.ForPlanetType` は client native table の全8 planet type と EveJS fallback を使う。品質値は引き続き eve.rs の deterministic field から計算し、CCP native harmonic や extraction yield と同一とは主張しない。
- EveJS 261108.1 の `planetStaticData.getPlanetResourceTypeIDs` を Node から直接呼び、native type `11, 12, 13, 2014, 2015, 2016, 2017, 2063` の全8 sets、未知の `30889` が空、fallback type `73911` が5種であることを確認した。native type の結果は `dotnet/src/Eve.Planets/PlanetResourceTypes.cs` に反映した。
- 回帰: `PlanetResourceInfoHostWireTests.Planet_resource_info_returns_the_client_resource_set_through_the_registered_bound_host_wire`。修正前は dogma なしで期待5種、実際0種となって失敗した。修正後は actual Host bind/bound call、永続 planet row の type 参照、`PyInt` key、`PyReal` value、通知なしを検査する。

### 2. 負の PI link level を0に clamp する

- クライアントは `ValidateCreateLink` で level の上限と colony CPU/Power を検査するが下限を検査しない (`/home/user/eve-logic/eve/common/script/planet/baseColony.py:442-474`)。`ValidateSetLinkLevel` も上限のみを検査する (`baseColony.py:604-628`)。通常 UI は level 0 から操作するが、client RPC の受理条件には負数 floor がない。実際の calls は `clientPlanet.py:297-310,332-345`。
- EveJS の `planetRuntimeStore.createLink` と `setLinkLevel` は負値を `Math.max(0, ...)` にする (`/home/user/evejs-261108.1/server/src/services/planet/planetRuntimeStore.js:3495-3528`)。
- 旧 eve.rs は負値を保存し、link の fractional level modifier があると負数の累乗から NaN が生じ得た。`ColonyCommands.CreateLink` と `SetLinkLevel` で、費用計算・状態保存前に0へ clamp する。
- 回帰: `PlanetLinkLevelHostWireTests.Negative_link_levels_are_clamped_to_zero_through_the_registered_host_wire_and_persisted`。修正前の実 Host test は create `-2` に対し `-2` が返り、期待0で失敗した。修正後は create `-2` と set `-3` の両方で応答、DB状態、commit 後 callback が観測する状態を確認する。

## 照合した既存機能

### Sovereignty、Hub、development indices

- クライアント全体の主な読み書き経路は `sovSvc.py:71-119,210-224,294-305,380-396`。Sov claim / Hub info、Hub fuel ACL、Skyhook cleanup の結果を UI が使う (`sovHub/hubController.py:247-249,382-386`, `skyhooksCleanup/cleanupWndController.py:118-145`, `structureDeployment.py:111-119`)。戦略指数の表示は Hub の `claimTime` から経過秒を計算する (`sovSvc.py:210-224`)。
- EveJS の `map/sovMgrService.js:57-115` は同じ SovMgr read/write surface を扱い、`map/devIndexManagerService.js:19-41` は system index dict を返す。`sovereignty/sovState.js:3423-3438` は military/industrial を保存点数から返し、strategic `1615` を `claimedForDays * 86400` 秒、`increasing=false` とする。
- eve.rs は shared sovereignty table/read model を使う (`dotnet/src/Eve.Host/Persistence/Sovereignty/PersistenceSovFwReadModel.cs:42-100`); `DevIndexManagerRpcService` は attribute ID -> `{points,increasing}` dict を返す (`dotnet/src/Eve.Host/OperationalRpcServices.cs:20-58`)。回帰 `DevelopmentIndicesHostWireTests` は `1583`, `1584`, `1615` の値と wire fields を確認する。主権 claim read、Hub read、fuel ACL update、Skyhook destroy/acquire/settlement/retry は `SovMgrPostgresHostWireTests.Sov_mgr_reads_and_skyhook_and_fuel_access_writes_round_trip_through_host_and_postgres` が DB/Host 経路で確認する。Neutral Hub claim ownership と restart projection は `SovHubClaimTests`、foreign IHub anchor delay と persisted duration は `StructureAnchorTests` にある。
- 現在の EveJS に capture/deployment source はある (`sovereignty/sovPlayerDeployment.js:622-635`, `sovCaptureState.js:393-437,767`)。この作業ではこれらの既存 capture/deploy flow を再実行していない。過去の `handoff-sovMgr-coverage-cx4.md` と structure handoff の結果はこの文書に履歴として記載し、今回の新規 pass と混同しない。

### Wars、war eligibility、negotiations

- V24.01 `warPermit.py:20-50` は自 corporation の `allowWar` を読み、相手が alliance の場合は alliance executor corporation を解決して status を参照する。War UI は同じ値を検索結果と HQ 選択に使う (`warDeclare.py:194-199,359-367`)。
- eve.rs の `WarEligibilityComposition` は SDE 上の eligible Upwell types、anchored control towers、customs offices、live sovereignty ownership を合成し、corporation/alliance の現 affiliation に適用する。SDE/owner source の回帰と notification event source は `WarEligibilityOwnerSourcesTests` にある。EveJS 側では `corporation/warPermitState.js:27-70` が corporation / alliance executor の status を解決し、`lookupSvcService.js:578-626` が検索 payload に載せる。今回の照合では client と Host の owner resolution に差異を確認しなかった。
- V24.01 client は `war_cso.py:209-252` と `warWindows.py:245,264,325` を通じ pending offers を読み書きする。現行 eve.rs `WarRegistryService` は bound RPC を登録し、`WarRegistryNegotiations.cs` が relation row lock + `WarNegotiationStore` + wallet transfer/CONCORD fee + peace treaty + commit 後 notification を扱う。Host wiring は `CorporationRpc.cs:82-87`。EveJS の同じ状態遷移は `corporation/warRegistryService.js:150-250,253-331` と `warNegotiationRuntimeState.js:149-234` にある。
- 現在の PostgreSQL Host regression `WarRegistryHostWireTests.Registered_war_registry_creates_and_accepts_an_ally_offer_through_macho_with_persisted_state_and_notifications` は実 Host build/dispatch を通し、offer wire fields、notifications、accept 後 notification ordering、wallet balances、war ally relation、surrender の peace treaty read を確認する。DB/service tests は `Eve.Corporations.Tests/WarRegistryTests.cs` にある。したがって `docs/project/TODO.md` と `DEEPCHECK_wars.md` に残る negotiation 未実装記述は本状態では obsolete であり、今回後続更新で修正する。
- 旧 `deepfix-wars-sov.md` に記録された `GetPublicWarInfo` collection shape 修正は別の既存修正として維持する。この pass では negotiation や war information のコードは変更していない。

### Faction warfare victory points / enlistment

- V24.01 `facWarSvc.py` は `facWarMgr` RPC surface を通し、`fwVictoryPointSvc.py:30-58` は `GetLocalVictoryPointState` / `GetAllVictoryPointStates` を読み `OnVictoryPointStateUpdated` で cache を更新する。map、capture gauge、system info panel はこの state を消費する。enlistment callers と fields は `docs/project/service-audit/DEEPCHECK_facWarMgr.md` に whole-client source locations として列挙済み。
- EveJS checkout の `faction/facWarMgrService.js:197-298` は faction/status/rank/LP read surfaces を実装する。`fwVictoryPointMgrService.js:92-143` は `factionwarfare.victoryPointState.VictoryPointState` payload と update notification を扱い、state provider と authored `fwOpenModel.js:6-19` の二つの explicit system entries を優先/補完する。これは CCP standard occupancy/complex progression 全体の実行参照にはならない。
- eve.rs は persisted `FwStore` occupation、complex ticker、battlefield progress と notifications、enlistment downtime transitions を使う。既存根拠は `docs/project/FW_DESIGN.md` と `dotnet/tests/Eve.FactionWarfare.Tests` / Host tests (`FacWarMgrHostWireTests`, `FacWarMgrDonationHostWireTests`, `FwWarzoneSolarsystemHostWireTests`) にある。この pass ではこれらのテストを再実行していない。
- 残る明記済みの gameplay gaps: player kill VP producer、FW Hub destruction capture trigger、一部 statistics producer / NPC integration。これらは `FW_DESIGN.md` の「Not built」に記録されており、この pass で完了扱いにしない。

### Insurgency

- V24.01 client は campaign snapshot の二陣営 point requirements、state、notification transition を区別する (`/home/user/eve-logic/pirateinsurgency/campaignClientSnapshot.py:12-23`, `pirateinsurgency/client/dashboard/sidepanels/progress.py:36-48`, `eve/client/script/ui/services/insurgencyCampaignSvc.py:60-85`)。
- 現 EveJS `map/insurgencySolarsystemService.js:17-70,112-133` は hard-coded snapshots と no-campaign local pair のみ返し、campaign progression flow を実装していない。eve.rs の state machine、coverage、deadline transition notification に関する3件の回帰と修正は `docs/project/audit-fixes-insurgency.md` に記録済み。この pass では該当テストを再実行していない。

## 今回の検証

`eve_p4` を使用した。標準 `sde` はbuild 3396210でロード済みで、PI Host wire test が使う planet `40486618` は `sde.mapPlanets` 上の実 row、type ID `2016` だった。PI Host 起動に使用された `sde_mission` schema は build 3405148 で、PI の planet/type/planetary resource 参照には使っていない。2つの新しい Host regression を未修正 HEAD (`6faa0c8f9`) の一時worktreeで先に実行し、resource list は期待5種/実際0種、negative link create は期待0/実際-2でそれぞれ失敗することを確認した (2 failed, 0 skipped)。修正後に同じ2件が成功した。SDEデータはcommitしない。

Releaseビルドは変更した `Eve.Planets`, `Eve.Services.Planets`, `Eve.Planets.Tests`, `Eve.Host.Tests` で `-warnaserror` を付けて実施し、すべて0 warning / 0 error。

Focused test結果:

- `Eve.Planets.Tests` 全slice: 64 passed, 0 failed, 0 skipped (`eve_p4`)。資源分布はnative 8 planet typesすべての期待setを検査する。
- PI Host/DB selection: 11 passed, 0 failed, 0 skipped (`eve_p4`, post-merge rerun)。resource-info wire、negative link create/setと保存後通知、GML拒否、command-center concurrency、planet read/launch、customs-office Host pathsを含む。
- SOV/war/FW/insurgency Host selection: 26 passed, 0 failed, 0 skipped (`eve_p4`)。SOV claim/idempotency/restart、Hub ownership、fuel/Skyhook、development indices、war eligibility owner sources、registered war negotiation、FW enlistment/donation/occupation read、insurgency snapshot/deadline notificationsを含む。
- `Eve.Corporations.Tests`, `WarRegistryTests`: 9 passed, 0 failed, 0 skipped。
- `Eve.FactionWarfare.Tests` 全slice (enlistment, VP, capture, complex, battlefield, database, stats/rules): 104 passed, 0 failed, 0 skipped (`eve_p4`)。
- `Eve.Insurgency.Tests` 全slice: 17 passed, 0 failed, 0 skipped (`eve_p4`)。
- EveJS 261108.1 live: `run-evejs-live.sh` で V24 headless protocol client が login / character create / select / planet bind を通し、`GetPlanetResourceInfo` を1回読み取った。CachedMethodCallResult を unwrap した後、Barren planet type 2016 の5 resource IDs と正数 quality が Host regression および native distribution と一致 (1 passed, 0 skipped)。EveJS は SQLite を使うため、PostgreSQL fixtures 前提の colony mutation walk はそのまま実行していない。

修正前の2つのPlanet Host wire regressionは、それぞれ誤ったresource set (`0` resources) と負の保存link level (`-2`) を検出して失敗することを確認してから修正した。テスト追加後の今回の focused runs はskipなしで成功した。

## 未確認 / 残る差異

- Graphical V24.01 client は起動・接続していない。過去の PI live headless walk 結果は `PI_DESIGN.md` の履歴にあり、この pass では再実行していない。
- PI の native resource harmonic/band payload、depletion simulation、CCP extraction yield は未実装。V24.01 source の in-range skilled scan は `clientPlanet.CreateResourceInfo` line 781 の scalar/pair mismatch で `GetResourceData` RPC 前に失敗する。EveJS は synthetic SH data を返すが、その係数には source comment が capture-calibrated と説明する `RESOURCE_SH_CLIENT_CALIBRATION` があり、authoritative data として移植しなかった。現在の RPC no-band response と deterministic extractor yield は既知の明示的compatibility cutであり、GUI clientでは確認していない。
- Colony, extractor, processors, routes, launch pads の全 command permutation/concurrency/restart は今回再実行していない。既存 `PI_DESIGN.md` の live walk は過去の限定scenarioで、今回の headless live run ではない。
- SOV tests は claim/idempotency/Hub restart、fuel ACL/Skyhook、index reads、owner sources の記載範囲まで。claim capture timing と development-index accrual は全入力条件で実行していない。FW domain全104 testsはcapture/complex/battlefield state testsを含むが、native client acceptanceは未確認。War negotiation Host test covers ally offer/accept; dedicated surrender/restart graphical flow was not exercised. Insurgency domain and selected Host tests cover state/deadline snapshots, not a graphical client.
- EveJS では resource distribution helper の全8 native typeを直接実行し、さらに Barren planet の `GetPlanetResourceInfo` を live server 上で headless client から呼んだ。PI colony mutation、SOV、war、FW、insurgency の EveJS server上 end-to-end flow は実行しておらず、EveJS source と eve.rs の PostgreSQL/Host regression の照合に留めた。
- Codebase-memory MCP と Serena はこの環境から利用できず、local source reads/searches (`rg`, `sed`, `nl`) で補った。
