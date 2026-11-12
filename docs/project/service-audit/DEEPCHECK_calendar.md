# calendar Host サービス深掘り監査

調査日: 2026-10-27
対象: V24.01 eve-logic 全体、eve.rs Host、EveJS 261028.1

## サービス名と呼び出し経路

eve-logic の UI はローカルサービス `sm.GetService('calendar')` を呼びます。これは Host RPC 名ではありません。`eve/client/script/ui/services/eveCalendarsvc.py:63,71` で `RemoteSvc('calendarMgr')` と `ProxySvc('calendarProxy')` を束縛しています。全ソース検索では `calendar` という RemoteSvc/ProxySvc 名や calendar moniker は見つからず、直接の Host サービスは次の2つです。UI から facade を呼ぶ箇所には `eve/client/script/ui/shared/eveCalendar.py:1106,1447,1579,1584,1588,1597,1601,1664`、通知 formatter には `notifications/common/formatters/calendar.py:25` があります。

Host は `CalendarMgrService` を `[Service("calendarMgr")]`、`CalendarProxyService` を `[Service("calendarProxy")]` として公開します（`dotnet/src/Eve.Services.Core/CalendarMgrService.cs:33,501`）。メソッド属性は同ファイルの各行に記載しています。実運用では `CoreServices.Create` が store 付き両 service を作り一覧へ含め（`dotnet/src/Eve.Services.Core/CoreServices.cs:46-53,105-106`）、Host composition が永続 store とともに registry 登録します（`dotnet/src/Eve.Host/Composition/MailComposition.cs:277-281`; `dotnet/src/Eve.Host/Hosting/HostComposition.cs:128,236-265`）。

eve-logic の RPC 引数は全て位置引数です。UI の `CreateNewEvent(..., invitees=...)` はローカル facade の呼び出しで、実 RPC は `eveCalendarsvc.py:182,184` の位置引数です。クライアント transport は `machoVersion` を keyword に付け、`service.py:365-371` で通常の service 引数から取り除きます。Host wire test は実 Host listener に `Packets.CallWithKwargs` を送り、この transport keyword を含めます（`dotnet/tests/Eve.Host.Tests/CalendarRpcHostWireTests.cs:13-241`、送信helper `:481-487`; helper 定義 `ServiceResponseBatchHostWireTests.cs:14-16`）。Host の `LongValue` は PyInt/PyLong、nullable duration は None/PyInt/PyLong、text は None/string、character ID 群は None/PyList/PyTuple を受けます（`CalendarMgrService.cs:574-610`）。wire テストでは両整数型、None、list/tuple、transport keyword を混ぜて呼び出しています. メソッド用 keyword 引数はクライアントにありません。

## メソッド別結果

各行のクライアント位置は RPC の実呼び出し箇所です。返却列は同じ client の直後の消費箇所です。EveJS 261028.1 の manager/proxy ルートは `/home/user/evejs-261028.1/src/server/src/services/calendar/calendarMgrService.js:24-162`、`calendarProxyService.js:9-27` です。

