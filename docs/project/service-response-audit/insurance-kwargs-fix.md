# InsureShip voidOld kwargs 修正 handoff

## 結果・範囲

**実装済、静的レビュー済。担当側のbuild/test/実サーバー検証はnot performed。親からInsuranceServiceTests Debug subsetの30 PASS／0 FAIL／0 SKIPが報告された。** 対象は`insuranceSvc.InsureShip`が`voidOld` kwargsを無視する1件だけ。監査の未比較177 RPCはこのsliceでは調査・実装していない。Host DB/native acceptanceは未確認。

- canonical root: `C:\Users\yutai\eve.rs`、branch: `main`。
- 開始時HEAD: `c114dc0d8e292c056e8d71b37f496084f484676f`。
- 静的レビュー時HEAD: `b2557b42e8077fe8c6376a9189f8a0072a7d26be`。親の進行によりmainが進んだ。こちらではGitコマンド/変更操作を行っていない。`.git/HEAD`と`refs/heads/main`の読み取りで確認。
- branch/worktree/子agentは作成していない。ambient worktree1e47は使用していない。全shell実行のworkdirはcanonical root。
- 書き込みは下記3ファイルのみ。Host writer、ServiceCall、他source/test、`core-g-m.md`など他artifactは編集していない。

| 変更ファイル | 内容 |
|---|---|
| `C:\Users\yutai\eve.rs\dotnet\src\Eve.Services.Core\InsuranceService.cs` | InsureShipのreplaceValue取得1行のみ |
| `C:\Users\yutai\eve.rs\dotnet\tests\Eve.Services.Core.Tests\InsuranceServiceTests.cs` | 既存classへ6 regression tests/12ケース追加。既存assertion・位置引数テストを維持 |
| `C:\Users\yutai\eve.rs\docs\project\service-response-audit\insurance-kwargs-fix.md` | 本書 |

## 根本原因と修正

V24 clientの再契約確認は`InsureShip(itemID,quote.amount,isCorpItem,voidOld=True)`という**3位置引数＋kwargs**。元のserviceは第4位置引数だけを読み、HostへReplaceExisting=falseを渡していたため、確認後も`InsureShipFailedSingleContract`が返った。

serviceは既存の`ServiceCall.Kwarg("voidOld")`から先に取得し、キーがない場合だけ従来の第4位置引数へfallbackする。取得後は既存のTryBoolean/actor/amount validationとInsurancePurchaseCommandにそのまま流す。shared helperの変更・追加は不要。

| 入力 | ReplaceExisting/拒否 |
|---|---|
| V24の3 args＋voidOld=True | true、既存Host transactionによる再契約を要求 |
| voidOld=False | false、既存契約がactiveなら従来の再契約確認error |
| voidOld=None | false。位置引数trueが同時にあってもfalse |
| keywordと第4位置引数が両方ある | keyword優先。明示的なFalseも優先 |
| keywordなし | 既存の第4位置引数へfallback、双方なしならfalse |
| 選ばれたflagがbool/整数0・1以外 | 既存TryBooleanに従いInsureShipFailed、writer未呼び出し |

EveJSのhandlerはkwargsを取り出してvoidOldをruntimeへ渡す。V24が使うbool契約を修復した。EveJSの寛容な数値変換（positive number等）への一般的な拡張は行っていない。既存eve.rsの厳密なflag validationを維持しており、すべての任意入力に対するEveJS同一性とは主張しない。第4位置引数fallbackは既存eve.rs契約/回帰テストの維持。

参照根拠:

- `C:\Users\yutai\eve-logic\eve\client\script\ui\station\insurance\base_insurance.py:519` — 最初の3 args、UserErrorの確認、`:542` — voidOld=True再送。
- `C:\Users\yutai\evejs-v0.12.9\server\src\services\insurance\insuranceService.js:69` — kwargsからvoidOld取得、runtime.insureShipへ渡す。
- `C:\Users\yutai\eve.rs\dotnet\src\Eve.Services.Core\InsuranceService.cs:361` — InsureShip、`:371` — 修正行、`:481` — 既存TryBoolean。
- `C:\Users\yutai\eve.rs\dotnet\src\Eve.Services\ServiceCall.cs:31` — Kwargはキー不在時null、明示PyNoneはPyNoneを返す。この区別でFalse/Noneがfallbackに消されない。

## Authority・再契約・失敗の副作用

既存のHost writerへ渡る変更点は選択されたReplaceExistingだけ。shipID、premium cents、isCorp、session actor、start/end FILETIME（84日）、Cancellation、成功None/error mappingは維持。

serviceはdocked/characterID/corp roleをチェックした後にwriterを呼ぶ。Hostはshipの所有者、actor location、hangar、insurance price/quoteもチェックする。voidOldは所有権やcorp権限の代替ではない。

Hostはship/insurance contractをlockし、activeな旧契約についてReplaceExisting=falseならAlreadyInsuredを返す。trueなら旧契約のvoidと新契約を組み立て、premium wallet adjustmentとcontract upsertを同じtransactionへ入れる。commit後にWalletBalanceChangeを通知する。wallet failureはNotEligibleへ変換され、commit前のtransactionはdisposeされる。この構造・通知order・Host配線を変更していない。DB rollback/通知をこのsliceで実行検証したわけではない。

根拠:

