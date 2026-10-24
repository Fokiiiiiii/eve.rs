# Inventory RPC fixes — canonical main handoff

## Follow-up (2026-10-19)

The `charMgr` global-assets root gap is partially closed. `ListStations` and global `List` now resolve personal items through parent containers to either static stations or persistently deployed structures; `List` returns root items and `ListStations` reports location rows. `ListStationItems` returns personal items stored directly at either root, and `ListIncludingContainers` retains descendant items that resolve to a valid root. Structure IDs and location metadata come from the existing deployed-structure directory source; nested contents come from the authoritative inventory store. Focused `eve_p2` regression `Global_assets_resolve_structure_roots_and_nested_personal_items` passed. The Host wrapper end-to-end regression remains open in `docs/project/TODO.md`.

`contractProxy.GetLoginInfo` has a confirmed V24 callsite (`eve/client/script/ui/shared/neocom/contracts/contracts.py:190-205`). It remains open because the Host has no persisted contract records or read model. The ledger row is updated with the client fields and concrete missing state owner.


日付: 2026-10-13。root: `repository root`。branch: `main`。
開始HEAD: `dda03ca0464043822e68c8348604ff34eadedcac`。
保存前に観測したHEAD: `b2557b42e8077fe8c6376a9189f8a0072a7d26be`。親がmainを進行中。以下の修正はそのworking treeにある未commit差分を含む。
EveJS参照: `EveJS repository` / `828823a75f211fdecb945b087a26c225f111491f`。
CCP V24.01参照: `eve-logic repository` / `1dbb5711a072a146e1d228365668ac05655eefe1`。

## 結果と検証境界

- 再照合対象は `invbroker.MultiAdd`、`invbroker.ListByFlags`、`invbroker.SetLabel` の3 RPC。3件ともcurrent source・CCP callsite・EveJS本文を再照合した。未比較0。静的シナリオを回帰テストにしたが、実行による独立再現は未実行。
- 数量の引渡し、keyword flags、改名の拒否・正規化・保存後通知を修正した。旧QtySpec、新しいitem store、捕捉packetのhardcodeは導入していない。
- 回帰テスト16件追加、既存Inventoryテスト1件の拒否期待を更新。すべて未実行。PostgreSQLの実item行を使い、通知を復号してDB行と比べ、次のListとcallbackを検査する。
- `SetLabel` の周囲へのslim-name broadcastには既存Rust側の残件がある。今回のC# callbackは保存後の実stateを既存refreshへ渡すが、RPC全体のEveJS完全一致・live成功とは判定しない。
- build/test/Git mutation/子agentは行っていない。親のcompiler evidenceで判明した `PyFloat` 3箇所のCS0246は、既存wire型 `PyReal` に修正した。再compile結果は親待ち。
- 行番号はSerenaの最新working-tree symbol位置を1-basedに換算。CBMの既存graphは編集後に旧symbol範囲を返す場合があったため、旧範囲をcurrent行根拠に採用していない。

## 1. MultiAdd — 部分数量をauthorityへ渡す

### 確認した故障とcallsite

修正前のcurrentは`qty`/`quantity`を読まず、dockの `MoveAsync`/`MoveContainerAsync`へnull、spaceの`MoveAllAsync`へ数量なしで渡した。
入力例: own stack10、cargo inventory bound、`MultiAdd([itemID], stationID, qty=3, flag=5)`。旧currentは10全量を移し、sourceを残さない。EveJSは3を指定してmoveする。capacity判定、source数量、生成ID、通知量、次のListが変わる。

- CCPのkwargs転送: `eve-logic/eve/client/script/environment/invCache.py:1027`、特にmonikerへの`**kw`転送は1058。
- CCPの通常drag複数items: `eve-logic/eve/client/script/environment/invControllers.py:558`、560。複数選択はflagを渡す。数量を選ぶ通常single-item経路はAddであり、V24のMultiAddにquantityが高頻度で出るという主張はしない。
- EveJS: `evejs-v0.12.9/server/src/services/inventory/invBrokerService.js:12779`、数量正規化2592、適用量2707、spaceの合計容量preflight5126。
- 修正後current: `../../../dotnet/src/Eve.Services.Inventory/InvBrokerService.cs:922`。

### 修正と保持した契約

