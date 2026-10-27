# shipFitting サービス深掘り監査

監査対象は V24.01 クライアント全体の service・moniker 呼び出しと、Host/EveJS の対応実装。

## クライアント呼び出し一覧

クライアントに `shipFitting` という RPC service / moniker はない。全体検索での同名ヒットは、fit simulation UI、inventory slot flag、devtools の import に限られる（`shipFittingSimulationButton.py:3,9,24`、`fittingBrowser.py:28`、`buttons.py:626-627`、`inventorycommon/const.py:2704`、`inventoryutil/client/inventory.py:92`）。

最も近い RPC は `ship.GetShipFittingInfo`。全 client tree で呼び出しは次の2か所のみで、どちらも位置引数 `fitting_id` を渡す。

| 呼び出し元 | 呼び出しと利用 |
|---|---|
| `eve/client/script/ui/crate/controller.py:78-81` | 固定 crate の fitting loot。戻り値を name/icon/type/insurance に分け、insurance があれば保険 loot も追加 |
| `eve/client/script/ui/crate/controller.py:258-261` | hacking crate の fitting loot。同じ4項目を利用 |

`eve/common/script/net/eveMoniker.py:43,46,52` は `ship` moniker を作るが、`GetShipFittingInfo` を bound call する caller はない。RPC 呼び出しは `RemoteSvc('ship')` の上記2か所のみ。ローカル `fittingSvc` は別物で、`fittingSvc.py:41,85,267` にある保存 fitting manager の処理である。

## メソッド監査: ship.GetShipFittingInfo

| 観点 | 確認結果 |
|---|---|
| 登録・到達 | `ShipService.cs:13,102-119` が `ship.GetShipFittingInfo` を公開。`CharacterServiceList.cs:67` が `AgentRpc()` を含め、`AgentRpc.cs:108` で fitting provider 付き `ShipService` を生成し、`HostComposition.cs:261` で service registry に登録する。TCP Host 経由の wire test は `ShipFittingInfoHostTests.cs:11-124`。`AgentRpc.cs:108-110` は `spaceSessions` がある通常構成では full `ShipService` を登録し、`SpaceComposition.cs:110,115,220` の通り `EVE_SIM=off` 等で simulation がなくても `ShipFittingInfoOnlyService` を `ship` として登録する。これは fitting lookup だけを公開する（`ShipFittingInfoOnlyService.cs:6-12`）。HostComposition は `HostComposition.cs:261` で registry に登録。TCP Host wire tests は `ShipFittingInfoHostTests.cs:11-124` と `ShipFittingInfoNoSpaceHostTests.cs:11-50`。 |
| 引数 | client は位置引数を1個渡す。Host の `IdOf` は `PyInt` と `PyLong` を受け、`PyNone` / 未対応型は ID 0 に正規化する。メソッド引数の keyword caller は client tree にない。`machoVersion` は RPC envelope kwargs として wire test `ShipFittingInfoHostTests.cs:62-70` で確認。 |
| 戻り値 | 4要素 tuple: name string, icon string, ship type integer, insurance string または None。`ShipService.cs:109-119` が形を作る。wire test は string の wire 表現 `PyBytes` と client 文字列値、`PyInt`、`PyNone` を厳密に確認する。client は controller の4変数代入後に `FittingLootItem` / `InsuranceLootItem` に渡す（上記2 caller、`lootItem.py:95-119`）。 |
| エラー | 未知 fitting は UserError ではなく、EveJS と同じ既定 tuple（`Fitting N`, default icon, 0, None）を返す。provider の例外はこのメソッドで握りつぶさない。client の crate open は `controller.py:100-103,282-285` で stack 状態を戻して例外を再送出し、特定 UserError を要求しない。 |
| 状態・永続化 | 修正前の本番 callback は Pg saved-fitting store だけを検索していた。一方、crate source fitting は `savedFittings` GameStore root にあり、crate claim backend も同じ root を読む（`PersistenceCrateServiceBackend.cs:141-149`）。そのため source ID が SQL store にない場合、Host は実在 fitting を既定値で返していた。修正後は callback が GameStoreCache の永続 root を先に読み、crate claim と同じ `FindFitting` parser で nameLabel/icon/insurance と alias fields を解決する（`FittingRpc.cs:13-21`、`PersistenceCrateServiceBackend.cs:170-221,252-266`）。既存の Pg store は fallback として残す。lookup 自体は read-only。client の次の `ClaimFitting` は別 RPC であり、Host は新しい fitting を Pg store に保存する（`lootItem.py:102-104`、`PersistenceCrateServiceBackend.cs:155-166`）。 |
| 通知 | lookup は読み取り専用で server notification を送らない。client の `OnClientEvent_CrateOpen` は先行する `crateService.OpenCrate` の後、metadata lookup より前に scatter される（`controller.py:94-98,273-278`）。本メソッド後の通知は client 側でも要求されない。 |

## EveJS 261018.1 との比較

`server/src/services/ship/shipService.js:1325-1351` は位置引数 ID を使い、見つからない fitting に同じ4要素既定 tuple を返す。見つかった場合は `nameLabel || name`、icon、shipTypeID、truthy insurance を返す。`server/src/_secondary/fitting/fittingStore.js:370-407` は label/icon/insurance alias を canonical field にし、`:603-633` は owners を順に調べる。今回の変更はこの source-of-truth と reply semantics に合わせた。EveJS の package scripts に本メソッド用 test entry は見つからず、EveJS runtime を直接起動しての再現はしていない。

既存の DB test `CrateServicePersistenceTests.cs:108-170,207-234` は GameStore source fitting ID 699 と claim 後の Pg ID 700 を分けている。テストコード上の根拠として確認したが、この job は PostgreSQL を使わないため実行していない。

## 検証状況

**実行済み:** `Eve.Host` build 成功、`Eve.Host.Tests` build 成功、ship fitting wire tests 3/3 成功。`ShipFittingInfoHostTests.cs` は canonical/alias metadata、optional fields、unknown ID、None、PyInt/PyLong と `machoVersion` を確認。`ShipFittingInfoNoSpaceHostTests.cs` は simulation session なしでも fitting lookup の route が届き、他の simulation method は公開されないことを確認。別の既存 wire test は `MachoBindObject(GetDirtTimestamp)` を確認するが、client に fitting method の bound caller はない。

**制約付き:** PostgreSQL 接続変数を外して Host test project を実行した結果は 520 passed / 100 failed / 227 skipped。ログで見た失敗は DB lane の `EVE_PG_URL` 欠落で開始時に止まっており、DB は接続していない。skip と failure は pass に数えない。

**未実施:** 実クライアント GUI、DB-backed persistence test、EveJS runtime の直接実行。これらはこの job の no-DB 条件および対象 method 用 EveJS test entry 不在のため確認できていない。
