# Slash catalog 契約の最小修正

更新日: 2026-10-13。作業 root は repository root、branch main、作業開始時 HEAD は 52b8421f34e635900a18459da4403f26f2dfaec0。最終 root/branch 確認時 HEAD は dda03ca0464043822e68c8348604ff34eadedcac。親の focused 検証は Debug 19 PASS / 0 FAIL / 0 SKIP。親の全 Dogma regression は Debug 153 PASS / 0 FAIL / 0 SKIP（2026-10-13受領）、Git は親担当。

## 結果

SlashCmd("/") が成功文字列を返す破損を修正した。既存の command dispatch を一つの登録表へ移し、同じ表から catalog を作る。provider が null の command は登録せず、catalog にも出さない。新しい gameplay handler/store、架空 entries、新しい RPC は追加していない。公開 RPC は SlashCmd と ReportSlashCommandUsage の2件のまま。

変更は SlashService.cs、Slash 専用の SlashCatalogTests.cs、market-wallet-slash.md、本ファイルのみ。Market suite、StartupCheck source、Host/Core の権限処理は編集していない。

## 独立して確認した client / EveJS wire

- eve-logic/eve/devtools/script/svc_slash.py:466–485 の FetchRemoteCommandList は RemoteSvc("slash").SlashCmd("/") を呼び、UserError.args[1]["reason"] を読み、": " の後を Python list として評価・sort する。例外がない旧実装では cmds の代入に到達しなかった。
- 同ファイル:487–518 の MatchCmd は slash を付けない command 名に startswith する。必要な catalog は list of bare strings であり、成功値の PyList や "/dmg" はこの入口の契約ではない。
- evejs-v0.12.9/server/src/services/admin/slashService.js:197–204,214–226 は空 message の wrapped UserError、reason="Commands: ['name', ...]" を返す。名前の源泉は evejs-v0.12.9/server/src/services/chat/chatCommands.js:291–454。ただし reference の全 command をコピーすると現行未実装を advertise するため、現行登録を源泉にした。
- wire は既存 ../../../dotnet/src/Eve.Services/MachoErrors.cs:57–72,108–109 の UserErrorPayload / ThrowUserError を使う。ObjectEx1 header は token eveexceptions.UserError、args=("",{"reason":PyStr})、state の msg/dict。MachoErrorType.WrappedException の既存 dispatcher 経路で client に例外を送る。共通 wire の新設・変更は不要だった。

## 現行 dispatch / Host / authority

- ../../../dotnet/src/Eve.Services.Slash/SlashService.cs:10–61 は唯一の手書き partial SlashService。[Service("slash")] と2 [Method] を独立確認した。private Commands は RPC export ではない。
- ../../../dotnet/src/Eve.Host/Rpc/AgentRpc.cs:66–76 は同じ SlashService を登録し、publicQaSlashService に保持する。health は必須、item/skill は実 provider、death/suicide/npc は spaceSessions がある時だけ実 provider。この条件を登録表に移した。
- catalog の最大9名は deathtest / dmg / fit / giveitem / giveskill / heal / npc / suicide / unfit。health のみの instance なら dmg / heal の2名。space がない Host の6名は dmg / fit / giveitem / giveskill / heal / unfit。未実装 help/createitem/spawn/killright 等は掲載しない。
- ../../../dotnet/src/Eve.Services.Core/UnportedFeatureRpcServices.cs:330–359 と ../../../dotnet/src/Eve.Host/Composition/CharacterComposition.cs:182–197 を確認した。publicQaToolsServer.SlashCmd は IsStaff が true のときだけ既存 instance へ委譲する。この guard は変更していない。
- 直接 slash には現行 source 上の staff role gate はない。これを「GM role が必須」と架空に説明しない。今回の catalog はその既存 dispatch の実登録だけを表す。giveskill は caller 自身のみ（../../../dotnet/src/Eve.Services.Slash/SkillCommands.cs:43–50）、fit/unfit は me と caller の active ship（ItemCommands.cs:107–125,176–196）。catalog は他 character 向けの新操作・argument variant を advertise しない。
- docked/space/character の前提、ownership/domain 判定、case normalization、argument split、unknown message、ReportSlashCommandUsage の None を維持した。状態前提は引き続き handler が判定し、catalog discovery は store/simulation を呼ばない。登録 table は instance 作成時に構成し、dispatch もそこだけを見る。

## 回帰 tests / 検証状況