- `qty`を優先。未指定またはNoneなら`quantity`へfallback。正のPyInt/PyLongを通し、None/0/負数/非整数wire型は既存のwhole-stack既定へ戻す。EveJSのNumber coercion全種を新規導入したものではない。
- dock/containerは既存のowner/source/destination/role/capacity/locking判断を通したまま、数量だけを渡す。無効source IDのskip、他ownerの拒否、authorityの超過数量拒否、singleton扱い、先行itemのcommitと通知後に後続が失敗する現行契約を保持する。
- spaceは既存batch入口を維持。helperの数量overloadは `../../../dotnet/src/Eve.Services.Ships/ISpaceContainers.cs:42`、production委譲は `../../../dotnet/src/Eve.Services.Space/SpaceContainers.cs:286`。
- authoritative batch: `../../../dotnet/src/Eve.Ships/CharacterShips.Containers.cs:288`。actual selected units全件の容量を変更前に判定し、既存 `PgItemStore.MoveStackAsync` をreuseしてreal splitを作る。一つのtransaction commit後にsource残量行・new split行を返す。
- 既存split helper: `../../../dotnet/src/Eve.Inventory/Items.cs:413`付近の`MoveStackAsync`（最新symbol位置は親の進行で変動し得る）。item mutation/insurance/event ownerも同じhelperを通る。
- production通知は既存 `SpaceContainers.TellMoveAllAsync` を保持: `../../../dotnet/src/Eve.Services.Space/SpaceContainers.cs:320`。commit済みrows → OnItemsChanged → sceneのcontainer状態 → batch client event → 本当に空の場合のみOnWreckLootAll/empty-can removal。
- 数量なしの旧interface/callerは従来overloadにdelegateする。partial batch未対応の代替providerは数量を捨てた成功を返さず明示的に拒否する。production SpaceContainersはquantity overloadを実装済み。

回帰例: source10→source7+新ID3、同ID重複、qty/quantity優先、None/0、超過数量、container、corp deposit/take、後続owner拒否、後続capacity拒否、space全件容量拒否（残容量0.05m3・3units×2=0.06m3）、成功後List。

## 2. ListByFlags — CCPのkeyword-only呼出し

CCP `invCacheContainer.ListByFlags` は未cache flagsを `self.moniker.ListByFlags(flags=uncachedFlags)` と送る。
根拠: `eve-logic/eve/client/script/environment/invCache.py:1167`、keyword-only call1174。
旧currentはarg0=Noneから空集合を作るため、存在するitemsも0行となり、clientはそのflagをlisted扱いにしてしまう。

EveJS根拠: `evejs-v0.12.9/server/src/services/inventory/invBrokerService.js:11096`、flag-list normalization2522。
修正後: `../../../dotnet/src/Eve.Services.Inventory/InvBrokerService.cs:444`。
arg0がNoneの時だけkwargs.flagsへfallback。明示的な空list/tupleはpositional優先。重複flagは集合化。bound/session/owner filteringと現在のPyList/PyPackedRowを保持。
Row type差は今回の破損に混ぜていない。

回帰例: args=[]/args=[None]＋flags=[4,4]はowned行のみ1件。args=[[]]＋flags=[4]はempty。scalar flagsはempty。他sessionのboundはNone。

## 3. SetLabel — 拒否、名前規則、保存後通知

### 根拠

- CCP rename entry: `eve-logic/eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:321`、RPC343、clientのOnItemNameChange344。
- fittingからの直接rename: `eve-logic/eve/client/script/environment/fittingSvc.py:525`、625。
- ship maxLength32: `eve-logic/eve/client/script/ui/shared/shipNameDialog.py:45`。container UI maxLength100はmenuFunctions335。
- cfg cache consumer: `eve-logic/carbon/common/script/sys/cfg.py:146`。OnCfgDataChangedはevelocationsを更新しOnPostCfgDataChangedへ進む。
- EveJS handler: `evejs-v0.12.9/server/src/services/inventory/invBrokerService.js:10636`。ItemNotYours/CrpAccessDenied、空名no-op、container OnItemChange→OnCfgDataChanged→presentation。cfg payload helper2606、ship normalization `evejs-v0.12.9/server/src/services/ship/shipNameUtils.js:5`。
- 修正後current: `../../../dotnet/src/Eve.Services.Inventory/InvBrokerService.cs:667`。拒否683付近、保存703、cfg710、callback714。

### 変更

