# Inventory service audit fixes (r14)

この記録は `/home/user/reports/90-audit-eve-services-inventory-r14.md` の各所見を、eve.rs、V24.01 client `/home/user/eve-logic`、EveJS `/home/user/evejs-261028.1` の実装で照合した結果と修正をまとめる。6 件すべて実在し、誤検出として見送った所見はない。

codebase-memory-mcp と Serena は利用できなかったため、対象コードは `rg` / `sed` で追跡した。クライアントの全ソース検索、EveJS の該当 RPC 実装、Host provider、永続化層、返却値と通知のテストを確認した。

## 高 — Capsuleer Deliveries を開けず、権限検証もなかった

V24.01 は `containerCapsuleerDeliveries = 10015` と `flagCapsuleerDeliveries = 187` を使う（`inventorycommon/const.py:221, 2632`）。`invControllers.py:1075-1100` はこの bound inventory を開く。`invCache.py:501-525` は docked Upwell structure では 10015 を structure container 10014 に解決し、該当 inventory を開く。UI は `invWindow.py:1933-1941`、item lock は `ui/services/corporation/itemLocking.py:47` で Project Manager または Director を要求し、controller は one-way / drop 不可としている。

EveJS は `invBrokerService.js:10760-10783` で container 10015 を flag 187、corp owner、現在地に bind し、`8728-8844` で current location、corp ownership、role を確認する。eve.rs は container 10015 を認識せず `None` を返していた。

`InvBrokerService` に専用 bound context と flag 187 を追加した。一覧は現在 docked location にある corp 所有の flag 187 item だけを返す。station の 10015 bound path に加え、structure path が 10014 に変換される場合も、通常の structure contents から flag 187 item を除外した後、現在 structure の Capsuleer Deliveries item を同じ role gate で追加する。閲覧・取得には Host が `CorporationStore` から読み出す有効 corp membership と Project Manager / Director role を要求し、格納は拒否する。通常の inventory RPC は `InventoryRpc.cs:47-52` から権限 provider を渡す。

`HomeStationHostTests.Registered_invbroker_checks_capsuleer_deliveries_roles_and_persists_taken_items` は実 Host registry と PostgreSQL を使い、station での一般 member の拒否、Project Manager と Director の閲覧、Project Manager による取得、item owner/location/flag の永続化、および `OnItemChange` を検証する。同じ実 Host test で structure inventory path からの一般 member 拒否、Project Manager の閲覧・取得、永続化と通知も確認する。サービス focused test は structure path を実装する前に空結果で失敗し、追加後に通過した。`InvBrokerInventoryHostWireTests` は実 Macho 応答の bound-object wire shape も確認する。

## 高 — 通常の invbroker から結晶ダメージ provider が欠けていた

V24.01 `eveMoniker.py:122-129` は station または solar-system の `invbroker` を選び、`ui/services/inventorysvc.py:381-388` は `CrystalDamageTracker` から `GetDamageForCrystals` を呼ぶ。EveJS `invBrokerService.js:11774-11793` は所持中の damageable crystal のダメージ率を返す。

`InvBrokerService.GetDamageForCrystals` は provider がないと `InvalidOperationException` を投げる一方、通常の Host `InventoryRpc.cs` は provider を渡していなかった。`CharacterRpc.cs` の global-assets broker にだけ設定されていた。

通常の Host invbroker にも `modules.ModuleConditionsAsync` 由来の provider を接続した。`HomeStationHostTests.Registered_invbroker_returns_persisted_crystal_damage_from_module_state` は実 Host 登録から persisted module condition `0.375` が返ることを検証する。

## 中 — MultiAdd が権限拒否後の item を skip しなかった

クライアント `invControllers.py:525-563` は複数 item に `MultiAdd` を呼ぶ。EveJS `invBrokerService.js:13623-13625` は corp Take 権限のない item を skip して後続へ進む。移動が 1 件以上なら `14112-14125` で batch を確定して `true` を返し、全件移動できなければ `8406-8439` / `14098-14109` で UserError を返す。

eve.rs は権限のない corp item で `None` を返し、後続 item を見ていなかった。拒否 item はその周回だけ skip するよう変更した。新しい `MultiAdd_skips_a_denied_corporation_item_and_moves_later_personal_items` は denied corp item を personal item の間に置き、後続 item の移動、denied item の未変更、2 件分の通知を検証する。

既存 `MultiAdd_corporation_split_keeps_source_custody_and_requires_take_role_for_the_reverse_move` の「権限拒否単独なら `PyNone`」という期待値は、EveJS の全件不動時 `CustomNotify` と一致しないことを上記実装で確認したため、未移動 error と元 item の不変を検証する assertion に直した。部分成功時の assertion は維持した。

## 中 — ListByFlags の wire 値が Python set でなく list だった

