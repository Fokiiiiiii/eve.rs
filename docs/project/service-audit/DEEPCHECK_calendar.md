# calendar Host サービス深掘り監査

調査日: 2026-10-27
対象: V24.01 eve-logic 全体、eve.rs Host、EveJS 261018.1

## サービス名と呼び出し経路

eve-logic の UI はローカルサービス `sm.GetService('calendar')` を呼びます。これは Host RPC 名ではありません。`eve/client/script/ui/services/eveCalendarsvc.py:63,71` で `RemoteSvc('calendarMgr')` と `ProxySvc('calendarProxy')` を束縛しています。全ソース検索では `calendar` という RemoteSvc/ProxySvc 名や calendar moniker は見つからず、直接の Host サービスは次の2つです。UI から facade を呼ぶ箇所には `eve/client/script/ui/shared/eveCalendar.py:1106,1447,1579,1584,1588,1597,1601,1664`、通知 formatter には `notifications/common/formatters/calendar.py:25` があります。

Host は `CalendarMgrService` を `[Service("calendarMgr")]`、`CalendarProxyService` を `[Service("calendarProxy")]` として公開します（`dotnet/src/Eve.Services.Core/CalendarMgrService.cs:16,333`）。メソッド属性は同ファイルの各行に記載しています。実運用では `CoreServices.Create` が store 付き両 service を作り一覧へ含め（`dotnet/src/Eve.Services.Core/CoreServices.cs:46-53,105-106`）、Host composition が永続 store とともに registry 登録します（`dotnet/src/Eve.Host/Composition/MailComposition.cs:252-255`; `dotnet/src/Eve.Host/Hosting/HostComposition.cs:128,236-265`）。

eve-logic の RPC 引数は全て位置引数です。UI の `CreateNewEvent(..., invitees=...)` はローカル facade の呼び出しで、実 RPC は `eveCalendarsvc.py:182,184` の位置引数です。クライアント transport は `machoVersion` を keyword に付け、`service.py:365-371` で通常の service 引数から取り除きます。Host wire test は実 Host listener に `Packets.CallWithKwargs` を送り、この transport keyword を含めます（`dotnet/tests/Eve.Host.Tests/CalendarRpcHostWireTests.cs:13-40,437-441`; helper 定義 `ServiceResponseBatchHostWireTests.cs:13-15`）。Host の `LongValue` は PyInt/PyLong、nullable duration は None/PyInt/PyLong、text は None/string、character ID 群は None/PyList/PyTuple を受けます（`CalendarMgrService.cs:400-440`）。wire テストでは両整数型、None、list/tuple、transport keyword を混ぜて呼び出しています. メソッド用 keyword 引数はクライアントにありません。

## メソッド別結果

各行のクライアント位置は RPC の実呼び出し箇所です。返却列は同じ client の直後の消費箇所です。EveJS の manager/proxy ルートは `evejs-261018.1/src/services/calendar/calendarMgrService.js:24-162`、`calendarProxyService.js:9-27` です。

