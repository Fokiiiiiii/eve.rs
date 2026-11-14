# Social and organization audit — CX3

日付: 2026-11-14
対象: eve.rs social / organization services, V24.01 client build 3396210, EveJS 261108.1

この記録は、今回ソース経路を再確認して回帰テストを追加・実行した差分と、誤検知として照合した項目を記す。既存の広い機能監査をすべて再実行したという意味ではない。既に `docs/project/audit-fix-*.md` に記録された修正は繰り返さず、fleet / chat / calendar / mission / corporation / alliance / war / standing の先行調査は末尾の記録へ引き継ぐ。

## 確認して修正した不具合

### BOOKMARK-001 — 4時間ブックマークの有効期限が3時間だった — confirmed, fixed

V24 は `BOOKMARK_EXPIRY_4HOURS = 1` と定義し、UI の表示名を `ExpireIn4Hours` として選択肢に出す（`/home/user/eve-logic/eve/common/script/mgt/bookmarkConst.py:17-20`; `eve/client/script/ui/shared/bookmarks/bookmarkLocationWindow.py:31-34,126-131`）。全ツリーの client callsite は `BookmarkStaticLocation(..., expiry, ...)` を呼び、返答の `expiryDate` を受け取る（`eve/client/script/ui/shared/bookmarks/bookmarkSvc.py:609-619`）。EveJS は mode 1 に4時間を加える（`/home/user/evejs-261108.1/server/src/services/bookmark/bookmarkRuntimeState.js:932-944`）。

Host の実際の登録経路は `CharacterServiceList` → `AccessGroupBookmarkMgrService.BookmarkStaticLocation` → `BookmarkStore.CreateBookmarkAsync` であり、store が Unix milliseconds で期限を保持し、service が返答 tuple / bookmark row の `expiry` を FILETIME に変換する（`dotnet/src/Eve.Host/Rpc/CharacterServiceList.cs:82-83`; `dotnet/src/Eve.Services.Core/AccessGroupBookmarkMgrService.cs:120-148,387-414`; `dotnet/src/Eve.Bookmarks/BookmarkStore.cs:360-375`）。mode 1 の store 計算だけが3時間だった。

Host dispatch・PostgreSQL・restart を通る新規 `CharacterHostTests.BookmarkAudit` は、修正前に4時間との差 `14,400,000 ms` を期待し、実値 `10,800,000 ms` で失敗した。計算を4時間に修正し、永続値、wire FILETIME、restart 後の読み戻しを確認する。unit の expiry mode 期待値もV24 / EveJSに合わせた。

### MAIL-001 — mailing list参加時のwelcome mailがautomated bitを欠いていた — confirmed, fixed

V24 は `mailStatusMaskAutomated = 32` を定義する（`/home/user/eve-logic/eve/common/lib/appConst.py:1776`）。`mailSvc.OnMailSent` は bit 32 かつ `senderID == toListID` のとき mailing-list display name を sender name に使う（`eve/client/script/ui/services/mail/mailSvc.py:1119-1145`）。EveJS の `joinMailingList` は加入者に `senderID=listID`, `toListID=listID`, `statusMask=32` でwelcome mailを送る（`/home/user/evejs-261108.1/server/src/services/mail/mailState.js:42,1356-1408`）。

Host は welcome mailを永続化していたが、旧値16は draft bit であり automated bit ではなかった。登録済み Host RPC / PostgreSQL / restart test は修正前に期待32に対し16を返した。adapter は保存statusと `OnMailSent` statusを32に統一し、sender ID も mailing-list ID にする。テストは wire header、保存message、recipient status、restart 後の同期を検査する。

push CI run 1803 は、この変更を反映しない既存 `HostServerTests.Mail_host_adapter_commits_mail_before_returning_id_without_a_server_mail_notification` の期待値16で失敗した。V24 / EveJS の32と保存値の `long` 型を確認した上で、テストを32 / `long` に更新した。これはassertionを弱める変更ではなく、MAIL-001のwire・保存契約に合わせた修正。

### MAIL-002 — 管理画面の “Send welcome mail to all” が通常のpilot送信として扱われていた — confirmed, fixed

V24 の mailing-list config UI は “to all” checkbox が有効なら `SaveAndSendWelcomeMail` を呼び、proxy は `mailingListsMgr.SendWelcomeMail` に転送する（`/home/user/eve-logic/eve/client/script/ui/shared/neocom/evemailingListConfig.py:494-506`; `eve/client/script/ui/services/mail/mailingListsSvc.py:185-189`）。EveJS は list に保存したwelcome内容を全memberへ配信し、senderID / senderName / toListID を list の値にし、automated status 32、sender copyなしで送る（`/home/user/evejs-261108.1/server/src/services/mail/mailState.js:1753-1805`）。V24 は `OnMailSent` でこのidentity / bitを使って mailing-list sender名を表示する（`mailSvc.py:1119-1145`）。

