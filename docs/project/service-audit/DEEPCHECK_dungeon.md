# dungeon 全クライアント呼び出し・Host深掘り監査

対象は eve-logic V24.01 / build 3396210 全ツリーとCSVの16行。unique methodは5。全callerはeve/client/script/ui/inflight/dungeoneditor.pyにある。

## 登録とHost data source

Host DungeonRpc.cs:60はDungeonServiceに実editor catalog providerを渡す。providerはSDE/editor read modelからcatalogを読み、DungeonRpcServices.cs:47-120がclient mappingへ変換する。読み取り専用でmutation, persistence writer, follow-up notificationはない。clientはpositional引数なしで、filtersをkwargsで送る。machoVersion=1はtransport kwargs。

## 全RPCとreply consumer

| メソッド | 全client call / consume | Host wire result / EveJS |
|---|---|---|
| DEGetDungeons | :142 archetypeID/factionID kwargs。rowのfactionID,dungeonNameID,dungeonIDを読む (:143-155)。selected dungeonは:224でdungeonID keyword、結果[0].dungeonNameIDを読む。別room editor pathは:1380で同じkeywordを送る。 | PyList[KeyVal]。各rowはdungeonID,dungeonNameID,dungeonName,factionID,archetypeID。未知/None filterは全catalog rowに対するread。EveJS fixture queryもlist rows。 |
| DEGetArchetypes | :196-197。reply tuplesをsortしcomboへ入れる。 | PyList[(name,id)]。 |
| DEGetFactions | :202-204。factionIDをowner nameへ変換してcomboへ入れる。 | PyList[ID]。 |
| DEGetRooms | :236-239、dungeonID keyword。(roomID,roomName) tupleを反復。 | PyList[Tuple(ID, str)]。見つからないdungeonはempty list。 |
| DEGetRoomObjectPaletteData | :1102-1117,1439。replyをgroupItemsへ渡す。GetGroupTypes (:1112-1127) はlist of (id,name) とdictionary keyed by (groupID,groupName) の両方を処理する。 | providerのPyList/PyDict treeをそのまま返し、clientが認識する両形を保つ。 |

IDsはclient SDE data由来integer、None filterはall。Host parserはdungeonID positional fallbackも許すがV24 callsitesはkwargs。bad catalog IDはempty listになり、これらのread RPCにapplication UserErrorはない。SDE catalog readだけで永続変更や通知を発生させない。

## EveJS照合とtests

EveJS v0.12.9 server/src/services/dungeon/dungeonService.js:172-215は同じcatalog filtersを適用してlist rows, room tuples, faction/archetype choicesを返す。Hostはstatic fixtureでなくproduction read modelを読む。DungeonDynamicResourceHostWireTests.csのDungeon_and_dynamic_resource_cache_calls_round_trip_through_host_registrationはHost routeからpayload shapesを検査する。

- Dungeon_and_dynamic_resource_cache_calls_round_trip_through_host_registration: 1 passed、0 skipped。
- Production SDE/database catalog contentと実GUI dungeon editorは今回起動していない。route/wire contract以外に修正gapは見つからなかった。


## 引数型
DEGetDungeonsのarchetypeID/factionID、dungeonIDはclientからkeywordで送られ、省略/Noneは未指定filter。DEGetRoomsもdungeonID keyword。その他は引数なし。IDsはinteger、Noneはfilter無しを表す。Hostにはpositional dungeonID fallbackもあるがV24は使わない。machoVersion=1はtransport kwargs。
