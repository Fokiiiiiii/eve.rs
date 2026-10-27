# mailMgr deep check

対象: EVE V24.01 build 3396210 の `/home/user/eve-logic` 全体、Host の `mailMgr`、EveJS 261018.1 の `src/server/src/services/mail`。

## 結果

- クライアント全体の検索で、mailMgr は `mailSvc.py:78` の `RemoteSvc('mailMgr')` と `xmppchatsvc.py:1795` の一時的な RemoteSvc 呼び出しだけだった。bound object / moniker 経由の mailMgr 呼び出しは見つからなかった。
- mailSvc が呼ぶ RPC はすべて位置引数。GPCS は transport kwargs に `machoVersion=1` を付ける（`carbon/common/script/net/ServiceCallGPCS.py:221-225`）。Host wire test helper も同じ kwargs を送る（`ServiceResponseBatchHostWireTests.cs:17-20`）。
- 既存の `HasMethod` と mutation dispatch に欠けていた8 RPCを追加した。ID の `PyInt`/`PyLong`、ID collection の list/tuple、クライアントが送る `None` を処理する。mailbox ID collection の独自件数拒否を取り除き、501件の Host wire 呼び出しで確認した。EveJS 同様に不正要素を無視する。
- mutation は共有 `mail` state を gate 内で読み、mailbox state を書き、孤立 message を削除して flush してから別セッションへ通知する。SendMail は durable mail flush 後に OnMailSent を送り、送信元の response 先 session には response 後に通知する。ReplaceLabels は system label bits を保持する。DeleteMail / EmptyTrash は status を削除し、他 mailbox が参照しない message だけを prune する。
- Notification の引数は client が受け取る positional args として送る。Host wire test は2つ目のセッションで通知 packet を decode し、args と順序を確認する。client は `OnMailSent`、`OnMailDeleted`、`OnMailUpdatedByExternal`、`OnMailUndeleted`、`OnMailTrashed`、`OnMailRestored`、`OnLabelsCreatedByExternal` を `mailSvc.py:1118-1210` で消費する。
- Client の EditLabel は name/color が両方欠ける場合に `UserError('MailLabelMustProvideName')` をローカルで出す（`mailSvc.py:711-714`）。EveJS 同様、server の不正・存在しない label 操作は成功を偽装せず、変更なしの `None` とする。永続化エラーは Host 例外として伝播する。

## 全 RPC