- 他ownerにはItemNotYours。同corpのlocal divisionは既存Take authorityで検査し、role拒否をCrpAccessDenied＋reason=(101,InsufficientRoles label)で表す。corp/locality/roleを飛ばしてrenameしない。
- shipは既存 `CharacterShips.NormalizeName` をreuse（`../../../dotnet/src/Eve.Ships/CharacterShips.Assembly.cs:135`）。containerは改行列をspace、trim、100文字。None/empty/空白だけはno-opで既存名を消さない。
- `PgItemStore.SetCustomNameAsync(Item expected,...)` はrow lock下でauthorized snapshotの全fieldを再照合。不一致は書かない。UPDATE RETURNINGで取得した実rowをcommit後に返す。根拠: `../../../dotnet/src/Eve.Inventory/Items.cs:236`、239。
- 既存long-ID→bool overloadは保持。AssemblyとCharacterSelect testの呼出し契約を変更しない。
- active shipのsession shipNameを更新。dock containerはOnItemChange、続いてOnCfgDataChanged(charid,evelocations,7-field list)。shipはcfgの後に既存SpaceContainers helperから既存SpaceSessions.RefreshShipAsyncを呼ぶ。Host constructor配線を増やさず、既存spaceContainers instanceをreuse。
- wireの浮動小数型は `PyReal(double)`。`../../../dotnet/src/Eve.Marshal/PyValue.cs:146` でSerena/CBM確認し、親compilerのPyFloat CS0246三箇所を修正した。修正後compileは未実行。
- ALSC audit-rule、Structure rename、in-space container renameへsource範囲を広げていない。

### 意図差・残件

既存eve.rsが認める「local corp shipをTake権限でrename」およびdock containerの対応範囲は保持した。EveJSのcorp分岐はsingleton cargo containerに限定されるため、このアクセス範囲をEveJS一致とはしない。今回の依頼は既存owner/place/role保持であり、既存Inventory回帰もcorp ship許可を記述している。CCPの全role意図は別の検証項目。

既存refreshは実DB snapshotから新名をSimShipSpecへ渡す:
`../../../dotnet/src/Eve.Services.Space/SpaceContainers.cs:308` →
`../../../dotnet/src/Eve.Services.Space/SpaceSessions.Modules.cs:683`、643 →
`../../../crates/eve-space/src/scene/session.rs:433`。

**修正済み:** `Scene::respec` now includes name changes in the existing slim notification condition. The V24 bracket handler replaces its slim item and clears the cached display name; EveJS updates the caller's presentation without broadcasting this notification. The compatibility extension is recorded in `docs/reference/compat/DEVIATIONS.md`. The focused Rust regression passed; a graphical-client observer walk remains outstanding.

## 4. caller / Host確認

変更した共有helperの既存direct callerはCBM inboundとliteral検索で照合した。

| 対象 | callerと判断 |
|---|---|
| PgItemStore.SetCustomNameAsync旧overload | CharacterShips.AssembleOneAsync(`../../../dotnet/src/Eve.Ships/CharacterShips.Assembly.cs:75`)、InvBrokerService.SetLabel（新guard overloadへ変更）、CharacterSelect test(`../../../dotnet/tests/Eve.Services.CharacterSelect.Tests/CharUnboundMgrTests.cs:512`)。旧bool APIを保持。 |
| CharacterShips.MoveAllInSpaceAsync | production SpaceContainers.MoveAllAsync。既存引数列の後ろにoptional quantityを追加。数量なしは従来状態。 |
| ISpaceContainers.MoveAllAsync | InvBrokerService、Space LootAllTests、Inventory FakeSpace、Host InSpaceInventory test adapter。旧overload保持。Inventoryの既存LootAll callback assertionsを維持。新quantity callbackの拒否を追加テスト。 |
| new RefreshShipNameAsync | callerはSetLabelのみ。productionは既存SpaceSessions.RefreshShipAsyncへ接続。Inventory test callbackはcommit済みrowとcfg順序を読む。 |

CCPのinventory RPC callerは、UIの同名SetLabel(button label等)を除いて照合:
- MultiAdd: invCache1027/1058、invControllers558/560、droneFunctions271、tryFit135、fittingUtil157、item1071、buttonInventory45、orbitalMaterialUI185。
- ListByFlags: invCache1174、itemShip415、inventoryutil/client/inventory.py111/139。remoteへのkwargs変換はinvCacheにある。
- SetLabel: menuFunctions343、fittingSvc525/625。MenuSvc.SetName3647はmenuFunctionsへ委譲。
すべて `eve-logic/` 配下。index全体を一括scanする検索はdeadlineとなったため、environment/UI/service/inventoryutil等に分割した。期限切れを「callerなし」の根拠にしていない。dynamic/reflectionで構築される全呼出しの非存在までは静的に証明していない。

