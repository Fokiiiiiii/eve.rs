# userSvc 全呼び出し監査（V24.01 / Host）

更新日: 2026-10-28

## 対象と経路

V24.01 の全 eve-logic を userSvc で検索し、サービス呼び出しと応答消費元を追跡した。通常のプレイヤーRPCは以下の20メソッドで、bound/moniker 別の別名は見つからなかった。サーバー内部イベント ProcessEVESessionCreated(sessionID, address) は carbon/common/script/net/machoNet.py:4027–4029 で proxy モード時にのみ ConnectToSolServerService('userSvc') を通る。プレイヤーRPCではなく、Hostにproxy routeはない。クライアントは各呼び出しで machoVersion を含む kwargs を送る。TCP wire テストは実Hostの userSvc routeを CallWithKwargs で通す。

Hostは dotnet/src/Eve.Services.Core/StoreOffersService.cs:288–289 の [Service("userSvc")] と各 [Method] で登録され、production wiringは dotnet/src/Eve.Host/Rpc/RewardRpc.cs:69–93。実データは redeem runtime、複数訓練provider、キャラクターログオフ、アカウント名/作成日 readerへ接続される。wire回帰は dotnet/tests/Eve.Host.Tests/UserSvcRpcHostWireTests.cs の実TCP Hostでサービス登録と応答packetを検証する。

### メソッド別結果

各記録の「クライアント」は /home/user/eve-logic 内の全参照検索結果であり、応答を読む箇所も含む。型はPython 2 wire型（PyInt/PyLong/PyBytes等）に対応する。未変換のHost例外はMacho ErrorResponse上のUnexpectedFailureになり、clientにはcorrelation ID付きRuntimeErrorが返り、例外型/文面はHost logに残る。業務UserErrorへ変換するのは複数訓練活性化のCustomInfo pathである。redeem系の共通認証失敗は UnauthorizedAccessException("An authenticated account is required to access redeeming items.")。

1. **CreateRedeemTokenQA**
   - クライアント: redeem/client/insider.py:52。全値をkwargsで送る: userID, typeID, quantity, blueprint値、expiry, label, description, addedByContext, addedByExtra, available, soulbound。expiry=None, addedByExtra=None, available=None が有効な形。返答は読まない。
   - Host: staff roleなし、無効ID、またはfallbackでは False; staff roleでproduction handlerが有効な場合のみredeem行を永続化する。通知なし。今回のwireはQA roleを設定し、Noneを含むkwargsが実routeを通ることとfallback False を確認した。DB書き込み結果はDB禁止のため未確認。
   - 失敗: 不正データは例外でなく False。EveJS userService.js:1041–1076 はQA tokenを永続化し成功payloadを返す。

2. **GetSessionPlaytimeMinutes**
   - クライアント: regionalui/utils.py:11–15、呼び出し :12。引数なし。整数分を使い、例外時は0にする。
   - Host: PyInt(0)、未永続・通知なし・通常例外なし。EveJS userService.js:1139–1141 も0。

3. **GetUserToken**
   - クライアント: eve/common/script/util/urlUtil.py:16–29、呼び出し :18。引数なし、応答文字列をURLの token に入れる。例外は記録して再送出。
   - Host: production handlerがアカウント/キャラクター期限付きHS256 tokenを返す。wire上はmarshal後 PyBytes のPython文字列。handler未接続時は InvalidOperationException("User token issuance is not wired on this host.")。状態永続・通知なし。EveJS userService.js:1149–1153 はtoken文字列を返す。

4. **GetUpdatedMultiCharacterTraining**
   - クライアント: eve/client/script/ui/shared/activateMultiTraining.py:137–170。force boolを位置引数にする。返答は (trainingID, trainingExpiry) の列として整列・表示し、空列は無効扱い。force=False/Trueの両方を送る。
   - Host: PyList[PyTuple(PyInt(slot), PyLong(filetime))]。forceは使わず毎回永続MCT状態を読むため、通知なし。providerなしなら空列。EveJS userService.js:765–768。

