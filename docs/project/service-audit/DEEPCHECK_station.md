# station / stationSvc / officeManager 深掘り監査

確認日: 2026-10-27

## 範囲と本番経路

`/home/user/eve-logic` 全体を検索し、クライアントが実際に送る station 系RPC、その戻り値の消費先、Monikerを通る officeManager 呼び出しを追跡した。station の業務RPCは7件。officeManager は通常RPC 1件、Moniker経由の業務RPC 10件、プロトコル用 `MachoResolveObject` / `MachoBindObject`。stationSvc にあるoffice用転送メソッドは、V24.01クライアントのMoniker先ではない。クライアントは `Moniker('officeManager', stationID)` を使う（`eve/client/script/ui/services/corporation/officeManager.py:49-50`）。

本番の `StationRpc` は `stationSvc` を組み立て、共有OfficeManagerとSDE SolarSystemCatalogを渡す（`dotnet/src/Eve.Host/Rpc/StationRpc.cs:42`）。HostCompositionが `station` を `stationSvc` の別名として登録する（`dotnet/src/Eve.Host/Hosting/HostComposition.cs:280`）。CorporationCompositionが永続store・wallet・item store・structure providerをOfficeManagerServiceへ配線し（`dotnet/src/Eve.Host/Composition/CorporationComposition.cs:263-282`）、CorporationRpcのサービス一覧へ登録する（`dotnet/src/Eve.Host/Rpc/CorporationRpc.cs:71`）。

クライアントのサービス呼び出しは位置引数。V24.01 transportは `machoVersion` をkwargsへ付ける（`eve/common/script/net/ServiceCallGPCS.py:221-225`、`eve/common/script/net/ObjectCallGPCS.py:399-403`）。Host PacketDispatcherはこれをアプリkwargsから除去してからServiceCallへ渡す（`dotnet/src/Eve.Services/PacketDispatcher.cs:219`）。ID読取はstation側の `IdOf`、office側の `Integral` がPyInt/PyLongを受け、station moniker IDはPyInt/PyLongとtuple/listラッパーを受ける。Host wire helperはすべて `machoVersion=1` を付けている。

## stationSvc 業務RPC

### GetStation

- クライアント呼び出し: `eve/common/script/util/notificationUtil.py:67`、`eve/client/script/ui/shared/maps/mapSvc.py:528`。結果は `notificationUtil.py:71` の `stationTypeID` と `mapSvc.py:529-531` のnull確認後の `stationName` 等に使われる。station取得のローカルservice呼び出しは別経路で、追加Host RPCではない。
- Hostは位置引数のPyInt/PyLong stationIDを読み、既知stationならCachedMethodCallResult(KeyVal)、未知IDならNone（`StationService.cs:63-67`）。SDE StationCatalogから読み、状態変更や通知はない。
- Host wireは両整数型、未知IDのNone、キャッシュwrapperを確認する。

### GetStationItemBits

- 呼び出しと消費: `eve/client/script/ui/station/base.py:574`、返値を `Row(['ownerID','itemID','operationID','stationTypeID'], bits)` にする同ファイル `:582-586`。
- 引数なし。Hostはstation dock時に4個のPyIntを含むPyList、stationなしならNoneを返す（`StationService.cs:96-100`）。EveJSも配列を返す（`stationService.js:117-132`）。tuple/listどちらもクライアントのRowが反復するが、HostをEveJSの配列形に合わせた。
- 静的station状態の読取のみ。通知なし。

### GetGuests

- クライアントは `eve/client/script/ui/station/base.py:102` で引数なし呼び出しをし、同ファイル `:104-110` で4値を展開してguest辞書へ入れる。表示側は `eve/client/script/ui/shared/dockedUI/guests.py:93-95`。
- Hostはstation内の現行guestを `[characterID, corporationID, allianceID, warFactionID]` のPyList行で返す。session snapshots / character read modelから作り、observer用guest ledgerへ基準集合を渡す（`StationService.cs:103-145`）。行をlistにしたのはEveJSのarray形との一致。クライアントは反復・展開するため消費形も一致する。
- 読取時に永続状態変更や通知送信はない。後続guest差分通知用の基準だけ記録する。

### GetStationsForOwner

