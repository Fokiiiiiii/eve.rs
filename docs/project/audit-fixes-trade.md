# 取引監査所見の修正

対象レポート: `/home/user/reports/90-audit-eve-trade-r14.md`。クライアントの全体コード `/home/user/eve-logic`、実行可能な参照 `/home/user/evejs-261028.1`、Host 登録から PostgreSQL 更新・通知までを照合した。

## 判定と修正

### P1: fitted charge が trade に入らない — 確認済み、修正

V24 の `invControllers.py` は module を PlayerTrade に移す前に Dogma の `UnloadAmmo` を呼び、charge 単体の追加も `UnloadAmmoToContainer` 経由で同じ操作を使う（`eve/client/script/environment/invControllers.py:558-575, 1648-1691`; `eve/client/script/dogma/clientDogmaLocation.py:1077-1127`）。EveJS は trade container を Dogma unload の宛先として扱い、TradeMgr custody に charge を入れて fitting transition を通知する（`server/src/services/dogma/dogmaService/moduleOperation.js:3979-4115`; `server/src/services/trade/tradeMgrService.js:1376-1420`）。eve.rs の通常の ammo unload はその virtual container を受け付けず、`NothingHappened` を返していた。

TradeMgr の共有 custody transaction に fitted charge の全 stack を staging する経路を追加した。既存の charge ID、ship、slot を保ち、abort と起動時 recovery で元の slot に戻す。Dogma 側は module の charge quantity / attribute と fitting refresh を通知し、TradeMgr は両参加者へ item change と priming を送る。module の後続 Add と settlement も Host 経由で検証する。部分 stack は EveJS と同じく拒否する。

確認対象の回帰テスト: `Registered_dogma_trade_unload_stages_a_fitted_charge_and_abort_restores_its_slot`, `Registered_trade_restart_restores_a_staged_fitted_charge_to_its_slot`, `Registered_dogma_unloads_fitted_charge_before_trade_stages_module_and_both_items_settle`。

### P2: corporation office division の item が trade に出ない — 確認済み、修正

V24 PlayerTrade は owner が character かどうかで item を拒否せず、Inventory の source ID から Add を呼ぶ（`eve/client/script/environment/invControllers.py:1673-1691`; `eve/client/script/environment/invCache.py:998-1025`）。EveJS は同一 station の直接 office division row について、active office、division Take 権限、lock を照合する（`server/src/services/trade/tradeMgrService.js:1476-1503`）。eve.rs は actor 所有だけを許していた。

TradeAuthority の corporate item 経路を直接 office division row、同じ station、active で未期限切れの office、persisted Take 権限、未 lock の item に限定した。nested corporation inventory は拒否する。新しい `origin_owner_id` migration は custody 中の offering actor と元 corporation owner を分け、abort/recovery では corporation owner に戻し、完了時にだけ相手へ移す。contained descendants の owner も完了時に recipient に更新する。

確認対象の回帰テスト: `Registered_trade_stages_corporation_office_items_and_restores_or_transfers_the_original_owner`。CEO の成功経路、非 member と locked item の拒否、abort の owner 復元、両者 acceptance 後の recipient 転送を確認する。

### P2: mutation 前に相手の現在位置を検査しない — 確認済み、修正

EveJS は Add / MultiAdd / OfferMoney / MakeOffer で双方の live session と trade location を検査し、相手が不在または dock 外なら trade を abort する（`server/src/services/trade/tradeMgrService.js:1068-1084, 2250-2336`）。eve.rs の bound service は呼び出し側だけを検査し、OfferAsync 自体は DB 状態だけで完了を判断していた。

これらの bound mutation と fitted charge staging で双方の現在 session と docked location を確認する。不一致なら escrow を abort して item を返し、acceptance を残さない。検査は EveJS と同じく mutation の入口で行う。

確認対象の回帰テスト: `Registered_trade_mutation_aborts_and_restores_items_when_the_peer_is_no_longer_docked`。

### P3: nested descendants が priming 通知から抜ける — 確認済み、修正