| RPC | eve-logic 呼び出しと戻り値消費 | Host wire / state / error / notification |
|---|---|---|
| `calendarMgr.GetResponsesForCharacter()` | `eveCalendarsvc.py:251`。返却 row の `eventID/status` を辞書に保存（同:252-255）。 | route: `CalendarMgrService.cs:41`; PyList of KeyVal rows。Host store から本人の response を読む。wire テストは owner/invitee の後続応答を確認。応答変更通知はこの read では発生しない。EveJS response read: `calendarRuntimeState.js:321-340`。 |
| `calendarMgr.GetResponsesToEvent(eventID, ownerID)` | `eveCalendarsvc.py:414`; `status == eventResponseUninvited` を除いて `characterID -> status` を組み立てる（同:415-419）。UI は `eveCalendar.py:1447,1664` から同 facade を呼ぶ。更新後の cache invalidation は `eveCalendarsvc.py:201,309,367`。 | route: `CalendarMgrService.cs:49`; PyList of KeyVal(`characterID/status`)。missing / owner mismatch / audience 不許可はそれぞれ ClientNotify。wire test は invited/removed/added status、outsider denial を確認。read のため通知なし。EveJS read/visibility: `calendarRuntimeState.js:218-224,321-340`。 |
| `calendarMgr.CreatePersonalEvent(dateTime,duration,title,description,importance,invitees)` | CSPA 経由 `eveCalendarsvc.py:182` または直接 `:184`; 作成 ID を facade が受け取り、ローカル一覧・response cache を更新（同:185-187）。UI 作成フォームは `eveCalendar.py:1579`。 | route: `CalendarMgrService.cs:69`; PyLong event ID。イベント、creator の Accepted、招待者の Undecided を同 store transaction に保存。成功後に viewer へ8引数 `OnNewCalendarEvent`（呼出 session を除外）。UserError は `CalendarEventMustSpecifyTitle`, `CalendarTitleTooLong`, `CalendarInvalidDuration`, `CalendarCannotPlanThePast`, `CalendarTooFarIntoFuture(numMonths=12)`; 招待上限/説明長/欠権は ClientNotify。wire tests は None duration/description、tuple invitees、成功通知と各 validation を確認。EveJS: `calendarRuntimeState.js:405-489`, validation `:342-380`。 |
| `calendarMgr.CreateCorporationEvent(dateTime,duration,title,description,importance)` | `eveCalendarsvc.py:190`; facade は作成 ID を受ける。 | route: `CalendarMgrService.cs:72`; PyLong event ID。PostgreSQL event transaction 後、corp audience に8引数 `OnNewCalendarEvent`。無権限時は client-visible corporation permission CustomNotify。wire test は作成/list 表示/edit と通知、create denial を確認。EveJS create route/runtime: manager `:24-162`, runtime `:405-489`。 |
| `calendarMgr.CreateAllianceEvent(dateTime,duration,title,description,importance)` | `eveCalendarsvc.py:192`; facade は作成 ID を受ける。 | route: `CalendarMgrService.cs:75`; PyLong event ID。保存後 alliance audience に `OnNewCalendarEvent`。無権限時は alliance permission CustomNotify。wire test は作成/list 表示/edit と通知、create denial を確認。EveJS create route/runtime: manager `:24-162`, runtime `:405-489`。 |
| `calendarMgr.UpdateEventParticipants(eventID,add,remove)` | CSPA/direct は `eveCalendarsvc.py:198,200`; cache invalidation は `:201`; UI は `eveCalendar.py:1588`。 | route: `CalendarMgrService.cs:158`; None。personal responses を transaction で更新。実際の追加分へ `OnNewCalendarEvent`、削除分へ3引数 `OnRemoveCalendarEvent(isDeleted=False)`。欠権、非personal event、上限超過は ClientNotify。wire test は PyTuple add/PyList remove、後続 response read、追加/削除通知と非personal/欠権/上限拒否を確認。EveJS: `calendarRuntimeState.js:665-710`。 |
| `calendarMgr.EditPersonalEvent(eventID,oldDateTime,dateTime,duration,title,description,importance)` | `eveCalendarsvc.py:212`; UI の edit は `eveCalendar.py:1584`。クライアントは RPC 値を直接利用せず成功後 True を返す。 | route: `CalendarMgrService.cs:102`; None。old timestamp 条件付き transaction。日時変更時は既存 Accepted/Maybe/Undecided response を Undecided に戻し、その後8引数 `OnEditCalendarEvent`。missing/deleted/permission/stale は個別 CustomNotify、入力 validation は create と同じ UserError。wire test は edit、response reset、notification、permission/stale/deleted を確認。EveJS: `calendarRuntimeState.js:609-663`。 |
| `calendarMgr.EditCorporationEvent(...)` | `eveCalendarsvc.py:214`; facade edit と UI 呼び出しは `:205-217`, `eveCalendar.py:1584`。 | route: `CalendarMgrService.cs:105`; None。同 transaction と viewer edit notification、group manager 検査。wire test は成功 edit、後続 detail の更新と notification を確認。permission/stale/deleted/validation は共通 EditEventAsync 分岐（`CalendarMgrService.cs:112-137`）。EveJS: `calendarRuntimeState.js:609-663`。 |
| `calendarMgr.EditAllianceEvent(...)` | `eveCalendarsvc.py:216`; facade/UI は上記と同じ `:205-217`, `eveCalendar.py:1584`。 | route: `CalendarMgrService.cs:108`; None。transaction 後に alliance viewers へ edit notification。wire test は成功 edit と notification を確認。permission/stale/deleted/validation は共通 edit 分岐。EveJS: `calendarRuntimeState.js:609-663`。 |
| `calendarMgr.DeleteEvent(eventID,ownerID)` | `eveCalendarsvc.py:318`; UI は `eveCalendar.py:1601`。facade は成功時 True を返し、RPC 戻り値は使わない。 | route: `CalendarMgrService.cs:140`; None。soft-delete と response status を store transaction で更新し、削除前に取得した viewer に `OnRemoveCalendarEvent(..., True)`。missing/owner mismatch/permission は個別 ClientNotify。wire test は deleted list row、remove notification、deleted edit refusal を確認。EveJS: `calendarRuntimeState.js:712-730`。 |
| `calendarMgr.SendEventResponse(eventID,ownerID,response)` | `eveCalendarsvc.py:295`; UI facade は `eveCalendar.py:1597`; client は optimistic response cache 更新と invalidation を行う（`eveCalendarsvc.py:298-309`）。 | route: `CalendarMgrService.cs:204`; None。response transaction 後、他 viewer へ `OnEventResponseByExternal(eventID,eventKV,response)`。eventKV は `ownerID/flag/eventDateTime`。missing/owner mismatch/access/invite/response range は具体的 ClientNotify。wire test は Maybe の後続 read、notification KeyVal と outsider/invalid response denial を確認。EveJS: `calendarRuntimeState.js:732-770`, notifications `:78-138`。 |
| `calendarProxy.GetEventList(month,year)` | `eveCalendarsvc.py:238`; client は tuple の全slotを走査し None を飛ばし、row ごと `utillib.KeyVal` 化（同:239-241）。それから `ownerID/autoEventType` から flag を補う。 | route: `CalendarMgrService.cs:349`; 3つの PyList を持つ PyTuple（personal/CCP/automated、corp、alliance）、行は PyDict。DB から月内の可視イベントを読む。wire test は 3 slot、row fields、corp/alliance を確認。通知なし。EveJS payload は2,3番目を null にする (`calendarPayloads.js:70-78`); client が None を許容するため Host は保存済み group rows を返す意図的差分。記録: `docs/reference/compat/DEVIATIONS.md` の Calendar mutation notifications 段落。 |
| `calendarProxy.GetEventDetails(eventID,ownerID)` | `eveCalendarsvc.py:260`; UI は `eveCalendar.py:1106-1111` で `eventText/creatorID` を読む。通知 formatter も facade を呼ぶ (`notifications/common/formatters/calendar.py:25`)。 | route: `CalendarMgrService.cs:368`; Py.KeyVal object の `eventID/ownerID/creatorID/eventText`。missing/owner mismatch/access denied は個別 CustomNotify。wire test は attribute access、更新後 text、outsider access、missing event を確認。read のため通知なし。EveJS: `calendarRuntimeState.js:282-302`。 |