Host実配線:
`../../../dotnet/src/Eve.Host/Rpc/InventoryRpc.cs:43` →
`../../../dotnet/src/Eve.Host/Persistence/Inventory/HostInventoryServiceFactory.cs:40`。
same items/ships/statics/spaceContainersと実corporation role providerを渡す。role providerは同file:62以降。route存在だけをstate同値の証拠にしていない。

## 5. regression tests / 親の直列実行

全16件がPgFact。EVE_PG_URL未設定ではskip、SDEがない場合はRigで失敗する。parentの既存PostgreSQL/SDE環境を使う。新しいtest DB／server／buildはこの担当で起動していない。

| test | 1-based根拠 | 実行 |
|---|---|---|
| `Flags_kwargs_populate_the_uncached_client_flags_and_positional_flags_take_precedence` | `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryRpcFixTests.cs:64` | 未実行 |
| `MultiAdd_honors_both_quantity_names_and_publishes_only_committed_real_split_rows` | `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryRpcFixTests.cs:83` | 未実行 |
| `MultiAdd_qty_precedence_null_fallback_and_invalid_amount_keep_existing_authority_rules` | `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryRpcFixTests.cs:122` | 未実行 |
| `MultiAdd_preserves_prior_commits_and_notifications_when_a_later_owner_is_refused` | `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryRpcFixTests.cs:148` | 未実行 |
| `MultiAdd_into_a_docked_container_uses_the_same_real_stack_authority` | `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryRpcFixTests.cs:173` | 未実行 |
| `SetLabel_normalizes_ship_names_then_publishes_the_committed_location_name` | `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryRpcFixTests.cs:190` | 未実行 |
| `SetLabel_container_sends_item_change_before_cfg_and_refuses_foreign_ownership` | `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryRpcFixTests.cs:221` | 未実行 |
| `SetLabel_corporation_container_requires_take_role_and_keeps_the_authoritative_owner` | `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryRpcFixTests.cs:251` | 未実行 |
| `Guarded_name_update_refuses_a_stale_item_instead_of_announcing_a_fabricated_rename` | `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryRpcFixTests.cs:274` | 未実行 |
| `Guarded_name_update_refuses_changed_custody_and_only_one_concurrent_writer_can_rename_a_snapshot` | `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryRpcFixTests.cs:287` | 未実行 |
| `Space_batch_quantity_splits_real_rows_and_transfers_only_the_selected_units_to_the_pilot` | `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryRpcFixTests.cs:325` | 未実行 |
| `Space_batch_capacity_is_checked_for_all_selected_units_before_any_item_is_changed` | `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryRpcFixTests.cs:346` | 未実行 |
| `MultiAdd_keeps_the_first_split_committed_when_the_next_stack_exceeds_remaining_capacity` | `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryRpcFixTests.cs:370` | 未実行 |
| `MultiAdd_corporation_split_keeps_source_custody_and_requires_take_role_for_the_reverse_move` | `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryRpcFixTests.cs:394` | 未実行 |
| `MultiAdd_forwards_quantity_to_the_space_batch_callback_and_preserves_its_refusal` | `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryRpcFixTests.cs:453` | 未実行 |
| `SetLabel_space_presentation_callback_reads_committed_name_after_the_cfg_notification` | `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryRpcFixTests.cs:477` | 未実行 |

上表のlineは追加時点。全16件の一覧は最新fileのPgFactから確認した。
既存test変更: `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryServiceTests.cs:453`、corp Take拒否のNone期待をCrpAccessDeniedに更新し、許可時の成功およびAdd/MultiAdd/mutation assertionsを保持。

親へ渡すコマンド（この担当は未実行）:

```powershell
Set-Location eve.rs
dotnet test dotnet/tests/Eve.Services.Inventory.Tests/Eve.Services.Inventory.Tests.csproj --no-restore --filter FullyQualifiedName~InventoryRpcFixTests
dotnet test dotnet/tests/Eve.Services.Inventory.Tests/Eve.Services.Inventory.Tests.csproj --no-restore
dotnet test dotnet/tests/Eve.Services.Space.Tests/Eve.Services.Space.Tests.csproj --no-restore --filter FullyQualifiedName~LootAllTests
dotnet test dotnet/tests/Eve.Services.Dogma.Tests/Eve.Services.Dogma.Tests.csproj --no-restore --filter FullyQualifiedName~MultiAdd_uses_the_client_auto_fit_flag
dotnet build dotnet/src/Eve.Host/Eve.Host.csproj --no-restore
```