| RPC | eve-logic 呼び出しと戻り値消費 | Host wire / state / error / notification |
|---|---|---|
| `calendarMgr.GetResponsesForCharacter()` | `eveCalendarsvc.py:251`。返却 row の `eventID/status` を辞書に保存（同:252-255）。 | route: `CalendarMgrService.cs:65`; PyList of KeyVal rows。Host store から本人の response を読む。wire テストは owner/invitee の後続応答を確認。応答変更通知はこの read では発生しない。EveJS response read: `calendarRuntimeState.js:321-340`。 |
| `calendarMgr.GetResponsesToEvent(eventID, ownerID)` | `eveCalendarsvc.py:414`; `status == eventResponseUninvited` を除いて `characterID -> status` を組み立てる（同:415-419）。UI は `eveCalendar.py:1447,1664` から同 facade を呼ぶ。更新後の cache invalidation は `eveCalendarsvc.py:201,309,367`。 | route: `CalendarMgrService.cs:73`; PyList of KeyVal(`characterID/status`)。missing / owner mismatch / audience 不許可はそれぞれ ClientNotify。wire test は invited/removed/added status、outsider denial を確認。read のため通知なし。EveJS read/visibility: `calendarRuntimeState.js:218-224,321-340`。 |
| `calendarMgr.CreatePersonalEvent(dateTime,duration,title,description,importance,invitees)` | CSPA 経由 `eveCalendarsvc.py:182` または直接 `:184`; 作成 ID を facade が受け取り、ローカル一覧・response cache を更新（同:185-187）。UI 作成フォームは `eveCalendar.py:1579`。 | route: `CalendarMgrService.cs:93`; PyLong event ID。イベント、creator の Accepted、招待者の Undecided を同 store transaction に保存。成功後に viewer へ8引数 `OnNewCalendarEvent`（呼出 session を除外）。UserError は `CalendarEventMustSpecifyTitle`, `CalendarTitleTooLong`, `CalendarInvalidDuration`, `CalendarCannotPlanThePast`, `CalendarTooFarIntoFuture(numMonths=12)`; 招待上限/説明長/欠権は ClientNotify。wire tests は None duration/description、tuple invitees、成功通知と各 validation を確認。EveJS: `calendarRuntimeState.js:405-489`, validation `:342-380`。 |
| `calendarMgr.CreateCorporationEvent(dateTime,duration,title,description,importance)` | `eveCalendarsvc.py:190`; facade は作成 ID を受ける。 | route: `CalendarMgrService.cs:96`; PyLong event ID。PostgreSQL event transaction 後、corp audience に8引数 `OnNewCalendarEvent`。無権限時は client-visible corporation permission CustomNotify。wire test は作成/list 表示/edit と通知、create denial を確認。EveJS create route/runtime: manager `:24-162`, runtime `:405-489`。 |
| `calendarMgr.CreateAllianceEvent(dateTime,duration,title,description,importance)` | `eveCalendarsvc.py:192`; facade は作成 ID を受ける。 | route: `CalendarMgrService.cs:99`; PyLong event ID。保存後 alliance audience に `OnNewCalendarEvent`。無権限時は alliance permission CustomNotify。wire test は作成/list 表示/edit と通知、create denial を確認。EveJS create route/runtime: manager `:24-162`, runtime `:405-489`。 |
| `calendarMgr.UpdateEventParticipants(eventID,add,remove)` | CSPA/direct は `eveCalendarsvc.py:198,200`; cache invalidation は `:201`; UI は `eveCalendar.py:1588`。 | route: `CalendarMgrService.cs:193`; None。personal responses を transaction で更新。実際の追加分へ `OnNewCalendarEvent`、削除分へ3引数 `OnRemoveCalendarEvent(isDeleted=False)`。欠権、非personal event、上限超過は ClientNotify。wire test は PyTuple add/PyList remove、後続 response read、追加/削除通知と非personal/欠権/上限拒否を確認。EveJS: `calendarRuntimeState.js:665-710`。 |
| `calendarMgr.EditPersonalEvent(eventID,oldDateTime,dateTime,duration,title,description,importance)` | `eveCalendarsvc.py:212`; UI の edit は `eveCalendar.py:1584`。クライアントは RPC 値を直接利用せず成功後 True を返す。 | route: `CalendarMgrService.cs:135`; None。old timestamp 条件付き transaction。日時変更時は既存 Accepted/Maybe/Undecided response を Undecided に戻し、その後8引数 `OnEditCalendarEvent`。missing/deleted/permission/stale は個別 CustomNotify、入力 validation は create と同じ UserError。wire test は edit、response reset、notification、permission/stale/deleted を確認。EveJS: `calendarRuntimeState.js:609-663`。 |
| `calendarMgr.EditCorporationEvent(...)` | `eveCalendarsvc.py:214`; facade edit と UI 呼び出しは `:205-217`, `eveCalendar.py:1584`。 | route: `CalendarMgrService.cs:138`; None。同 transaction と viewer edit notification、group manager 検査。wire test は成功 edit、後続 detail の更新と notification を確認。permission/stale/deleted/validation は共通 EditEventAsync 分岐（`CalendarMgrService.cs:112-137`）。EveJS: `calendarRuntimeState.js:609-663`。 |
| `calendarMgr.EditAllianceEvent(...)` | `eveCalendarsvc.py:216`; facade/UI は上記と同じ `:205-217`, `eveCalendar.py:1584`。 | route: `CalendarMgrService.cs:141`; None。transaction 後に alliance viewers へ edit notification。wire test は成功 edit と notification を確認。permission/stale/deleted/validation は共通 edit 分岐。EveJS: `calendarRuntimeState.js:609-663`。 |
| `calendarMgr.DeleteEvent(eventID,ownerID)` | `eveCalendarsvc.py:318`; UI は `eveCalendar.py:1601`。facade は成功時 True を返し、RPC 戻り値は使わない。 | route: `CalendarMgrService.cs:174`; None。soft-delete と response status を store transaction で更新し、削除前に取得した viewer に `OnRemoveCalendarEvent(..., True)`。missing/owner mismatch/permission は個別 ClientNotify。wire test は deleted list row、remove notification、deleted edit refusal を確認。EveJS: `calendarRuntimeState.js:712-730`。 |
| `calendarMgr.SendEventResponse(eventID,ownerID,response)` | `eveCalendarsvc.py:295`; UI facade は `eveCalendar.py:1597`; client は optimistic response cache 更新と invalidation を行う（`eveCalendarsvc.py:298-309`）。 | route: `CalendarMgrService.cs:305`; None。response transaction 後、他 viewer へ `OnEventResponseByExternal(eventID,eventKV,response)`。eventKV は `ownerID/flag/eventDateTime`。missing/owner mismatch/access/invite/response range は具体的 ClientNotify。wire test は Maybe の後続 read、notification KeyVal と outsider/invalid response denial を確認。EveJS: `calendarRuntimeState.js:732-770`, notifications `:78-138`。 |
| `calendarProxy.GetEventList(month,year)` | `eveCalendarsvc.py:238`; client は tuple の全slotを走査し None を飛ばし、row ごと `utillib.KeyVal` 化（同:239-241）。それから `ownerID/autoEventType` から flag を補う。 | route: `CalendarMgrService.cs:517`; 3つの PyList を持つ PyTuple（personal/CCP/automated、corp、alliance）、行は PyDict。DB から月内の可視イベントを読む。wire test は 3 slot、row fields、corp/alliance を確認。通知なし。EveJS payload は2,3番目を null にする (`calendarPayloads.js:70-78`); client が None を許容するため Host は保存済み group rows を返す意図的差分。記録: `docs/reference/compat/DEVIATIONS.md` の Calendar mutation notifications 段落。 |
| `calendarProxy.GetEventDetails(eventID,ownerID)` | `eveCalendarsvc.py:260`; UI は `eveCalendar.py:1106-1111` で `eventText/creatorID` を読む。通知 formatter も facade を呼ぶ (`notifications/common/formatters/calendar.py:25`)。 | route: `CalendarMgrService.cs:536`; Py.KeyVal object の `eventID/ownerID/creatorID/eventText`。missing/owner mismatch/access denied は個別 CustomNotify。wire test は attribute access、更新後 text、outsider access、missing event を確認。read のため通知なし。EveJS: `calendarRuntimeState.js:282-302`。 |

