# d916d77 simulation startup backpressure

## 検証した事実

- main の CI run 1683（`d916d77b670d`）は `test_a_ship_undocks_flies_and_docks_as_the_reference_did[race-1]` で失敗した。undock RPC は `CustomNotify` を返し、後続の ballpark と dock 通知が欠けていた。
- 同じ run の `live-logs/host.out` に `the simulation link: outbound queue is full (64 frames)` が記録されている。
- `RegisterEssSystemsAsync` は pinned SDE の対象 nullsec 全てに ESS を登録する（`dotnet/src/Eve.Host/Composition/EssComposition.cs:44-65,69-88`）。この SDE に同じ条件を照会すると 3,285 系が対象だった。
- `d916d77` は ESS 配置前に全系のアンカーを一括取得するよう変更した。その後のループは配置ごとに同期 `PlaceStructure` を呼び、`SimClient.Send` は満杯の bounded queue を link failure にする（`dotnet/src/Eve.Host/Composition/EssComposition.cs:233-253`, `dotnet/src/Eve.Space/SimClient.cs:27-29,584-603,1174`）。これにより 3,285 件の配置が 64 枠へ一気に送られる。
- `SimClient.SendAsync` は queue に空きが出るまで待機する既存の backpressure 経路である（`dotnet/src/Eve.Space/SimClient.cs:659-689`）。配置初期化からこの経路を使うようにした。

## 変更と確認

- `ISimulation.PlaceStructureAsync` を追加し、`SimClient` では `SendAsync` に接続した。ESS 初期配置はこの API を await し、送信途中の切断を初期化エラーとして報告する。
- `SimClientTests.Structure_placements_apply_backpressure_without_breaking_the_simulation_link` は送信側 stream を止め、queue 容量の 2 倍の構造物配置を行い、link を維持したまま全 frame を送ることを確認する。

## 未確認

- この修正後の server stack と graphical V24.01 client はまだ実行していない。push 後の main CI live job で undock シナリオを確認する。