parentはbuild/testを直列で管理。テスト16件の成功、CS0246の消失、実clientの表示はここでは未確認。
ID値はfresh split IDを元itemと別であること/real rowであることとして検査し、特定IDの一致を要求しない。時刻/snの固定同値は要求せず通知name・payload・順序・commit境界を検査する。

## 6. 今回触らないTODO / 修正候補

以下は105 RPC監査snapshot由来のhandoff。親が別修正を進行中のため、今回の最新HEADで未実装確定と再認定した一覧ではない。着手前に現行mainと親diffを再確認する。Row typeだけの差を破損へ分類しない。

| 区分 | 全method名 | 具体的候補・回帰 |
|---|---|---|
| 実装済み破損候補 | trademgr.Add、trademgr.MultiAdd | sourceLocation/active ship/tradability/accessibility＋batch preflight。valid+invalid mixed IDsでstage前の拒否、partial quantity/custody/abort。 |
| 実装済み破損候補 | trademgr.Abort | 新callIDのretryで二重復元しないclosed-success契約。complete/disconnect競合。 |
| 通知意図差・未確認 | trademgr.MakeOffer、trademgr.OfferMoney、trademgr.List | manifest mismatch/reset/complete時のevent sequenceとV24 bool state消費を比較。state representationだけを破損にしない。 |
| 実装済み破損候補 | reprocessingSvc.Reprocess | fromLocationID/全selected sourceの一致、explicit destination、wallet failure時のrollback。source違い2containersを一つへ集約しない。 |
| 未実装branch候補 | reprocessingSvc.MachoBindObject、reprocessingSvc.GetQuotes、reprocessingSvc.Reprocess | structureのservice/docking/access、SDE/skill/standing/implant yield、persisted outputsとwallet/通知。GetQuotes/Reprocessのouter list/tuple差だけは別区分。 |
| 未配線候補・親差分確認必要 | dogmaIM.InjectSkillIntoBrain | SkillHandler authority再利用、item消費/skill作成/拒否/後続read。親のSkillbook修正を二重実装しない。 |
| 未配線候補 | dogmaIM.CreateNewbieShip | no hull/already newbie/not docked、実ship作成後List/dogma/session/reconnect。 |
| 未実装provider候補 | map.GetBeaconCount、map.GetHistory、map.GetSolarSystemVisits | beacon lifecycle、statID/hours1/24、character visitsのpersistence/restart。 |
| 未実装branch候補 | repairSvc.MachoBindObject、repairSvc.RepairItemsInStructure、invbroker.GetInventory | structure/deliveryの真正stateとowner/service権限。station成功をstructure完成として数えない。 |
| consumer未確認 | stationSvc.GetAllianceSystems、stationSvc.GetSystemsForAlliance | global/alliance filterとrow形をV24 callsite/traceで確認。Alliance creation修正とは別メソッド。 |
| legacy export候補・V24必要性未確認 | repairSvc.MachoResolveObject、reprocessingSvc.MachoResolveObject | Moniker lifecycle traceで必要性を確定。古いroute名だけで未実装を断定しない。 |
| FIXED (Rust unit) | invbroker.SetLabel → Scene.respec | `name_changed` now joins the existing slim refresh condition; `scene::tests::a_renamed_ship_is_shown_again_in_the_slim_item` passes. Graphical-client presentation remains unchecked. |

Trade/Reprocess/Skillbook/Newbie/Map/Structure、Host/Rust source、および他担当artifactを変更していない。

## 7. 変更files / MCP / 最終レビュー

変更は以下9 filesのみ。parentの他dirty源泉は保持。

1. `../../../dotnet/src/Eve.Services.Inventory/InvBrokerService.cs`
2. `../../../dotnet/src/Eve.Inventory/Items.cs` — existing rename helperのguard/RETURNINGのみ。
3. `../../../dotnet/src/Eve.Services.Ships/ISpaceContainers.cs` — existing Inventory transfer helper contractの数量overload/rename callback。
4. `../../../dotnet/src/Eve.Services.Space/SpaceContainers.cs` — 同じInventory helperへの数量受渡しと既存ship refresh委譲のみ。range/safety/loot notification処理は保持。
5. `../../../dotnet/src/Eve.Ships/CharacterShips.Containers.cs` — existing authoritative Inventory batchの数量/capacity/splitのみ。
6. `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryServiceTests.cs`
7. `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryRpcFixTests.cs`（new、16 tests）
8. `../../../docs/project/service-response-audit/inventory-dogma-ships.md`
9. `../../../docs/project/service-response-audit/inventory-rpc-fix.md`（本書）

