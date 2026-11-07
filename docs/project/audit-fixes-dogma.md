# Dogma service R14 audit findings

監査報告 `/home/user/reports/90-audit-eve-services-dogma-r14.md` の全7件を、現行コード、V24.01 クライアント `/home/user/eve-logic`、EveJS v0.12.9 `/home/user/evejs-261028.1` で照合した。6件を修正し、1件は実在する未修正の機能欠落として残した。誤検知だった項目はない。

## 修正した項目

### P1: DogmaIM のスキル本注入

`skillsvc.py:784-788` は `InjectSkillIntoBrain` の RPC が成功した後に注入成功をクライアントへ通知する。EveJS `server/src/services/dogma/dogmaService.js:4190` は本の注入処理へ委譲する。Host が DogmaIM に `skillHandler.InjectSkillBooksAsync` を渡していなかったため、本もスキルも更新されないまま RPC が正常終了していた。

Host callback を配線し、スキル本消費、スキル永続化、通知順、Host 再起動後の値を確認する `CharacterHostTests.DogmaIM_skill_injection_consumes_the_skillbook_and_persists_the_skill_through_the_host` を追加した。

### P2: `UnloadAmmo` の個人用・法人用コンテナ搬出

V24.01 `clientDogmaLocation.py:1087-1104, 1106-1155` はコンテナ ID、owner ID、flag を DogmaIM に送り、監査ログセキュアコンテナの法人 owner も指定する。EveJS `moduleOperation.js:2819-2887, 3975-4115` は destination owner を保持し、所有権・アクセスを確かめて移動、数量更新、通知、戻り値を組み立てる。従来の eve.rs はタプルの owner を落とし、船倉・ハンガー以外の行き先を無処理にしていた。

Host からインベントリ state owner へ配線し、個人コンテナと法人コンテナの部分搬出・全量搬出、スタック統合、法人 owner、無権限拒否、容量不足時の変更なし、再起動後の永続化、同一法人コンテナに対する並行搬出を確認した。DogmaIM 応答の item ID と通知順もホストテストで確認する。対象はインベントリ内の実体 cargo container である。

### P2: `ChangeDroneSettings`

V24.01 `environment/godma.py:2337-2357` が要求する属性と型を確認し、EveJS `droneSettings.js:49,104` の部分更新・永続化と照合した。既存のキャラクター永続化 owner に接続し、属性の検証、部分更新、Host 再起動後の値を確認した。属性 ID は pinned SDE の `dogmaAttributes.jsonl` と一致する。

### P2: `CreateNewbieShip`

V24.01 `ui/station/base.py:597-617` と `idCheckers.py:493` は dock 状態、既に rookie ship に乗っている場合、作成 RPC のエラーを扱う。EveJS `dogmaService.js:4153-4176` と `ship/rookieShipRuntime.js` は race の rookie ship を選び、作成・fit・搭乗を処理する。Host callback を配線し、race と ship type は `CreationCatalog` と pinned SDE から解決する。船と starter fitting の作成、active ship と session の更新、通知順、二度目の要求に対する `AlreadyInNewbieShip` を確認した。

### P2: `GetAllInfo` ship-only 応答

V24.01 `environment/godma.py:2435` と EveJS `dogmaService/itemInfo.js:1525,1572,1792` の dock/space 分岐および ship-modified character attributes を照合した。ship-only prime の dock 状態別 wire 値と属性情報を修正し、Dogma service/wire テストを更新した。

### P2: structure brain 更新通知の target ID

V24.01 `common/script/dogma/effect.py:79` の `toItemID` と EveJS の初回 brain prime / 更新通知を照合した。初回応答と `OnServerBrainUpdated` が同じ target ID 解決を使うよう修正し、structure がある場合の ID を wire テストで比較した。

## 追加で確認し修正した通知不一致

クライアントの dock 判定は station と structure の両方を扱う（`ui/station/base.py:597-600`、`environment/invControllers.py:433`）。EveJS `structure/structureLocation.js:52` も両方を dock として判定する。DogmaIM の `TellAmmo` は `stationid` のみを見ていたため、`structureid` でドックしたクライアントに宇宙空間向け `OnItemsChanged` を送っていた。`ClientSession.DockedLocationId` に基づく判定にし、structure-dock wire test で `OnItemChange` 系の通知と順序を確認した。テストは変更前に誤った `OnItemsChanged` を検出した。

## 実在するが未修正の項目

### P3: Starbase `AddTargetOBO` / `RemoveTargetOBO`

V24.01 `parklife/playerOwnedTargetMgr.py:84-102` は battery ID を伴う OBO lock/unlock を DogmaLM に送り、`playerOwned.py:501` が `OnTargetOBO` を受け取る。EveJS `dogmaService/targeting.js:27-41` は `starbaseControlRuntime.js:210-225` へ委譲し、同 runtime が battery の control grant と独立した target lock state を管理する。eve.rs の DogmaIM targeting partial には通常の船向け targeting しかなく、`ISpaceTargeting.OrderAsync` / `SpaceSessions.OrderAsync` もログイン session の自艦 target state を扱う。POS battery の control grant、target state、通知、simulation command を所有する state owner は現行 .NET host/space runtime にない。

この欠落は実在する。DogmaIM に RPC 名だけを足したり、自艦の targeting API に委譲したりすると、battery の lock を作らない、または別の ship state を変更するため修正とは呼べない。既存の `posMgr` は control tower 一覧と unanchor を扱うだけ (`Eve.Services.Core/PosMgrService.cs`) であり、Upwell structure control は別機能である。POS control と simulation の authoritative path を追加する変更が必要なため、この監査修正では OBO を実装せず、未修正 gap として残す。

## 検証

2026-11-07 に merge 後のコードで実行した検証は次のとおり。

- Release build: `Eve.Host`, `Eve.Services.Dogma`, `Eve.Ships`, `Eve.Characters` は各 0 warnings / 0 errors。
- `eve_p5` の関連 .NET tests は全 pass、skip 0: `CharacterHostTests` 47件、`DogmaServiceTests` / `DogmaWireTests` / `ReloadServiceTests` 33件、`AmmoTests` / `ReloadTests` 14件、`SkillHandlerTests` 56件、登録済み Host の newbie/ammo wire 2件。
- 変更前の失敗確認: スキル注入 state、ship-only wire shape、brain target ID、drone settings 永続化、newbie ship 搭乗、一般コンテナ ammo 搬出、structure-dock の通知形式。
- GitHub Actions main CI run 1593 (`fcea50118de1`) は全 job と required gate が success。push 後のこの branch の CI は未確認。

V24.01 `playerOwned.py:428-472` は POS battery の assume/relinquish control を `posMgr` に送り、その後 `playerOwnedTargetMgr.py:84-102` が DogmaIM OBO RPC を呼ぶ。現行 `PosMgrService.cs:16-50` には tower list/unanchor だけがあり制御 grant callback がない。シミュレーション `targeting/mod.rs:382-410` は通常 ship pilot か controlled Upwell structure の lock state を持つが POS battery actor/lock state は持たない。DogmaIM に handler だけ足すと grant と simulation の authoritative path が欠け、自艦 target state の誤更新か fake success になるため、OBO は未修正 gap のまま残す。

`eve.js` の `npm run` には `start` と migration/data scripts のみがあり test runner はないため、同じシナリオの runtime 比較は未実施。ソース経路は照合した。実クライアント GUI、POS battery の live simulation、この branch の GitHub Actions CI は未確認。