旧Hostは手動broadcastを汎用 `MailSendRequest` として発行し、memberへの配送自体はしても、永続messageと通知を所有pilot sender / 通常statusとして記録した。Host E2E は修正前にsenderID `listID` を期待し、実値 `ownerCharacterID=140000001` を確認した。専用 `MailingListWelcomeBroadcast` 経路を設け、list manager権限・active list・保存済みtitle/body・現在のsession actorを再検査してから全memberへ送る。messageとrecipient statusに32を保存し、notificationにもlist ID / 32を使う。Host E2E はownerを含むmemberへの通知、persisted sender/name/list/status、両recipient status、Host restart後のmail syncを検査する。

EveJS 261108.1 の `mailState` を別の一時 GameStore と `maintenance` writer role で直接実行した。`createMailingList → saveMailingListWelcomeMail → joinMailingList → sendMailingListWelcomeMail` の結果、join message は `senderID=toListID=500000000`, status 32、broadcast recipients はownerとmemberの両方、各mailbox statusは32となった。これはEveJSのservice-state実行であり、EveJS packet dispatcherやgraphical clientのE2Eではない。

### MAIL-003 — list-only automated mailにInbox labelを付けていた — confirmed, fixed

V24 の `mailSvc.OnMailSent` は、直接 recipient の `toCharacterIDs` に自分が含まれる場合だけ `mailLabelInbox` を加え、corp/alliance mailだけ別のlabelを加える（`/home/user/eve-logic/eve/client/script/ui/services/mail/mailSvc.py:1119-1138`; label値は `appConst.py:1777-1781`）。welcome mailは V24 proxy から `SendWelcomeMail` へ進み、EveJS は join/broadcastのどちらも `senderID=toListID=listID`, `toCharacterIDs=[]` で保存し、配信対象だけを別指定する（`/home/user/evejs-261108.1/server/src/services/mail/mailState.js:707-738,1158-1165,1395-1408,1776-1805`）。したがって list-only automated mailのrecipient statusは label mask 0。Host recipient resolverは status 32 とともに label mask 1 を保存しており、次の `SyncMail` でV24のlist-only headerへ不一致のInbox bitを付けた。

登録済み Host RPC / PostgreSQL / restart 回帰は、join後の`SyncMail`と手動broadcast後の保存message/statusに label 0 を期待する。修正前は両テストが actual 1 で失敗した。`MailComposition`はjoinおよびmanual broadcastのrecipient label maskを0にし、一般のcharacter/list/corp mailのlabel規則は変更しない。再実行後はmail Host flow 3件（expiry/join/broadcast）がすべてpassした。

## 調べたが不具合ではなかった項目

### FP-001 — `standing2` alias はV24 RPC互換性問題ではない — false positive

V24 全ツリーを検索した結果、standing UI のremote RPC proxyは `standingMgr` を使う（`/home/user/eve-logic/eve/client/script/ui/services/standingsvc.py:114`）。`standing2` の client 側一致は `standingData.py:16-33` の `standing2to1` data field で、`RemoteSvc('standing2')` callsiteはない。Host の追加 `standing2` aliasは余分な登録であり、clientが呼ぶ route を欠いていない。全RPC呼び出し・reply shape・既存Host登録確認は `docs/project/service-audit/DEEPCHECK_standing.md` に記録済み。

### FP-002 — bookmark expiry の内部Unix msとwire FILETIMEの単位差 — false positive

storeはUnix millisecondsを使うが、bookmark rowと `BookmarkStaticLocation` tupleの expiry は `Py.FileTimeFromUnixMs` でFILETIMEへ変換している（`dotnet/src/Eve.Services.Core/AccessGroupBookmarkMgrService.cs:387-414`）。ここは単位変換済みであり、誤差の原因ではなかった。見つかった不具合はmode 1の加算時間だけ。

### FP-003 — shared bookmark folderで権限を無視して作成する — false positive

Host service はactor、folder、access listsを含む呼び出しをstoreへ渡す（`dotnet/src/Eve.Services.Core/AccessGroupBookmarkMgrService.cs:141-148`）。storeはcreate/update/move pathのtransaction内で `RequireLevel(..., FolderAccess.CanUse)` を呼ぶ（`dotnet/src/Eve.Bookmarks/BookmarkStore.cs:310-345,393,467-468`）。権限検査の欠落は確認できなかった。