Serena manual読了/canonical activate、CBM mandatory実施。CBM Transport closed retry後は指定documented CLIの既存`C-Users-yutai-eve.rs`を使用し、rootがcanonicalであることをstatus/absolute snippetで確認。refsはread-only。
canonical AGENTS/AGENT_OVERRIDE/CLAUDE、tdd-loop/plan-first/ponytail/self-reviewを適用。ユーザーの実行禁止に従い、TDDのred/green実行はparentへhandoff。
diff全文とsource/test symbolを静的レビューし、scope内tracked filesの `git diff --check` は問題なし。new filesも保存後readbackを確認する。実行検証・compile検証の代用とはしない。

## 8. 親Inventory Debug compile指摘への最小修正

2026-10-13。canonical root `repository root` / main、開始時に観測したHEAD `9db7d3544e7241d05a9c16ecb02b785030d1eccd`。
親から「production Host buildは0 errors成功、Inventory Debugsuite testcompileはCS8604/xUnit2012/xUnit2031で失敗」の証拠を受領。以下は親の診断への修正記録で、担当によるbuild/test成功記録ではない。

変更対象は `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryRpcFixTests.cs` と本docの2 filesのみ。production source、他test、Git stateを変更していない。build/testは実行していない。

| 親diagnostic / 行 | 最小修正 | assertionの意味 |
|---|---|---|
| CS8604 / 204 | `saved.CustomName!` をPyStrへ渡す | 直前の `Assert.Equal("A B", saved.CustomName)` が非nullの期待値を検査済み。期待値検査を削らず、実DB名とのpayload比較を保持。 |
| xUnit2012 / 203 | Assert.True(Any(predicate))をAssert.Contains(collection,predicate)へ | 7-field location rowのIDと保存済み名が一致する要素の存在。 |
| xUnit2031 / 207・492 | Assert.Single(Where(predicate))をAssert.Single(collection,predicate)へ | OnCfgDataChangedがちょうど1件であること。2箇所とも保持。 |
| xUnit2031 / 409 | corporate itemのAssert.Singleをpredicate overloadへ | corporation ownerの行がちょうど1件で、そのreal itemを後続数量／custody検査に使う。 |

行数は変えていない。修正後の1-based根拠は同file:203、204、207、409、492。
Serena canonical activate後に3 test本文と全置換結果を確認。CBM既存projectも使用したが、この新testはgraph/snippetおよび限定検索で取得できなかったため、coverage gapとして扱い、Serenaのcurrent本文を根拠にした。検索0件を「test不存在」とは判定していない。

親の次回直列rerun（担当は未実行）:

```powershell
dotnet test dotnet/tests/Eve.Services.Inventory.Tests/Eve.Services.Inventory.Tests.csproj -c Debug --no-restore
```

production Host build 0 errorsは親からの確認済み情報。今回のtestcompile診断の消失、Inventory test pass/skip数は親のrerun待ち。assertionの弱化やproduction sourceへの追加修正は行っていない。

## 9. 親Inventory rerun 62 PASS / 1 FAILへのfixture修正

2026-10-13、canonical `repository root`。親のInventory suite結果は **63 total / 62 PASS / 1 FAIL / 0 SKIP**。
失敗は `SetLabel_normalizes_ship_names_then_publishes_the_committed_location_name` の旧203行のpayload要素検査。今回の修正後rerunは未実行であり、63 PASSとは記録しない。production Host build 0 errorsは前節の親報告を保持。

### 根拠と原因

- EveJS `_emitSetLabelCfgDataChanged` はrowを明示的な `buildList` で送る:
  `evejs-v0.12.9/server/src/services/inventory/invBrokerService.js:2606` →
  `evejs-v0.12.9/server/src/services/_shared/serviceHelpers.js:8`。
  フィールドはitemID/name/None/0/0/0/Noneの7件。tupleへのfixture変更やlist/tuple両方の許容は行わない。
- ASCII名はEveJSでも通常のPython str経路:
  `evejs-v0.12.9/server/src/services/_shared/clientNameUtils.js:13`、18。
- CCPは `OnCfgDataChanged(what,data)` から `UpdateCfgData` へ渡し、rowをcolumn indexで読んでcfgへ保存する:
  `eve-logic/carbon/common/script/sys/cfg.py:130`、146。tupleを必須にする契約ではない。
- current productionは7-field PyListを送信:
  `../../../dotnet/src/Eve.Services.Inventory/InvBrokerService.cs:710`。
  `ClientNotification.Build` はargsをmarshal bufferへ入れる:
  `../../../dotnet/src/Eve.Services/SessionChanges.cs:62`。