- 呼び出し: `eve/client/script/ui/station/loyaltyPointStore/lpStoreSvc.py:534`。各結果の `stationID` と `solarSystemID` を同ファイル `:535-537` で使う。
- ownerIDを位置引数で受け、PyInt/PyLongの両方を許容。Hostはstation情報を持つKeyValのPyListを返す（`StationService.cs:70-93`）。station静的カタログからの読取のみ、通知なし。
- 実Host wireでPyInt/PyLong owner、row fieldsを確認する。

### GetAllianceSystems

- 呼び出し: `eve/client/script/ui/shared/maps/starMapSvc.py:1607` と `eve/client/script/ui/shared/mapView/mapViewData.py:99`。両方とも各rowの `solarSystemID` と `allianceID` を読む（同ファイル `:1608-1609`、`:100-101`）。
- 引数なし。Hostは現行sovereignty read modelから `[{solarSystemID, allianceID}]` のKeyVal列を作る（`StationService.cs:150-158`）。永続変更・通知なし。

### GetSystemsForAlliance

- 呼び出し: `eve/client/script/ui/shared/neocom/Alliances/all_ui_systems.py:31`。結果は同ファイル `:82,96` で表示対象systemとして使われる。
- allianceIDを位置引数で受け、PyInt/PyLongの両方を許容。Hostは `solarSystemID` と `allianceID` を持つKeyValのPyListを返す（`StationService.cs:160-168`）。sovereignty read modelの読取のみ、通知なし。

### GetSolarSystem

- 呼び出し: `eve/devtools/script/insider.py:347` は明示systemIDを位置引数で渡し、応答の `factionID` を即座に使う。session中のsystemと同一とは限らず、dockも前提にしていない。
- Hostは要求IDのPyInt/PyLongを pinned-SDE-backed `SolarSystemCatalog` から読み、systemID/name、constellationID、regionID、security、nullable factionIDをKeyValで返す（`StationService.cs:127-147`）。永続変更・通知なし。
- Host wireはdockなし、要求したsystem、PyLong、未知IDのNoneを確認する。EveJS `stationService.js:218` は引数を無視してsession stationの記録を返す（データ取得 `stationStaticData.js:223`）。要求systemを読むV24.01 callsiteを優先した差分を `docs/reference/compat/DEVIATIONS.md` に記録した。

## officeManager Moniker / 業務RPC

OfficeManagerクライアントは `officeManager.py:49-50` でstation/structure IDを使いMonikerを作り、Moniker transportがresolve/bind後のRPCを送る。resolveはProxyNodeIdをPyIntで返し、bindは `(bound PySubStruct, nestedResult)` を返す。初回のnested argsはtuple/list、nested kwargsはServiceCall kwargsとして渡される。後続bound callsもPacketDispatcherの実bound object解決を通る。

### MachoResolveObject / MachoBindObject

- 呼び出し元はofficeManager Moniker（`eve/client/script/ui/services/corporation/officeManager.py:49-50`）、bound初期呼び出しは `:107,113,116,121,135,142,148,151`。別stationへのunrentもMonikerでbindする（`:126-128,155-156`）。
- HostはofficeManager service登録を経てresolve/bindする。station IDはPyInt/PyLongを受け、tuple/list argsとNone/nested methodを処理する（`OfficeManagerService.cs:27-78`）。object IDはHostのbound object tableに登録され、後続BoundCallは同じHost routeでdispatchされる。
- 実Host wireでresolve、PyInt/PyLongのbind、nested PrimeOfficeItem/GetPriceQuote、後続BoundCall、machoVersion kwargsを確認する。protocol処理は永続化やoffice通知を起こさない。無効IDなど不正要求はservice error。

### GetMyCorporationsOffices

