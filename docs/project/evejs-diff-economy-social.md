# EveJS 261108.1 経済・ソーシャル機能の差分確認

確認日: 2026-11-16
対象クライアント: V24.01 build 3396210 (`/home/user/eve-logic`)
機能参照: EveJS 261108.1 (`/home/user/evejs-261108.1`)
eve.rs の基準: 作業用 branch `cx5-econ-social` の開始 commit `78cdc1337`

## 読み方と検証範囲

このファイルは今回のミッション実測と、経済・ソーシャル各領域について既に記録されている Host / PostgreSQL / client / EveJS の調査結果をまとめる。各領域の詳細な file:line 根拠、失敗前の再現、テスト数はリンク先の記録に置いた。このターンで全領域の全 RPC を再実行したという意味ではない。

V24.01 client source が確定するのは呼び出し、marshal 形、クライアント側の遷移、通知 consumer である。EveJS は観測可能な挙動の実行参照にし、CCP/SDE またはクライアント根拠なしに captured value を恒久データにしない。下記の「未確認」は同等性を主張しない。

この作業では `tools/mitm/mitm.py` を再利用し、call/reply の有無、marshal shape、error code/text、notification 名・順序を表示する比較機能とその unit test を引き継いだ。volatile な leaf 値と dict key 順だけを比較時に無視する。live capture は `/tmp/cx5-economy-social/` に保存し、リポジトリには入れない。

## 確認したミッション導線

### client → bound `agentMgr` → offer → accept

| 段階 | 根拠と観測 |
| --- | --- |
| 初回会話 | V24 `agentDialogueWindow._GetConversation` は `DoAction(None)` を呼び、通常 agent では返された最初の Request action も送る (`eve/client/script/ui/station/agents/agentDialogueWindow.py:402-411`)。`ExecuteAgentAction` は bound moniker の `DoAction` を呼ぶ (`:427-428`)。button type は V24 定数 Request=2 / Accept=3 / Decline=9 / Defer=10 / Complete=6 / Quit=11 (`eve/common/lib/appConst.py:146-159`)。 |
| 文面と keyword | Client は `(messageID, contentID)` を `ProcessMessage` へ渡し、`GetMissionKeywords(contentID)` と agent args を使って localized text を解決する (`eve/client/script/ui/station/agents/agents.py:626-669`)。Journal は bound `GetMissionJournalInfo` を呼ぶ (`:746-747`)。 |
| EveJS offer | Bound `MachoBindObject` は `agentMgrService.js:964-972`、`DoAction` は `:1097-1130` 以降を通り、mission runtime の `offerMission` (`agentMissionRuntime.js:6887`) と offered response (`:8680-8698`) を使う。Offered actions は Accept / Decline / Defer。 |
| eve.rs Host 経路 | `AgentMissionService.HasMethod` は bind, DoAction, journal, keyword methods を公開する (`dotnet/src/Eve.Services.Core/AgentMissionService.cs:78-99`)。Bound `DoActionAsync` は session の agent/character を引き、Host が agent data と現在位置を渡して `AgentDialogue.StepAsync` を呼ぶ (`:504-530`)。Host offer picker は storyline, epic arc, basic security, courier, mining の順に provider を調べる (`dotnet/src/Eve.Host/Composition/MissionComposition.cs:1199-1218`)。 |
| 永続化 / 返答 | Request は `AgentMissionStore.OfferAsync`、Accept は `AcceptAsync` を通し、Host callbacks と通知は commit 後に行う。Post-accept は別の bound read で accepted journal row を取得する。Host dispatch regression は実 `agentMgr` registration、wire action tuple、PostgreSQL state、expiry と method-table refusal を検査する。 |

V24 client の action 変換と EveJS runtime の実際の response は別の意味を持つ。EveJS `buildAcceptedConversation` (`agentMissionRuntime.js:8701-8754`) は Accept 直後 `Complete` token 380 を返し、発行 agent にいる時だけ `Quit` token 381 を追加する。ウィンドウを再度開いた通常会話では token 382 / 383 を返す。現在の session station と agent station が異なる場合、Accept 直後に Quit がないのが正しい。

