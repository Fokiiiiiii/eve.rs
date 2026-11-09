# Relations R14 audit fixes

対象: `/home/user/reports/90-audit-eve-relations-r14.md` の3件。コード、V24.01 client、EveJS v0.12.9を照合した結果、3件ともこのworktreeの基準revision `7ee8b2c40` で確認できた。誤検知として除外した所見はない。

## 修正内容

### 1. 同じMany関係への初回並行書き込み

基準revisionでは、`SetAsync` が `OnePerSubject` だけをadvisory lockし、未作成行への `SELECT ... FOR UPDATE` は競合を防げなかった。migration 0001の `(kind, subject, object)` 一意制約により、並行初回insertは一方のtransactionを失敗させる。`AdjustAsync` は読み取り後に値を計算していたため、既存行では更新を失い得た。

すべてのwrite、end、expiryがschema/kind/subject/object単位のtransaction advisory lockを取るようにした。`OnePerSubject` は従来どおりsubject lockも先に取り、置換の順序を保つ。`AdjustAsync` は `TransformAsync` を使い、同じロック内で最新値を読み、変換して保存する。NPC faction standingも別system間の同じstanding pairを原子的に更新する。

V24.01はcharacter/corporation standingsを取得し、`OnStandingSet` と `OnStandingsModified` を受け取る（`/home/user/eve-logic/eve/client/script/ui/services/standingsvc.py:41-73,114-130`）。EveJSのNPC kill pathはcharacter/faction/system単位でdebounceした後、既存standingにraw lossを適用する（`/home/user/evejs-261028.1/server/src/services/character/factionKillStandingRuntime.js:97-140`）。同じcharacter/factionに対する複数killが別systemから来ても、それぞれのstate transitionを保存する必要がある。

### 2. relation historyの属性・期限の欠落

値が同じでも属性や期限が変わったらhistoryを追加し、`attrs == null` では保持後の属性を `new_attrs` と旧来の `attrs` に記録する。relation終了時は削除前の属性と期限をhistoryとdomain eventに保存する。新しい `0003_relation_history_state` migrationは既存historyの `attrs` を `new_attrs` に移し、過去の履歴を維持する。`RelationHistoryEntry.Attrs` は既存利用側との互換用に残した。

V24.01のwar rowは`timeFinished`、`mutual`、`openForAllies`等を持ち、war relationとalliesの有効期限を判定に使う（`/home/user/eve-logic/eve/client/script/ui/services/wars/war_cso.py:139-158,296-332`）。EveJSのwarsInfoMgr row descriptorも同じ列を投影する（`/home/user/evejs-261028.1/server/src/services/corporation/warsInfoMgrService.js:24-42,66-75`）。現行RPC row projectionのshapeは変更していない。

### 3. affiliationとwar判定の異なるsnapshot混在

`AffiliationOfAsync`を単一SQL statementにして、corporation、alliance、character/corporation/alliance factionを同じstatement snapshotから解決する。`ChainOfAsync`はconnection/transactionを受け取れるようにした。`RelationWars`のlegalityとremote assistanceの読み取りはread-only `RepeatableRead` transaction内でchain、war、allyをまとめて読む。

