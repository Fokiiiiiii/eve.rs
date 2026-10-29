# XmppChatMgr 全クライアント呼び出し・Host深掘り監査

対象は eve-logic V24.01 / build 3396210 全ツリーとcoverage CSVの19行。unique methodは13。duplicate rowsは同じclient callsite/signatureの再収録。

## 登録・引数

HostComposition.cs:309-315はXmppChatMgrServiceをXmppChatHubに結び付けて登録する。remote methodsはpositional args。GetHelpChannel(languageID)は言語を受け取るcallsiteとsession languageで解決するcallsiteの両方を持つ。machoVersion=1はtransport kwargs。stringsはPyStr、結果のchannel listはPyList[PyStr]、成功/拒否の状態はPyBoolまたはPyNone。

## 全client callとreply consumer

| メソッド | V24全callsite / 消費 |
|---|---|
| Hostname | xmppchatsvc.py:284。_CreateConnection :276-294がhostnameとしてXmppConnectionへ渡す。replyはstr。 |
| GetDeprecatedPrefsFallback | xmppchatsvc.py:312。ejabberd host preferenceが空の時のfallback接続先として使用 (:302-315)、str。 |
| ResyncSystemChannelAccess | xmppchatsvc.py:350,1124。:350-360でchannel name listを受けて個々をJoinChannelへ渡す。 |
| GetHelpChannel | help.py:125, xmppchatsvc.py:1001,1005。languageIDをpositionalに渡す場合がある。xmppchatsvc.py:986-1008でroom nameをJoinChannelへ渡す。 |
| GetRookieChannel | xmppchatsvc.py:997,1008。返ったroom nameをJoinChannelへ渡す。 |
| GrantChannelOwnership | xmppchatsvc.py:1083,1135、xmppchannelsettings.py:125。channel name positional。Host bool replyは呼び出し元で捨てられる。 |
| CreatePrivateChannel | xmppchatsvc.py:1198。charid positional。戻ったchannel nameでJoinChannelし、Noneならprivate roomを開始しない。 |
| CreatePlayerOwnedChannel | xmppchatsvc.py:1204。name positional。replyはroom名として後続のjoinに使われる。 |
| AddCharacterToChannel | xmppchatsvc.py:1258。channelName, charid, reason positional。成功boolは呼び出し元で捨てられる。 |
| SetMotd | xmppchatsvc.py:1485。roomName,motd positional。replyは捨てられる。 |
| EnsureResourceWarsChannelExists | resourcewars/client/rwservice.py:91-103。instanceID positional、replyは捨て、resourcewars_%d roomへJoinChannel。 |
| GMMute / GMUnmute | chat/client/chat_service.py:239-247。GMMuteはchannel_id, character_id, reason, duration_seconds; GMUnmuteはchannel_id, character_id。両replyとも捨てられる。 |

## Host状態・EveJS差

Hostはchannel creation/ownership/member/MOTDをXmppChatHub経由で処理し、player-owned channelとcorporation MOTDはstoreを使う。Resyncはserver stateからjoin可能channel一覧を作る。client-visible通知は後続XMPP presence/messageに属し、RPC reply自体はnotificationを送らない。player-owned channels/MOTDの永続成功経路はDBを必要とする。

EveJS v0.12.9 xmppChatMgrService.js:170-216はGMMute/GMUnmuteを登録し、mute対象・role確認に対しfalse、成功時trueを返す。chatRuntime.js:1252-1278はtimed restrictionを保持し、:1010でmuteを適用する。同service :229-239はinstanceIDからresourcewars_%dを作り、static verified roomを用意する。Host XmppChatMgrService.csはこれら3 methodを登録していない。client RPCはmethod未登録で失敗し、期待されるbool false/trueやresource-wars room作成はない。既存Hubにmoderation authority/timed mute storeやresource-wars membership registryがないため、常時true routeを足して隠すのは誤り。状態ownerとrole-gated mute enforcementを接続する実装が残る。