## 削除済みpersonal eventのアクセス

`CalendarStore.DeleteEventAsync` は response status が `Deleted` または `Uninvited` の行を維持し、実際の招待者と creator の既存応答を `Deleted` にする。`ListMonthAsync` は status 0 (`Uninvited`) を可視条件から除き、`CalendarWire.CanViewAsync` も同 status を拒否するため、削除前に明示的に uninvite された character は削除後の月一覧と詳細の両方から除外される。owner と残る招待者は tombstone event と `eventText` を引き続き取得できる (`CalendarStore.cs:98-117,218-232`; `CalendarMgrService.cs:371-386,539-550`)。

これは EveJS 261028.1 と同じアクセス結果である。`updateEventParticipants` が invitee ID を削除し、response を Uninvited にする (`calendarRuntimeState.js:665-710`); `deleteEventRecord` は event を tombstone 化して invitee list を変更せず (`:712-730`)、`canViewEvent` と `getEventDetails` がその list を照合して詳細を拒否する (`calendarAccess.js:226-257`; `calendarRuntimeState.js:282-302`)。

V24.01 は `GetEventList` の返却 tuple を走査し `None` slot を飛ばして row を `KeyVal` 化する (`eveCalendarsvc.py:235-248`); 月表示は `isDeleted` 行を隠す一方 (`eveCalendar.py:607-615`)、今後の変更一覧は deleted event を canceled として残す (`eveCalendarsvc.py:518-555,576-586`; `eveCalendar.py:1719-1731`). 詳細画面は `eventText` と `creatorID` を読む (`eveCalendar.py:1106-1111`); uninvite 通知は当該 event row を cache から外し、開いている詳細 window を閉じる (`eveCalendarsvc.py:378-394`; `eveCalendar.py:1672-1675`)。