V24.01 Crimewatchはattack legalityでwar relationshipを確認し（`/home/user/eve-logic/eve/client/script/ui/services/crimewatchSvc.py:438-472`）、`war_cso.py`はcorp/alliance所属と有効なwar/alliesを用いる（同`war_cso.py:139-158,302-332`）。EveJSのaffiliation更新はsession alliance IDと`SessionChange`を更新する（`/home/user/evejs-261028.1/server/src/services/corporation/corporationSessionAffiliationSync.js:165-186`）。Hostは`CrimewatchComposition`で`RelationWars.AreAtWarAsync`をCrimewatchのlegal callbackに登録し、その結果を`CrimewatchRuntime.RecordAggressionAsync`が使う（[CrimewatchComposition.cs](../../dotnet/src/Eve.Host/Composition/CrimewatchComposition.cs#L40), [CrimewatchRuntime.cs](../../dotnet/src/Eve.Services.Crimewatch/CrimewatchRuntime.cs#L195)）。

## 検証

### 修正前の再現

test-only commit `c78c087a2` に回帰テストを置き、production変更前のdetached worktreeで実行した。全件が意図どおり失敗した。

- 初回Many `SetAsync` / `AdjustAsync`: 同一relation transaction lock待ちを観測できず、テスト失敗。
- war属性を変えたhistory: 2件を期待したが1件しかなかった。
- affiliation/war更新と同時のCrimewatch legality: 正当なwar attackの期待値`Legal`に対し`Criminal`。
- 異なるsystemからのNPC faction kills: 2回分のstandingを期待したが1回分だけ保存された（`-0.001`対`-0.002`）。

EveJSでは実際の`recordNpcFactionStandingLoss`と`standingRuntime.applyStandingChanges`を呼び、characterStateのみin-memory adapterにした比較を実施した。異なるsystemからの2件とも適用され、最終値は`-0.002`になった。EveJS全サーバーや永続databaseを起動した比較ではない。

### 修正後

`eve_p4`を`EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p4`で使用。Release buildは変更対象の3 production projectsと3 test projectsを`--warnaserror`で実行し、各buildが0 warnings / 0 errorsだった。Relations migration test追加後は`Eve.Relations.Tests`を再buildしてテストした。

関連test classのDB検証結果（失敗0、skip 0）:

- `Eve.Relations.Tests`: `RelationTests`, `StandingDirectionMigrationTests` — 18 passed
- `Eve.Characters.Tests`: `CreationTests` — 21 passed
- `Eve.Corporations.Tests`: `AllianceTests`, `CorporationProtocolTests`, `CorporationTests`, `StandingMgrServiceTests`, `WarRegistryTests` — 120 passed
- `Eve.FactionWarfare.Tests`: `FwDatabaseTests` — 15 passed
- `Eve.Host.Tests`: relation/war/affiliation/mission/reprocessing integration classes — 29 passed;追加でCrimewatch、StandingMgr、WarRegistry wire testsとweapon legality host test — 11 passed
- `Eve.Services.CharacterSelect.Tests`: `CharUnboundMgrTests` — 36 passed
- `Eve.Services.Dogma.Tests`: `StandingCommandTests` — 5 passed
- `Eve.Services.Crimewatch.Tests`: `RuntimeTests`, `NpcSecurityStatusRecoverySubscriberTests` — 38 passed
- `Eve.Services.Space.Tests`: `NpcFactionStandingTests`, `UndockContrabandTests` — 8 passed
- `Eve.Services.Station.Tests`: `ReprocessingServiceTests` — 13 passed

上記の対象クラス合計は314 passed、0 failed、0 skipped。mainをmergeした後、Relations、Crimewatch、Spaceのproduction/test 6 projectをRelease buildし、すべて0 warnings / 0 errorsだった。初回の広いHost filterは関連外の`AbyssalHostFlowTests.Real_sim_cleanup_confirms_live_npc_environment_and_reward_scene_removal_with_another_run_intact`も拾い、120 passed、1 skippedだったため集計に含めていない。関係するHostクラスを個別指定して再実行し、上記29 + 11件がすべてskipなしで通過した。

回帰テストは実DBで永続化、relation history、domain event順序、期限job、NPC standingのrestart、Crimewatchの実`RecordAggressionAsync`結果を確認する。Host側の既存wire testsも通過し、standings notification shapeを担う既存コード（`/home/user/eve-logic/eve/client/script/ui/services/standingsvc.py:41-73`、`/home/user/wt-cx4/dotnet/src/Eve.Host/Composition/SpaceComposition.cs:451-470`）に変更はない。

## 未確認

- V24.01のGUI clientへの接続と実serverでの操作は行っていない。検証はheadless PostgreSQL testsと既存Host wire tests。
- EveJSは上記standing path以外の全war/affiliation server flowを実行していない。war/affiliationの比較は指定client/EveJS callsitesのコード読解とeve.rsのDB回帰テスト。
- push後のGitHub Actions main CIは、pushしてから`tools/dev/ci-status.py wait`で確認する。
