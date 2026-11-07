# カレンダー監査所見の修正記録

対象: `/home/user/reports/90-audit-eve-calendar-r14.md`。各所見をeve.rs、V24.01クライアント、EveJS v0.12.9のソースで照合してから修正した。

## 確認済みの所見と修正

### P2 — 参加者変更通知の競合

**所見を確認した。** 旧サービスは別々の読み取り結果から通知差分を作っていたため、解除のcommitと再招待のcommitが競合すると、再招待後の状態が`Undecided`でも`OnNewCalendarEvent`が欠落し得た。

- V24.01の編集画面は招待者の追加・削除を差分として送る: `eve/client/script/ui/shared/eveCalendar.py:1583-1589`
- クライアントは`OnNewCalendarEvent`で月表示へ追加し、`OnRemoveCalendarEvent`で削除またはtombstone化する: `eve/client/script/ui/services/eveCalendarsvc.py:333-350,379-397`
- EveJSは現在状態から実際に変化した追加・削除IDを得て通知する: `server/src/services/calendar/calendarRuntimeState.js:665-709`, `server/src/services/calendar/calendarMgrService.js:129-149`

参加者更新はイベント行をロックしたトランザクション内で実際の追加・解除IDを算出するようにした。イベント単位のPostgreSQL session advisory lockをmutation前に取得し、commit後の通知が完了するまで保持する。これにより複数Hostプロセス間でも同一イベントのmutationと通知順を直列化する。Host DB回帰テストは解除通知の送信を保留し、再招待の完了待ち、最終応答状態、`OnRemoveCalendarEvent`から`OnNewCalendarEvent`への通知順を確認する。修正前、このテストでは解除後の再招待通知が欠落することを確認した。

### P2 — 応答通知が古いイベント日時を送る

**所見を確認した。** 旧サービスは応答書き込み前に取得したイベント行を通知へ使っていたため、処理中にownerが日時を変更すると、DBの現在日時と通知の`eventDateTime`が異なり得た。

- V24.01は外部応答通知のイベント行を次回イベントcacheへ渡す: `eve/client/script/ui/services/eveCalendarsvc.py:300-310,409-412`
- EveJSは応答処理が返すイベントsnapshotを通知へ渡す: `server/src/services/calendar/calendarRuntimeState.js:732-769`, `server/src/services/calendar/calendarMgrService.js:112-126`

`SendResponseAsync`は行ロック下のイベントsnapshotと参加者IDを更新結果として渡し、commit直後、イベントlockを解放する前に通知する。DB Host回帰テストは応答処理を保留して日時編集を先にcommitし、通知の`eventDateTime`が編集後の値と一致することを確認する。修正前、このテストでは古い日時が通知されることを確認した。

### P3 — 削除が辞退応答を上書きする

**所見を確認した。** 旧soft deleteは`Declined`を`Deleted`へ書き換えていた。V24.01は削除通知後も`Declined`応答を保持し、削除イベントを更新一覧へ含める際にも辞退済みイベントを除外する。

- V24.01は再接続時に`GetResponsesForCharacter`の応答をcache化し、削除通知でも`Declined`を保持する: `eve/client/script/ui/services/eveCalendarsvc.py:250-257,379-397`
- 削除済みイベントは`Declined`応答がなければ更新一覧に表示し、辞退済みなら除外する: `eve/client/script/ui/services/eveCalendarsvc.py:577-585`
- キャンセル表示はresponse rootの`Deleted`値ではなく、イベントrowの`isDeleted`から表示用response/iconを決める: `eve/client/script/ui/services/eveCalendarsvc.py:459-469`; `eve/client/script/ui/shared/eveCalendar.py:1336-1341`
- EveJSの削除処理はイベントをtombstone化し、response rootを書き換えない: `server/src/services/calendar/calendarRuntimeState.js:712-729`

削除では`events.is_deleted`だけを変更するようにした。Host wire回帰テストは`Accepted`、`Declined`、`Uninvited`、`Undecided`の全状態を作り、削除後に別のHost sessionから応答一覧を取得して、各応答が永続化されていることを確認する。修正前、このテストでは応答状態が`Deleted`へ上書きされることを確認した。

## 誤りだった所見

なし。監査報告の3件はいずれもソース、V24.01、EveJSの状態遷移と一致した。

## 検証済みと未確認

**検証済み:** PostgreSQL専用DB`eve_p6`を使ったHost calendar関連クラスとHostServer/HomeStationクラスの424件が成功し、skipはなかった。Coreの`CalendarMgrServiceTests`10件も成功した。参加者競合の単独テストも成功した。修正前に3つの新規回帰ケースでそれぞれ問題を再現し、修正後は参加者競合、応答日時、削除後のresponse永続化を検査した。`Eve.Calendar`、`Eve.Services.Core`、`Eve.Host.Tests`、`Eve.Services.Core.Tests`はRelease buildが各0 warningで成功した。`git diff --check`も成功した。V24.01の全`eve/client`ソースとEveJSの呼び出し元、通知処理、cache遷移を横断検索した。

**未確認:** V24.01グラフィカルクライアントでの実操作と表示。EveJSの実行結果との比較。EveJS runtime flowの実行を試したが、`server/node_modules/better-sqlite3`のnative bindingがなく、state読み込み前に停止した。CLAUDE.mdが指定するSerena/codebase-memory MCPは利用可能なツールに存在しなかったため、ファイル・行番号を指定したソース参照で代替した。