| RPC | eve-logic caller / 引数 | client の応答・Host の状態と通知 |
|---|---|---|
| `SyncMail` | `mailSvc.py:156`; `firstID,lastID`、最初の同期では `None` | `newMail,oldMail,mailStatus` を返す。応答件数の独自上限を設けず、mailStatus は mailbox 全件を返す。client は全 status を取り込み、続いて不足 header / body を取得する（`mailSvc.py:203-234`）。EveJS も全 mailbox entry を返す（`mailState.js:2081-2121`）。 |
| `GetMailHeaders` | `mailSvc.py:192`; ID list | header row の list。`toCharacterIDs` は comma-separated string または `None`、`toCorpOrAllianceID` は scalar ID または `None`。client は corp/alliance ID を単一 recipient として読む（`mailSvc.py:638-674`、特に 641-643）；EveJS も scalar を作る（`mailMgrService.js:129-138`）。 |
| `GetBody` | `mailSvc.py:511`; message ID, bool | compressed bytes または `None`。client は `None` を空本文として扱い、bytes を zlib 展開する（493-523）。read flag が変わる場合は status flush 後に別 session へ OnMailUpdatedByExternal。 |
| `GetLabels` | `mailSvc.py:677`; 引数なし | label ID→KeyVal dict。client は static labels と合わせて表示する（676-710）。snapshot 読み取りのみ。 |
| `MarkAsRead` | `mailSvc.py:338`; ID list | `None`; read bit を保持しつつ立て、flush 後 OnMailUpdatedByExternal(ids, true, None)。 |
| `MarkAsUnread` | `mailSvc.py:325`; ID list | `None`; read bit を消し、flush 後 OnMailUpdatedByExternal(ids, false, None)。 |
| `MarkAllAsRead` | `mailSvc.py:416`; 引数なし | `None`; trash を除いて read にし、flush 後 OnMailUpdatedByExternal。 |
| `MarkAllAsUnread` | `mailSvc.py:383`; 引数なし | `None`; trash を除いて unread にし、flush 後 OnMailUpdatedByExternal。 |
| `MarkAsReadByLabel` | `mailSvc.py:427`; label mask | `None`; EveJS と同じく trash を除いて read にし、更新通知。 |
| `MarkAsReadByList` | `mailSvc.py:438`; list ID | `None`; trash を除いて read にし、更新通知。 |
| `MarkAsUnreadByLabel` | `mailSvc.py:394`; label mask | `None`; trash を除いて unread にし、更新通知。 |
| `MarkAsUnreadByList` | `mailSvc.py:405`; list ID | `None`; trash を除いて unread にし、更新通知。 |
| `MoveToTrash` | `mailSvc.py:293`; ID list | `None`; trashed bit を立て、flush 後 OnMailTrashed(ids)。 |
| `MoveFromTrash` | `mailSvc.py:304`; ID list | `None`; trashed bit を消し、flush 後 OnMailRestored(ids)。 |
| `MoveAllToTrash` | `mailSvc.py:351`; 引数なし | `None`; status を更新し OnMailTrashed(ids)。 |
| `MoveAllFromTrash` | `mailSvc.py:449`; 引数なし | `None`; status を更新し OnMailRestored(ids)。 |
| `MoveToTrashByLabel` | `mailSvc.py:361`; label mask | `None`; status を更新し OnMailTrashed(ids)。 |
| `MoveToTrashByList` | `mailSvc.py:372`; list ID | `None`; status を更新し OnMailTrashed(ids)。 |
| `EmptyTrash` | `mailSvc.py:459`; 引数なし | `None`; trash statuses を削除し、孤立 message を prune、flush 後 OnMailDeleted(ids)。 |
| `DeleteMail` | `mailSvc.py:479`; ID list | `None`; mailbox statuses を削除し、孤立 message を prune、flush 後 OnMailDeleted(ids)。 |
| `AssignLabels` | `mailSvc.py:764`; ID list, label mask | `None`; mask を OR して保存し、flush 後 OnMailUpdatedByExternal(ids, None, finalMask)。 |
| `RemoveLabels` | `mailSvc.py:782`; ID list, label mask | `None`; mask を消して保存し、flush 後 OnMailUpdatedByExternal(ids, None, finalMask)。 |
| `ReplaceLabels` | `mailSvc.py:799`; ID list, label mask | `None`; system bits 15 を保って置換し、flush 後 OnMailUpdatedByExternal(ids, None, finalMask)。 |
| `CreateLabel` | `mailSvc.py:733`; name, optional color | label ID (`PyInt`) または `None`。trim/truncate、上限、mask allocation は EveJS と同じ。作成時 flush 後 OnLabelsCreatedByExternal(KeyVal)。 |
| `EditLabel` | `mailSvc.py:714`; label ID, optional name/color | `None`; 欠けた値は保持。system label は color が与えられた場合に作成できる。client が自分の表示を更新し、外部通知はない（711-725）。 |
| `DeleteLabel` | `mailSvc.py:740`; label ID | `None`; custom label のみ削除し全 status から mask を除去。client が表示を更新し、EveJS と同じく外部通知はない（739-754）。 |
| `SendMail` | `evemail.py:2257-2280` から keyword で local mailSvc を呼び、mailSvc は RPC を位置引数で送る（`mailSvc.py:959,964`）。recipients/list/corp-or-alliance/title/body/reply/forward。リスト宛権限がない場合は client が `UserError('EveMailCanOnlySendToOwnMailinglists')` をローカルで出す（`mailSvc.py:961-963`） | message ID または `None`; client は ID がある場合だけ送信済み状態を更新する（959-992）。subject/body は trim 後 150/8000 UTF-16 code units へ切り詰める（EveJS `mailState.js:84-96`）。durable MailSendWriter が保存/flush 後、受信者へ OnMailSent(messageID,senderID,sentDate,toCharacterIDs,toListID,toCorpOrAllianceID,title,statusMask) を送る。corp/alliance ID は client が読む scalar、空 list は `None` に正規化。EveJS の非権限 corp/alliance 宛は `None`。 |
| `PokePlayerAboutChatMsgGm` | `xmppchatclient/xmppchatwindow.py:724` から channel receiver を渡し、`xmppchatsvc.py:1794-1795` が RPC を呼ぶ | ID または `None`; GML 権限を確認し、Unicode channel name を受ける。EveJS と同様に 256 文字制限はなく、生成本文を trim 後 8000 UTF-16 code units に切り詰める（`mailMgrService.js:576-581`, `mailState.js:2159-2176`）。durable GM-mail callback が保存し、受信者へ OnMailSent を送る。呼出元は戻り値を無視する（1795-1796）。 |

## Host 配線と検証

- 実 route: `CharacterServiceList.cs:62` が `MailRpc()` を登録し、`MailRpc.cs:138-152` が `mailMgr` と durable callbacks を構成する。全28名は `MailService.cs:40-62` に登録。
- 永続化/通知: `MailRpc.cs:164-201` が mutation gate、`MailboxMutation.Apply`、mailbox write、orphan remove、flush の順に実行。通知の payload は `MailService.cs:633-701` で組み立てる。
- Wire coverage: `MailRpcHostWireTests.cs:122-251` で追加8 RPC、`252-335` で SendMail、`336-387` で GM poke を Host socket 経由で呼び、machoVersion kwargs、list/tuple、int/long、None、PyWString、通知 args、後続 read を確認。`HostServerTests.cs:4457-4699` は通常送信と GM poke の durable write/flush、OnMailSent、本文上限を確認。
- Local check: Core focused test 1/1 passed; full `Eve.Services.Core.Tests` 842 passed / 8 failed / 57 skipped. Host focused mailMgr tests 5/5 and registry snapshot 1/1 passed; full `Eve.Host.Tests` 511 passed / 100 failed / 227 skipped. Failures include tests that require `EVE_PG_URL`; other failures are outside mailMgr and were not individually investigated. No local database was used.
- CI: mail focused run `40445026610` for `4bd4590` had 82 passed / 2 failed because existing assertions expected a recipient array and a SyncMail 5,000-entry rejection; both were corrected against client/EveJS shapes. Push CI run `40446392725` for `b2062b1c` exposed and fixed the agent-mission fallback assertion and stale Host inventory snapshot (1,424 expected vs 1,442 actual). For commit `661e12cc`, push CI `40449317563` completed successfully with no failed jobs, and focused agent-test `40449329238` passed. Host inventory confirmed 1,442 total methods, including 28 mailMgr methods.
- GUI の V24.01 client では確認していない。PostgreSQL を使うテストは実行していない（この作業では DB を使わない）。

## EveJS 比較

主な参照: `server/src/services/mail/mailMgrService.js:339-518,548-581`、`mailState.js:871-985,1045-1100,1102-1254,1935-1961,2056-2079`、`mailNotifications.js:31-115`。意図的な挙動差は記録していない。