- 通常RPCで呼び出す（`officeManager.py:40`）。全クライアントの利用箇所: `corp_ui_member_find.py:246`、`corp_dlg_edit_member.py:182`、`corp_ui_home.py:483`、`corp_ui_member_view_task_mgt.py:374`、`corp_ui_member_listentry.py:68`、`corp_ui_member_newroles.py:307`、`tryFit.py:262`、`itemLocking.py:48`、`menuSvcExtras/invItemFunctions.py:407`、`invCache.py:684`、officeManager-local `:92`。
- Host reads active rentals from CorporationOfficeStore and returns KeyVal objects in a PyList; fields include `officeID`, `stationID`, lowercase `solarsystemID`, and `stationTypeID` (OfficeManagerService.cs:78-98). `corp_ui_home.py:483-519` and `officeManager.py:86-87` consume these names. The previous Host row omitted the latter two required fields; they are now populated from persisted office system ID and the authoritative station/structure catalog. officeID/folder IDs remain long values.
- Persistent read only. No notification. The no-DB Host wire test uses an injected read callback to verify the actual RPC reply. PG-backed service regressions cover persisted store behavior and stale client quotes (compiled; execution was excluded by the no-database task constraint).

### GetCorporationsWithOffices

- Moniker call `officeManager.py:33`; docked UI delegates via `baseController.py:32`.
- No explicit args; bound station ID or session station/structure resolves location. Host returns a Python set of active corporation IDs (OfficeManagerService.cs:252-261), matching EveJS `buildPythonSet` and the client's owner priming/iteration. Store read only; no notification.

### PrimeOfficeItem

- Called only after a matching office row is found in `officeManager.py:92-107`; client sets local `isPrimed` and ignores the RPC result (`:105-107`).
- Host returns None, as EveJS does. It does not mutate or prime authoritative state; no notification.

### GetPriceQuote

- Client calls `officeManager.py:113` with `session.corpid` as one positional arg. UI consumes the numeric quote via `baseController.py:59` and `dockedUI/offices.py:140-142`.
- Host accepts PyInt/PyLong corpID, verifies it matches the session, then reads station/structure rent. A safe integral price returns PyLong, fractional price PyReal (OfficeManagerService.cs:100-112,557-560), matching EveJS Number.isSafeInteger behavior. A quote initializes/updates the persisted office_station_prices row as needed; no notification.
- A corporation mismatch returns the exact `CrpAccessDenied` UserError; an ordinary valid quote has no error. The Host wire test covers both integer widths and PyLong reply type.

### RentOffice

- Client sends accepted quote as the only positional arg (`officeManager.py:115-116`, `baseController.py:61-62`, `dockedUI/offices.py:140-142`). EveJS returns None on success.
- クライアントは表示中の見積額を位置引数で送る。Hostは引数値を課金額にせず、station lock内で現行価格を再計算してから、office row・folder・rent bill・両walletを同一transactionで更新する（OfficeManagerService.cs:114-173）。EveJSも引数を使わず現行価格を決める（`officeManagerService.js:450-498`）。不足残高は `NotEnoughMoney(amount,balance)` UserErrorにする。
- Success sends OnItemsChanged followed by OnOfficeRentalChange, matching EveJS order. Client invalidates office caches and scatters OnOfficeRentalChanged (`officeManager.py:68-75`); inventory listeners consume OnItemsChanged (`environment/invCache.py:218-223`). Host wire checks service route, quote result, and result shape. PG tests check persistent rent using a stale Python-long quote and no partial office/wallet mutation on insufficient funds.

### UnrentOffice

- Client calls station-bound `officeManager.py:119-121`; remote-office entrypoint binds the requested station and calls the same method at `:126-128`. Return is ignored.
- Host requires CEO/Director, locks station and transactionally ends active rental, cancels open rent bill, deletes office folder, and moves corporation hangar assets to Impounded when present (OfficeManagerService.cs:175-203; StationOfficeImpound.cs:14-31). Returns None.
- Denied role is exact `CrpAccessDenied`. On success Host sends office folder OnItemsChanged when there were no assets, then OnOfficeRentalChange; when assets were impounded, it follows EveJS by omitting the folder item event and sends rental change. UI refreshes office list and office-folder cache (`dockedUI/offices.py:187-190`).

### GetEmptyOfficeCount

- Client wrapper calls only while in a station; structure mode returns None locally (`officeManager.py:133-135`). Station lobby displays the numeric slot count through `baseController.py:32-38`.
- Host counts active persisted rentals and returns PyInt `max(0, capacity - occupied)`; structure returns None (OfficeManagerService.cs:264-271). Read only, no notification.

### HasCorpImpoundedItems