- `C:\Users\yutai\eve.rs\dotnet\src\Eve.Services.Core\InsuranceService.cs:379` — actor validation、`:390` — command、`:454` — None/InsureShipFailedSingleContract(ownerName)/InsureShipFailedのmapping。
- `C:\Users\yutai\eve.rs\dotnet\src\Eve.Host\Persistence\Insurance\PersistenceInsuranceWriteModel.cs:54` — validation、ship lock/ownership/hangar、active contract確認、transaction/commit/postcommit notification。
- Host配線の既読監査根拠: `C:\Users\yutai\eve.rs\dotnet\src\Eve.Host\Composition\CharacterComposition.cs:129`、`C:\Users\yutai\eve.rs\dotnet\src\Eve.Host\Rpc\MailRpc.cs:41`。このsliceでは配線を変更していない。

## Regression tests（担当側not performed）

| 新規test | ケース数 | 確認する境界 |
|---|---:|---|
| InsureShip_forwards_voidOld_keyword_with_unchanged_purchase_terms |2|personal/corporationの両方。actor全値、ship、premium cents、owner kind、ReplaceExisting、start/end、Noneをassert |
| InsureShip_voidOld_keyword_takes_precedence_over_positional_flag |3|True/False/Noneと反対の位置引数。False/Noneをfallbackで上書きしない |
| InsureShip_invalid_voidOld_keyword_is_rejected_before_writer |2|string "true"/整数2を拒否。位置引数trueへの逃げを許さずwriter.Purchaseがnull |
| InsureShip_voidOld_keyword_does_not_bypass_actor_authority |3|undocked、corp roleなし、characterなしを拒否しwriter未呼び出し |
| InsureShip_recontract_confirmation_preserves_refusal_then_forwards_voidOld |1|最初はreplaceなし→UserError/ownerName。確認後3 args＋kwargs→replaceあり、他command値同一、成功None |
| InsureShip_voidOld_keyword_preserves_writer_rejection |1|writer NotEligibleはkwargs trueでもInsureShipFailed。拒否を偽の成功に変換しない |
| **計** |**12**|**6 tests** |

新規test開始行: `C:\Users\yutai\eve.rs\dotnet\tests\Eve.Services.Core.Tests\InsuranceServiceTests.cs:171`、`:205`、`:229`、`:251`、`:278`、`:314`。

既存`InsureShip_forwards_the_wire_amount_owner_kind_and_replace_flag`（`:140`）は未変更で位置引数fallbackの回帰確認に使える。新規のkwargs正例/優先/無効値/確認再送は修正前のkwargs無視を検出するassertionになっているが、RED実行はしていない。actor拒否とwriter拒否は変更しない動作の保護。stub writerによりservice境界を検査するテストであり、Host DB transactionの統合証明ではない。

## MCP・静的レビューと親への検証handoff

- Serena manualを読み、`C:\Users\yutai\eve.rs`へactivate。symbol read/editを使用。production置換はdry-runで1 occurrenceを確認し、stale occurrence guard付きで適用した。前後のmethodと新規testsを静的レビュー済み。
- CBM接続はindex_status/search_graphともTransport closed、再試行も同じ。ユーザー指定のdocumented CLI `C:\Users\yutai\AppData\Local\Programs\codebase-memory-mcp\codebase-memory-mcp.exe cli`で復旧。CLI index_statusはcanonical root/main/start HEADでready。graph/snippetからinsurance method、既存tests、ServiceCall、EveJS/V24 callsiteを確認した。MCP transportは未復旧だがCLI経由のCBMは利用できた。
- 関連AGENTS/CLAUDE/overrideは既読、追加の`dotnet/tests/AGENTS.md`を確認。tdd-loop/self-reviewを適用。実行禁止の指定に従い、RED/GREEN、build、test、Git diff/check、commit/pushは行っていない。

親がcanonical rootで直列実行する推奨コマンド（本担当は実行していない）:

```powershell
dotnet test dotnet/tests/Eve.Services.Core.Tests/Eve.Services.Core.Tests.csproj --filter "FullyQualifiedName~InsuranceServiceTests"
```

その後、親の既存Host insurance統合検証でactive contract→確認→kwargs再契約、無権限/別owner/別hangar、insufficient funds時の旧契約・残高非変更、成功時だけpremium一回/commit後通知を確認する。専用Host test追加/実行は本担当scope外。V24 UIの再確認後に同じerrorへ戻らない実client検証も未実施。

## 親の検証結果追記・handoff

ユーザー経由で受領した親の実行結果を記録する。担当自身の実行結果とは区別し、担当側not performedの記録を維持する。

| 実行者・範囲 | 構成 | PASS | FAIL | SKIP | 証拠・状態 |
|---|---|---:|---:|---:|---|
| 親：InsuranceServiceTests subset | Debug | 30 | 0 | 0 | ユーザーからの親結果報告。担当による再実行なし |
| 本担当：build/test | — | — | — | — | not performed。親の直列実行指定を維持 |

この30件の結果をCore全体やHost DB/native acceptanceの合格へ拡張しない。Host DBでの契約・残高の原子更新、失敗時rollback、通知order、V24 native clientの再契約UI acceptanceは引き続き未確認。

結果追記の変更は本書だけ。確定済みsource/testは追加変更していない。明示stage/commitおよびcloseは親へhandoffする。本担当はGit操作を実施していない。
