# 監査サマリー（短縮版）

数値は[監査記録の集計](audit-summary.md)の分野別小計・全体合計から転記。読取記録は計数可能記録とNC記録の合計、NCは計数不能な記録。

## 分野別集計

| 分野 | 読取記録 | 所見掲載 | 修正確認済 | 誤検知 | 未解決・保留 | 計数不能記録 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| コア・ハブ | 35 | 29 | 26 | 0 | 3 | 21 |
| キャラクター・スキル | 18 | 27 | 24 | 1 | 1 | 7 |
| フィッティング・Dogma | 11 | 31 | 24 | 3 | 4 | 1 |
| 船・宇宙空間 | 33 | 73 | 63 | 0 | 10 | 7 |
| 戦闘・ドローン | 21 | 36 | 32 | 2 | 2 | 7 |
| 市場・ウォレット・取引 | 21 | 46 | 39 | 5 | 1 | 5 |
| 法人・契約 | 22 | 30 | 20 | 6 | 4 | 12 |
| 構造物・POS | 14 | 26 | 14 | 10 | 2 | 8 |
| 惑星・産業 | 10 | 13 | 13 | 0 | 0 | 4 |
| ミッション・エージェント | 15 | 16 | 15 | 0 | 1 | 9 |
| 艦隊・チャット・メール | 11 | 5 | 5 | 0 | 0 | 7 |
| 宇宙・SDE・データ | 11 | 6 | 6 | 0 | 0 | 7 |
| Host・wire・marshal | 69 | 33 | 26 | 1 | 4 | 58 |
| scheduler・イベント・基盤 | 5 | 3 | 3 | 0 | 0 | 3 |
| **全分野** | **296** | **374** | **310** | **28** | **32** | **156** |

## 後続作業による状態更新（2026-11-17）

- SW-04 のdeployment dynamic blocker行（旧L67）は Host / Rust scene / SimLink query とfocused regressionで修正確認した。詳細とGUI未確認範囲は [`space-world-gaps-implementation.md`](space-world-gaps-implementation.md)。
- Ansiblexの移動RPC行（旧L59-L60、別記録に重複）は、登録済みHost travel、永続fuel/fee settlement、retry/concurrencyをPostgreSQL Host testで確認したため未解決一覧から除いた。anchoring/setup、初期scene配置、GUI walkは引き続き開いている。
- Control BunkerはHull-depletion handlerとdowntime capture state transitionを実装したが、配置先のauthoritative sourceがなく本番scene spawnはfail-closed。重複する2記録はこの未解決理由を保持する。

## 外部監査所見

| ID | 短い題名 | 状態・記録された検証 |
| --- | --- | --- |
| C-01 | 報奨金・産業の要求識別 | 修正済。PlayerBountyHostTests、IndustryFacilityHostFlowTests |
| H-01 | 題名・所見なし | 集計記録のdocs検索で該当なし。状態不明 |
| H-02 | 記録なし | 題名・状態・検証記録なし |
| H-03 | AllianceラベルIDはビットマスク | 修正済。AllianceTests、Host回帰 |
| H-04 | LPストアの必要品数 | 修正済。登録LPStoreMgrのHost/DB回帰 |
| H-05 | Courier受渡場所と原子的完了 | 修正済。MissionCourierDeliveryHostTests |
| H-06 | Redeem IDの64-bit型 | 修正済。UserSvc境界Host回帰、季節報酬DB回帰 |
| H-07 | role stasis中の自己退会 | 報告内容は誤検知。HostServerTestsで経路確認 |
| H-08 | 露見後の防御ノード再攻撃 | 修正済。Hacking Host-wire回帰 |
| H-09 | Command Centerスタック消費 | 修正済。planetMgr登録Host/DB回帰 |
| M-01 | 法人契約wallet division | 修正済。ContractCorporateWalletDivisionHostTests |
| M-02 | 報酬のみのitem-exchange契約 | 修正済。登録Host RPC回帰（名称記載なし） |
| M-03 | Module-end欠落後の戦闘期限 | 修正済。WeaponHostTests、Crimewatch RuntimeTests |

## 未解決・保留項目（元の一覧行。複数所見・重複記録あり）

- コア・ハブ — communityTrackMgrの進捗生成元なし — audit-fixes-core.md:L28
- フィッティング・Dogma — Starbase AddTargetOBO/RemoveTargetOBO未実装 — audit-fixes-dogma.md:L41
- 戦闘・ドローン — Control Bunkerの本番配置位置・通常spawn不足（capture handlerは部分実装） — audit-fixes-factionwarfare.md:L49
- ミッション・エージェント — SDE mission poolからofferを作る経路なし（件数外） — audit-fixes-agents.md:L21
- 戦闘・ドローン — Control Bunker IDはlive group-925 ballの確認が必要。ball配置source未解決（重複記録） — service-audit/DEEPCHECK_facWarMgr.md:L64
- 船・宇宙空間 — Filamentのcomponent操作が未実装 — service-audit/DEEPCHECK_beyonce.md:L70
- 船・宇宙空間 — FitShips未登録 — service-audit/DEEPCHECK_ship.md:L22
- 船・宇宙空間 — LaunchFromContainer未登録 — service-audit/DEEPCHECK_ship.md:L27
- 船・宇宙空間 — ScoopToFleetHangar未登録 — service-audit/DEEPCHECK_ship.md:L29
- 船・宇宙空間 — ScoopToInfrastructureHold未登録 — service-audit/DEEPCHECK_ship.md:L31
- 船・宇宙空間 — ScoopToMobileDepotHold未登録 — service-audit/DEEPCHECK_ship.md:L32
- 船・宇宙空間 — BoardStoredShip未登録 — service-audit/DEEPCHECK_ship.md:L36
- 船・宇宙空間 — SetShipPassword未登録 — service-audit/DEEPCHECK_ship.md:L43
- ミッション・エージェント — GetMyEscalatingPathDetailsの本番providerなし — service-audit/DEEPCHECK_journal.md:L47
- 法人・契約 — warStatisticMgrのreport provider未実装（他2件は後続解消） — service-response-audit/corporations.md:L20
- フィッティング・Dogma — 報酬永続化・再試行記録・旧fitting移行 — service-response-audit/crate-fitting-fix.md:L22
- キャラクター・スキル — skill-bundle待機報酬の永続化候補 — service-response-audit/crate-skill-retry-fix.md:L75
- Host・wire・marshal — 惑星RPC・material monitor・惑星状態merge — service-response-audit/host-native-services.md:L206
- 構造物・POS — POS force-field ball/harmonic/admissionは未実装。燃料切れ後のtower/module offline同期は修正済 — deepfix-structures-pos.md:L53; pos-force-field-and-modules.md
- 構造物・POS — Starbase AddTargetOBO/RemoveTargetOBO未実装 — audit-fixes-dogma.md:L41
- 構造物・POS — Control Bunkerの本番配置・通常scene spawnは未解決（capture handlerは部分実装） — audit-fixes-factionwarfare.md:L49; service-audit/DEEPCHECK_facWarMgr.md:L64

この表は記録単位の掲載数で、重複不具合を除いた総数ではない。NCは「不具合なし」を意味しない。
修正確認は記録されたテスト範囲に限り、GUI実クライアントの受入れや監査完了を示さない。
