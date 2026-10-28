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
