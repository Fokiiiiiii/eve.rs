# petitioner サービス深掘り監査

## 結論

V24.01 クライアントには unbound の remote service `petitioner` が存在し、全ソース検索で31種類のメソッド呼び出しを確認した。呼び出しは `sm.RemoteSvc('petitioner')` に限られ、petitioner の bound object / moniker 呼び出しは見つからなかった。代替サービスを探す必要はない。

`CoreServices.Create` は `new PetitionerService()` を登録する（`dotnet/src/Eve.Services.Core/CoreServices.cs:61`）。サービスの名前とRPC属性は `dotnet/src/Eve.Services.Core/PetitionerService.cs:15-56` にあり、Hostのサービス一覧とdispatcherから到達する。実Hostソケットテスト `Petitioner_client_calls_return_reference_shapes_through_host_registration` を31種類すべてに拡張した。各呼び出しは `machoVersion` kwargsを含み、クライアントのpositional/keyword形、2/3引数のチャット、64 bit ID、Noneを送る。登録とwire replyの形は確認済み。

ただし `CoreServices.Create` はproviderを渡さず、`PetitionerService` は空応答とacknowledgementを返すだけである（同サービス `:8-10,59-82`）。petitions、messages、ratingsの保存・後続read・サーバー通知は存在せず、正常に見えるmutationの一部はno-opである。EveJS 261018.1にも状態providerはない（`server/src/services/support/petitionerService.js:96-301`）。そのため wire互換と実機能の完了は区別する。データや成功を作り足す修正は行っていない。

## 全呼び出しとクライアント契約

クライアントのfile:lineは /home/user/eve-logic からの相対パス。主な呼び出し元は eve/client/script/ui/shared/neocom/{petition.py,petitionWindow.py,help.py}、通知経由は notifications/client/notificationCenter.py。全行でHost登録経路は CoreServices.Create → ServiceRegistry → PacketDispatcher。以下の引数形を実Hostソケットテストで送信し、現在のHost replyを検査する。通常のremote callに付く machoVersion はdispatcherでtransport metadataとして除去される。任意のservice kwargsは残る。テストは結果がPython 2 stringとなる項目についてwire上の PyBytes も検査する。