## 永続化・通知の根拠

実 Host は `MailComposition` で PostgreSQL schema を migrate し `CalendarStore` を作成します（`MailComposition.cs:252-255`）。作成・編集・soft delete・参加者更新・response 更新は各 transaction 内で commit します（`dotnet/src/Eve.Calendar/CalendarStore.cs:159-260`）。月別 list は DB から読み出し audience/group 条件で絞ります（同:98-117）。manager/proxy は同 store を共有して `CoreServices.Create` から route されます。

Host wire tests はサービス単体直呼びではなく strict RPC Host listener、registry、wire packet を使います。永続化インターフェースはテスト用 in-memory store で、後続 RPC からの状態可視性と通知 packet の引数型/個数を検査します（`CalendarRpcHostWireTests.cs:13-203`）。error test は UserError 名と CustomNotify 内容を検査します（同:205-235,419-441）。通知 client 側 consumer は `eveCalendarsvc.py:332-409`（New/Edit/Remove/External response）です。EveJS の通知 wire shapes は `calendarNotifications.js:78-138`。Host は store mutation 完了後に通知を発行し、RPC handler return 前に通知 helper を終えます。

## 検証状況

確認済み:
- eve-logic 全体を検索し、facade、remote/proxy endpoint、直接 RPC call sites、reply/notification consumers を追跡。
- Focused Host wire tests: 2 passed, 0 skipped.
- Host 全 shard: 503 passed, 227 skipped, 101 failed。確認できた失敗例は `EVE_PG_URL` に assigned PostgreSQL lane が必要な DB test。DB 禁止のため PostgreSQL 接続はしていない。
- Host route / persistence composition と CalendarStore transaction implementation をソースで確認。

今回確認していない:
- 実 PostgreSQL による restart 後の永続状態、実際の V24.01 graphical client、EveJS 実行時の end-to-end 対照。
- 全 Host shard は合格していない。CI push gate と指定された事前条件を満たせないため、push はしない。
