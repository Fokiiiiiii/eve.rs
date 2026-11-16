# 監査記録の集計

監査記録に明示された所見件数を、記録ごとに集計した。ルート指定パターン71件と、`service-audit` 182件、`service-response-audit` 43件を読み、計296記録（Markdown 294件、CSV台帳2件）を対象にした。

**数え方:** 所見ID・番号付き項目・同等の有限な項目表がある場合だけ件数に含めた。方法一覧、coverage行、作業引継ぎ、広い確認メモは、不具合であるとは書いていない候補を所見に読み替えず `NC`（この記録からは計数不能）とする。記録に明記された「0件」は `0` とした。重複する同一不具合も記録単位では別々に数える。`修正確認済（test実行）` は記録が確認テストを実行済みとしている項目のみ。

test名・commit・判定は各行の根拠リンク先にある記録の記載を示す。表の合計は監査所見のユニーク不具合数ではなく、記録単位で数えた掲載数である。未解決数は各記録時点の状態を集計し、後続記録で解消されたものは末尾に別記した。

## 外部監査ID

| ID | 記録上の状態 | 根拠 |
| --- | --- | --- |
| C-01 | 確認済み・修正済み。報奨金と産業活動の要求主体を分け、Host回帰で検証。 | [audit-fix-economy.md L5](audit-fix-economy.md#L5), [検証 L29](audit-fix-economy.md#L29) |
| M-01 | 確認済み・修正済み。法人契約のwallet division（財布区分）をHost/DB回帰で確認。 | [audit-fix-economy.md L13](audit-fix-economy.md#L13), [検証 L29](audit-fix-economy.md#L29) |
| H-01 | `docs/` 以下をID完全一致検索したが該当記録なし。状態を確認できない。 | 検索対象: `docs/**/*.md` と `docs/**/*.csv` |

C-01/M-01 は下表の `audit-fix-economy.md` に含めており、ID表では再加算しない。

## 分野別記録

### コア・ハブ

| 記録 | 所見数 | 修正確認済（test実行） | 誤検知 | 未解決/保留 | 修正記載・test未確認 | 根拠・備考 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| [audit-fixes-core.md](audit-fixes-core.md#L1) | 2 | 1 | 0 | 1 | 0 | [根拠・test/commit](audit-fixes-core.md#L56)。記録時点の未解決/保留を計上。状態根拠と後続状況は末尾一覧を参照。 |
| [audit-fixes-inventory-r14-mutation.md](audit-fixes-inventory-r14-mutation.md#L1) | 4 | 4 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-inventory-r14-mutation.md#L36)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-inventory.md](audit-fixes-inventory.md#L1) | 6 | 6 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-inventory.md#L65)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-login-session-accounts.md](deepfix-login-session-accounts.md#L1) | NC | NC | NC | NC | NC | [対象](deepfix-login-session-accounts.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_accessGroupBookmarkMgr.md](service-audit/DEEPCHECK_accessGroupBookmarkMgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_accessGroupBookmarkMgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_activityMgr.md](service-audit/DEEPCHECK_activityMgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_activityMgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_alert.md](service-audit/DEEPCHECK_alert.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_alert.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_bookmarkMgr.md](service-audit/DEEPCHECK_bookmarkMgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_bookmarkMgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_config.md](service-audit/DEEPCHECK_config.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_config.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_contextualOfferMgr.md](service-audit/DEEPCHECK_contextualOfferMgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_contextualOfferMgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_contractProxy.md](service-audit/DEEPCHECK_contractProxy.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_contractProxy.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_filterSvc.md](service-audit/DEEPCHECK_filterSvc.md#L1) | 0 | 0 | 0 | 0 | 0 | [根拠・test/commit](service-audit/DEEPCHECK_filterSvc.md#L5)。対象はクライアント内のitemFilterであり、遠隔routeの所見は0件。 |
| [DEEPCHECK_kiringMgr.md](service-audit/DEEPCHECK_kiringMgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_kiringMgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_lookupSvc.md](service-audit/DEEPCHECK_lookupSvc.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_lookupSvc.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_neocom.md](service-audit/DEEPCHECK_neocom.md#L1) | 0 | 0 | 0 | 0 | 0 | [根拠・test/commit](service-audit/DEEPCHECK_neocom.md#L3)。対象はクライアント内UI serviceであり、遠隔routeの所見は0件。 |
| [DEEPCHECK_overviewPresetMgr.md](service-audit/DEEPCHECK_overviewPresetMgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_overviewPresetMgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_seasonManager.md](service-audit/DEEPCHECK_seasonManager.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_seasonManager.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_tourneyMgr.md](service-audit/DEEPCHECK_tourneyMgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_tourneyMgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [deepfix-courier-collateral.md](service-audit/deepfix-courier-collateral.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-courier-collateral.md#L25)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-fleet-finder.md](service-audit/deepfix-fleet-finder.md#L1) | 3 | 1 | 0 | 2 | 0 | [根拠・test/commit](service-audit/deepfix-fleet-finder.md#L23)。記録時点の未解決/保留を計上。状態根拠と後続状況は末尾一覧を参照。 |
| [deepfix-inventory-stacking.md](service-audit/deepfix-inventory-stacking.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-inventory-stacking.md#L26)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-inventory.md](service-audit/deepfix-inventory.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-inventory.md#L25)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-login.md](service-audit/deepfix-login.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-login.md#L32)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-notifications.md](service-audit/deepfix-notifications.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/deepfix-notifications.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [deepfix-overview-bookmarks.md](service-audit/deepfix-overview-bookmarks.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-overview-bookmarks.md#L50)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-sessionwalks.md](service-audit/deepfix-sessionwalks.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-sessionwalks.md#L9)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-skins-cosmetics.md](service-audit/deepfix-skins-cosmetics.md#L1) | 3 | 3 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-skins-cosmetics.md#L33)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-uiservices.md](service-audit/deepfix-uiservices.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-uiservices.md#L38)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [pr30-verification.md](service-audit/pr30-verification.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/pr30-verification.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [raffle-client-mechanics-audit.md](service-audit/raffle-client-mechanics-audit.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/raffle-client-mechanics-audit.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [sweep-comments-eve-accessgroups.md](service-audit/sweep-comments-eve-accessgroups.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/sweep-comments-eve-accessgroups.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [sweep-comments-eve-bookmarks.md](service-audit/sweep-comments-eve-bookmarks.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/sweep-comments-eve-bookmarks.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [sweep-optimize-eve-accessgroups.md](service-audit/sweep-optimize-eve-accessgroups.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/sweep-optimize-eve-accessgroups.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [tournament-configuration-import.md](service-audit/tournament-configuration-import.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/tournament-configuration-import.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [tournament-cutoff-tracking.md](service-audit/tournament-cutoff-tracking.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/tournament-cutoff-tracking.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| **小計: コア・ハブ** | **29** | **26** | **0** | **3** | **0** | 計数可能 14 記録、NC 21 記録。 |

### キャラクター・スキル

| 記録 | 所見数 | 修正確認済（test実行） | 誤検知 | 未解決/保留 | 修正記載・test未確認 | 根拠・備考 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| [audit-fixes-characters.md](audit-fixes-characters.md#L1) | 3 | 3 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-characters.md#L34)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-identity.md](audit-fixes-identity.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-identity.md#L27)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-services-characterinfo.md](audit-fixes-services-characterinfo.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-services-characterinfo.md#L21)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-services-characterselect.md](audit-fixes-services-characterselect.md#L1) | 4 | 4 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-services-characterselect.md#L41)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-skills-r14.md](audit-fixes-skills-r14.md#L1) | 6 | 6 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-skills-r14.md#L26)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-skills.md](audit-fixes-skills.md#L1) | 4 | 3 | 1 | 0 | 0 | [根拠・test/commit](audit-fixes-skills.md#L36)。修正済みと誤検知の内訳は数列の通り。test/commit名を含む判定根拠は原記録を参照。 |
| [deepfix-skills-clones-characters.md](deepfix-skills-clones-characters.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](deepfix-skills-clones-characters.md#L29)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [DEEPCHECK_userSvc.md](service-audit/DEEPCHECK_userSvc.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_userSvc.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [deepfix-attributes-remap.md](service-audit/deepfix-attributes-remap.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-attributes-remap.md#L25)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-character skills and clones and implants and insurance.md](service-audit/deepfix-character%20skills%20and%20clones%20and%20implants%20and%20insurance.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-character%20skills%20and%20clones%20and%20implants%20and%20insurance.md#L53)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-skillqueue.md](service-audit/deepfix-skillqueue.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-skillqueue.md#L26)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [charMgr-getpublicinfo-host-handoff.md](service-response-audit/charMgr-getpublicinfo-host-handoff.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/charMgr-getpublicinfo-host-handoff.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [charMgr-rpc-batch-01114-01138-20261024.md](service-response-audit/charMgr-rpc-batch-01114-01138-20261024.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/charMgr-rpc-batch-01114-01138-20261024.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [charMgr-rpc-batch-20261024.md](service-response-audit/charMgr-rpc-batch-20261024.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/charMgr-rpc-batch-20261024.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [character-contact-notepad-rpc-handoff.md](service-response-audit/character-contact-notepad-rpc-handoff.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/character-contact-notepad-rpc-handoff.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [crate-skill-retry-fix.md](service-response-audit/crate-skill-retry-fix.md#L1) | 2 | 0 | 0 | 1 | 1 | [根拠・test/commit](service-response-audit/crate-skill-retry-fix.md#L3)。未解決/保留および未testの内訳は数列の通り。判定・検証根拠は原記録を参照。 |
| [skills-rpc-02496-02522-handoff.md](service-response-audit/skills-rpc-02496-02522-handoff.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/skills-rpc-02496-02522-handoff.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [skills-test-fixture-fix.md](service-response-audit/skills-test-fixture-fix.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/skills-test-fixture-fix.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| **小計: キャラクター・スキル** | **27** | **24** | **1** | **1** | **1** | 計数可能 11 記録、NC 7 記録。 |

### フィッティング・Dogma

| 記録 | 所見数 | 修正確認済（test実行） | 誤検知 | 未解決/保留 | 修正記載・test未確認 | 根拠・備考 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| [audit-fixes-dogma-r14.md](audit-fixes-dogma-r14.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-dogma-r14.md#L19)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-dogma.md](audit-fixes-dogma.md#L1) | 7 | 6 | 0 | 1 | 0 | [根拠・test/commit](audit-fixes-dogma.md#L52)。記録時点の未解決/保留を計上。状態根拠と後続状況は末尾一覧を参照。 |
| [audit-fixes-fitting.md](audit-fixes-fitting.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-fitting.md#L38)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-skills-fitting.md](audit-skills-fitting.md#L1) | 8 | 5 | 3 | 0 | 0 | [根拠・test/commit](audit-skills-fitting.md#L63)。修正済みと誤検知の内訳は数列の通り。test/commit名を含む判定根拠は原記録を参照。 |
| [deepfix-dogma-fitting-combat.md](deepfix-dogma-fitting-combat.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](deepfix-dogma-fitting-combat.md#L37)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [DEEPCHECK_ram.md](service-audit/DEEPCHECK_ram.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_ram.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [deepfix-combat and dogma and modules.md](service-audit/deepfix-combat%20and%20dogma%20and%20modules.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-combat%20and%20dogma%20and%20modules.md#L17)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-fitting-loadouts.md](service-audit/deepfix-fitting-loadouts.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-fitting-loadouts.md#L24)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [crate-fitting-fix.md](service-response-audit/crate-fitting-fix.md#L1) | 5 | 2 | 0 | 3 | 0 | [根拠・test/commit](service-response-audit/crate-fitting-fix.md#L19)。記録時点の未解決/保留を計上。状態根拠と後続状況は末尾一覧を参照。 |
| [inventory-rpc-fix.md](service-response-audit/inventory-rpc-fix.md#L1) | 3 | 3 | 0 | 0 | 0 | [根拠・test/commit](service-response-audit/inventory-rpc-fix.md#L269)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [inventory-ui-actions-2026-10-26.md](service-response-audit/inventory-ui-actions-2026-10-26.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-response-audit/inventory-ui-actions-2026-10-26.md#L34)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| **小計: フィッティング・Dogma** | **31** | **24** | **3** | **4** | **0** | 計数可能 10 記録、NC 1 記録。 |

### 船・宇宙空間

| 記録 | 所見数 | 修正確認済（test実行） | 誤検知 | 未解決/保留 | 修正記載・test未確認 | 根拠・備考 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| [audit-fixes-calendar.md](audit-fixes-calendar.md#L1) | 3 | 3 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-calendar.md#L45)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-crate-behaviors.md](audit-fixes-crate-behaviors.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-crate-behaviors.md#L35)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-crate-destiny.md](audit-fixes-crate-destiny.md#L1) | 3 | 3 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-crate-destiny.md#L32)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-crate-sim-r15-revalidation.md](audit-fixes-crate-sim-r15-revalidation.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-crate-sim-r15-revalidation.md#L29)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-crate-sim.md](audit-fixes-crate-sim.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-crate-sim.md#L33)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-crate-space.md](audit-fixes-crate-space.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-crate-space.md#L23)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-ships.md](audit-fixes-ships.md#L1) | 8 | 8 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-ships.md#L134)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-space.md](audit-fixes-space.md#L1) | 5 | 5 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-space.md#L55)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-wormholes.md](audit-fixes-wormholes.md#L1) | 7 | 7 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-wormholes.md#L25)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-chat-ui-startup.md](deepfix-chat-ui-startup.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](deepfix-chat-ui-startup.md#L39)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-fleets-mail-calendar.md](deepfix-fleets-mail-calendar.md#L1) | 6 | 6 | 0 | 0 | 0 | [根拠・test/commit](deepfix-fleets-mail-calendar.md#L58)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [DEEPCHECK_beyonce.md](service-audit/DEEPCHECK_beyonce.md#L1) | 2 | 0 | 0 | 2 | 0 | [根拠・test/commit](service-audit/DEEPCHECK_beyonce.md#L70)。記録時点の未解決/保留を計上。状態根拠と後続状況は末尾一覧を参照。 |
| [DEEPCHECK_michelle.md](service-audit/DEEPCHECK_michelle.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_michelle.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_scanMgr.md](service-audit/DEEPCHECK_scanMgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_scanMgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_ship.md](service-audit/DEEPCHECK_ship.md#L1) | 7 | 0 | 0 | 7 | 0 | [根拠・test/commit](service-audit/DEEPCHECK_ship.md#L22)。記録時点の未解決/保留を計上。状態根拠と後続状況は末尾一覧を参照。 |
| [DEEPCHECK_shipFitting.md](service-audit/DEEPCHECK_shipFitting.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_shipFitting.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [beyonce-config-audit-cx3.md](service-audit/beyonce-config-audit-cx3.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/beyonce-config-audit-cx3.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [deepfix-autopilot-routes.md](service-audit/deepfix-autopilot-routes.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-autopilot-routes.md#L31)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-capitals.md](service-audit/deepfix-capitals.md#L1) | 5 | 5 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-capitals.md#L22)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-incursions-dynamic.md](service-audit/deepfix-incursions-dynamic.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-incursions-dynamic.md#L32)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-jump-drives-cyno.md](service-audit/deepfix-jump-drives-cyno.md#L1) | 2 | 1 | 0 | 1 | 0 | [根拠・test/commit](service-audit/deepfix-jump-drives-cyno.md#L39)。記録時点の未解決/保留を計上。状態根拠と後続状況は末尾一覧を参照。 |
| [deepfix-maps.md](service-audit/deepfix-maps.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-maps.md#L17)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-probes-scanning.md](service-audit/deepfix-probes-scanning.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-probes-scanning.md#L29)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-rpcvalidation.md](service-audit/deepfix-rpcvalidation.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/deepfix-rpcvalidation.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [deepfix-ships.md](service-audit/deepfix-ships.md#L1) | 0 | 0 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-ships.md#L23)。原記録は確認対象について所見0件と明記。 |
| [deepfix-simcore.md](service-audit/deepfix-simcore.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-simcore.md#L42)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-stations.md](service-audit/deepfix-stations.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-stations.md#L28)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-targeting-locks.md](service-audit/deepfix-targeting-locks.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-targeting-locks.md#L48)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-travel.md](service-audit/deepfix-travel.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-travel.md#L55)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-undock-dock-sequences.md](service-audit/deepfix-undock-dock-sequences.md#L1) | 5 | 5 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-undock-dock-sequences.md#L50)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-wormholes.md](service-audit/deepfix-wormholes.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-wormholes.md#L30)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [handoff-cx6-ship-configuration.md](service-audit/handoff-cx6-ship-configuration.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-cx6-ship-configuration.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-shipCosmeticsMgr-coverage-cx3.md](service-audit/handoff-shipCosmeticsMgr-coverage-cx3.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-shipCosmeticsMgr-coverage-cx3.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| **小計: 船・宇宙空間** | **73** | **63** | **0** | **10** | **0** | 計数可能 26 記録、NC 7 記録。 |

### 戦闘・ドローン

| 記録 | 所見数 | 修正確認済（test実行） | 誤検知 | 未解決/保留 | 修正記載・test未確認 | 根拠・備考 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| [audit-fix-hacking-token.md](audit-fix-hacking-token.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](audit-fix-hacking-token.md#L46)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-crimewatch.md](audit-fixes-crimewatch.md#L1) | 3 | 3 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-crimewatch.md#L5)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-factionwarfare.md](audit-fixes-factionwarfare.md#L1) | 6 | 5 | 0 | 1 | 0 | [根拠・test/commit](audit-fixes-factionwarfare.md#L60)。記録時点の未解決/保留を計上。状態根拠と後続状況は末尾一覧を参照。 |
| [audit-fixes-hacking.md](audit-fixes-hacking.md#L1) | 6 | 6 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-hacking.md#L61)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-insurgency.md](audit-fixes-insurgency.md#L1) | 3 | 3 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-insurgency.md#L43)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-npc.md](audit-fixes-npc.md#L1) | 3 | 3 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-npc.md#L55)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-space-combat.md](audit-space-combat.md#L1) | 7 | 5 | 2 | 0 | 0 | [根拠・test/commit](audit-space-combat.md#L71)。修正済みと誤検知の内訳は数列の通り。test/commit名を含む判定根拠は原記録を参照。 |
| [deepfix-npc-crimewatch-security.md](deepfix-npc-crimewatch-security.md#L1) | NC | NC | NC | NC | NC | [対象](deepfix-npc-crimewatch-security.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_bountyProxy.md](service-audit/DEEPCHECK_bountyProxy.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_bountyProxy.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_crimewatch.md](service-audit/DEEPCHECK_crimewatch.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_crimewatch.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_facWarMgr.md](service-audit/DEEPCHECK_facWarMgr.md#L1) | 1 | 0 | 0 | 1 | 0 | [根拠・test/commit](service-audit/DEEPCHECK_facWarMgr.md#L64)。記録時点の未解決/保留を計上。状態根拠と後続状況は末尾一覧を参照。 |
| [DEEPCHECK_fwCharacterEnlistmentMgr.md](service-audit/DEEPCHECK_fwCharacterEnlistmentMgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_fwCharacterEnlistmentMgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_insurgencySolarsystem.md](service-audit/DEEPCHECK_insurgencySolarsystem.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_insurgencySolarsystem.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [deepfix-crimewatch.md](service-audit/deepfix-crimewatch.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-crimewatch.md#L19)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-drones.md](service-audit/deepfix-drones.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-drones.md#L38)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-fighters-superweapons.md](service-audit/deepfix-fighters-superweapons.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-fighters-superweapons.md#L33)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-loot-salvage.md](service-audit/deepfix-loot-salvage.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-loot-salvage.md#L23)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-npc-ai-aggro.md](service-audit/deepfix-npc-ai-aggro.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/deepfix-npc-ai-aggro.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [deepfix-npcs.md](service-audit/deepfix-npcs.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-npcs.md#L20)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-security-concord.md](service-audit/deepfix-security-concord.md#L1) | 0 | 0 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-security-concord.md#L131)。原記録は確認対象について所見0件と明記。 |
| [handoff-fwWarzoneSolarsystem-cx5.md](service-audit/handoff-fwWarzoneSolarsystem-cx5.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-fwWarzoneSolarsystem-cx5.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| **小計: 戦闘・ドローン** | **36** | **32** | **2** | **2** | **0** | 計数可能 14 記録、NC 7 記録。 |

### 市場・ウォレット・取引

| 記録 | 所見数 | 修正確認済（test実行） | 誤検知 | 未解決/保留 | 修正記載・test未確認 | 根拠・備考 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| [audit-fix-economy.md](audit-fix-economy.md#L1) | 3 | 3 | 0 | 0 | 0 | [根拠・test/commit](audit-fix-economy.md#L51)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-gamestore.md](audit-fixes-gamestore.md#L1) | 7 | 7 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-gamestore.md#L34)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-market.md](audit-fixes-market.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-market.md#L46)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-trade.md](audit-fixes-trade.md#L1) | 5 | 5 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-trade.md#L54)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-wallet.md](audit-fixes-wallet.md#L1) | 3 | 3 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-wallet.md#L33)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-market-wallet.md](audit-market-wallet.md#L1) | 6 | 1 | 5 | 0 | 0 | [根拠・test/commit](audit-market-wallet.md#L47)。修正済みと誤検知の内訳は数列の通り。test/commit名を含む判定根拠は原記録を参照。 |
| [deepfix-contracts-trade-wallet.md](deepfix-contracts-trade-wallet.md#L1) | NC | NC | NC | NC | NC | [対象](deepfix-contracts-trade-wallet.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_account.md](service-audit/DEEPCHECK_account.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_account.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_billMgr.md](service-audit/DEEPCHECK_billMgr.md#L1) | 1 | 0 | 0 | 1 | 0 | [根拠・test/commit](service-audit/DEEPCHECK_billMgr.md#L20)。記録時点の未解決/保留を計上。状態根拠と後続状況は末尾一覧を参照。 |
| [DEEPCHECK_lpstore.md](service-audit/DEEPCHECK_lpstore.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_lpstore.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_marketProxy.md](service-audit/DEEPCHECK_marketProxy.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_marketProxy.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [deepfix-contracts.md](service-audit/deepfix-contracts.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-contracts.md#L25)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-market and trade and contracts.md](service-audit/deepfix-market%20and%20trade%20and%20contracts.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-market%20and%20trade%20and%20contracts.md#L13)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-market-history.md](service-audit/deepfix-market-history.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-market-history.md#L61)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-nesstore.md](service-audit/deepfix-nesstore.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-nesstore.md#L32)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-persistence.md](service-audit/deepfix-persistence.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-persistence.md#L44)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-plex-store-subscription.md](service-audit/deepfix-plex-store-subscription.md#L1) | 8 | 8 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-plex-store-subscription.md#L44)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-wallet-journal.md](service-audit/deepfix-wallet-journal.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-wallet-journal.md#L32)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [handoff-contractProxy-coverage-cx3.md](service-audit/handoff-contractProxy-coverage-cx3.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-contractProxy-coverage-cx3.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [insurance-kwargs-fix.md](service-response-audit/insurance-kwargs-fix.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-response-audit/insurance-kwargs-fix.md#L91)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [market-asks-fix.md](service-response-audit/market-asks-fix.md#L1) | 1 | 0 | 0 | 0 | 1 | [根拠・test/commit](service-response-audit/market-asks-fix.md#L13)。修正記載はあるがtest未実施の項目を計上。検証状況は原記録を参照。 |
| **小計: 市場・ウォレット・取引** | **46** | **39** | **5** | **1** | **1** | 計数可能 16 記録、NC 5 記録。 |

### 法人・契約

| 記録 | 所見数 | 修正確認済（test実行） | 誤検知 | 未解決/保留 | 修正記載・test未確認 | 根拠・備考 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| [audit-fix-corp-contract.md](audit-fix-corp-contract.md#L1) | 4 | 3 | 1 | 0 | 0 | [根拠・test/commit](audit-fix-corp-contract.md#L41)。修正済みと誤検知の内訳は数列の通り。test/commit名を含む判定根拠は原記録を参照。 |
| [audit-fixes-corporations.md](audit-fixes-corporations.md#L1) | 3 | 2 | 0 | 1 | 0 | [根拠・test/commit](audit-fixes-corporations.md#L31)。記録時点の未解決/保留を計上。状態根拠と後続状況は末尾一覧を参照。 |
| [audit-fixes-relations.md](audit-fixes-relations.md#L1) | 3 | 3 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-relations.md#L46)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-social-corp.md](audit-social-corp.md#L1) | 9 | 5 | 4 | 0 | 0 | [根拠・test/commit](audit-social-corp.md#L89)。修正済みと誤検知の内訳は数列の通り。test/commit名を含む判定根拠は原記録を参照。 |
| [deepfix-corporations.md](deepfix-corporations.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](deepfix-corporations.md#L50)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [DEEPCHECK_allianceRegistry.md](service-audit/DEEPCHECK_allianceRegistry.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_allianceRegistry.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_corpRecProxy.md](service-audit/DEEPCHECK_corpRecProxy.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/DEEPCHECK_corpRecProxy.md#L30)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [DEEPCHECK_standing.md](service-audit/DEEPCHECK_standing.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_standing.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_standingMgr.md](service-audit/DEEPCHECK_standingMgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_standingMgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_wars.md](service-audit/DEEPCHECK_wars.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_wars.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [deepfix-corporations.md](service-audit/deepfix-corporations.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-corporations.md#L25)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-wars-sov.md](service-audit/deepfix-wars-sov.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-wars-sov.md#L23)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [handoff-corpmgr-coverage-cx3.md](service-audit/handoff-corpmgr-coverage-cx3.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-corpmgr-coverage-cx3.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-corpmgr-coverage-cx4-increment.md](service-audit/handoff-corpmgr-coverage-cx4-increment.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-corpmgr-coverage-cx4-increment.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-corpmgr-coverage-cx4.md](service-audit/handoff-corpmgr-coverage-cx4.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-corpmgr-coverage-cx4.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-cx4-votemanager.md](service-audit/handoff-cx4-votemanager.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-cx4-votemanager.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-facwarmgr-coverage-cx3.md](service-audit/handoff-facwarmgr-coverage-cx3.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-facwarmgr-coverage-cx3.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [sweep-comments-eve-corporations.md](service-audit/sweep-comments-eve-corporations.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/sweep-comments-eve-corporations.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [sweep-security-eve-corporations.md](service-audit/sweep-security-eve-corporations.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/sweep-security-eve-corporations.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [alliance-route-fix.md](service-response-audit/alliance-route-fix.md#L1) | 1 | 0 | 1 | 0 | 0 | [根拠・test/commit](service-response-audit/alliance-route-fix.md#L18)。誤検知とした件数。判定根拠は原記録を参照。 |
| [corporations.md](service-response-audit/corporations.md#L1) | 4 | 1 | 0 | 3 | 0 | [根拠・test/commit](service-response-audit/corporations.md#L9)。記録時点の未解決/保留を計上。状態根拠と後続状況は末尾一覧を参照。 |
| [medal-rpc-coverage-01317-01323-handoff.md](service-response-audit/medal-rpc-coverage-01317-01323-handoff.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/medal-rpc-coverage-01317-01323-handoff.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| **小計: 法人・契約** | **30** | **20** | **6** | **4** | **0** | 計数可能 10 記録、NC 12 記録。 |

### 構造物・POS

| 記録 | 所見数 | 修正確認済（test実行） | 誤検知 | 未解決/保留 | 修正記載・test未確認 | 根拠・備考 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| [audit-fixes-station.md](audit-fixes-station.md#L1) | 3 | 3 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-station.md#L33)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-structures.md](audit-fixes-structures.md#L1) | 5 | 5 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-structures.md#L43)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-structures-pos.md](deepfix-structures-pos.md#L1) | 4 | 4 | 0 | 0 | 0 | [根拠・test/commit](deepfix-structures-pos.md#L57)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [DEEPCHECK_station.md](service-audit/DEEPCHECK_station.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_station.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_structureCompressionMgr.md](service-audit/DEEPCHECK_structureCompressionMgr.md#L1) | 10 | 0 | 10 | 0 | 0 | [根拠・test/commit](service-audit/DEEPCHECK_structureCompressionMgr.md#L19)。誤検知とした件数。判定根拠は原記録を参照。 |
| [DEEPCHECK_structureDirectory.md](service-audit/DEEPCHECK_structureDirectory.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_structureDirectory.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_structureSettings.md](service-audit/DEEPCHECK_structureSettings.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_structureSettings.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [deepfix-structure-fuel-services.md](service-audit/deepfix-structure-fuel-services.md#L1) | 2 | 1 | 0 | 1 | 0 | [根拠・test/commit](service-audit/deepfix-structure-fuel-services.md#L20)。記録時点の未解決/保留を計上。状態根拠と後続状況は末尾一覧を参照。 |
| [deepfix-structures.md](service-audit/deepfix-structures.md#L1) | 2 | 1 | 0 | 1 | 0 | [根拠・test/commit](service-audit/deepfix-structures.md#L25)。記録時点の未解決/保留を計上。状態根拠と後続状況は末尾一覧を参照。 |
| [handoff-essMgr-coverage-cx4.md](service-audit/handoff-essMgr-coverage-cx4.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-essMgr-coverage-cx4.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-structure-services-cx5.md](service-audit/handoff-structure-services-cx5.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-structure-services-cx5.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-structureCompressionMgr-cx6.md](service-audit/handoff-structureCompressionMgr-cx6.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-structureCompressionMgr-cx6.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-structureControl-cx5.md](service-audit/handoff-structureControl-cx5.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-structureControl-cx5.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-structureDeployment-cx6.md](service-audit/handoff-structureDeployment-cx6.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-structureDeployment-cx6.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| **小計: 構造物・POS** | **26** | **14** | **10** | **2** | **0** | 計数可能 6 記録、NC 8 記録。 |

### 惑星・産業

| 記録 | 所見数 | 修正確認済（test実行） | 誤検知 | 未解決/保留 | 修正記載・test未確認 | 根拠・備考 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| [audit-fix-planet-lpstore.md](audit-fix-planet-lpstore.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](audit-fix-planet-lpstore.md#L28)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-planets.md](audit-fixes-planets.md#L1) | 5 | 5 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-planets.md#L13)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-industry.md](deepfix-industry.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](deepfix-industry.md#L25)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-planets-sov-warfare.md](deepfix-planets-sov-warfare.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](deepfix-planets-sov-warfare.md#L61)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [DEEPCHECK_industryMonitor.md](service-audit/DEEPCHECK_industryMonitor.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_industryMonitor.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_industryNotifications.md](service-audit/DEEPCHECK_industryNotifications.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_industryNotifications.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_planetMgr.md](service-audit/DEEPCHECK_planetMgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_planetMgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [deepfix-industry.md](service-audit/deepfix-industry.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-industry.md#L21)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-mining.md](service-audit/deepfix-mining.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-mining.md#L32)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [handoff-cx6-industry-scanmgr.md](service-audit/handoff-cx6-industry-scanmgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-cx6-industry-scanmgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| **小計: 惑星・産業** | **13** | **13** | **0** | **0** | **0** | 計数可能 6 記録、NC 4 記録。 |

### ミッション・エージェント

| 記録 | 所見数 | 修正確認済（test実行） | 誤検知 | 未解決/保留 | 修正記載・test未確認 | 根拠・備考 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| [audit-fixes-agents.md](audit-fixes-agents.md#L1) | 4 | 4 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-agents.md#L37)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-dungeons.md](audit-fixes-dungeons.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-dungeons.md#L30)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-missions-agents.md](deepfix-missions-agents.md#L1) | 5 | 5 | 0 | 0 | 0 | [根拠・test/commit](deepfix-missions-agents.md#L39)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [DEEPCHECK_agentMgr.md](service-audit/DEEPCHECK_agentMgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_agentMgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_dungeon.md](service-audit/DEEPCHECK_dungeon.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_dungeon.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_dungeonInstanceCacheMgr.md](service-audit/DEEPCHECK_dungeonInstanceCacheMgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_dungeonInstanceCacheMgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_insurance.md](service-audit/DEEPCHECK_insurance.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_insurance.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_journal.md](service-audit/DEEPCHECK_journal.md#L1) | 1 | 0 | 0 | 1 | 0 | [根拠・test/commit](service-audit/DEEPCHECK_journal.md#L47)。記録時点の未解決/保留を計上。状態根拠と後続状況は末尾一覧を参照。 |
| [DEEPCHECK_petitioner.md](service-audit/DEEPCHECK_petitioner.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_petitioner.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_taleMgr.md](service-audit/DEEPCHECK_taleMgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_taleMgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [deepfix-missions.md](service-audit/deepfix-missions.md#L1) | 3 | 3 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-missions.md#L31)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-npe-career.md](service-audit/deepfix-npe-career.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-npe-career.md#L16)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [sweep-comments-eve-agents.md](service-audit/sweep-comments-eve-agents.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/sweep-comments-eve-agents.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [dungeon-keeper-contract-handoff.md](service-response-audit/dungeon-keeper-contract-handoff.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/dungeon-keeper-contract-handoff.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [petitioner-rpc-host-coverage-02249-02274.md](service-response-audit/petitioner-rpc-host-coverage-02249-02274.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/petitioner-rpc-host-coverage-02249-02274.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| **小計: ミッション・エージェント** | **16** | **15** | **0** | **1** | **0** | 計数可能 6 記録、NC 9 記録。 |

### 艦隊・チャット・メール

| 記録 | 所見数 | 修正確認済（test実行） | 誤検知 | 未解決/保留 | 修正記載・test未確認 | 根拠・備考 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| [DEEPCHECK_XmppChatMgr.md](service-audit/DEEPCHECK_XmppChatMgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_XmppChatMgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_calendar.md](service-audit/DEEPCHECK_calendar.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_calendar.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_calendarMgr.md](service-audit/DEEPCHECK_calendarMgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_calendarMgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_mailMgr.md](service-audit/DEEPCHECK_mailMgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_mailMgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_sharedSettingsMgr.md](service-audit/DEEPCHECK_sharedSettingsMgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_sharedSettingsMgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [deepfix-calendar-petitions.md](service-audit/deepfix-calendar-petitions.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-calendar-petitions.md#L25)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-chatmail.md](service-audit/deepfix-chatmail.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-chatmail.md#L19)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-fleets and chat and mail and contacts.md](service-audit/deepfix-fleets%20and%20chat%20and%20mail%20and%20contacts.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-fleets%20and%20chat%20and%20mail%20and%20contacts.md#L40)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-killmails.md](service-audit/deepfix-killmails.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-audit/deepfix-killmails.md#L17)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [sweep-optimize-eve-calendar.md](service-audit/sweep-optimize-eve-calendar.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/sweep-optimize-eve-calendar.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [mail-host-rpc-01868-01894-handoff.md](service-response-audit/mail-host-rpc-01868-01894-handoff.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/mail-host-rpc-01868-01894-handoff.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| **小計: 艦隊・チャット・メール** | **5** | **5** | **0** | **0** | **0** | 計数可能 4 記録、NC 7 記録。 |

### 宇宙・SDE・データ

| 記録 | 所見数 | 修正確認済（test実行） | 誤検知 | 未解決/保留 | 修正記載・test未確認 | 根拠・備考 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| [audit-fixes-content.md](audit-fixes-content.md#L1) | 3 | 3 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-content.md#L43)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-data.md](audit-fixes-data.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-data.md#L50)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-sde.md](audit-fixes-sde.md#L1) | 0 | 0 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-sde.md#L20)。原記録は確認対象について所見0件と明記。 |
| [audit-fixes-universe.md](audit-fixes-universe.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-universe.md#L25)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [DEEPCHECK_ProjectDiscovery.md](service-audit/DEEPCHECK_ProjectDiscovery.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_ProjectDiscovery.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_abyssal_content_manager.md](service-audit/DEEPCHECK_abyssal_content_manager.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_abyssal_content_manager.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_map.md](service-audit/DEEPCHECK_map.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_map.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-RPC-01981-02006-map.md](service-audit/handoff-RPC-01981-02006-map.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-RPC-01981-02006-map.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-RPC-02032-02057-map.md](service-audit/handoff-RPC-02032-02057-map.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-RPC-02032-02057-map.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [sweep-comments-eve-content.md](service-audit/sweep-comments-eve-content.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/sweep-comments-eve-content.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [sweep-optimize-eve-content.md](service-audit/sweep-optimize-eve-content.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/sweep-optimize-eve-content.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| **小計: 宇宙・SDE・データ** | **6** | **6** | **0** | **0** | **0** | 計数可能 4 記録、NC 7 記録。 |

### Host・wire・marshal

| 記録 | 所見数 | 修正確認済（test実行） | 誤検知 | 未解決/保留 | 修正記載・test未確認 | 根拠・備考 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| [audit-fixes-host.md](audit-fixes-host.md#L1) | 3 | 3 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-host.md#L51)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-macho.md](audit-fixes-macho.md#L1) | 5 | 5 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-macho.md#L51)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-marshal.md](audit-fixes-marshal.md#L1) | 4 | 4 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-marshal.md#L47)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-readside.md](audit-fixes-readside.md#L1) | 3 | 3 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-readside.md#L44)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-services-core-hub-r15-revalidation.md](audit-fixes-services-core-hub-r15-revalidation.md#L1) | 3 | 3 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-services-core-hub-r15-revalidation.md#L32)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-services-core-hub.md](audit-fixes-services-core-hub.md#L1) | 3 | 3 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-services-core-hub.md#L53)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-verification-marshal-r14.md](audit-verification-marshal-r14.md#L1) | 4 | 4 | 0 | 0 | 0 | [根拠・test/commit](audit-verification-marshal-r14.md#L22)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [deepfix-wire-shapes-marshal.md](deepfix-wire-shapes-marshal.md#L1) | NC | NC | NC | NC | NC | [対象](deepfix-wire-shapes-marshal.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [CLIENT_CALL_MATRIX.csv](service-audit/CLIENT_CALL_MATRIX.csv#L1) | NC | NC | NC | NC | NC | [対象](service-audit/CLIENT_CALL_MATRIX.csv#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [CLIENT_VERIFIED.md](service-audit/CLIENT_VERIFIED.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/CLIENT_VERIFIED.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [CORE_PATH_RECHECK.md](service-audit/CORE_PATH_RECHECK.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/CORE_PATH_RECHECK.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_DB2.md](service-audit/DEEPCHECK_DB2.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_DB2.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_eventLog.md](service-audit/DEEPCHECK_eventLog.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_eventLog.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_machoNet.md](service-audit/DEEPCHECK_machoNet.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_machoNet.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [DEEPCHECK_sessionMgr.md](service-audit/DEEPCHECK_sessionMgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/DEEPCHECK_sessionMgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [EVEJS_261028_MIGRATION.md](service-audit/EVEJS_261028_MIGRATION.md#L1) | 1 | 0 | 0 | 1 | 0 | [根拠・test/commit](service-audit/EVEJS_261028_MIGRATION.md#L43)。記録時点の未解決/保留を計上。状態根拠と後続状況は末尾一覧を参照。 |
| [EVE_RS_COVERAGE.csv](service-audit/EVE_RS_COVERAGE.csv#L1) | NC | NC | NC | NC | NC | [対象](service-audit/EVE_RS_COVERAGE.csv#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [HANDOFF-RPC-02805-02816.md](service-audit/HANDOFF-RPC-02805-02816.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/HANDOFF-RPC-02805-02816.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [HANDOFF.md](service-audit/HANDOFF.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/HANDOFF.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [HANDOFF_CX4_RESCUE.md](service-audit/HANDOFF_CX4_RESCUE.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/HANDOFF_CX4_RESCUE.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-RPC-00668-00923-client.md](service-audit/handoff-RPC-00668-00923-client.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-RPC-00668-00923-client.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-RPC-01049-01113-charMgr.md](service-audit/handoff-RPC-01049-01113-charMgr.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-RPC-01049-01113-charMgr.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-RPC-01379-01430-host.md](service-audit/handoff-RPC-01379-01430-host.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-RPC-01379-01430-host.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-RPC-01431-01455.md](service-audit/handoff-RPC-01431-01455.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-RPC-01431-01455.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-RPC-01483-01519.md](service-audit/handoff-RPC-01483-01519.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-RPC-01483-01519.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-RPC-02224-02248-petitioner-host.md](service-audit/handoff-RPC-02224-02248-petitioner-host.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-RPC-02224-02248-petitioner-host.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-RPC-02231-02340-client.md](service-audit/handoff-RPC-02231-02340-client.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-RPC-02231-02340-client.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-RPC-02258-02848-client.md](service-audit/handoff-RPC-02258-02848-client.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-RPC-02258-02848-client.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-RPC-02618-02711-client.md](service-audit/handoff-RPC-02618-02711-client.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-RPC-02618-02711-client.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-RPC-03035-03055-host.md](service-audit/handoff-RPC-03035-03055-host.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-RPC-03035-03055-host.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-coverage-services-cx4.md](service-audit/handoff-coverage-services-cx4.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-coverage-services-cx4.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-cx5-service-coverage.md](service-audit/handoff-cx5-service-coverage.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-cx5-service-coverage.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-project-discovery-coverage-cx3.md](service-audit/handoff-project-discovery-coverage-cx3.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-project-discovery-coverage-cx3.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-project-discovery-local-verification.md](service-audit/handoff-project-discovery-local-verification.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-project-discovery-local-verification.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-public-relocation.md](service-audit/handoff-public-relocation.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-public-relocation.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-raffle-proxy-implementation.md](service-audit/handoff-raffle-proxy-implementation.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-raffle-proxy-implementation.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-rescue-cx3.md](service-audit/handoff-rescue-cx3.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-rescue-cx3.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-rpc-01468-01513-cx3.md](service-audit/handoff-rpc-01468-01513-cx3.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-rpc-01468-01513-cx3.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-rpc-host-coverage-02489-03003.md](service-audit/handoff-rpc-host-coverage-02489-03003.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-rpc-host-coverage-02489-03003.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-rpc-host-coverage-cx5.md](service-audit/handoff-rpc-host-coverage-cx5.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-rpc-host-coverage-cx5.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-service-audit-cx3.md](service-audit/handoff-service-audit-cx3.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-service-audit-cx3.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-sovMgr-coverage-cx4.md](service-audit/handoff-sovMgr-coverage-cx4.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-sovMgr-coverage-cx4.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-tourney-current.md](service-audit/handoff-tourney-current.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/handoff-tourney-current.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [tourneyMgr-space-handoff.md](service-audit/tourneyMgr-space-handoff.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/tourneyMgr-space-handoff.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [CLIENT_VERIFIED.md](service-response-audit/CLIENT_VERIFIED.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/CLIENT_VERIFIED.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [README.md](service-response-audit/README.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/README.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [agent-local-collection-handoff.md](service-response-audit/agent-local-collection-handoff.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/agent-local-collection-handoff.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [core-a-f.md](service-response-audit/core-a-f.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/core-a-f.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [evejs-only-responses.md](service-response-audit/evejs-only-responses.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/evejs-only-responses.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [evejs-only-summary.md](service-response-audit/evejs-only-summary.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/evejs-only-summary.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-RPC-01693-01741-host.md](service-response-audit/handoff-RPC-01693-01741-host.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/handoff-RPC-01693-01741-host.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-RPC-02317-02380-host.md](service-response-audit/handoff-RPC-02317-02380-host.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/handoff-RPC-02317-02380-host.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-rpc-host-coverage-01956-01980.md](service-response-audit/handoff-rpc-host-coverage-01956-01980.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/handoff-rpc-host-coverage-01956-01980.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-rpc-host-coverage-01981-02006-cx3.md](service-response-audit/handoff-rpc-host-coverage-01981-02006-cx3.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/handoff-rpc-host-coverage-01981-02006-cx3.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-rpc-host-coverage-cx12.md](service-response-audit/handoff-rpc-host-coverage-cx12.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/handoff-rpc-host-coverage-cx12.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [handoff-rpc-host-coverage-cx13.md](service-response-audit/handoff-rpc-host-coverage-cx13.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/handoff-rpc-host-coverage-cx13.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [host-native-services.md](service-response-audit/host-native-services.md#L1) | 5 | 0 | 0 | 3 | 2 | [根拠・test/commit](service-response-audit/host-native-services.md#L199)。未解決/保留および未testの内訳は数列の通り。判定・検証根拠は原記録を参照。 |
| [host-protobuf.md](service-response-audit/host-protobuf.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/host-protobuf.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [rpc-00741-handoff.md](service-response-audit/rpc-00741-handoff.md#L1) | 1 | 0 | 1 | 0 | 0 | [根拠・test/commit](service-response-audit/rpc-00741-handoff.md#L9)。誤検知とした件数。判定根拠は原記録を参照。 |
| [rpc-config-01179-01194-handoff.md](service-response-audit/rpc-config-01179-01194-handoff.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/rpc-config-01179-01194-handoff.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [rpc-host-coverage-00877-01239-handoff.md](service-response-audit/rpc-host-coverage-00877-01239-handoff.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/rpc-host-coverage-00877-01239-handoff.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [rpc-host-coverage-00949-01173-handoff.md](service-response-audit/rpc-host-coverage-00949-01173-handoff.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/rpc-host-coverage-00949-01173-handoff.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [rpc-host-coverage-01355-01389-handoff.md](service-response-audit/rpc-host-coverage-01355-01389-handoff.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/rpc-host-coverage-01355-01389-handoff.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [rpc-host-coverage-01520-01544-handoff.md](service-response-audit/rpc-host-coverage-01520-01544-handoff.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/rpc-host-coverage-01520-01544-handoff.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [rpc-host-coverage-01725-02804-handoff.md](service-response-audit/rpc-host-coverage-01725-02804-handoff.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/rpc-host-coverage-01725-02804-handoff.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [rpc-host-coverage-cx5.md](service-response-audit/rpc-host-coverage-cx5.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/rpc-host-coverage-cx5.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [rpc-host-coverage-cx8.md](service-response-audit/rpc-host-coverage-cx8.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/rpc-host-coverage-cx8.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [runtime-comparison.md](service-response-audit/runtime-comparison.md#L1) | NC | NC | NC | NC | NC | [対象](service-response-audit/runtime-comparison.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [slash-catalog-fix.md](service-response-audit/slash-catalog-fix.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](service-response-audit/slash-catalog-fix.md#L40)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| **小計: Host・wire・marshal** | **33** | **26** | **1** | **4** | **2** | 計数可能 11 記録、NC 58 記録。 |

### scheduler・イベント・基盤

| 記録 | 所見数 | 修正確認済（test実行） | 誤検知 | 未解決/保留 | 修正記載・test未確認 | 根拠・備考 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| [audit-fixes-events.md](audit-fixes-events.md#L1) | 2 | 2 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-events.md#L27)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [audit-fixes-scheduler.md](audit-fixes-scheduler.md#L1) | 1 | 1 | 0 | 0 | 0 | [根拠・test/commit](audit-fixes-scheduler.md#L40)。修正確認済（test実行）の件数。test名・commitが記録されていれば根拠節に記載。 |
| [ci-red-1475-1477.md](service-audit/ci-red-1475-1477.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/ci-red-1475-1477.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [ci-red-1478-rust.md](service-audit/ci-red-1478-rust.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/ci-red-1478-rust.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| [ci-red-1479.md](service-audit/ci-red-1479.md#L1) | NC | NC | NC | NC | NC | [対象](service-audit/ci-red-1479.md#L1)。有限の所見一覧がないためNC。候補件数は不具合数に換算しない。 |
| **小計: scheduler・イベント・基盤** | **3** | **3** | **0** | **0** | **0** | 計数可能 2 記録、NC 3 記録。 |

## 全体合計

| 対象 | 全記録 | 計数可能 | NC | 所見数 | 修正確認済（test実行） | 誤検知 | 未解決/保留 | 修正記載・test未確認 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 全分野 | 296 | 140 | 156 | 374 | 310 | 28 | 32 | 4 |

NCには coverage / call matrix を含む。`EVE_RS_COVERAGE.csv` の row 状態、`core-a-f.md` の旧44 export分類、`evejs-only-responses.md` の476 named-export候補・182 gateway候補は、それぞれ明記の通り不具合件数ではない。

## 未解決・保留として数えた項目

以下は各記録に明記された未解決・保留項目。上表で件数に含めたものと、有限な所見一覧がなく件数に含めていない補足経路を区別した。後続記録で解消された履歴項目は、その旨を併記した。

- [audit-fixes-core.md](audit-fixes-core.md#L28): P2 `communityTrackMgr` の本番の進捗生成元なし（明示 open）。
- [audit-fixes-dogma.md](audit-fixes-dogma.md#L41): Starbase `AddTargetOBO` / `RemoveTargetOBO` の実装不足（確認済み・未修正）。
- [audit-fixes-factionwarfare.md](audit-fixes-factionwarfare.md#L49): 脆弱化システムの占領について、Control Bunkerの権威あるID・位置・占領処理への接続が不足。
- [audit-fixes-agents.md](audit-fixes-agents.md#L21): SDE mission pool からofferを作るlive pathなし。これは4件の修正済み所見とは別に残る未完了経路で、件数には含めていない。
- [DEEPCHECK_facWarMgr.md](service-audit/DEEPCHECK_facWarMgr.md#L64): Control BunkerのID取得元が不足。上記FW所見と同じ主題を別記録として計上。
- [DEEPCHECK_beyonce.md](service-audit/DEEPCHECK_beyonce.md#L70): `FILAMENT_SPOOLUP` と `JumpThroughRandomFilamentGate` の component操作が未実装。監査全体も未完了。
- [DEEPCHECK_ship.md](service-audit/DEEPCHECK_ship.md#L22): `FitShips` が未登録。
- [DEEPCHECK_ship.md](service-audit/DEEPCHECK_ship.md#L27): `LaunchFromContainer` が未登録。
- [DEEPCHECK_ship.md](service-audit/DEEPCHECK_ship.md#L29): `ScoopToFleetHangar` が未登録。
- [DEEPCHECK_ship.md](service-audit/DEEPCHECK_ship.md#L31): `ScoopToInfrastructureHold` が未登録。
- [DEEPCHECK_ship.md](service-audit/DEEPCHECK_ship.md#L32): `ScoopToMobileDepotHold` が未登録。
- [DEEPCHECK_ship.md](service-audit/DEEPCHECK_ship.md#L36): `BoardStoredShip` が未登録。
- [DEEPCHECK_ship.md](service-audit/DEEPCHECK_ship.md#L43): `SetShipPassword` が未登録。
- [DEEPCHECK_journal.md](service-audit/DEEPCHECK_journal.md#L47): `GetMyEscalatingPathDetails` の 本番providerなし。EveJSもempty rowsetなので、実データの見え方に差があるかは未確認。
- [EVEJS_261028_MIGRATION.md](service-audit/EVEJS_261028_MIGRATION.md#L49): Ansiblex `CmdJumpThroughStructureStargate` のHostの移動経路に未接続。
- [deepfix-jump-drives-cyno.md](service-audit/deepfix-jump-drives-cyno.md#L48): 別の作業記録も同じAnsiblex移動経路の未解決を記載。
- [corporations.md](service-response-audit/corporations.md#L20): `warStatisticMgr` のbound report provider/methodが未実装。CharPayBillと戦争交渉は当時未解決だったが、後続記録で解消済み。
- [crate-fitting-fix.md](service-response-audit/crate-fitting-fix.md#L22): skill/fitting報酬の再起動後の保持、DB commit結果が不明な場合の一度限りの処理記録、旧LegacyGameStoreのfitting移行。
- [crate-skill-retry-fix.md](service-response-audit/crate-skill-retry-fix.md#L75): skill-bundle報酬待ち状態の永続化候補。
- [host-native-services.md](service-response-audit/host-native-services.md#L206): 惑星編集RPC一式、industry material monitorの更新provider、GetPlanetsForCharの状態mergeが未完了と明記（この記録内で3件）。
- [deepfix-structure-fuel-services.md](service-audit/deepfix-structure-fuel-services.md#L17): fuel切れ後に`fitting.module_state`を同期する未解決差分。
- [deepfix-structures-pos.md](deepfix-structures-pos.md#L53): POS付属module/effect/forcefieldと燃料切れ後のcleanupは検証範囲に未解決部分がある。
- [deepfix-structures-pos.md](deepfix-structures-pos.md#L54): 稼働中のsim ballとwarp-in pointを含む配置検証が未完了。

### 後続記録で解消された項目

- [audit-fixes-corporations.md L15](audit-fixes-corporations.md#L15) と [DEEPCHECK_billMgr.md L20](service-audit/DEEPCHECK_billMgr.md#L20): `CharPayBill` は当時は未確認・未接続。後続の [deepfix-contracts-trade-wallet.md L5](deepfix-contracts-trade-wallet.md#L5) が個人請求の永続化と支払いを追加。
- [corporations.md L20](service-response-audit/corporations.md#L20): war negotiationは当時未解決、後続の [audit-fixes-corporations.md L5](audit-fixes-corporations.md#L5) で実装。
- [deepfix-fleet-finder.md L15](service-audit/deepfix-fleet-finder.md#L15) と L19 のrate制限とboost通知は、[deepfix-fleets-mail-calendar.md L7](deepfix-fleets-mail-calendar.md#L7) の後続修正で解消。
- [deepfix-structures.md L7](service-audit/deepfix-structures.md#L7) の `GetControlTowers` provider不足は [deepfix-structures-pos.md L31](deepfix-structures-pos.md#L31) で修正。
- [HANDOFF_CX4_RESCUE.md L12](service-audit/HANDOFF_CX4_RESCUE.md#L12) の `/reportdesync` は、同じフォルダー内の [EVE_RS_COVERAGE.csv L1809](service-audit/EVE_RS_COVERAGE.csv#L1809) が `IMPLEMENTED-TESTED` と記録。

## 計数外の記録内容

- `NC` は「不具合なし」と同義ではない。有限なfinding listがないため数を割り当てていないという意味で、個別記録にある未実装・未検証の記載は元の場所を参照する。
- `core-a-f.md` は旧EveJS-onlyの44行を「未実装33、別service対応3、legacyで未確認6、実装済み2」と分類するが、本文が明確に全未実装行を機能不全とは扱っていない。`evejs-only-responses.md` も476件のexport候補や182件のgateway候補を所見総数としていないため、NCとした。
- 既存CIの失敗・skip、GUIクライアント未実施、EveJS実行未実施は、所見の処置状態とは別情報である。各記録の実行結果はリンク先で確認する。
