# 駅サービス監査 R14 の修正記録

対象: `/home/user/reports/90-audit-eve-station-r14.md`。報告の3件をコード、V24.01クライアント `/home/user/eve-logic`、EveJS `/home/user/evejs-261028.1` で照合した。3件とも実在する不具合だった。誤検知として省略した指摘はない。

## 確認済み・修正済み

### P2: Structure で再処理できない

V24.01は `/home/user/eve-logic/eve/common/script/net/eveMoniker.py:88-101` で `session.structureid` と `session.stationid` の両方から `reprocessingSvc` をbindする。再処理UIは同 `/home/user/eve-logic/reprocessing/ui/controller.py:78-82` でサービスを呼び、StructureのHangarを出力先候補にする（`reprocessingWnd.py:344-366`）。EveJSはdock対象を `/home/user/evejs-261028.1/server/src/services/reprocessing/reprocessingRuntime.js:355-393` で解決してサービス状態を確認し、`:466-520` でrig・security・structure bonusを計算し、`:536-586` でfacility taxを読む。監査前のC# bindはStationCatalogだけを見ていたため、この呼出しを拒否していた。

`ReprocessingService` を station/structure 共通のbindと計画処理に拡張した。Structureではコア、破壊状態、Reprocessingサービスのオンライン状態、Directory accessを確認し、再処理トランザクション内でもstructureとサービス状態を再確認する（`dotnet/src/Eve.Services.Station/ReprocessingService.cs:130-175,374-386`）。効率はピン留めSDE dogmaとrig effect、rig flag 92–99、system securityから求め、構造物のfacility taxと所有corporationを使う（同 `:420-425,513-561`）。rig行は全インベントリを読み込まず、該当flag範囲だけを同一transaction内でロックして読む（`dotnet/src/Eve.Inventory/Items.cs:815-841`）。Hostは既存StructureDirectoryのサービスアクセス判定とSolarSystemCatalogを注入する（`dotnet/src/Eve.Host/Rpc/StationRpc.cs:45-50`）。

Structure taxはcharacter walletから引くだけでなく、同じtransactionでowner corporationのISK walletへ入金し、commit後に両walletのbalance changeを通知する（同 `ReprocessingService.cs:286-299,334-348`）。facility taxの値は既存Host facility managerと同じ保存済みstructure値 `industryTaxRate` / `taxRate` を使う（`dotnet/src/Eve.Host/Composition/IndustryComposition.cs:399-421`）。これはEveJSのowner-credit settlement（`/home/user/evejs-261028.1/server/src/services/reprocessing/reprocessingSettlementState.js:1061-1074`）に対応する。Host回帰ではcharacterのdebit、owner corporationのcredit、再起動後retryでどちらも二重更新されないことを検証する。

SDE build 3396210の確認値: Tatara type 35836 はrig size 3と再処理yield bonus 5.5%、Standup L-Set Monitor II type 46640 はyield 0.53。Jitaのhigh-sec倍率1を適用するとstructure効率は `0.53 × 1.055 = 0.55915`。Veldspar type 1230はore処理。これらの値をregistered Host回帰でquoteに照合している。テストは成功・サービスoffline・アクセス拒否を通り、成功後のyield、tax、入力消費、通知も確認する。Host再起動後の同一item再試行が空結果となり、yieldとwalletを二重更新しないことも確認する。

### P2: 権限のあるcorporation hangar入力を無視する

V24.01の `/home/user/eve-logic/reprocessing/ui/controller.py:41-49` はreward、mineral、active ship、character自体を除外するが、corporation ownerを拒否しない。再処理先UIはofficeとdivisionを列挙する（`/home/user/eve-logic/reprocessing/ui/reprocessingWnd.py:344-366`）、実行時にownerとlocationを渡す（`/home/user/eve-logic/reprocessing/ui/itemReprocessor.py:17-46`）。EveJSは `/home/user/evejs-261028.1/server/src/services/reprocessing/reprocessingRuntime.js:835-873` でcorp officeとdivisionのHangar Take権限を調べ、同 `:875-889` のquoteでもそのresolverを使う。以前のC#はキャラクター所有item以外を計画から落としていた。