## 先行監査から引き継ぐ領域

次の記録には全クライアントの呼び出し・通知consumerを調べた証拠と、個別のHost / store検証がある。このターンでは記載の全状態組合せを再実行していない。

| 領域 | 参照記録 |
| --- | --- |
| chat/XMPP ACL、mail通知、calendar、fleet | `docs/project/deepfix-fleets-mail-calendar.md`; `docs/project/service-audit/DEEPCHECK_XmppChatMgr.md`; `DEEPCHECK_mailMgr.md`; `DEEPCHECK_calendar.md`; `DEEPCHECK_calendarMgr.md` |
| agents、missions、standings | `docs/project/deepfix-missions-agents.md`; `docs/project/service-audit/DEEPCHECK_agentMgr.md`; `DEEPCHECK_standing.md`; `DEEPCHECK_standingMgr.md` |
| corporation / alliance / war | `docs/project/audit-fix-corp-contract.md` (H-07); `docs/project/deepfix-corporations.md`; `docs/project/deepfix-planets-sov-warfare.md`; `docs/project/service-audit/DEEPCHECK_allianceRegistry.md`; `DEEPCHECK_wars.md`; `docs/project/service-audit/deepfix-corporations.md` |
| bookmark ACL / shared folders | `docs/project/service-audit/DEEPCHECK_accessGroupBookmarkMgr.md`; `DEEPCHECK_bookmarkMgr.md` |

これらは既存成果を参照するもので、同じ finding を `audit-fix-*` から移したり、今回再監査したと主張したりしない。

## 今回の検証

### 確認済み

- `Eve.Bookmarks.Tests` Release: 34 passed, 0 failed, 0 skipped。
- `Eve.Services.Core.Tests` の `MailServiceTests|MailingListServiceTests|NotificationMgrServiceTests|CalendarMgrServiceTests|AgentMissionServiceTests|AgentMissionStandingRulesTests|AgentDialogueTests` Release: 143 passed, 0 failed, 0 skipped。
- `Eve.Corporations.Tests` Release 全体: 194 passed, 0 failed, 0 skipped。法人、alliance、war、standing のprotocol / persistence testを含む。
- `Eve.Host.Tests.HostServerTests` class: 512 passed, 0 failed, 0 skipped。
- `Eve.Host.Tests.AccessGroupBookmarkMgrHostWireTests`: 3 passed, 0 failed, 0 skipped。
- Host + PostgreSQL focused scenarios: bookmark expiry / mailing-list join / manual broadcast 3 passed, 0 failed, 0 skipped。法人自己退社Host E2E 1 passed, 0 failed, 0 skipped。
- 変更対象の Release / warnings-as-errors build (`Eve.Bookmarks`, `Eve.Services.Core`, `Eve.Services.Corporations`, `Eve.Host`, `Eve.Host.Tests`, `Eve.Corporations.Tests`): 各0 warning / 0 error。
- CIで更新漏れが判明した `Mail_host_adapter_commits_mail_before_returning_id_without_a_server_mail_notification`: Release focused test 1 passed, 0 failed, 0 skipped。`Eve.Host.Tests` Release warnings-as-errors rebuild: 0 warning / 0 error。
- EveJS 261108.1 isolated `mailState` direct execution: automated join and list broadcast assertions passed。

### 失敗・skip・未確認

- `Eve.Host.Tests.CharacterHostTests` class全体を、開始時点から存在した無関係な未コミットSkill変更を含むdirty worktreeで実行した結果: 87 passed, 1 failed, 4 skipped。唯一の失敗は `Clone_jump_rebases_a_running_queue_when_implants_change_its_learning_rate` で、追加されたclone-jump notification assertionと実際の通知列が異なる（`CharacterHostTests.cs:537-584`）。このSkillコード・テストは今回のsocial/org差分に含めていない。今回追加・変更したbookmark/mail Host回帰は、この同じclassのfocused実行でpassした。
- 4 skipは `RealAbyssalSimPgFact` のlive Rust simulation tests。`EVE_ABYSSAL_SIM_BINARY` と `EVE_ABYSSAL_SIM_SDE` が未設定のためskipされ、passとして数えていない。
- push CI run 1803 は上記の古い welcome-mail mask assertionで `.NET (host tests 2/6)` が失敗した。期待値の修正をpushした後のCI rerunは未完了。
- V24 graphical client、live packet trace、GUI上のmail sender display / bookmark expiry UIは今回起動していない。
- 先行監査に記した未確認項目と全social/org permission edge matrixは、本記録によって閉じない。