### 修正内容と判定

| Finding | Verdict | 根拠 |
| --- | --- | --- |
| Basic level-1 security agent から bound `DoAction(Request)` で mission offer を作れなかった | **確認済みの欠落を部分実装で修正。** L1–L5 security encounter を versioned mission catalog と mission-level tags から選び、`AgentMissionStore` に保存する。Request / Defer は同じ offered mission を返す。V24 UI が使う Accept / Decline / Defer actions と message/content tuple を追加した。 | `BasicKillOffers.MinLevel` は1 (`dotnet/src/Eve.Services.Core/BasicKillOffers.cs:11-16`)、Host picker は上表。V24 の `DoAction` / `ProcessMessage` は上表。`AgentDialogueTests`, `AgentMissionDispatchTests`, `test_agents_live.py` が state と wire を検査。 |
| Accept 直後に candidate は actions を返さず、EveJS は conversational `Complete` token を返す | **Confirmed wire break, fixed.** Direct response は `Complete(380,6)`、発行元 station にいるときだけ `Quit(381,11)`。通常の再表示は `CompleteReady(382,6)` / `QuitReady(383,11)`。 |
| Accept 中の `lastActionInfo` は EveJS で3 mission flags が false | **Confirmed wire difference, fixed.** Offered / accepted conversation は `missionCompleted`, `missionQuit`, `missionDeclined` を false として返す。状態遷移自体は authoritative store が決める。 |
| 不完全な mission に client から Complete が送られる場合 | **未完了のまま拒否。** `objectiveCompleted && atAgentLocation && OnMissionTurnIn` の条件を満たす時だけ turn-in callback と settlement が走る。追加 Core test は未完了 Complete の後も Accepted、payout なしを確認する。 |
| Client localization と mission journal payload | **wire shape を修正。** Message IDs は V24 の mission text catalog から取得し、journal の objective, dungeon, location, bookmark fields は catalog/runtime state/SDE から作る。実行時の pocket position がない間は coords を省略し、架空座標を返さない。V24 は `agentDialogueUtil.py:178-202` で coords 有無の両方を扱い、`agents.py:640-669` で message ID / label を解決する。 |
| Mission reward amount / reward row | **未解決の presentation gap。** 対象 V24 mission reward source を得られず、mission reward を推測していない。`agentDialogueUtil.py:347-378` は reward rows があれば表示するが、candidate の current static rows は値を欠く場合がある。Mission build 3405148 は client build 3396210 と一致する証拠がなく、EveJS の補助 estimate は CCP authoritative data ではない。 |
| 全 agent→mission pool と同一選出 | **未確認。** L1–L5 basic security offer path はあるが, storyline/research/FW/heraldry pool と source-backed agent membership は揃っていない。Candidate は versioned catalog から選ぶが、EveJS と同一の pool / random selection を証明しない。 |
| Standing GM 通知 | **確認した wire arity mismatch を修正。** V24 の `Standing.OnStandingSet(self, fromID, toID, standing)` は3引数を読む (`/home/user/eve-logic/eve/client/script/ui/services/standingsvc.py:41-63`)、docked Agents UI も同じ callback を購読する (`.../shared/dockedUI/agents.py:44,145-148`)。EveJS は3値を通知 (`/home/user/evejs-261108.1/server/src/services/character/standingRuntime.js:1148-1166`)。MITM 初回 capture では eve.rs が1個の list 値を送っていたため、AgentRpc callback を3 positional values に修正し、実 Host slash route + PostgreSQL persistence の regression を追加した。修正後 capture は arity が3。EveJS の Python-2 `int` に対して候補は `long` / `float` を encode する値型差が残るが、client consumer は数値の比較・保存を行い、rendered UI での確認はしていない。 |

### 同一 headless シナリオ