`RootAtFacilityAsync` がcorporation itemについて、認証済みcorporation、facilityのoffice folder、division flag 115–121、persisted Hangar Take権限を確認し、container親もたどるようにした（`dotnet/src/Eve.Services.Station/ReprocessingService.cs:472-505`）。corp outputのoffice folder照合もinventory上の`OfficeFolderId`に統一した（同 `:265-277`）。DB Host回帰はregistered RPCでquoteとcorp hangarへのyieldを確認し、別office、Hangar Takeを持たないmemberのquote/実行を拒否し、入力・出力が変わらないことを確認する（`dotnet/tests/Eve.Host.Tests/ReprocessingHostTests.cs:133-240`）。

### P2: mutated moduleの修理quoteが0 damageになる

V24.01の `/home/user/eve-logic/eve/client/script/ui/station/repairshop/base_repairshop.py:62-71` はquote後にstation/structure修理RPCを呼ぶ。UIは同 `:238-253` でquoteの`damage`、`maxHealth`、価格から表示を作り、`:245-247` で切り上げdamageが0の行を落とす。EveJSは `/home/user/evejs-261028.1/server/src/services/station/repairRuntime.js:355-363` でmutated result typeではなくsource typeのdogmaを使う理由を説明し、`/home/user/evejs-261028.1/server/src/services/fitting/liveFittingState.js:1781-1794` でsource属性に保存済みmutation overrideを重ねる。旧C#はresult typeだけからHPを読んでいた。

`RepairService.QuoteRowAsync` はItemMutationStoreのsource type dogmaを読み、保存済み`MutatedValues`を重ねてshield/armor/hull HPを計算する（`dotnet/src/Eve.Services.Station/RepairService.cs:238-269`）。registered Host回帰はmutated moduleを記録した後にHostを再起動し、quoteの正damageと変異後maxHealth、修理後condition、wallet debit、journal、`OnModuleAttributeChanges`→`OnItemChange`の順を確認する（`dotnet/tests/Eve.Host.Tests/ReprocessingHostTests.cs:371-434`）。

## テスト先行と実行結果

修正前に3件それぞれのHost回帰を追加して未変更コードで実行した。structure bind拒否、corp quote欠落、mutated moduleの0 damageで各テストが失敗することを確認してから修正した。さらにstructure testでowner corporation walletへの入金をassertし、owner credit実装前にそのassertが失敗することも確認してからsettlementを追加した。修正後は次のコマンド群を`EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p4`で実行した。DB `eve_p4`は存在確認済み。

- Release build: `Eve.Inventory`, `Eve.Services.Station`, `Eve.Host` は全て成功、各0 warning。
- `Eve.Services.Station.Tests`: `ReprocessingServiceTests`と`RepairServiceTests`、21 passed / 0 failed / 0 skipped。
- `Eve.Host.Tests`: `ReprocessingHostTests`、station repair wire、structure repair wireの絞り込み、7 passed / 0 failed / 0 skipped。
- `Eve.Inventory.Tests`: `ItemMutationStoreTests`と`ItemMutationServiceTests`、29 passed / 0 failed / 0 skipped。

## 確認していないこと

- GUIクライアントから接続して行うwire captureと画面上の実操作。Host回帰は実graphical-client検証ではない。
- EveJS runtime/serverを起動して同じfixtureを実行する比較。EveJSの実装は該当処理をファイル・行単位で追跡したが、同リポジトリにはこの3経路を実行するfocused test command/fixtureが見つからなかった。
- 独立した並行リクエスト競合試験。既存transaction locksと既存のstation retryテストは通したが、structure/corp経路のraceを新規に注入してはいない。
- 今回のpush後のmain CI。直近main CIは確認時点で実行中だったため、push後に完了まで監視する。

以上より、監査報告の3所見はすべて真として修正した。誤検知はなかった。