| メソッド | V24.01 caller / 引数と消費 | Host reply・エラー | 永続状態・通知 |
|---|---|---|---|
| `AddPetitionRating` | `petition.py:451`; petition ID、3評価、comment、rating FILETIME。返却値は無視し、直後にratingをローカルrecordへ反映（`:452-456`）。 | `None`。 | ratingを保存しない。後続一覧readでratingは戻らない。通知なし。 |
| `CancelPetition` | `petition.py:1008`; petition ID。返却値を無視し、一覧を再読込（`:1009-1015`）。 | `None`。 | petition状態を変更しない。通知なし。 |
| `ClaimPetition` | `petition.py:1053`; petition ID。intが0より大きい時だけclaimedへ移行し、そうでなければ拒否dialog（`:1053-1062`）。 | `PyInt(0)`。 | 正しい拒否応答であり成功を装わない。状態変更・通知なし。 |
| `ClosePetition` | `petition.py:1019`; petition ID。返却値を無視しclaimed一覧を再読込（`:1020-1025`）。 | `None`。 | 状態変更なし。通知なし。 |
| `CreatePetition` | `petitionWindow.py:309` は8 positional引数を送り、返却値を見ずローカル `OnPetitionCreated` をscatterして作成成功を表示（`:310-312`）。別経路 `petition.py:716` は5 positional + `chatLog`/`combatLog` kwargsで、falseなら拒否dialog（`:716-718`）。 | `False`。 | 永続化なし。前者はfalseを無視して成功表示するクライアント側の不整合。Host応答だけでは解消できず、`/home/user/eve-logic` の変更が必要。 |
| `DeletePetition` | `petition.py:997`; petition ID。ローカル変数に代入するが値は使わず、一覧を再読込（`:997-1002`）。 | `None`。 | 削除状態なし。通知なし。 |
| `EscalatePetition` | `petition.py:1043`; petition IDとescalatesTo。返却値を無視しclaimed一覧を再読込（`:1043-1049`）。 | `None`。 | escalation状態なし。通知なし。 |
| `GetCategories` | `petition.py:580`; category recordsを受け、`categoryID`と`displayName`で検索・表示（`:583-592`）。 | 空 `PyList`。 | read only。category providerなし。通知不要。 |
| `GetCategoryHierarchicalInfo` | `petitionWindow.py:318`; 親/子/説明/billingの4辞書をunpackし、言語で絞ってUIを構築（`:318-345`）。 | 空辞書4個のlist。 | read only。データproviderなし。通知不要。 |
| `GetCategoryProperties` | `petitionWindow.py:204`; category IDからproperty ID列を受け、必須・型・名前等を組み立てる（`:204-215`）。 | 空 `PyList`。 | read only。property definitionsなし。通知不要。 |
| `GetClaimedPetitions` | `petition.py:288`; petition recordsを一覧へ展開（`:288-317`）。 | 空 `PyList`。 | recordsの保存元なし。通知不要。 |
| `GetClientPickerInfo` | `petitionWindow.py:257`; filterとelement名を送り、(表示名, ID) rowの先頭をpickerへ設定（`:257-267`）。 | 空 `PyList`。 | read only。検索providerなし。通知不要。 |
| `GetLog` | `petition.py:835`; petition IDを送り、logDateTime/characterID/userName/eventIDをrowsetへ移す（`:835-856`）。 | 空 `PyList`。 | log保存元なし。通知不要。 |
| `GetMyPetitionsEx` | `petition.py:244`; petition recordをcacheし一覧表示。recordはpetitionID、petitionerID、category/status/date等を使う（`:244-275,501-563`）。 | 空 `PyList`。 | petition read modelなし。cacheは `OnPetitionCreated` / `OnPetitionMessage` で更新される。 |
| `GetPetitionMessages` | `petition.py:875`; petition IDに対するmessagesを読み、senderID等で表示（`:875-900`）。 | 空 `PyList`。 | message storeなし。message通知もなし。 |
| `GetPetitionQueue` | `petition.py:471`; queue IDに対するpetition recordsを一覧表示（`:471-496`）。 | 空 `PyList`。 | queue/petition storeなし。通知不要。 |
| `GetQueues` | `petition.py:574`; queueName/queueIDでtabsを構成（`:220-221,574-575`）。 | 空 `PyList`。 | queue providerなし。通知不要。 |
| `GetUnreadMessages` | `petition.py:120`; rowsのpetitionID/text/messageIDを読む（`:120-125`）。 | 空 `PyList`。 | unread storeなし。結果が空なので後続MarkAsReadも発生しない。 |
| `GetUserCatalogCountry` | `petitionWindow.py:158`; countryをbillingCategoriesのkeyに使用（`:158-162`）。 | `US`。wireではASCII `PyBytes`。 | read only。固定値はEveJS 261018.1の同じ応答（`:193-196`）。通知不要。 |
| `GetZendeskJwtLink` | `petitionWindow.py:106`, `help.py:117`, `notificationCenter.py:370`; URLをbrowserへ渡す。notification callerは `ticketID=notification.data['text']` をkeywordで送る。 | URL文字列（wireでは `PyBytes`）。kwargsのticketIDはnumeric stringを含む。無効/なしはhelp-center新規request URL。 | 外部Zendesk URL生成のみ。Hostのpetition状態は使わず、通知も送らない。 |
| `HasOpenTickets` | `help.py:83`; truthyなら旧petition labelを表示。 | `False`。 | 固定feature response。ticket storeなし。 |
| `IsSerenity` | `petition.py:212`; IsZendeskSwapEnabledと併せてpetition UIを切替。 | `False`。 | 固定feature response。状態なし。 |
| `IsZendeskEnabled` | `help.py:89`, `petitionWindow.py:125`; help-center UIの表示をgateする。 | `True`。 | 固定feature response。状態なし。 |
| `IsZendeskSwapEnabled` | `help.py:58,81`, `petition.py:212`; Zendesk help-centerとlegacy petitionの表示を切替。 | `True`。 | 固定feature response。状態なし。 |
| `MarkAsRead` | `petition.py:132`; messageIDを送り、returnは無視。RPC前にclient自身のread listへID追加（`:128-133`）。 | `None`。 | server側read stateなし。client-local stateのみ。 |
| `MayPetition` | `petitionWindow.py:179`; categoryID/OOC characterを送り、負値を拒否理由へ対応（`:179-201`）。 | int `-4`。 | 明示的拒否であり成功ではない。petition stateなし。 |
| `PetitioneeChat` | `petition.py:921` はpetitionID/message/commentの3引数、`:978` は2引数。 | `None`。 | clientは `UserError('PetitionClosed')` を捕捉して送信失敗dialogを出す（`:923-934,980-984`）。Host defaultはこのerrorを出さず、message保存/通知もしない。 |
| `PetitionerChat` | `petition.py:930,962`; petitionID/messageの2引数。 | `None`。 | clientは同じ `PetitionClosed` を捕捉（`:932-943,964-968`）。Host defaultはerror・message保存・通知を行わない。 |
| `PropertyPopulationInfo` | `petitionWindow.py:213`; inputInfo/OOC characterからdropdown用の(ID, text) rowsを受ける（`:212-215`）。 | 空 `PyList`。 | read only。lookup providerなし。通知不要。 |
| `UnClaimPetition` | `petition.py:1029`; petition ID。返却値を無視しclaimed一覧を再読込（`:1029-1035`）。 | `None`。 | claim stateなし。通知なし。 |
| `UpdatePetitionRating` | `petition.py:447`; petition ID、3評価、comment。reply後client recordを更新（`:447-456`）。 | `None`。 | ratingを保存しない。後続readでは消える。通知なし。 |