## 永続化・通知の根拠

実 Host は `MailComposition` で PostgreSQL schema を migrate し `CalendarStore` を作成します（`MailComposition.cs:277-281`）。作成・編集・soft delete・参加者更新・response 更新は各 transaction 内で commit します（`dotnet/src/Eve.Calendar/CalendarStore.cs:159-260`）。月別 list は DB から読み出し audience/group 条件で絞ります（同:98-117）。manager/proxy は同 store を共有して `CoreServices.Create` から route されます。

Host wire tests はサービス単体直呼びではなく strict RPC Host listener、registry、wire packet を使います。削除後の list/detail regression は PostgreSQL `CalendarStore` を使い、後続 RPC と persisted response status を検査する（`CalendarRpcHostWireTests.cs:13-241`）。個別の拒否 wire ケースは別の Host test method と `WireCalendarStore` で UserError 名と CustomNotify 内容を検査する（同:243-382、`WireCalendarStore` は `:496-629`）。通知 client 側 consumer は `eveCalendarsvc.py:332-409`（New/Edit/Remove/External response）です。EveJS の通知 wire shapes は `calendarNotifications.js:78-138`。Host は store mutation 完了後に通知を発行し、RPC handler return 前に通知 helper を終えます。

## 検証状況 (2026-11-04)

確認済み:
- `Eve.Calendar`, `Eve.Services.Core.Tests`, `Eve.Host.Tests` の build はそれぞれ 0 warning / 0 error。
- `CalendarMgrServiceTests`: 10 passed, 0 failed, 0 skipped。
- PostgreSQL `eve_p8` の Host focus: 5 passed, 0 failed, 0 skipped。削除前に招待解除した character の status、fresh `GetEventList`、`GetEventDetails` 拒否と、owner/残る招待者の tombstone/details access を実 store/Host wire で確認。既存の success/failure notification assertions も同じ focus で通過。
- V24.01 client の全ソース検索で `GetEventList` / `GetEventDetails` の callsites と consumers を確認。EveJS 261028.1 の runtime/access source を比較し、この scenario の tombstone invitee semantics が一致することを確認。
- EveJS `package.json` に test script はないため、比較は current runtime source を追跡して行った。

今回確認していない:
- 実際の V24.01 graphical client session。