../../../dotnet/tests/Eve.Services.Dogma.Tests/SlashCatalogTests.cs に追加した。この既存 project は Slash を既に参照し、DeathTestCommandTests / NpcCommandTests / HealthTests も持つため、別 project や依存を追加していない。

- UserError の class / error code / ObjectEx1 / args[0] の空文字 / args[1] の reason PyStr と bare-name catalog。
- "/" と whitespace を含む "/"、healthのみ、全9 registrations、5種の optional provider 個別構成。
- null provider と未実装 command の unknown 応答、既存 case normalization / character / 自己対象 guard。
- 19 case。catalog 中に store/simulation が呼ばれないことを確認するため collaborator は null tripwire。command handler 自体は実 class、RPC も生成 IService adapter を通す。

### 担当別の検証結果（2026-10-13）

|項目|状態|根拠・担当|
|---|---|---|
|本担当の .NET build/test|not performed|本担当は実行していない。追加前の failure / 修正後の PASS を自分の実行結果として報告しない|
|親の SlashCatalogTests|**Debug: 19 PASS / 0 FAIL / 0 SKIP**|2026-10-13 にユーザーから親検証結果として受領|
|親の Serena review|レビュー済み|SlashService と新 tests の全体レビュー済みとの親報告を受領|
|親の全 Dogma regression|**Eve.Services.Dogma.Tests Debug: 153 PASS / 0 FAIL / 0 SKIP**|2026-10-13 に追加親検証結果として受領。focused 19 PASS とは別の suite 結果|
|native V24 GUI|未確認|catalog 復元・autocomplete・実 command の GUI 完走は未確認|
|marshal byte の実行比較|未確認|focused tests の PASS を native wire の完全一致へ拡張しない|

本担当は読み戻し・scope の diff・静的 self-review を行い、識別子の scope 衝突を解消した。親の focused PASS は記録したが、本担当自身の実行結果とは区別する。

focused test の再実行用参考 command:

`dotnet test dotnet/tests/Eve.Services.Dogma.Tests/Eve.Services.Dogma.Tests.csproj --no-restore --configuration Debug --filter FullyQualifiedName~SlashCatalogTests`

親の全 Dogma regression は Eve.Services.Dogma.Tests Debug 153 PASS / 0 FAIL / 0 SKIP（2026-10-13受領）。既存 HealthTests / DeathTestCommandTests / NpcCommandTests も含む suite 結果として記録した。本担当自身は実行していない。PG が必要な既存 tests の SKIPPED を PASS としない。native client の "/" → catalog parse → autocomplete → 登録済 command dispatch は未確認のまま。

本更新はこの文書と market-wallet-slash.md のみ。確定した source/tests は変更せず、本担当による build/test/Git mutation は行っていない。

## StartupCheck の再分類（source 変更なし、TODO 候補）

以前の audit の「障害検知省略＝破損」という表現を絞り込んだ。

|分類|根拠・結論|
|---|---|
|確認済|正常時 None は双方共通。EveJS MarketProxyService.js:6181–6189 は daemon startupCheck を呼ぶ。marketDaemonClient.js:82–86 は接続と互換性を確認し、失敗は MarketProxyService.js:1932–1940 の CustomInfo に変換する|
|確認済の機構差|現行 MarketProxyService.cs:124–125 は None。Host Composition/CorporationComposition.cs:71–87 は PG market schema migration と MarketStore 作成を行う。Composition/CharacterServicesComposition.cs:58–103 は CorporationStores / MarketSeed を await してから CharacterServiceList を構成する。外部 EveJS market daemon の接続確認をそのまま必要としない|
|意図差分の判断|外部 daemon startupCheck を省くことは現行 Host の構成と整合する。これだけを破損の確証としない。StartupCheck の明示的な互換設計として文書化・実行検証されたことは未確認|
|不足・TODO候補|起動後の PG unavailable / incompatible state で、この RPC と後続 GetOrders が返す client-visible error を同じ意味の状態で比較する。V24 quote.py:63–77 は UserError を regionUpAt に保存するが、現行 StartupCheck には検出 branch がない。失敗 state の定義・期待 error・通知/再試行を決めて regression を追加する候補。backend probe を今回無条件に追加してはいない|

上記 market source の略記は ../../../dotnet/src/Eve.Services.Market/MarketProxyService.cs、Host は ../../../dotnet/src/Eve.Host/、reference は evejs-v0.12.9/server/src/services/market/、client は eve-logic/eve/client/script/ui/shared/market/quote.py。未実装 bounty13 / LPtransfer2 / Concord1 の全 method 名は market-wallet-slash.md の追補に残した。