実シナリオは Level 1 station security agent を選び、`DoAction(None)`, Request, `GetMissionJournalInfo`, `GetMissionKeywords`, Defer, Accept, journal read の順に進む。V24 build 3396210 mission text ID 127450 / content ID 875 を EveJS runtime の `EVEJS_FORCE_MISSION_ID=875` で固定した。この環境変数は比較用 EveJS hook であり、eve.rs の runtime input ではない。今回の capture を比較する時は server bootstrap の違い、session/location、通知 consumer が無視可能な leaf 値を分けて記録した。

| 項目 | EveJS 261108.1 | eve.rs | Verdict |
| --- | --- | --- | --- |
| Offered reply / journal state | Accept / Decline / Defer、state 1、message/content ID tuple | 同じ action/button pairs と tuple、state 1 | **対象シナリオで一致** |
| Defer | offered record と message を維持 | offered record と message を維持 | **一致** |
| Direct Accept | Complete token。Quit は agent station にいる時だけ追加。active flags false | 同じ位置条件・token・false flags | **一致**。Test は位置条件に基づき期待 action を厳密に検査。 |
| Accepted journal | state 2、journal に agent と mission row | state 2、journal に agent と mission row | **対象シナリオで一致** |
| mission type label / rowset | Candidate が `missionTypeLabel` と rowset columns を追加で返す場合がある | V24 `agents.py:89-114` は header/lines を読み agent fields で index する | **この client path の crash は確認されず**。extra columns は unused metadata、agent population / source-build 差は別問題として残す。 |
| Mission reward fields | EveJS が catalog estimates を含める場合がある | target static reward source がない場合は absent | **Possible presentation gap / data source未確認**。架空値を追加しない。 |
| Initial setup / standing / mission notifications | reference/candidate で onboarding と session setup の通知列・順序が異なる。両者に offered / accepted の `OnAgentMissionChange` がある。Standing 通知は修正後 arity 3 だが numeric marshal type は異なる | 初回 bootstrap と session setup の差を含む | **差分が残る。** missing `OnStartClientNodeGraph` / onboarding notices と通知順を今回確認したが、全 consumer に対する影響は未確認。Graphical client での再描画確認はしていない。 |

Same-scenario capture results: reference recorded 16 calls; candidate recorded 17 because it performs an extra `machoNet.GetServiceInfo` on a second connection. The direct offer, Defer, Accept replies and accepted mission state are `SAME` in `mitm diff`; other unmatched replies include agent rowset population, initial `DoAction(None)`, mission keywords, journal presentation and `GetMyJournalDetails`. The candidate sends 20 additional agent rows (10,966 vs 10,946) and has some missing station IDs. V24 `agents.py:89-114` explicitly handles agents with a station, an in-space location, or no location while building its indexes; this headless walk did not show a crash. No GUI conclusion follows from that observation.

The remaining journal shape differences include absent normal/bonus reward rows, different `missionNameID` scalar type, missing `ownerID`, and absent location `coords`. V24 `agents.py:644-662` accepts either message text or a message ID; `agentDialogueUtil.py:178-202` has a location path without coordinates; reward rows are rendered only when present (`agentDialogueUtil.py:347-378`). Thus the first two are accepted representations, coordinates are intentionally omitted until runtime owns an origin, and rewards remain a visible data gap. `GetMissionKeywords` also lacks `rewardTypeID` / `rewardQuantity`; no reward values were guessed.

Capture paths are temporary, not committed: `/tmp/cx5-economy-social/mission-reference.jsonl` and `/tmp/cx5-economy-social/mission-candidate-fixed.jsonl`. The corrected candidate's `OnStandingSet` records are capture records #40 and #43 in `mitm show ... --method OnStandingSet`; each has owner, character and standing as three top-level values. The focused headless test also asserts Defer preserves state, Accept flags, mission type label and accepted journal transition. Comparator unit tests cover missing replies, marshal shape, errors and ordered notifications.

## 経済・ソーシャル領域の監査結果 index

以下は先行する finding record への index。詳細な client / EveJS / eve.rs file:line、failed-before-fix、Host registration と DB evidence は記録先を参照する。ここに列挙した領域を今回すべて再実行したとは扱わない。