## エラー、状態遷移、通知

- 有効なRPC引数で `machoVersion` は追加metadataとして扱われる。CreatePetitionの8 positionalと5 positional + chat/combat kwargs、PetitioneeChatの2/3 positional、ticket linkのticketID keywordを実Host wire testに含む。petition IDsは `PyLong`、MayPetition / PropertyPopulationInfoのNoneも含む。
- クライアントが明示的に捕捉するpetition UserErrorはチャット時の `PetitionClosed`（`petition.py:923-934,932-943,964-968,980-984`）。通常の未知例外はdispatcherがcorrelation ID付きgeneric RuntimeErrorへ変換する（`PacketDispatcher.cs:258-299`）。現在のdefault handlerは例外を投げず、チャットも `None` を返すため、closed-ticket error pathを満たさない。providerもなく、petitionがclosedかを判断する状態もない。
- `OnPetitionCreated` は `petitionWindow.py:310` がclient内でscatterする。client listenerは `petition.py:107-116` で一覧をreloadする。server notificationではない。
- `OnPetitionMessage` はclient consumer（`petition.py:939-956`）で、cacheを無効化しmessage UIを更新する。現在のHostにもEveJS handlerにも、chat後にこの通知を送る実装はない。
- cancel/close/unclaim/escalate/delete後はclientが自分で画面を再読込する。ratingもclient-local recordを更新する。これらはserver persistenceの代替にならない。
- callbackはserviceのテスト用/将来接続用constructorにあるが、production registrationはcallback無し。callback例外をrootで意味あるUserErrorへ変換する処理や通知providerは無い。

## EveJS 261018.1比較

`/home/user/evejs-261018.1/src/server/src/services/support/petitionerService.js:101-300` は、同じfeature booleans、US country、link builder、empty read lists、MayPetition=-4、CreatePetition=false、ClaimPetition=0、mutation nullを返す。mutation handlerはaudit eventを記録するだけでpetition stateを保持しない。Hostのデフォルトwire値はこの動作と一致するが、clientの `PetitionClosed` 分岐と `OnPetitionMessage` consumerはどちらの実装でも成立していない。

## 確認結果 / 未解決

- 確認済み: 31メソッドの登録と実Host socket dispatch、machoVersion kwargs、観測したpositional/keyword形、reply shape。`Petitioner_client_calls_return_reference_shapes_through_host_registration` は1 passed / 0 skipped。Host.Tests buildは0 warnings / 0 errors。PostgreSQLは使用していない。
- Host.Tests全体も指定どおり実行したが、結果はFailed 329 / Passed 507 / Skipped 2 / Total 838。`EVE_PG_URL`を空にしたため、DB fixtureのNpgsql接続初期化失敗とRPC fuzzのDB要件エラーがログにある。別にRPC registry件数 assertion (期待1424、実測241) も出た。出力が切り詰められたため全失敗の原因までは分類していない。skip 2件は成功に数えない。
- 未解決: petition/category/message/ratingのauthoritative provider、mutation persistence、後続readの可視性、OnPetitionMessage通知、closed petitionの正確なUserError。V24.01の実GUIクライアントでは操作していない。EveJSにもproviderがなく、category rowsなどのnon-empty payloadを根拠なく作るのは適切でないため、機能完了とは判定しない。全Host.Tests shardが赤いためpushしていない.