- Client wrapper rejects NPC corps and offices before the Moniker call (`officeManager.py:137-142`); UI uses the bool to decide whether to show release button (`dockedUI/offices.py:122-125`).
- Host checks corp existence/type, active rental, and item store; returns PyBool. Structure/no eligible impound returns False (OfficeManagerService.cs:274-291). Read only, no notification.

### GetImpoundReleasePrice

- Client first checks HasCorpImpoundedItems; if false it returns None without RPC, else calls bound method (`officeManager.py:144-148`). Base controller and lobby UI display/confirm numeric price (`baseController.py:78-82`, `dockedUI/offices.py:165-170`).
- Host returns PyReal half of the current station rent; returns zero for structure, non-player corporation, active office, or no impounded assets (OfficeManagerService.cs:294-313). Quote read may initialize/update persisted office price as above. No notification.

### GetItemsFromImpound

- Client sends the displayed quote as one positional arg through `baseController.py:81-82` and `officeManager.py:150-151`; after RPC, lobby invokes `_load()` itself (`dockedUI/offices.py:169-170`).
- HostはCEO/Directorを確認し、クライアントの表示quote値ではなくtransaction中に再計算した現在のrelease feeを請求する。wallet debit/creditとimpounded itemsのCorpSAG1移動を同一transactionで行い、Noneを返す（OfficeManagerService.cs:205-249）。不足残高はEveJSと同じ `NotEnoughMoney(amount,balance)` UserError。
- EveJS does not send OnItemsChanged for release; client explicitly reloads the lobby after the call. Host sends no additional notification.

### TrashImpoundedOffice

- Client asset action invalidates GetAssetInventoryForLocation, calls the Moniker, then scatters OnReloadCorpAssets (`eve/client/script/ui/shared/neocom/corporation/corp_ui_accounts.py:410-413`; Moniker call in `officeManager.py:155-156`).
- Host requires CEO/Director, deletes matching impounded item rows transactionally, and returns None (OfficeManagerService.cs:316-340). It emits no server notification because the client performs cache invalidation and asset reload itself.
- EveJS additionally emits OnOfficeRentalChange when it removes the impounded runtime office (`officeManagerService.js:631-654`). In eve.rs, active office row/caches were already removed on UnrentOffice and this endpoint removes items only; client evidence shows it performs its asset reload. Difference recorded as no further station/office notification needed after the client reload.

## EveJS比較、テスト、制約

EveJS station実装は `src/server/src/services/station/stationService.js:67-232`。officeManagerは `src/server/src/services/corporation/officeManagerService.js:415-654`、office row columnsは同ファイル `:317-340`。一致させた箇所は、station item bits / guest list rowの配列形、PyLongの安全な整数quote、office rowの `solarsystemID` / `stationTypeID`、access deniedと不足残高のUserError、Rent/Unrentの通知順。GetSolarSystemの差分は互換記録に記載した。

実Host wire tests:
- `StationStructureRpcHostWireTests.cs`: station alias/route、kwargs、GetGuests / GetStation / GetStationItemBits / GetStationsForOwner / alliance methods。
- `StationSolarSystemHostWireTests.cs`: SDE-backed requested system、PyInt/PyLong、unknown None。
- `OfficeManagerHostWireTests.cs`: resolve/bind/nested and subsequent BoundCall、office row fields、PyInt/PyLong、quote result type、UserError wire。
- `CorporationTests.cs`: PG-backed persistence, wallet, stale-quote settlement and UserError regressions. `CorporationOfficeNotificationTests.cs` checks notification order and recipients.

## 実行確認（2026-10-27）

- `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false`: 成功（0 warnings / 0 errors）。
- `dotnet build dotnet/tests/Eve.Corporations.Tests/Eve.Corporations.Tests.csproj -p:UseSharedCompilation=false`: 成功（0 warnings / 0 errors）。
- `OfficeManager_access_denial_is_a_UserError_on_the_bound_Host_route`: 成功（1 passed / 0 skipped）。
- PostgreSQLを使う可能性のあるHost.Tests全件、Corporations.TestsのPGケース、およびGUIクライアント接続は、タスク指定に従い実行していない。従って永続化・競合時の新しいPG回帰テストはコンパイル確認までで、実行結果は得ていない。
- push前に必要なHost.Tests全件の確認を満たせず、pushしていない。
