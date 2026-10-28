# wars / warsInfoMgr / warRegistry 深掘り監査

## 結論

V24.01 client に RPC サービス名 `wars` はない。近い Host surface は read RPC `warsInfoMgr` と、`eveMoniker.GetWar()` が作る bound moniker `warRegistry`。`svc.war` はクライアント内サービスで Host service ではない。

静的追跡で、全6件の `warsInfoMgr` RPC と `warRegistry` bound methods を確認した。重大な差分は、`GetPublicWarInfo` の sequence/iterable 契約、実 set 入力を送らない wire test、negotiation state/API の未実装、及び client が呼ぶGM methodsの欠落。実装・回帰テストは完了していない。

Serena の既存ファイル置換は `No language servers available in the manager` で失敗した。origin/main merge では既存テスト・監査資料に競合が出たため abort し、先行作業を変更していない。PostgreSQL 接続なし。

## サービス名と route

- `eve/client/script/ui/services/wars/war_cso.py:23-68` の `Wars` は local `svc.war`; `GetMoniker()` が `eveMoniker.GetWar()` を Bind する。
- `eve/common/script/net/eveMoniker.py:185-198` は `Moniker('warRegistry', ...)` を作成。server mode は `IsAllianceOrCorpLocal` を問い合わせる。
- 実 Host composition は `dotnet/src/Eve.Host/Rpc/CorporationRpc.cs:73,78` で両 service を登録する。wire test `dotnet/tests/Eve.Host.Tests/WarsAndWormholeRpcHostWireTests.cs` は warsInfoMgr のみ手動登録し、PostgreSQL 依存の `[PgFact]`; bound `warRegistry` の Host wire test は見つからなかった。
- local `svc.war` の利用は `corp_ui_wars.py:212,220`, `warLandingPage.py:156`, `warDeclare.py:401,635`, `state.py:1019` など。これらは Host method ではない。

## warsInfoMgr RPC 全件

| RPC | 全体 client callers と消費 | Host route / state / errors / notifications | 判定 |
|---|---|---|---|
| `GetWarsRequiringAssistance()` | `corp_ui_wars.py:172`; 引数なし。iterable の wars、`warID`, `declaredByID`, `againstID` と len を使う（同:160-205）。 | 登録済み。session alliance/corp を補完し、共有 `war`/`war.ally` relation から list。read-only、専用 error/notification なし。既存 Host wire test にあり。 | 静的な形は整合。DB runtime check なし。 |
| `GetTop50(maxWarID)` | `corp_ui_wars.py:178,182`; cursor を送り、CRowset の `warID`/owner IDs と len を使って paging（同:160-205）。 | 登録済み。最大50行の cached CRowset。既存 Host wire test は PyLong cursor と `machoVersion` kwargs を送る。read-only。 | 形は整合。PyInt variant は wire test 未確認。 |
| `GetWarsByOwnerID(ownerID)` | `corp_ui_wars.py:227`, `warHistoryCont.py:61,66`, `infosvc.py:2712`; CRowset の `Index('warID')`, `timeFinished`, `timeStarted` を消費（`infosvc.py:2736-2748`）。 | 登録済み。owner side/ally の wars relation を rowset に投影。既存 Host wire test は ally war を確認。read-only。 | 静的な形は整合。PyInt variant は wire test 未確認。 |
| `GetWarsByOwners(ownerIDs)` | `war_cso.py:132-134`; 実入力は Python `set`。owner -> warID -> KeyVal の dict を `iteritems()` で読む。 | 登録済み。Host parser は list/tuple と set marshal 形を読むが、wire test は PyList のみ。共有 relation read、通知なし。 | 実クライアントの set wire regression がない。 |
| `GetWarsForStructure(structureID)` | `panelWarHQ.py:41`; KeyVal iterable を `timeDeclared` 順で表示（同:30-42）。 | 登録済み。warHQID が合う KeyVal list。既存 Host wire test にあり。read-only。 | 静的な形は整合。DB runtime check なし。 |
| `GetPublicWarInfo(wars)` | `structureIcon.py:44` が `self.wars` を渡し、同:50-65 で結果を iterable として走査し `againstID`/`declaredByID` を読む。`structureInfo.wars` は `structureEntryController.py:195-196` で返り、`structureDeployment.py:58-60` で件数として使う。 | 登録済みだが Host は引数0を単一IDとして扱い、KeyVal/PyNone を返す。EveJS も `src/server/src/services/corporation/warsInfoMgrService.js:210-214` で単一IDを読む。wire test なし。 | client が sequence/iterable を要求する形と Host/EveJS の単一値形が合わない。全体 client evidence を基準に実 wire 契約を修正すべき。 |

Host 実装は `dotnet/src/Eve.Services.Corporations/WarsInfoMgrService.cs`; EveJS 全6 methods は `src/server/src/services/corporation/warsInfoMgrService.js:147-222`。

## warRegistry bound RPC

### bind と war read

