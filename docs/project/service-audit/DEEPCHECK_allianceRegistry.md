# allianceRegistry 深掘り監査

## 範囲と判定

V24.01 (build 3396210) に正確なサービス名 `allianceRegistry` がある。クライアントは `eve/common/script/net/eveMoniker.py:169-180` で alliance moniker を作り、`GetAllianceEx` は `Moniker('allianceRegistry', (allianceID, isMaster))` を返す。server mode では `IsAllianceLocal(allianceID)` を先に呼ぶ。bound-object 通信の `MachoResolveObject` / `MachoBindObject` もこのサービスに向かう。

実 Host は `CorporationComposition.cs:178-190` で `AllianceRegistryService` に CorporationStore、PgCharacterStore、RelationStore、SOV snapshot、session notifier 等を注入し、`CharacterServiceList.cs:78-80` で service registry に登録する。RPC dispatch は `PacketDispatcher.cs:216-225` から登録済み service に args / kwargs を渡す。`machoVersion` は `PacketDispatcher.cs:307-315` で除去される。V24.01 側も `eve/common/script/net/eveMachoNet.py:538-542` で同名の transport keyword を除く。

引数の共通形: moniker の alliance ID は int/long のどちらでも届き、bound call では (allianceID, isMaster) tuple の先頭が ID になる (`AllianceRegistryService.cs:66-72`)。`None` は warHQ、EditLabel の任意値、AddBulletin の新規作成などに現れる。通常 RPC は位置引数。例外は bulletin 編集で、V24.01 は `bulletinID` と `editDateTime` を kwargs で送る (`eve/client/script/ui/services/corporation/base_corporation.py:969`)。その二値を Host が positional-only で読んでいたため、今回 kwargs 優先・positional fallback に修正した (`AllianceRegistryService.cs:1132-1150`)。Bill 支払いは V24.01 では位置引数だが、Host は `fromAccountKey` kwargs も読む。

Host service の role refusal は `CrpAccessDenied` UserError (reason = `[101, UI/Corporations/AccessRestrictions/InsufficientRoles]`; `AllianceRegistryService.cs:256-257`)。GetMembers だけは membership 不在時に `CustomInfo` / “Alliance membership required.” を送る (`285-291`)。その他の `CorporationError` は AllianceRegistryService に exception mapper がなく、Host dispatcher の一般例外経路では wrapped `exceptions.RuntimeError` になる (`PacketDispatcher.cs:257-280`)。下表の domain code は service が投げる識別子で、wire 上はこの一般例外経路に乗る。


## 引数形式の横断確認

- V24.01 の全 caller を検索した結果、service method の通常引数は位置引数。transport の `machoVersion` だけが全 RPC に付く場合のある kwargs で、Host dispatcher が service invocation の前に除く (`PacketDispatcher.cs:307-315`); client 側も同じ transport field を除去する (`eve/common/script/net/eveMachoNet.py:538-542`)。
- 例外的な method kwargs は bulletin edit の `bulletinID` と `editDateTime` (`base_corporation.py:969`); Host は kwargs を優先し、positional index 2/3 を互換 fallback とする (`AllianceRegistryService.cs:1133`)。bulletin create は title/body の2 positional args で optional ID/time は欠落または None (`base_corporation.py:960`)。EditLabel は omitted でなく None を positional 送信する (`all_cso.py:271`); DeclareWarAgainst の optional warHQ も None を送る (`all_cso.py:190`)。PayBill の client 形は billID/account key の positional 2値だが Host は `fromAccountKey` kwarg も受理する (`AllianceRegistryService.cs:679-680`)。
- ID/numeric input の wire integer は PyInt / PyLong の両方を受理する: `Number` は PyInt, PyLong と bound-moniker tuple の先頭を読む (`AllianceRegistryService.cs:94`); positive IDs と contact relationship integers も両クラスを明示的に扱う (`:1023-1034`)。ID 配列は PyList または PyTuple、正の int/long 要素、最大500件を受ける (`:1037-1050`)。GetRankedAlliances は None limit を既定値100にし int/long の非負値を受ける (`:259-273`); SetPrimeHour / SetCapitalSystem は int/long のみを受ける (`:400-407,452-459`)。各 method の引数位置は上表の whole-client callsite citations と同じ形で照合した。

## クライアントから呼ばれる全メソッド