呼び出しで明示されたUserErrorはない。missing routeはapplication UserError responseではなくdispatcherのmethod-not-found errorとなる。その他のRPCはHost wire testsでroute shapeを確認済みだが、persistent player-channel successは今回DBを使っていないため実行していない。

## 確認した範囲

XmppChatMgrHostWireTests.csのclient wire testはcoverage内で実装済みのchannel methodsをHost dispatch経由で通す。今回追加変更なし。resource-wars channel ensure、mute/unmuteのroot feature・wire testは未実装。PostgreSQLとGUI clientは使っていない。


## 引数型
全RPCはアプリ引数をpositionalで送る。GetHelpChannelだけlanguageIDを任意の1 positional argとして使い、session.languageIDがあればHostはそちらを優先する。AddCharacterToChannelはchannel string, character integer, reason string。SetMotdはroom/motdの2 strings。GMMuteのduration_secondsはinteger seconds。IDはPyInt/PyLongを許容する。machoVersion=1はtransport kwargs。

## MOTD JID と muc#owner 実装引き継ぎ (2026-10-29)

V24.01全体の利用確認: `xmppchatsvc.py:1190-1192` は `GetFullRoomName` で `room@conference.<host>` を作る。`SetMotd` は corp/alliance category のどちらも同じフルJIDを `XmppChatMgr.SetMotd` に渡す (`1474-1485`)。`ConfigureChannel` は muc#owner SET のIQ `result` をboolで返し (`1492-1506`)、`xmppchannelsettings.py:217-259` は失敗時にRuntimeError/UserErrorを上げる。`GetChannelConfig` は muc#owner GET のresult stanzaを返し (`1625-1634`)、設定画面は `xmppchannelsettings.py:279-323` でroomname/password/default accessをフォームへ反映する。`DeleteChannel` は `<destroy/>` をSETし、成功時だけ保存済みchannel一覧を消して `OnChannelDeleted` をscatterする (`xmppchatsvc.py:1205-1228`); 呼び出し可能なmenu項目はplayer-room owner限定 (`xmppchannelfield.py:89-123`)。

eve.rsはSetMotdのfull JIDから有効なconference domainを検証してroom IDを抽出し、corp/allianceの現在のsession IDとdirector roleを確認してpersist後にsubjectを配信する。player-owned muc#owner GET/SETは既存`XmppPlayerChannelStore`の設定値を使い、owner-only destroy後にroom内occupantへunavailable presenceを順に送る。room-configに設定したpassword、members-only、members-by-defaultは再起動後も保持し、join/message権限に反映する。player-room configフォームを `XmppPlayerChannelStore` の既存tableへ加えた追加列は `ADD COLUMN IF NOT EXISTS` で既存schemaにも適用する。

比較: EveJS 261028.1 `server/src/services/chat/xmppStubServer.js:3053-3160` はowner IQ GETでconfig formを返し、SETをchannel adminに限定し、destroy時にchannelを削除してroom memberを退室させる。`server/src/services/chat/xmppChatMgrService.js:337-378` のSetMotdは `corp_` のみを正規化するが、V24.01 client callsiteはalliance roomも渡すため、Hostはclient観測形状に合わせてalliance roomも実装する。Host regressionは`XmppChatMgrHostWireTests.Xmpp_chat_mgr_calls_keep_client_wire_shapes_through_host_dispatch`, `XmppOrganizationRoomTests.Corporation_motd_is_authorized_broadcast_and_returned_on_join`, and `XmppOrganizationRoomTests.Player_room_owner_iq_configures_persists_and_destroys_with_member_presence`。前2件とXMPP非DB選択テスト30件はローカルpass。Actions run 40862223202で招待済みmemberのMUC roleをvisitorと期待して失敗したため、memberはparticipant、未招待guestはvisitorを確認するassertへ修正した。PostgreSQLテストをActionsで再実行する。GUI clientでの操作確認は引き続き未実施。