V24 trade service は `OnPrimingNeededForTradeItems` の受信 ID を `cfg.evelocations.Prime` に渡す（`eve/client/script/ui/station/pvptrade/pvptradesvc.py:109-111`）。EveJS は staged root と全 descendant を通知し、eve.rs は staged root のみを通知していた（EveJS `server/src/services/trade/tradeMgrService.js:1026-1059, 1087-1107`）。

TradeAuthority が staged root の item tree を列挙し、重複を除いた root と descendants の ID を両側に送る。

確認対象の回帰テスト: `Registered_trade_priming_includes_nested_inventory_items`。

### P3: structure の virtual trade item が typeID 0 — 確認済み、修正

V24 は PlayerTrade の fake inventory item に `GetSelfInvItem` を使う（`eve/client/script/environment/invControllers.py:1689-1691`; `eve/client/script/environment/invCache.py:968-974, 998-1017`）。eve.rs は NPC `StationCatalog` にない Upwell structure で typeID 0 を返していた。EveJS は trade virtual item に valid な default station typeID `1529` を返す（`server/src/services/trade/tradeMgrService.js:735-754, 886-900`; `server/src/services/_shared/stationStaticData.js:37-55`）。

NPC station は catalog の typeID を維持し、catalog にない virtual trade location には EveJS と同じ SDE station typeID `1529` を返す。StructureStore の dynamic structure type を使う案は採用しなかった。EveJS の virtual trade item は docked location の structure type ではなく固定 station type を返すためである。

確認対象の回帰テスト: `Registered_trade_self_item_uses_the_default_station_type_at_a_structure`。

**監査所見の判定:** 5 件すべてをコード、V24 client、EveJS で照合し、真の不具合として修正した。誤りと判定して除外した所見はない。

## 検証

### 実行した確認

- 実装前に追加した Host E2E 7件を元のコードで実行。7件すべてが各所見の症状で失敗し、skip は 0 件: fitted charge unload 3件は `PyNone`、corp office の `Add` は拒否、相手が dock を離れた後の mutation は例外なし、nested priming は root ID のみ、structure の typeID は `0`。
- Release build (`-warnaserror`) 成功、警告 0・エラー 0: `Eve.Inventory`, `Eve.Trade`, `Eve.Ships`, `Eve.Services.Dogma`, `Eve.Services.Station`, `Eve.Services.Space`, `Eve.Npc`, `Eve.Host`, `Eve.Host.Tests`。Host.Tests build/test は下記の既存 untracked source を除く一時 MSBuild target を指定した。
- DB-backed focused tests (`EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p6`): `TradeAuthorityTests` 12、ammo 関連 Ships tests 22、`TradeMgrRpcTests` 8、`AmmoHostTests` 12 はすべて成功、skip 0。
- Host E2E filter (`MarketTradeHostWireTests`, `NewEdenStoreHostTests`, `HostServerTests.Unload_ammo_returns_the_destination_item_ids_after_its_inventory_notice`) は実装前の 7 件が所見の症状で失敗した後、merged tree で29件すべて成功、skip 0。
- 修正後に実行した focused test 合計は83件、成功83・失敗0・skip 0。Trade 12、Ships 22、Station TradeMgr 8、Space ammo Host 12、Host E2E 29。
- 監査所見5件はすべてコード、V24.01 client callsites、EveJS と照合済み。`origin/main` の取り込みに使った CI run 1602 (commit `e839cfb7c`) は成功。
- 既存 untracked `dotnet/tests/Eve.Host.Tests/InventoryAuditHostWireTests.cs` は今回変更せず、事前からの compile error (誤った `InvBrokerService` constructor 引数) があるため Host.Tests の compile/test から一時 target で除外した。この別件のテストは未確認であり pass と数えていない。

### 未確認

- graphical V24.01 client を起動した画面結果、実 client packet trace、notification の実時間順序。
- この修正を含む push 後の main CI 結果。
- `/home/user/wt-cx6/.cache/sde` はこの worktree には存在しない。DB-backed tests は `eve_p6` の既存 SDE テーブルを使う。
- code-navigation MCP / Serena は公開されていないため、`rg` と `sed` で全クライアント callsite を検索・確認した。