返却型は Py marshal 上の型。V24.01 の呼び出し位置と返却値を実際に使う位置、永続化と通知を併記した。EveJS 列は `/home/user/evejs-261018.1/src/server/src/services/corporation/allianceRegistryRuntime.js` の対応 handler。

| メソッド | V24.01 呼出・返却値の利用 | Host の応答・状態・エラー・通知 / EveJS |
|---|---|---|
| `MachoResolveObject` | moniker 解決で暗黙呼出。根拠: `eveMoniker.py:177-180` | alliance があれば ProxyNodeId、なければ None。状態変更なし。EveJS: 513-516。 |
| `MachoBindObject` | moniker bind / nested first-call。根拠: `eveMoniker.py:177-180` | OID と bind 時刻の tuple、nested method の返値。OID と alliance ID の対応を保持し、release 時に削除。存在しない alliance は None。EveJS: 519-524。 |
| `IsAllianceLocal` | `eveMoniker.py:177-180` server mode で alliance ID を渡す | 存在なら PyInt(1)、なければ PyInt(0)。EveJS: 509-511。 |
| `GetAlliance` | `all_cso_alliance.py:50,52`: 自 alliance は bound no-arg、他 alliance は RemoteSvc + positional ID。LoadAlliance が `__header__` と `allianceID` を使う (:55-58)。 | Alliance KeyVal / `__header__`; 未存在は None。CorporationStore、character count、prime time、capital、SOV read model から組立。変更/通知なし。EveJS: 526-529。 |
| `GetAlliancePublicInfo` | `all_cso.py:135`; WarPermit が `executorCorpID` を読む (`warPermit.py:25`) | GetAlliance と同じ KeyVal/None。読取のみ。EveJS: 531-534。 |
| `GetRankedAlliances` | `all_cso_alliance.py:29-33`; 各 row の `allianceID` を standings lookup に使う | PyList of util.Row (18 header fields)。None limit は100、int/long >=0 を受け入れる。負値/別型は `ALLIANCE_RANKED_LIMIT_INVALID`。読取のみ。EveJS: 536-545。 |
| `GetAllianceMembers` | `infosvc.py:2894`; corporationID を rowset から priming | `corporationID, allianceID, chosenExecutorID, startDate, deleted` の Rowset。読取のみ。EveJS: 547-550。 |
| `GetMembers` | `all_cso_members.py:25,37-55`; CIndexedRowset を cache し、member-change を適用 | corporationID keyed CIndexedRowset。membership 不在は上記 CustomInfo UserError。読取のみ。EveJS: 552-558。 |
| `GetEmploymentRecord` | `infosvc.py:2669-2680`; allianceID/startDate/deleted を履歴行として読む | corporation / alliance 用 `allianceID,startDate,deleted` Rowset。レコードが無い場合も空 Rowset。読取のみ。EveJS: 560-578。 |
| `GetDaysInAlliance` | `corpDonateEMPanel.py:43-44`, `personalDonateEMPanel.py:55,60`; 数値を最小在籍日数と比較 | PyInt 日数。不一致/不正 ID は0。読取のみ。EveJS: 1219-1223。 |
| `GetAllianceMembersOlderThan` | `corpDonateEMPanel.py:50-51`; corporation ID list を eveowners priming に使う | PyList of corporation ID PyInt。負/不正日数は `ALLIANCE_MEMBERSHIP_DAYS_INVALID`。読取のみ。EveJS: 1225-1229。 |
| `GetAllianceSovereigntyStructuresInfo` | `sovSvc.py:335-343`; 3値を unpack し campaign score を sourceItemID/teamID で引く | (TCU rows, iHub rows, campaign-score rows) の3 PyList。session が alliance member である必要あり。SOV snapshot 不在は空3-list。EveJS: 1091-1096。 |
| `UpdateAlliance` | `all_cso_alliance.py:38`; description/url positional | None。description/url を store に保存。権限拒否は CrpAccessDenied。成功後 `OnAllianceChanged`。EveJS: 580-595。 |
| `GetApplications` | `all_cso_applications.py:25-34`; CIndexedRowset を cache、rejected state を client 側で除外 | corporationID keyed CIndexedRowset、applicationText/state/date 列。executor director が必要。読取のみ。EveJS: 597-600。 |
| `UpdateApplication` | `all_cso_applications.py:38`; corpID/text/state positional | None。application row と accepted-date scheduler state を store に保存し scheduler を wake。invalid length/state/missing app: `ALLIANCE_APPLICATION_TEXT_TOO_LONG`, `ALLIANCE_APPLICATION_INVALID_STATE`, `ALLIANCE_APPLICATION_NOT_FOUND`; 成功後 `OnAllianceApplicationChanged`。EveJS: 602-614。 |
| `DeclareExecutorSupport` | `all_cso_members.py:32`; chosen corpID positional | None。vote/executor state を保存。権限拒否は CrpAccessDenied。成功後 `OnAllianceMemberChanged`, `OnAllianceChanged`。EveJS: 631-669。 |
| `DeleteMember` | `all_cso_members.py:36`; target corpID positional | None。departure/receipt と affiliation を transaction に保存。権限や対象不整合は CrpAccessDenied。成功時 `OnAllianceMemberChanged`, `OnAllianceChanged`, `OnCorporationChanged(allianceID)`; shared-folder affiliation notifications は応答後。EveJS: 670-713。 |
| `GMForceAcceptApplication` | `all_cso.py:283`; corporationID positional | None。GML check、application accept と due scheduler を保存。非GML は CrpAccessDenied、missing app は `ALLIANCE_APPLICATION_NOT_FOUND`。成功後 `OnAllianceApplicationChanged`。EveJS: 616-629。 |
| `GetBills` | `all_cso.py:206,212-216`; accountant role を client でも確認 | PyList of bill KeyVal (billID/type/amount/due/paid 等)。accountant/junior accountant が必要。読取のみ。EveJS: 1123-1134。 |
| `GetBillsReceivable` | `all_cso.py:226`; accountant role を client でも確認 | PyList of bill KeyVal。権限拒否は CrpAccessDenied。読取のみ。EveJS: 1136-1147。 |
| `GetBillBalance` | `all_cso.py:196-206`; billID positional、client role gate | 数値 balance。account key は session 属性から選び、未設定/不正時1000。accountant/junior accountant が必要。読取のみ。EveJS: 1149-1160。 |
| `PayBill` | `all_cso.py:190-196`; billID/fromAccountKey positional | None。wallet/bill state を store に保存。accountant が必要。bad/nonpayable bill は `BILL_NOT_PAYABLE`。コード上の明示的 notification は無し。EveJS: 1162-1194。 |
| `DeclareWarAgainst` | `all_cso.py:190`; warHQ は optional positional で client default None | War KeyVal (warID, declaredByID, againstID, HQ, times, billID, mutual, allies 等)。war declaration と bill を transaction 保存。同 alliance: `ALLIANCE_WAR_DECLARATION_INVALID`; HQ 不正: `ALLIANCE_WAR_HEADQUARTERS_INVALID`; replay は通知を重複送信せず、初回は `OnWarChanged`。EveJS: 1196-1217。 |
| `GetRelationships` | `all_cso_relationships.py:24,27`; keyed crowset を dict のように参照 | Ex2 CIndexedRowset keyed `toID`, fields `toID,relationship`; member が必要。読取のみ。EveJS: 1029-1038。 |
| `SetRelationship` | `all_cso_relationships.py:32`; relationship/toID positional | None。RelationStore と corporation transaction に保存。値は -10..10、外部 ID は正値かつ alliance 自身以外。変更後 `OnAllianceRelationshipChanged`。EveJS: 1040-1051。 |
| `DeleteRelationship` | `all_cso_relationships.py:39`; toID positional。client は既存時だけ送る (:34-39) | None。standing relation を終了。invalid target は CrpAccessDenied。変更後 `OnAllianceRelationshipChanged`。EveJS: 1053-1062。 |
| `GetAllianceContacts` | `all_cso.py:242`; `GetContactList` は alliance 無しなら {}、有ればこの RPC (:238-243) | KeyVal: addresses Rowset (contactID/inWatchlist/relationshipID/labelMask) と empty blocked Rowset。member が必要。読取のみ。EveJS: 714-727。 |
| `AddAllianceContact` | `all_cso.py:245`; contactID/relationshipID positional | None。contact state 保存。director/executor authorization。成功後 `OnOrganizationContactsUpdated`。EveJS: 733-751。 |
| `EditAllianceContact` | `all_cso.py:249` | None。既存 contact relationship 保存。成功後 `OnOrganizationContactsUpdated`。EveJS: 753-755 (EveJS は Add handler に委譲)。 |
| `RemoveAllianceContacts` | `all_cso.py:253`; list positional | None。contact を保存層から削除。成功後 `OnOrganizationContactsUpdated`。EveJS: 757-776。 |
| `EditContactsRelationshipID` | `all_cso.py:257`; list と relationship positional | None。contact relation を保存。成功後 `OnOrganizationContactsUpdated`。EveJS: 778-801。 |
| `GetLabels` | `all_cso.py:261` | labelID keyed PyDict of KeyVal(labelID/name/color)。member が必要。読取のみ。EveJS: 803-816。 |
| `CreateLabel` | `all_cso.py:264`; client は default color=0 も positional で送る | 新 label ID の PyInt。label metadata 保存。name >40 は `ALLIANCE_CONTACT_LABEL_NAME_LIMIT_EXCEEDED`; color 範囲外は CrpAccessDenied。通知なし。EveJS: 818-839。 |
| `DeleteLabel` | `all_cso.py:267` | None。label を削除し各 contact mask から除去。通知なし。EveJS: 841-857。 |
| `EditLabel` | `all_cso.py:271`; name/color は省略せず None も positional で送る | None。None の field は現行値を維持。name >40 は `ALLIANCE_CONTACT_LABEL_NAME_LIMIT_EXCEEDED`; invalid color は CrpAccessDenied。通知なし。EveJS: 859-876。 |
| `AssignLabels` | `all_cso.py:275`; contact IDs/mask positional | None。mask を各 contact に保存。negative mask は CrpAccessDenied。通知なし。EveJS: 878-901。 |
| `RemoveLabels` | `all_cso.py:279` | None。mask を各 contact から除去。negative mask は CrpAccessDenied。通知なし。EveJS: 903-924。 |
| `GetBulletins` | `all_cso.py:230`; reply を15分 cache | carbon CRowset: header, RowClass=util.Row, bulletin lines。member が必要。読取のみ。EveJS: 926-935。 |
| `AddBulletin` | create: `base_corporation.py:960` は title/body positional。edit: :969 は bulletinID/editDateTime kwargs | create/edit 共に PyInt bulletinID。title/body を最大100/4000文字で保存。更新時は既存 ID と edit time を保持。超過は `ALLIANCE_BULLETIN_LIMIT_EXCEEDED`。明示 notification なし。EveJS: 954-985 は args[2]/args[3] のみ参照したため、client 編集の kwargs を失う。同差異を client 根拠で修正し `DEVIATIONS.md` に記録。 |
| `DeleteBulletin` | `base_corporation.py:949`; bulletin ID positional | None。director authorization 後に保存から削除。notification なし。EveJS: 999-1009。 |
| `UpdateBulletinOrder` | `all_cso.py:235`; list positional、client は RPC 後 cache timestamp を0に戻す | None。sort order を保存。director が必要。notification なし。EveJS: 1011-1027。 |
| `GetPrimeTimeInfo` | `all_cso.py:287` | KeyVal: currentPrimeHour/newPrimeHour/newPrimeHourValidAfter。member が必要。読取のみ。EveJS: 1065-1070。 |
| `SetPrimeHour` | `all_cso.py:290`; hour positional | None。次 vulnerability window に適用される prime hour を保存。0..23 以外は CrpAccessDenied、window 不在は `ALLIANCE_PRIME_TIME_WINDOW_UNAVAILABLE`。成功後 `OnAllianceChanged`。EveJS: 1072-1082。 |
| `GetCapitalSystemInfo` | `all_cso.py:294` | KeyVal current/new/valid-after。member が必要。SOV snapshot 不在は `ALLIANCE_SOV_SNAPSHOT_UNAVAILABLE`。EveJS: 1084-1089。 |
| `SetCapitalSystem` | `all_cso.py:297`; solarSystemID positional。client は成功後 OnUpdateCapitalInfo scatter (:297) | None。SOV hub を検証し capital transition を保存。invalid system `ALLIANCE_CAPITAL_SYSTEM_INVALID`; non-alliance hub `ALLIANCE_CAPITAL_SYSTEM_NOT_SOV_HUB`; 成功後 `OnAllianceChanged`。EveJS: 1098-1113。 |
| `CancelCapitalSystemTransition` | `all_cso.py:302`; client は成功後 OnUpdateCapitalInfo scatter (:302) | None。pending capital を cancel。SOV snapshot 不在は `ALLIANCE_SOV_SNAPSHOT_UNAVAILABLE`。成功後 `OnAllianceChanged`。EveJS: 1115-1121。 |