| RPC | Client evidence / consumer | Host/EveJS 結果 |
|---|---|---|
| `MachoResolveObject`, `MachoBindObject` | moniker 作成 `eveMoniker.py:185-198`; Bind `war_cso.py:58-65`。nested method/args/kwargs は bound packet で送信。 | Host で登録・nested dispatch。EveJS `warRegistryService.js:176-181`。bound route wire test なし。 |
| `IsAllianceOrCorpLocal()` | `eveMoniker.py:196`。 | Host/EveJS とも 1。 |
| `GetWars(ownerID)` | `war_cso.py:102`; 主要 consumers `corp_ui_wars.py:212`, `warWindows.py:307`, `warLandingPage.py:156`, `warDeclare.py:401,635`, `atWarTooltip.py:11`, `base_corporation.py:322,423`。warID->KeyVal と war fields/allies を使う。 | Host は共有 relations から dict。EveJS `warRegistryService.js:184-190`。read-only。 |
| `GetNegotiations()` | `war_cso.py:209,213`; ally/surrender type と warID で絞る。UI callers `corp_ui_wars.py:220`, `warEntry.py:105,217`, `warReport.py:238` が negotiation fields を読む。引数なし。 | Host は常に空 list。EveJS `warRegistryService.js:192-199` は owner の stored offers を返す。後続 read から offer が見えない。 |
| `GetWarNegotiation(id)` | wrapper `war_cso.py:230`; UI `warWindows.py:64`, `evewar/client/link.py:18`。UI は warID, owner IDs, iskValue, description, status を読む。 | Host は常に PyNone。EveJS `warRegistryService.js:282-285` は stored row を KeyVal 化。既存 negotiation を読めない。 |

### negotiation mutations

Client calls は `war_cso.py`; UI actions は `warWindows.py`。成功時 client は None reply を使い、専用 exception recovery はこれらの wrapper にない。IDs は Python int/long、ISK は数値、message は string。全 RPC は transport `machoVersion` kwargs を受ける必要があり、business kwargs はない。CreateSurrender は2引数 (`warWindows.py:244`) と3引数 (`:263`) の両方。

| RPC | client call / UI source | 現 Host | EveJS |
|---|---|---|---|
| `CreateWarAllyOffer(warID, iskValue, defenderID, message)` | `war_cso.py:217`; `warWindows.py:324` | `CustomNotify` exception、state change なし。 | `warRegistryService.js:201-223` offer を保存。 |
| `RetractWarAllyOffer(id)` | `war_cso.py:221`; `warWindows.py:332` | `CustomNotify` exception。 | `:225-247` retract state と通知。 |
| `CreateSurrenderNegotiation(warID, iskValue[, message])` | `war_cso.py:226`; `warWindows.py:244,263` | `CustomNotify` exception。 | `:249-280` offer を保存。 |
| `AcceptAllyNegotiation(id)` | `war_cso.py:234`; `warWindows.py:320` | `CustomNotify` exception。 | `:288-292` accept transition。 |
| `DeclineAllyOffer(id)` | `war_cso.py:238`; `warWindows.py:328` | `CustomNotify` exception。 | `:294-316` decline と通知。 |
| `AcceptSurrender(id)` | `war_cso.py:248`; `warWindows.py:246` | `CustomNotify` exception。 | `:337-341` accept transition。 |
| `DeclineSurrender(id)` | `war_cso.py:252`; `warWindows.py:250` | `CustomNotify` exception。 | `:343-365` decline と通知。 |

既存 deviation は `docs/reference/compat/DEVIATIONS.md:3373-3390` に negotiation 未実装として記録済み。Host には persistence がなく、EveJS の後続 read と通知の流れを実現しない。

### war mutation と client GM calls

- `SetOpenForAllies(warID,state)`: wrapper `war_cso.py:256`, UI call `warEntry.py:132-134`。Host は defender 側 relation を更新、Director を確認し、commit 後に `OnWarChanged` を送る。client subscriber は `war_cso.py:76-92`。EveJS `warRegistryService.js:367-380`。Host の追加 role gate は意図的差異として `DEVIATIONS.md:5126` に記録済み。
- `RetractMutualWar(warID)`: wrapper `war_cso.py:243`, UI `warEntry.py:304`。Host は war が mutual かつ caller が side の場合だけ end fields を保存し、commit 後 `OnWarChanged`。EveJS `warRegistryService.js:318-335`。
- client はさらに GM-only `GMJoinDefender` (`war_cso.py:262`, UI `warEntry.py:451`), `GMActivateDefender` (`:267`, `warAllyEntry.py:146`, `warEntry.py:448,494`), `GMDeactivateDefender` (`:272`, `warAllyEntry.py:149`), `GMExtendAllyContract` (`:277`, `warAllyEntry.py:148`), `GMSetDeclareTime` (`:282`, `warEntry.py:442,474`), `GMSetWarFinishTime` (`:287`, `warEntry.py:484,489`) を呼ぶ。Host に method 登録がなく、EveJS `warRegistryService.js:172-380` にも handler がないため Host は MethodNotFound。functional reference がない privileged surface として別途根拠が必要。

## 未確認と必要な regression

- GetPublicWarInfo sequence-in/sequence-out、GetWarsByOwners の実 Python set、bound nested calls を通す Host wire tests が必要。
- negotiation では persistence、subsequent read、通知順、再起動後の state を Host tests で確認する必要がある。EveJS は `warNegotiationRuntimeState.js` と `warNotificationCenter.js` を使う。
- int/long 両形、None/message optional、UserError/exception envelope、bound kwargs、DB runtime、GUI client はこの作業で実行確認していない。task のため PostgreSQL は未使用。
- 変更は行っていない。Serena existing-file edit tool の失敗と merge conflicts により、fix/test/merge/push は未完了。
