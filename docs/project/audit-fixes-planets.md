# PI 監査 findings 修正記録

監査入力: `/home/user/reports/90-audit-eve-planets-r14.md`。V24.01 の全該当 client callsites と EveJS v0.12.9 の service/runtime 経路を再確認し、実際の Host 登録 RPC と PostgreSQL の回帰テストで修正を確認した。

## 確認済みの修正

### P1 — launch quantity の検証

`Colony.Launch` は在庫数だけでなく、全要求量が正数であり在庫以下であることを、いずれの commodity も減算する前に検証する。空・0・負数は `PleaseSelectCommoditiesToLaunch`、在庫超過は `CannotLaunchCommoditiesNotFound` で拒否する。検証済みの quantity が colony、item stack、launch record に使われる。

根拠は `ColonyCommands.cs:573-587`。V24.01 の `spaceportPin.py:37-48` は各要求数を見ず在庫が 1 個以上かしか見ず、`clientPlanet.py:394-420` は remote RPC が例外になるとローカル colony を巻き戻す。EveJS `planetRuntimeStore.js:1186-1199,3943-4004` は非正数を空入力から除き、空入力と要求数超過を拒否する。

`Planet_launch_rejects_nonpositive_and_overstock_quantities_through_registered_host_wire` は 6、0、-1 を Host wire で拒否し、DB state と launch/item を変えず、正しい 3 個の要求は 3 個だけを launch can に保存することを確認する。修正前に同テストが不正 quantity を成功応答として通すことを再現した。EveJS の runtime 関数も disposable store で実行し、超過・非正数拒否と有効な数量 3 の永続化を確認した。

### P2 — 他 owner の command pin / ECU 読み出し

`GetCommandPinsForPlanet` は惑星上の全 colony から owner ID をキーとした dict を作り、`GetExtractorsForPlanet` は全 owner の ECU summary list を返す。summary に client が読む `pinID`, `typeID`, `ownerID`, `latitude`, `longitude` を含める。読み出しに `(planet_id, owner_id)` index を追加した。

根拠は `PlanetMgrService.cs:159-188`, `Persistence.cs:124-135`, `Migrations/0005_colonies_planet_owner_index.sql:1`。V24.01 `otherPinManager.py:41-47,104-143` は owner-keyed command dict と pin summary fields を使う。EveJS `planetMgrService.js:1198-1230` は惑星上の全 colony を列挙し、owner-keyed dict と ECU list を返す。

`Planet_other_pin_and_full_network_reads_match_client_wire_shapes_and_include_other_owners` は Host RPC を通し、2 owner の command center と ECU、owner keys、pin IDs、owner IDs を確認した。修正前は list / caller-only data / 欠落 field の契約違反で失敗した。

### P2 — 他 owner の full network 応答

`GetFullNetworkForOwner` は colony がない場合も常に `(pins, links)` の 2 list を返し、link は endpoint pair list とする。pin row に client が使う `pinID` を含めた。

根拠は `PlanetMgrService.cs:146-157,638-675`。V24.01 `planetSvc.py:98-108` が remote を呼び、`otherPinManager.py:65-87` が tuple を展開し、各 pin の `pinID` と `link[0]/link[1]` を参照する。EveJS `planetMgrService.js:1181-1195` は 2 list と endpoint pair を返す一方、その `buildPinRow` (`:310-345`) は `pinID` を省く。client は `pinID` を要求するため eve.rs 側では補っている。これは EveJS 実装との差分で、client callsite に合わせた意図的な修正。

同じ Host wire test は populated network の pin IDs と link endpoints、owner colony がないときの空 list 2 個を検証する。EveJS の直接 handler 実行ではコンテナと link 形状を確認し、EveJS 側の `pinID` 欠落も確認した。

### P2 — colony 上限検査の同時実行

各 owner の PI colony 変更は、同一 DB transaction 内で owner-scoped advisory transaction lock を取得してから、同じ transaction から他 planet の colony 数を読む。owner ごとに直列化されるので、別 planet への同時初回設置でも同一の上限判定になる。

根拠は `PlanetMgrService.cs:455-471,509-518`, `Persistence.cs:138-147`。`Concurrent_command_center_placements_for_one_owner_observe_the_planet_limit` は PostgreSQL trigger で INSERT を遅延させ、2 session の異なる barren planet への同時 Host RPC を実行し、成功 1 件・拒否 1 件・永続 colony 1 件を確認する。修正前は成功 2 件と colony 2 件を再現した。

### P2 — DB commit 後の launch 配置失敗

launch row、commodity item、colony state と durable placement job をひとつの transaction で保存する。RPC 成功応答を送った後に scheduler を wake し、job は `LaunchRecord` の位置と有効期限から space item を保存して simulation に can を配置する。配置例外は 5 秒後に再実行し、成功後に `OnPILaunchesChange` を送る。job registration は Host の Planet composition で scheduler 起動前に行う。

根拠は `PlanetMgrService.cs:329-403,455-499`, `PlanetComposition.cs:18-31`。V24.01 `clientPlanet.py:410-420` は RPC 例外時に colony を復元し、`planetUISvc.py:170-178` と `planetWindow.py:56-62` は `OnPILaunchesChange` 後に launch 一覧を再読込する。EveJS `planetMgrService.js:1349-1400` は launch state を保存して physical container と通知を続ける。eve.rs はこの post-commit 配置を durable job に分け、RPC 成否を既に commit 済みの can 配置に依存させない。

`A_launch_remains_a_success_when_space_placement_fails_after_its_database_commit` は Host RPC の後に customs office 配置・colony notification・launch can 配置の例外を注入する。RPC 成功と DB commit、job の retry 時刻、SpaceItem 保存、scheduler 再生成後の配置成功、通知順序と job 完了を確認する。修正前は DB commit 後の launch can 配置失敗が RPC の `ErrorResponse` になることを再現した。commit 後の customs office 配置および colony notification も失敗を記録して RPC 成功を保つ。

## 今回の変更対象から除外した項目

`GetResourceData` が空の harmonic response を返す点は監査で再確認したが、新しい不具合としては扱わない。`docs/project/PI_DESIGN.md` の D1 / Scope (`:81-100`) にある通り、CCP native resource harmonic が利用できず、現在の deterministic resource field との差が既知の制限として記録済みである。

他の監査 findings はすべてコードと client contract で確認できた。虚偽と判定して捨てた finding はない。

## 実行して確認したこと

- eve.rs Release build: `Eve.Planets`, `Eve.Services.Planets`, `Eve.Host`, `Eve.Host.Tests`, `Eve.Planets.Tests` はすべて warnings-as-errors で成功、各 build の warning は 0。
- DB `eve_p6` で Planets domain/store の関連テスト 23 件、Host PI wire / persistence / concurrency の関連テスト 13 件が成功。skip は 0。
- EveJS disposable runtime store で launch runtime を実行し、同 service の direct API handlers で owner reads と full-network shape を確認した。Type rows は `eve_p6.sde.types` の pinned SDE 3396210 から取得した値を disposable store に使った。

## 未確認

- V24.01 graphical client での実機確認はしていない。Host RPC wire テストは graphical client 確認ではない。
- EveJS は runtime と service handler を直接実行したが、EveJS の full network Host / client セッションを起動した end-to-end 実行はしていない。
- Serena/codebase-memory MCP と指定 CLI はこの環境に無いため、追加調査は `rg`, `sed`, PostgreSQL の pinned SDE を使って行った。