## 登録されているが V24.01 から直接呼ばれないメソッド

- `GetEveOwners`: allianceRegistry に対する V24.01 caller は見つからない。近い UI 呼出しは `bco_members.py:135` の `GetCorpRegistry().GetEveOwners()` で、別 service 名・別 store。EveJS handler は 1231-1238。
- `GetContactList`: alliance service の client wrapper は `GetAllianceContacts` を呼ぶため alias handler は未使用 (`all_cso.py:238-243`)。EveJS: 729-731。
- `GetBulletinEntries`, `GetBulletin`, `UpdateBulletin`: V24.01 client は GetBulletins の CRowset と AddBulletin の create/edit を使用し、これらの handler は呼ばない (`all_cso.py:230`, `base_corporation.py:949-969`)。EveJS: 937-952, 987-997。
- それ以外の AllianceRegistryService method で client caller が見つかったものは上表の46 RPC。CorpRegistryService の同名または類似 method は別 routing。

## 通知と状態遷移

通知は全て service call response 後に queue される (`CorporationNotificationAdapter.cs:9-27,116-128,252-333,359-382`)。V24.01 consumers は `all_cso.py:125-128` (OnAllianceChanged と member cache reset)、`all_cso.py:145-159` (application/member cache)、`all_cso.py:180-186` (relationship cache と UI event)。
- Alliance fields: `UpdateAlliance`, `SetPrimeHour`, `SetCapitalSystem`, `CancelCapitalSystemTransition` → `OnAllianceChanged`.
- Applications: `UpdateApplication`, `GMForceAcceptApplication` → `OnAllianceApplicationChanged`.
- Membership/executor: `DeclareExecutorSupport` → `OnAllianceMemberChanged`, then `OnAllianceChanged`; `DeleteMember` additionally sends `OnCorporationChanged(allianceID)` and shared-folder affiliation changes.
- Wars: `DeclareWarAgainst` → `OnWarChanged` once for a non-replayed declaration.
- Contacts: four contact mutations → `OnOrganizationContactsUpdated`.
- Relationship mutations → `OnAllianceRelationshipChanged`.
- Labels, bills, and bulletins have no explicit allianceRegistry notification; client locally clears/refetches bulletin cache after its own mutation call.

