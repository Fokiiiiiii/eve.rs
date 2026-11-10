# 法人機能の深掘り修正 — CX3

日付: 2026-11-10  
対象: eve.rs の corporation / alliance Host と V24.01 クライアント、EveJS 261108.1

## この作業で確認した差分

今回、実装と Host wire regression を追加した実差分は NPC station の事務所賃料とメダルの受取人別公開範囲の2件。いずれも登録済み Host service を通すテストを修正前に追加し、旧挙動で失敗することを確認してから実装を直した。

### NPC station の事務所賃料

V24.01 の `officeManager.GetPriceQuote` は `OfficeManager.GetPriceQuote` から呼ばれ、返値をそのまま `RentOffice(cost)` に渡す (`/home/user/eve-logic/eve/client/script/ui/services/corporation/officeManager.py:113-117`)。station UI controller も quote と impound release quote を server に問い合わせる (`/home/user/eve-logic/eve/client/script/ui/shared/dockedUI/controllers/baseController.py:59-63,79-83`)。そのため client が表示する値と server が請求する値は同じ権威ある station quote に揃える必要がある。

EveJS 261108.1 の NPC station record は `officeRentalCost: 10000` を持つ (`/home/user/evejs-261108.1/server/src/services/_shared/stationStaticData.js:24-64`)。`getOfficeRentalCost` はその station 値を quote と rent settlement に使い、structure の quote は structure 設定から読む (`officeManagerService.js:184-204,450-497`)。station office の impound release は同じ rental cost の半額 (`officeManagerService.js:387-393`)。Pinned SDE 3396210 の `npcStations.jsonl` は owner/type/system 等を持つ (`.cache/sde/eve-online-static-data-3396210-jsonl/npcStations.jsonl:1253`) が、`officeRentalCost` フィールドは無い。SDE 全体にもこの価格列は見つからなかったため、NPC station の固定値は EveJS reference 値 10,000 ISK に合わせた。これは SDE 由来の価格ではない。

旧 eve.rs は station ID 等から 10,000–110,000 ISK の擬似価格を生成し、日ごとの office capacity に応じて変動させていた。client/server のどちらにもその計算根拠は無く、EveJS の固定 station 値とも異なる。追加した `CorporationOfficeHostWireTests.Registered_office_manager_quotes_the_client_station_price_and_rents_it_through_wire` は修正前に quote が期待値 10,000 に対して 64,900 となり失敗した。現在は quote、rent、impound fee、`stationSvc.GetStation.officeRentalCost` が同じ NPC station 定数を参照する。テストは実 Host registry / PacketDispatcher 経由で quote の PyLong、station record の PyInt、rent response、wallet settlement、通知順、DB row、Host restart 後の office read を確認する (`dotnet/tests/Eve.Host.Tests/CharacterHostTests.CorporationOffices.cs:12-113`)。Client supplied quote は display value として扱い、rent/refund の決済値は server state から引く。

### メダルの受取人別 privacy

V24.01 の Decorations panel は、表示中の medal flags だけを `SetMedalStatus` の medalID→status map に入れる。全件変更も `GetMedalsReceived(session.charid)` の結果から medal IDs を作る (`/home/user/eve-logic/eve/client/script/ui/shared/neocom/charsheet/decorationsPanel.py:86-128`)。service wrapper は map を1 positional argument として corporationSvc に転送する (`/home/user/eve-logic/eve/client/script/ui/services/medals.py:53-58`)。従って medalID keyed wire shape は共有定義の更新を意味せず、現在の pilot が受け取った medal の表示設定を編集する。

EveJS 261108.1 は呼出元 character が実際に medal を受け取っていることを検証し、該当 recipient rows の status だけを更新する (`/home/user/evejs-261108.1/server/src/services/corporation/corpService.js:526-559`)。本人の `GetMedalsReceived` には award status が返り、他 pilot の読み取りは status 3 の award に限られる (`:472-479`)。`GetRecipientsOfMedal` も本人および public award recipients のみを返す (`:591-599`)。

旧 eve.rs は corporation medal definition の status を更新し、Director / Personnel Manager 権限を要求していた。Host test を修正前に追加して実行すると、普通の受取人が `SetMedalStatus` を呼んだ際に成功 `CallRsp` ではなく `ErrorResponse` となり失敗した。現在は `medal_awards.status` に受取人単位で保存し、各 ID が呼出元 character の受取 award であることを store transaction 内で検証する。混在する有効/無効 ID は transaction 全体を rollback する。migration 018 は旧 shared status を既存 award rows に移し、definition status を public に戻し、status を 1..3 に制約する (`CorporationStore.cs:389-403`, `CorporationStore.Medals.cs:41-59`)。Host test は通常 member の変更、個別 privacy、cross-character filter、notification 不在、SQL persistence、restart、混在 batch の rollback を登録済み service wire 経由で確認する (`dotnet/tests/Eve.Host.Tests/CharacterHostTests.CorporationMedals.cs:12-106`)。