V24.01 `invCache.py:1167-1196` は RPC 結果を反復して row cache に格納する。EveJS `invBrokerService.js:11648-11649` は `ListByFlags` を Python set と明記して返す。eve.rs は `PyList` を返していた。

`ListByFlags` を `InventoryWire.Set` に変更した。`ListByFlags_returns_a_python_set_on_the_wire` は非空・空 set と marshal roundtrip 後の set marker / row を確認する。既存 `InvBrokerTests` の期待値も wire shape に合わせた。

最初の push CI では headless client の新規キャラクター inventory 比較が失敗した。`oracle/fixtures/scenarios/inventory.json` は 2026-10-01、reference commit `b637d2a` の古い `ListByFlags` list 形を保持していた一方、指定された EveJS 261028.1 の `invBrokerService.js:10834-10842, 11648-11649` は `__builtin__.set` ObjectEx を構築して返す。V24.01 `invCache.py:1174-1196` と全 `ListByFlags` 呼び出し箇所は結果を iterable として処理し、set と互換である。fixture の 12 件を現在の EveJS set wire shape に更新し、各 item row と空結果をそのまま維持した。headless 比較は戻り値の wire type を含む厳密比較のままにしている。

## 中 — GetAssetWorth が dockable root 以外の item も評価していた

V24.01 `invCache.py:890-892` は `GetAssetWorth` を global-assets inventory から呼ぶ。`playerprogression/assetnetworthnotifier.py:52-65` が実際に取得した値を使う。EveJS `charMgrGlobalAssets.js:399-448` は station / dockable structure まで root を解決できた item のみ `allEntries` に含め、`621-657` はその一覧を評価する。

eve.rs は `OwnedByAsync` 全件を評価していた。既存 `GlobalAssetRootsAsync` を再利用し、station / structure に到達する item だけを計算対象にした。子 container item は親をたどって dockable root に到達するため含まれ、space ship / cargo は除外される。`GetAssetWorth_counts_dockable_asset_roots_only_and_keeps_fractional_isk` は station item、docked ship と cargo、space ship と cargo を一緒に置いて検証する。

## 低 — GetAssetWorth が小数 ISK を整数に丸めていた

EveJS `charMgrGlobalAssets.js:71-73, 657` は小数第2位まで丸める。eve.rs は桁数を省略した `Math.Round` で整数 ISK に丸めていた。`GetAssetWorth` の asset / PLEX 両方を2桁へ丸めるよう変更した。上記 SDE-backed test は pinned SDE type `353774` の `basePrice = 16873.5` を用いて cents を検証する。

## 検証結果

修正前の focused test では、Capsuleer Deliveries の bind が `None`、MultiAdd が拒否 item で停止、ListByFlags が `PyList`、GetAssetWorth が space 内の ship / cargo まで含めて整数に丸めることを再現した。通常 Host invbroker の crystal test は `Crystal damage state is not configured` で失敗した。

Release build は以下の変更プロジェクトすべてで完了し、各 0 warnings / 0 errors だった。

- `Eve.Inventory`
- `Eve.Services.Inventory`
- `Eve.Host`
- `Eve.Services.Inventory.Tests`
- `Eve.Host.Tests`

`EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p7` を使った Release regression 結果:

- `InventoryRpcFixTests`、`InvBrokerTests`、`StructureControlInfoTests`、`CargoCapacityInventoryTests`、`InventoryWireTests`: 76 passed, 0 skipped。
- invbroker / factory を参照する Host class 群: 449 passed, 20 skipped, 0 failed。20 件は `AbyssalHostFlowTests` の `RealAbyssalSimPgFact` 群で、`EVE_ABYSSAL_SIM_BINARY` と `EVE_ABYSSAL_SIM_SDE` が未設定のため skip された。skip を pass には数えていない。

**未確認:** GUI の V24.01 client、通信 trace、EveJS runtime の同一シナリオは実行していない。EveJS の該当実装と client callsites はソース照合した。EveJS `server/package.json` に test script はなく、この checkout には該当シナリオの独立実行 harness を見つけられなかった。

CI 1588 は上記 fixture 不一致で失敗した。訂正後、ローカルで `tools/headless-client/tests/test_recorded_scenarios.py` を実行し 15 passed を確認した。JSON parse と whitespace check も通過した。CI 1591 ではこの inventory 比較は通過し、唯一の失敗は別件の market test が空の `BuyMultipleItems` 応答に Rowset を期待していた点だった。V24.01 `marketsvc.py:270-273` は結果を反復し、EveJS `marketProxyService.js:7014-7082` は空入力時にも list を返すため、main の `d4443b39` は wire shape を `[]` と厳密に検証する assertion に修正した。CI 1592 は `d4443b39` で全 job 成功、failed tests/jobs なし。その後、コード状態 `fcea50118` の CI 1593 も Rust、.NET、Windows、headless end-to-end を含む全 job が成功した。後続の main 更新は文書のみ。