## ローカル検証結果

- `Eve.Services.Corporations` と `Eve.Host.Tests` を `UseSharedCompilation=false` で build: いずれも成功、警告0、エラー0。
- AddBulletin 引数テスト 1件と Host route tests 2件: 成功、失敗0、skip0。Host route の2件は登録済み `allianceRegistry` 経由で UserError と RuntimeError の wire envelope を確認。
- Host.Tests 全体: 868件中 成功539 / 失敗102 / skip227。task は PostgreSQL 不使用を指定しているため `EVE_PG_URL` を外して実行した。出力で確認した失敗原因には DB URL 不在による `PgConnection` の NullReference と、station RPC tests が PyList を期待して PyTuple を受けた型 assertion がある。全102件の原因調査・解消はこの task の範囲外で、project shard は green ではない。GUI client は起動していない。

## 確認したテストと、この作業で扱わない確認

新規 no-database Host test は `AllianceRegistryHostWireTests.cs` にあり、実 `allianceRegistry` service を PacketDispatcher/Host socket route に登録して GetMembers の CustomInfo wire error と machoVersion kwargs を確認する。別 fact は AddBulletin の V24 keyword args、positional legacy form、None create defaults、および int/long の受け渡しを検査する。validation の Host exception envelope も同ファイルで確認する。

永続化と通知の database-backed regression は `Eve.Corporations.Tests/AllianceTests.cs:217-299,433-578,579-743,744-947,1491-1588,2215-2340,2429-2478` にある。この監査では task の指示どおり PostgreSQL を使わず、これらは実行していない。PgFact は `HostServerTests.cs:16-20` のとおり `EVE_PG_URL` が無いと skip になる。GUI client での実プレイ確認はしていない。