### EveJS 参照コードの直接実行

EveJS 261108.1 の隔離した GameStore で `CorpService.Handle_SetMedalStatus` に本人の medal map を渡し、続けて `Handle_GetMedalsReceived` を実行した。award row の status が 2 に保存され、read RPC が2 response partsを返すことを確認した。これは EveJS service method の実行であり、EveJS の packet dispatcher / graphical client の end-to-end 検証ではない。

NPC office quote handler も直接呼ぶことを試したが、この実行環境では EveJS の static station table がロードされず、session station ID を station として解決できなかった。office の EveJS 比較は上記の source trace に限る。SDE を EveJS の static table 形式へ import した実行比較は未実施。

## 関連する法人領域の既存記録

次の領域は repository 内の既存調査・実装記録に client / EveJS callsites と focused evidence がある。今回すべてを再実装・再監査したという意味ではない。

| 領域 | 既存記録 | 今回の扱い |
| --- | --- | --- |
| 法人作成、membership、CEO の skill-based member limit | `docs/project/character-phase/handoff-corporation-member-limits-cx3.md` | 現行 `Eve.Corporations.Tests` 全 slice を実行。client window の GUI walk は未実施。 |
| shares、外部 shareholder read、dividend role | `docs/project/service-audit/deepfix-corporations.md`; `docs/project/TODO.md` | 関連 Host tests を選択実行。今回、GUI / live client は未実施。 |
| applications / invitations、war declaration replay / eligibility / notifications | `docs/project/service-audit/deepfix-corporations.md`; `docs/project/audit-fixes-corporations.md` | 既存の focused tests と記録を参照。競合する全 invitation/application 遷移の再実行は未実施。 |
| titles / roles / office access、hangars / wallet divisions | `docs/project/SECURITY_STATUS.md`; `docs/project/TODO.md` | corporations test slice と関連 Host slice で確認。全 permission 組合せの GUI walk は未実施。 |
| offices、rent bills、impounding | `docs/project/audit-fixes-corporations.md` とこの記録 | quote/rent Host flow と office focused slice を実行。remote unrent と全 impound transitions の live client 確認は未実施。 |
| alliance RPCs / alliance member and application windows | `docs/project/service-audit/DEEPCHECK_allianceRegistry.md` | 選択した Host alliance tests を実行。全 alliance lifecycle / client transitions の再監査は未実施。 |
| standings / standings window | `docs/project/service-audit/DEEPCHECK_standingMgr.md` と `DEEPCHECK_standing.md` | 選択した Host standing tests を実行。graphical client は未実施。 |

これらの過去記録は作成時点のテスト結果として扱う。今回の変更の通過は、表にある全 gameplay flow を Definition of Done として再証明したことを意味しない。

## 今回の検証結果

### 実行して成功したもの

- `Eve.Corporations.Tests` Release slice: 192 passed, 0 failed, 0 skipped (`EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p3`)。protocol/migration を含む corporation tests と office tests を含む。
- Host medal filters: 3 passed, 0 failed, 0 skipped (`eve_p3`)。
- Host office filters: 12 passed, 0 failed, 0 skipped (`eve_p3`)。
- Corporation / alliance / war / standing related Host selection: 102 passed, 0 failed, 0 skipped (`eve_p3`)。
- `StationServiceTests`: 12 passed, 0 failed, 0 skipped (`eve_p3`)。
- Release builds with warnings as errors succeeded with 0 warnings / 0 errors for `Eve.Corporations`, `Eve.Services.Corporations`, `Eve.Services.Station`, `Eve.Host`, `Eve.Corporations.Tests`, and `Eve.Host.Tests`。
- EveJS 261108.1 isolated GameStore direct service flow: `SetMedalStatus → GetMedalsReceived`, recipient status 2 persisted.

### 未確認

- V24.01 graphical client 3396210 の起動、live / full headless scenario、network packet trace は実施していない。
- EveJS の direct office quote は static station data 未ロードで実行確認できていない。office behavior の EveJS 根拠は source lines のみ。
- Corp creation to membership transitions, invitations, applications, shares/dividend, CEO succession, role/title edge matrix, hangars / wallet divisions, impounding, alliance lifecycle, standings, and corporation windows are not all re-exercised end to end in this task. See the linked prior records; their remaining gaps remain open.
