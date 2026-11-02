# PLEX・Omega/Alpha・ストア決済の深掘り handoff

更新: 2026-11-02（worktree cx8）

## 進捗

Redeem token の有効性、SP報酬、ブループリント配送、PLEX reverse-redeem を直し、DB付きHostテストで確認した。ストア購入の残作業があるため、この監査全体はまだ完了扱いにしない。

既知の残作業: dotnet/src/Eve.Host/Rpc/RewardRpc.cs:96-97 は StoreOffersService にカタログReaderだけを渡して決済providerを接続していない。StoreOffersService.cs:56-57 はproviderがないと None を返すため、クライアントに canPurchase=true と出ているPLX商品も購入できない。EveJS 261028.1 はV24の storeManager.buy_offer を storeFulfillment.js:186-252 からPLX精算へ進める。次はPLX建てOmega/MCT商品のみ実決済を接続し、USD等の外部決済は実providerがない限り失敗を維持する。

## V24.01 全クライアント経路

- Redeem画面は現在地かhome stationを選び、ClaimRedeemTokens(charID, token list, useHomeStation) を呼ぶ（eve/client/script/ui/shared/redeem/redeemItemsContainer.py:343-354）。サービスは返却された errors を msg/dict で表示し、成功後にキューを更新して OnTokensRedeemed(characterID, stationID, tokens, numItemsCreated) を送る（eve/client/script/ui/services/redeemsvc.py:98-133）。
- MCT soulbound tokenは通常claimから除き、Omegaのとき ActivateSoulboundMultiTraining(charID, tokens, trainingID, useHomeStation) に送る。Alphaでは「Requires Omega」を表示する（redeemsvc.py:197-205）。確認後の直接RPC経路は activateMultiTraining.py:226-232。
- OnItemChange はinventory cacheが受けてitem row/attributeを更新する（eve/client/script/environment/invCache.py:117-120, 228-303）。SP残高通知は OnFreeSkillPointsChanged(newFreeSkillPoints) を受け、技能一覧・skill queue・SP購入画面が読む（eve/client/script/ui/services/skillsvc.py:844-845, 898-899; skillQueuePanelNew.py:623-624; applySkillPointsWindow.py:211-212; skillsCataloguePanel.py:157-158）。
- ReverseRedeemはメニューからphysical itemIDを送り、最後にqueueを再取得する（eve/client/script/ui/services/menusvc.py:375-376; redeemsvc.py:66-74）。返却・拒否はUserErrorとしてUIへ流れる。
- Omega表示は GetCloneGrade / GetSubscriptionTime を読む（eve/client/script/ui/services/omega/client/clone_grade_svc.py:124-138; plexPanel.py:145-161）。時間更新イベント OnSubscriptionTimeUpdateServer はPLEX panelが消費する（plexPanel.py:24-25, 159-161）。
- Store購入は BuyOffer(offer,currency,qty,toCharacterID,message) を呼び、結果のtruthinessだけで成功/失敗panelを決める（eve/client/script/ui/view/aurumstore/vgsUiController.py:128-151; vgsDetailContainer.py:119-122）。現行Hostの None は成功扱いにならない。

## 直した欠陥と根拠

1. Available=false・期限切れtokenが通常claimおよび直接MCT RPCで消費されていた。Hostは両入口でavailability/expiryを確認する。通常claimはtokenごとに RedeemTokenNotFound rowを返し、clientが errors[].msg/dict を表示する。直接MCT RPCは無効tokenを消費せずslotも増やさない。EveJS 261028.1 userService.js:258-295, 780-817, 823-850 は読み取り時に除いたqueueからの通常動線を想定し、直接MCT handlerでは同じ検査をしていない。この防御差は直接RPCからの消費を塞ぐために記録した。
2. Type 49810の「100,000 Skill Points」はSDE group 526 / category 17。名前から100,000を読みquantity倍率し、同じDB transactionでfree SPを加算、itemは作らず OnFreeSkillPointsChanged を返した。SDE英語名を ItemTypeCatalog が提供する。EveJS map userService.js:83-145 はtype 49810を100,000としており、処理は 388-390, 556-590, 823-850。
3. Blueprint type 681（SDE group 104 / category 9）はrunsがあればcopyとして、runs/ME/TEを引き継ぐ。Blueprint Original/ResearchのEVE University資料はコピーの有限runsと研究値を示す: [Blueprint Original](https://wiki.eveuniversity.org/Blueprint_Original), [Research](https://wiki.eveuniversity.org/Research)。EveJSの該当形は userService.js:972-1000。
4. ReverseRedeemをHostで永続化し、station/structure hangarのPLEX itemを消して元のstationに結びついたtokenを作成、OnItemChangeを応答後に送る。EVE University [Accounts](https://wiki.eveuniversity.org/Accounts) はreverse-redeem後のPLEXを元の場所で再redeemすると説明する。EveJS 261028.1 src/server/src/services/user/userService.js:1096-1099 は成功値だけを返す。差異は docs/reference/compat/DEVIATIONS.md に記録済み。既存の詳細メモ: docs/project/service-audit/deepfix-nesstore.md。

SDE確認値: type 49810=100,000 Skill Points, group 526; type 44992=PLEX, group 1875; type 681=Clone Grade Beta Blueprint, group 104; type 34133=Multiple Pilot Training Certificate, group 1301。EVE University [Accounts](https://wiki.eveuniversity.org/Accounts) はMCTがaccount-basedで30日単位、複数queueで訓練するにはOmegaが必要と説明する。

## 検証済み

- 変更前のfocused DBテストで、期限切れ・available=false token claimが2件とも受理され、direct MCT RPCでslotが2つ増える失敗を再現した。
- dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false -m:2: 成功、warning/error 0。
- eve_p8と tools/dev/db-lock.sh でNewEdenStoreHostTests、NewEdenEntitlementHostTests、UserSvcRedeemTokenPersistenceTests、UserSvcHostWireTests、UserSvcRpcHostWireTests、CompatibilityRpcServiceTests、SubscriptionTransitionsTests、LoginOnboardingSubscriptionRpcHostWireTestsを実行: 37 passed、0 failed、0 skipped。
- focused redeem/blueprint/SP/reverse-redeem 4件も成功。
- GUIの実V24 client確認はしていない。

## 継続時の注意

dotnet/src/Eve.Host/Persistence/NewEden/PersistenceNewEdenStoreRuntime.Part2.cs:387-474 がreverse-redeem配送、NewEdenStoreHostTests.cs:177-254 がそのDB wire regression。ストア決済の残作業は、成功・不足残高・unsupported currency・同一CallId再送・Host再起動後再送をDB付きfocusedテストで確かめてから別のgreen pushにする。