5. **ActivateSoulboundMultiTraining**
   - クライアント: activateMultiTraining.py:227–231。charId, token-pair列を位置引数、trainingID と useHomeStation をkwargsで送る。新規枠のtrainingID=Noneも許容される。HostとEveJSはaccount sessionを使い、charId/useHomeStation値は活性化選択に使わない。成功値は読まず再読込し、UserError を捕捉。
   - Host: 成功時に更新済みslot/FILETIME tuple列を返す。provider不在、Omega不足、token不正等の InvalidOperationException は UserError("CustomInfo", info=...) へ変換。成功時はproviderがslot永続化をcommitした後、account sessionへ SkillNotifications.MultipleCharactersTrainingUpdated を通知し、クライアントはReload(force=True)を行う。TCP wireで ErrorResponse と CustomInfo/info を検証した。EveJS userService.js:780–816。

6. **ActivateMultiTraining**
   - クライアント: activateMultiTraining.py:229–231。itemID 位置引数、trainingID kwargs。新規枠ではtrainingID=Noneを送る。成功値は読まず、例外は上と同じく捕捉。
   - Host: PyLong/PyInt itemIDを受け、成功時slot/FILETIME tuple列。証明書を消費しslotを永続化する。commit後にaccount sessionへ SkillNotifications.MultipleCharactersTrainingUpdated を通知し、クライアントはReload(force=True)を行う。provider不在/業務拒否は UserError("CustomInfo", info=...)。wireテストはkeywordとpositional trainingIDの両方を通す。EveJS userService.js:770–778。

7. **GetMultiCharactersTrainingSlots**
   - クライアント: eve/client/script/ui/services/eve/client/script/ui/services/skillQueueSvc.py:847–860、eve/client/script/ui/shared/neocom/charsheet/plexPanel.py:43–47。引数なし。dict .items() と件数を読む。
   - Host: PyDict{PyInt(slot): PyLong(filetime)}。永続MCT状態を読み、通知なし。providerなしなら空dict。EveJS userService.js:752–763。

8. **InvalidateMultiCharacterTrainingCache**
   - クライアント: eve/client/script/ui/services/skillQueueSvc.py:847–851。session.userid を位置引数（long）で渡し、返答を無視してslotsを再取得。
   - Host: cacheがないので None、永続変更/通知なし。EveJS userService.js:818–821 はtrue。clientが値を読まないことを根拠にした互換差。

9. **CastUpvote**
   - クライアント: eve/client/script/ui/shared/systemMenu/customersatisficationcont.py:37。引数なし、返答無視、UIの hasVoted を更新。
   - Host: True、永続状態/通知なし。EveJS userService.js:1104–1107 もtrue。

10. **CastDownvote**
    - クライアント: eve/client/script/ui/shared/systemMenu/customersatisficationcont.py:44,89。引数なし、またはfeedback文字列1個。返答無視しローカルvote状態を更新。
    - Host: True、永続状態/通知なし。EveJS userService.js:1108–1113 もtrue。

11. **ReportBot**
    - クライアント: eve/client/script/ui/shared/info/infoContentBase.py:1689。相手itemIDを位置引数、確認/self-report guard後に呼び、返答無視。
    - Host: True、報告を保存せず通知なし。EveJS userService.js:1115–1120 もstub true。

12. **ReportISKSpammer**
    - クライアント: eve/client/script/ui/services/menuSvcExtras/devFunctions.py:63–69, eve/client/script/chat/client/chat_service.py:184–203。charID, channelID, spam-entry listを位置引数。clientは投稿者の最大10件を日時・名前・本文の文字列へ整形する（chat_service.py:184–203）。channelIDは数値または表示文字列。返答無視し、menu pathは対象をローカル表示から外してchannelを再読込。
    - Host: report sinkが未設定なら False; production wiringにもsinkなし。永続化/通知なし。wireはnumericとstring channelをともに確認。EveJS userService.js:1122–1127 はtrueを返すstub。Hostは実際に処理していないreportを成功扱いしない。