- current `PyStr("A B")` はstring table未掲載でOp.LongStringにencodeされる:
  `../../../dotnet/src/Eve.Marshal/MarshalEncoder.cs:75`。
  DecoderはLongStringを **PyBytes** として返す:
  `../../../dotnet/src/Eve.Marshal/MarshalDecoder.cs:362`。
  list opcodeはPyList、tuple opcodeはPyTupleとして区別される（同file:215）。
  StringTable.g.csに"A B"がないこともSerenaで確認した。
- 旧fixtureはdecoded nameを未marshalの `new PyStr(saved.CustomName)` とC#型込みで比較していた。外側collectionにPyListが2件あるという親出力は、row形をtupleへ変える理由にはならない。

### 最小修正と保持した検査

変更は `../../../dotnet/tests/Eve.Services.Inventory.Tests/InventoryRpcFixTests.cs:203` のfixture blockと本docのみ。production sourceは変更していない。

1. decoded PyListのうち7-field rowが**ちょうど1件**あることをAssert.Single predicate overloadで検査。
2. row全体を、同じship ID・DBから読んだ保存済み名のUTF-8 PyBytes・None2件・PyReal(0)3件の**厳密な7-field PyList**と比較。旧fixtureのitemID/nameだけの存在検査より、形と全fieldの検査を増やした。
3. 通知の同期callback内で別DB読み取りを行い、保存済み名が"A B"であることをassertするcommit境界の検査を保持。
4. OnCfgDataChangedが1件、session名、空名no-op、32文字制限、後続Listを保持。別testのItemNotYours/CrpAccessDeniedと会社role/custody assertionsは変更していない。

Serena／CBMでcodec・reference・CCP本文を照合し、Serenaで変更後test本文をreadbackした。現在のfixture block追加で以降の行は2行後ろへ移動した。
build/test/Gitは実行していない。親の次回直列rerun:

```powershell
dotnet test dotnet/tests/Eve.Services.Inventory.Tests/Eve.Services.Inventory.Tests.csproj -c Debug --no-restore
```

修正後test passと63 totalの最終内訳は親rerun待ち。




## Parent verification checkpoint (2026-10-13)
After the analyzer/string-wire fixture repairs, the parent observed the full Inventory Debug suite passing 63 tests, 0 failed/skipped. A selected Space suite covering abandon/control/structures/batch movement passed 18 tests, 0 failed/skipped. The existing Rust rename-only slim broadcast gap and graphical acceptance remain open.


## Follow-up: contracts read owner (2026-10-19)

`GetLoginInfo` and `GetMyContractEscrow` now use `PersistenceContractReadModel` and the PostgreSQL contracts schema. Core rowset tests (2) and the `eve_p3` Host read test (1) passed. Contract creation and lifecycle writes remain open; see `docs/project/CONTRACTS_DESIGN.md` and the contract row in `docs/project/TODO.md`.

## 11. Mixed item move/merge contention (2026-10-23)

The scale review explicitly left mixed-operation contention open. Added `ItemMutationStoreTests.A_move_racing_a_merge_leaves_one_valid_item_transition` with two independent PostgreSQL transactions released together. It verifies bounded completion and either legal result: the move wins and the original source remains in its new location, or the merge wins and the source is deleted while the destination has the combined quantity. The test uses the existing per-item row lock and ascending merge lock order; no production behavior changed. The test project builds locally. It was not run locally because this test requires PostgreSQL; focused Actions run `40039425362` passed. Corporation hangar move authorization is covered by `InventoryServiceTests.Corporation_ship_inventory_reads_and_docked_add_and_multiadd_enforce_division_access` and `InventoryRpcFixTests.MultiAdd_corporation_split_keeps_source_custody_and_requires_take_role_for_the_reverse_move`; focused Actions runs `40039559444` (corp assets) and `40039577298` (hangar role) passed.

## 10. Ship rename slim notification (2026-10-23)

The existing `invbroker.SetLabel` flow persists the custom name, then refreshes the active ship's `ShipSpec` through `SpaceSessions.RefreshShipAsync`. `Scene::respec` already emits `OnSlimItemChange` for security and stance changes, but omitted name-only changes. The existing payload serializes the ship's slim item, including its name; V24.01 `BracketMgr.OnSlimItemChange` replaces the displayed slim item and clears its cached display name. EveJS SetLabel also updates the client-facing presentation after saving the item name. No notification shape changed.