| 領域 | Verdict / 確認済みの主な差異 | 詳細記録と今回の範囲 |
| --- | --- | --- |
| Market orders / fees / matching / history / structures | 同じ request terms でも異なる CallID の buy を5秒拒否するガードは client-reachable break で、Host transaction receipt を保ったまま修正済み。Fee, tax, matching, expiry, escrow checked paths では confirmed discrepancy なし。Market history / quotes / structure-specific combinations の包括同等性は未確認。 | [audit-market-wallet.md](audit-market-wallet.md), [audit-fix-economy.md](audit-fix-economy.md)。このターンは market live capture / GUI を再実行していない。 |
| Contracts / auctions / direct trade | Courier/auction/wallet settlements、trade custody/permission の具体的な findings と Host/DB coverage は既存 record にある。Auction runtime の全 combinations と graphical UI は未確認。 | [deepfix-contracts-trade-wallet.md](deepfix-contracts-trade-wallet.md), [audit-fixes-trade.md](audit-fixes-trade.md)。今ターン未再実行。 |
| Wallet, bills, taxes, journals | Operation-key replay の二重 credit false positive と journal metadata completeness の確認済み case、および修正された wallet/division paths は record に記載。全 bill issuer/expiry/replay combinations は未確認。 | [audit-market-wallet.md](audit-market-wallet.md), [deepfix-contracts-trade-wallet.md](deepfix-contracts-trade-wallet.md), [audit-fix-economy.md](audit-fix-economy.md)。今ターン未再実行。 |
| Inventory and item moves | Capsuleer Deliveries access/flag、crystal damage provider、MultiAdd partial authorization、Python set wire shape、asset-root valuation の6件を confirmed として修正。Trade の fitted charge, office division, proximity, descendants, virtual item type の差異も修正済み。 | [audit-fixes-inventory.md](audit-fixes-inventory.md), [audit-fixes-trade.md](audit-fixes-trade.md)。記録された DB/Host slices のみを verified とする。 |
| Fitting / skills / queue | Training projection, online prerequisite, fitting attribute restrictions, damage fractions, capacitor threshold の findings は修正済み・client/root cause と regression 記録あり。今回未再実行。 | [audit-skills-fitting.md](audit-skills-fitting.md)。 |
| Corporations / alliances / wars | Office rent, medal privacy 等の corp findings は記録済み。Contact, bulletin, wallet division, role and war RPC の一部は先行 Host tests に含まれる。全 application / role / alliance / war combinations は未確認。 | [deepfix-corporations.md](deepfix-corporations.md), [audit-fix-economy.md](audit-fix-economy.md), [evejs-261108-diff-sweep.md](evejs-261108-diff-sweep.md)。今ターン未再実行。 |
| Bookmarks / mail / fleet / calendar / chat | Bookmark expiry、mail welcome status/inbox/notification、offline fleet invite の client-visible issues を個別に確認・修正。Offline fleet invite では EveJS が保存して通知しないケースを V24 UI の欠落に照らして修正。Calendar selected flows と chat room ACL/expiry は記録済み。全 UI view combinations は未確認。 | [audit-social-corp.md](audit-social-corp.md), [deepfix-fleets-mail-calendar.md](deepfix-fleets-mail-calendar.md)。今ターン未再実行。 |
| Agents / missions | Basic L1–L5 offer path は今回部分実装し、same client flow を EveJS と live capture で確認。Offer pool source、build reconciliation、missing reward inputs, other agent types remain open. | 本記録、および [deepfix-missions-agents.md](deepfix-missions-agents.md), [audit-fixes-agents.md](audit-fixes-agents.md)。今回のテスト数は下記。 |

EveJS 261028.1 → 261108.1 全体差分から本 job で扱う範囲外の変更・新しい service/data 変更を分類した根拠は [evejs-261108-diff-sweep.md](evejs-261108-diff-sweep.md)。その記録は source sweep であり、全機能を実行比較したという意味ではない。特に未確認として残るのは mission static data の対象 build 差、全 market/auction/contract combinations、corp application/war state、UI 上の各 notification、runtime-owned behavior を hidden service state まで含めた全パスである。