13. **GetRedeemTokens**
    - クライアント: eve/client/script/ui/services/redeemsvc.py:63。cacheに保存。全field consumer: eve/client/script/ui/shared/redeem/redeemItemsContainer.py:306–311,352–354, eve/client/script/environment/invControllers.py:1188–1200, eve/client/script/ui/shared/redeem/oldRedeemPanel.py:336–347,520, eve/client/script/ui/shared/redeem/window.py:70。必要列: tokenID,massTokenID,typeID,quantity,blueprintRuns,blueprintMaterialLevel,blueprintProductivityLevel,label,description,dateTime,expireDateTime,availableDateTime,stationID,soulbound。Noneはoptional mass/expiry/station等に対応。
    - Host: 14-column CRowset packed row; numeric descriptor columns marshal ints, date columns FILETIME longs, text WString, optional fields None. available=false と期限切れを返答から除く。読み込みは永続token state、read-time pruningはしない。wireで全14列、値/型、unavailable/expired filterを検証。EveJS userService.js:735–750 はavailable/expiryを絞りpending行を返す（EveJSは読込時にexpired行もprune）。

14. **ReverseRedeem**
    - クライアント: eve/client/script/ui/services/redeemsvc.py:72–74; eve/client/script/ui/services/menusvc.py:375–376 は表示条件を決める。itemID位置引数。finallyでqueueを再取得。
    - Host: True。永続pending token statusをreversedにする。server notificationなし、clientが再読込。EveJS userService.js:1078–1081 はtrue（状態変更なし）。

15. **ClaimRedeemTokens**
    - クライアント: eve/client/script/ui/services/redeemsvc.py:98–163, 呼び出し/UI eve/client/script/ui/shared/redeem/redeemItemsContainer.py:352–354; token dict list（tokenID,massTokenID,typeID）、long charID、useHomeStation bool kwarg。errors は (tokenID, error) の列でerror.msg/error.dictを読む。ほかにitemsCreated, optional stationID, isk_balance（if non-None）を読む。eve/client/script/ui/login/charSelection/characterSlots.py:660–662 はisk balanceを読む。成功時は最初にOnRedeemingQueueUpdatedがqueueを再読込しOnRedeemingTokensUpdatedをscatterし、その後OnTokensRedeemedをscatterする。UserError時はdialogを出し、RuntimeError時はqueue更新後に再throwする。
    - Host: errors/items/licences/isk/skillpoints/station/charの辞書を返し、accountと所有character、stationを検証。token receiptとtoken消費、inventory作成を同一transactionで保存し、retry時receiptのitemsを返す。普通のitemの成功時のみ確認済。server側専用notificationはなく、クライアントの成功返答処理が上記2イベントをその順でscatterする。例外は accountなし UnauthorizedAccessException("An authenticated account is required to access redeeming items."); character ID不正 ArgumentException("A positive character ID is required.", parameter "value"); account外character UnauthorizedAccessException("The requested character does not belong to this account."); station不在 InvalidOperationException("Redeem items can only be claimed at a station or structure."); missing/wrong mass/status token InvalidOperationException("A redeem token is unavailable or does not belong to this account."); invalid type/quantity InvalidOperationException("A redeem token contains an invalid item.")。これらはUserErrorでなくHost ErrorResponse。
    - EveJS userService.js:823–1039, result builder 696–722 はper-token errors, license, ISK, SPを実装。Hostにはspecial reward (SP/ISK/LP/expert system等)、blueprint state初期化とper-token error回収の差が残る。今のHostはこれらを実装しておらずDBなしのwire試験からも推定しない。

16. **TrashRedeemTokens**
    - クライアント: eve/client/script/ui/services/redeemsvc.py:157; eve/client/script/ui/shared/redeem/redeemItemsContainer.py:371。token list位置引数、返答後にqueue refresh。
    - Host: True。pending tokensを永続status trashed にする。server notificationなし。EveJS userService.js:1083–1095 はpending行を削除。

17. **ActivateCharacterReSculpt**
    - クライアント: eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:387–390。itemID位置引数、確認後呼び出し、reply未使用。
    - Host: PyInt/PyLong itemID。accountキャラクター所有の有効certificateのみを消費してresculpt状態を保存、無効なら False。server notificationなし。EveJS userService.js:1097–1102 はstub trueで副作用なし。SDE-backed Host挙動は互換偏差として docs/reference/compat/DEVIATIONS.md に記録済み。