Added `name_changed` to the existing slim refresh condition. `scene::tests::a_renamed_ship_is_shown_again_in_the_slim_item` checks the notice, updated sim state and same-name no-op; it passed locally (1/1). `cargo +1.94.1 fmt --all -- --check`, `cargo +1.94.1 check -p eve-space --lib`, and `cargo +1.94.1 clippy --workspace --all-targets -- -D warnings` passed. `dotnet build dotnet/tests/Eve.Inventory.Tests/Eve.Inventory.Tests.csproj -p:UseSharedCompilation=false --no-restore` passed with 0 warnings and 0 errors after syncing with `origin/main`. The mixed item move/merge regression passed focused Actions run `40039425362`. The graphical client has not been exercised. For commit `9f708b3b`, selected run `40038710701` and push CI run `40038689663` completed with failures in the broader .NET/headless checks; see section 12.

## 12. Push CI follow-up (2026-10-23)

Commit `c2e1db90` passed focused Actions run `40039425362` (mixed move/merge), `40039559444` (corporation assets) and `40039577298` (corporation hangar division access). Push CI `40039416006` failed on five existing checks after the latest-main merge: the control-tower headless flow and DB scoop test used a ship with 5,040 m³ free for an 8,000 m³ object; the courier keywords test lacked the expected EveJS destination/reward fields; the contact test assumed `PyObject` dictionary rows; the RPC inventory test expected an older method count. The full CI also skipped the slow movement/warp/jump/target/module checks and skill-training-speed check.

The latest main commits corrected the headless cargo ship, mission keywords and contact row decoding. Push CI then found the RPC inventory snapshot still expected 1,378 rows while the registered Host exposed 1,382; this follow-up changes the expected count. The Space DB regression now uses the SDE Obelisk (type 20187) for its successful scoop path; capacity checks remain unchanged. The Space test project compiled locally with 0 warnings and 0 errors, and its PostgreSQL test passed focused Actions run `40042876238` (6.7 minutes) on commit `e831a724`. Push CI `40042866526` failed on the stale RPC count and is awaiting a rerun. No PostgreSQL database was used locally.

## 13. Space scoop and structure return follow-up (2026-10-24)

The structure-return guard now checks the authoritative pilot ship and system after boarding has changed the session ship ID to the structure. The live headless board/eject flow passed in Actions run `40044327870`; the Host regression for a ship boarding from 200 km passed in push CI run `40045506688`. The Space scoop owner-mismatch regression initially failed because its test passed the absent session `corpid`; it now passes the recorded pilot corporation instead. The Space test project builds locally with 0 warnings and 0 errors; no PostgreSQL test ran locally.

Push CI `40045506688` reported the scoop fixture issue, a headless Macho error and two Market test failures. The scoop fixture now uses the recorded pilot corporation (`8d745aa1`); later `origin/main` merges include updated market/headless fixtures and structure-return code. The focused scoop test passed Actions run `40047200844` on `1a8a622c`. On the merged main commit `0fd2ca16`, every .NET test shard passed, including Space and Host; the live headless job failed with Macho error 2, and its summary did not identify the test. The slow movement/warp/jump/target/module checks and skill-training-speed check were skipped. The latest `tests/test_structures.py` Actions run (`40048239944`, ref `d4e141a3`) passed with no failed tests after 45 minutes. No graphical client run was performed.

## 14. Ship drop and corporation ship inventory follow-up (2026-10-24)

Later main merges added the bound `ship.Drop` quantity response with per-item refusal notifications and corporation ship cargo access checks. `ShipDropContractTests` passed Actions run `40051631303`; the corporation ship division access regression passed `40052122485`. Local builds for `Eve.Services.Space.Tests`, `Eve.Services.Inventory.Tests` and `Eve.Host.Tests` passed with 0 warnings and 0 errors. No PostgreSQL test ran locally.

Full CI `40052033248` exposed two stale expectations: the Host RPC registry count was 1,383 after `ship.Drop` was added, and the corporation market test expected buyer journal ref 2 where the actual entries were reserve ref 42, market transaction ref 46 and tax ref 149. The Host snapshot now expects 1,383 (`062b7ce2`), and the market regression was corrected in later main commit `3c27f0bd`. Focused `ShipDropContractTests` (`40051631303`), corporation ship access (`40052122485`) and the Host RPC snapshot (`40053919851`) passed. Full CI `40054154565` passed on `ef6286e9`, including headless, Windows, Rust, all .NET shards, build and fixtures. The standard headless run skipped its slow movement/warp/jump/target/module checks and skill-training-speed check. No PostgreSQL database was used locally.