## SimLink 差分の扱い

Dirty `/home/user/wt-cx5` の SimLink patch は、この worktree の開始 commit に対してそのまま適用しなかった。Dirty branch は protocol 97/98 の古い base にあり、今回の clean base `78cdc1337` は既に SimLink v99、`SmartbombHit` kind 164 の Rust encode/decode、C# codec / Host Crimewatch consumer、fixture、`SmartbombHitHostTests` を含む。`crates/eve-space/src/scene/docking.rs` の unbound synthetic-session widow guard と regression も既に存在する。古い patch を再適用すると v99 を98へ戻すため、これは port ではなく既存 main implementation の確認として扱った。

## 今回の検証記録

- `dotnet build -c Release -warnaserror`: `Eve.Services.Core`, `Eve.Host`, `Eve.Services.Space`, `Eve.Services.Core.Tests`, `Eve.Host.Tests`, `Eve.Services.Space.Tests` は各 0 warnings / 0 errors。
- `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p5` Core agent/mission slice: **223 passed, 0 failed, 0 skipped**。
- 同 Host agent/mission slice: **49 passed, 0 failed, 0 skipped**。最初は Accept 直後の旧 `Assert.Single` が失敗した。V24/EveJS が issuer location で Complete + Quit を返すため assertion を wire pair/order に合わせ、focused dispatch regression と slice を再実行した。
- Space dungeon-wave slice: **17 passed, 0 failed, 0 skipped**。
- Existing Smartbomb Host regression: **1 passed, 0 failed, 0 skipped** on `eve_p5`。
- `Eve.Space.Tests.SimCodecTests`: **499 passed, 0 failed, 0 skipped**。
- Rust: `cargo fmt -p eve-space -- --check`, `cargo check -p eve-space`, `cargo clippy -p eve-space --all-targets -- -D warnings` pass。Focused smartbomb / unbound widow tests 1+1 pass、`cargo test -p eve-space --test simlink_fixtures` 13 passed。
- `Eve.Host.Tests.Setstanding_sends_the_three_client_arguments_after_persisting_the_relation`: **1 passed** on `eve_p5`, over the registered Host `slash.SlashCmd`, with exact notification arity/value and persisted standing checks.
- `tools/headless-client/tests/test_mitm.py`: **26 passed, 0 failed**.
- Focused slow-live `test_mission_walk.py::test_a_basic_level_4_kill_mission_is_offered_accepted_and_its_dungeon_comes_into_space`: **1 passed** with the active dungeon bookmark assertions enabled; no other slow-live walks were run.
- EveJS live mission case: pytest reported **1 passed**. Its runner then returned exit 1 during temp-directory cleanup (`rm: ... Directory not empty`); treat the pytest assertion as passed and the runner teardown as failed.
- Candidate live mission case after the standing notification fix: pytest reported **1 passed** through the MITM proxy.
- Final raw `mitm diff --ignore-volatile --ignore-key-order`: 16 reference calls vs 17 candidate calls and **20 reported issues**, including bootstrap/notification differences and the data/presentation differences above. The initial nested-list `OnStandingSet` wire break no longer reproduces; capture and Host test show three positional values. A comparator pass is not claimed.
- V24 graphical clientは起動していない。headless wire run は live server check であり graphical-client verification ではない。

## 未確認 / 残るギャップ

- No full live headless suite or broader CI Actions suite was run locally; domain matrices above cite prior isolated reports, not this turn's test run.
- Build 3396210 に一致する mission binary/reward source が得られていない。Offer catalog、mission reward values、LP reward formulas、3 missing kill-dungeon authorities remain open in `TODO.md` section 43.
- Native V24.01 graphical client, rendered journal / mission images, every mission branch, retry/restart concurrency across the whole economy/social set, and all issuer / station location combinations remain unverified beyond the cited focused regressions.
- GitHub main CI result is recorded in the job summary after the push.