18. **UserLogOffCharacter**
    - クライアント: eve/client/script/parklife/gameui.py:498–500。引数なし、boolが DoCharacterLogoff を続けるかを選択。false時は拒否を記録。
    - Host: production callback経由で CharacterLogoff を実施しtrue。last_logoff_at, online stateとsession属性を更新/消去。userSvc固有の追加RPC通知はない。callbackなしはfalse。wireではconfigured callback trueを確認したが、DB persistenceと周辺session lifecycle通知順序はDB禁止で未確認。EveJS userService.js:1155–1176 はcharacter sessionを切断しtrueを返す。

19. **GetUserName**
    - クライアント: eve/devtools/script/connectiontest.py:155。session user IDを位置引数、返却usernameを消費。
    - Host: PyStr、wireはPyBytes。DB名readerが未取得なら user-{id}、引数/セッションの正IDがない場合 user-local。永続変更/通知なし。EveJS userService.js:1143–1147。

20. **GetCreateDate**
    - クライアント: newFeatures/newFeatureNotify.py:90–92。引数なし、FILETIMEを経過日数に使う。
    - Host: DB character creation FILETIMEまたはcompat fallback 132000000000000000 を PyLong で返す。永続変更/通知なし。EveJS userService.js:1129–1137。

### 修正と回帰検証

- GetRedeemTokens ではruntime JSON→Py token mapperが列の一部を落としていた。mapperを全14列入力に拡張し、available default trueを保持した。wire row packingがunavailable/expiredを除外し、EveJS/clientに合う全fieldを出すようにした。
- MCTのprovider未接続と業務拒否が別の例外responseになっていた。両方を UserError("CustomInfo", info=...) にそろえ、Host TCP wireでErrorResponseとuser error名/fieldを検証した。
- UserSvcRpcHostWireTests は全20 player RPCを呼び出し、machoVersion kwarg、PyInt/PyLong、positional/keyword/None trainingID、force boolの両値、nonempty spam string list、string/int channel ID、None optional QA fields、packed row、claim arguments、wire string/bytes形状を含めている。別wire testはredeem認証不足、GetUserToken未配線、Claimのcharacter ID不正をUnexpectedFailure responseとして確認し、Host側に記録される例外型とメッセージも照合する。内部proxy eventはplayer Host routeではないため対象外。
- Host wire testはcallback fixtureを使うのでDBに触れない。runtime persistence transaction自体はPostgreSQLを必要とし、今回の環境では対象外。

### 確認済み

- whole /home/user/eve-logic userSvc 呼び出し検索で上記20メソッドとproxy内部eventを確認。別bound/moniker callerなし。
- EveJS 261018.1参照: /home/user/evejs-261018.1/src/server/src/services/user/userService.js。メソッド範囲は各項目に記載。
- 変更projectのdotnet test compile成功。
- focused:
  User_service_compatibility_replies_are_dispatched_through_host_wire
  Soulbound_multi_training_without_provider_returns_a_user_error_over_wire
  User_service_unhandled_errors_are_error_responses_over_wire
  **3 passed, 0 failed, 0 skipped**。
- EVE_PG_URLをunsetにしてEve.Host.Tests全体を実行: **561 passed, 100 failed, 227 skipped / 888**。失敗はEVE_PG_URL必須のDB fixturesを起動できないもの（例 MoonRigReadModelTests のURL assertion、CrateServicePersistenceTests のURL assertion、IndustryReadyTests のPgConnection初期化）。ユーザー指定に従いPostgreSQLは使用していない。skipはpassに数えていない。
- graphical V24.01 clientでは実行していない。

### 今回確認できなかったこと

- DBなしのため、QA token create、redeem claim/reverse/trash transaction、resculpt、MCT DB notification順序、logoff永続化をdatabase-backed testで追跡していない。
- special redeem rewardとper-token errorをHostで実装すること、redeem expiry pruningのpersisted cleanup、proxy event routeは今回の変更対象外であり、互換差を残す。
