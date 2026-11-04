# kiringMgr 全クライアント呼び出し・Host深掘り監査

対象は eve-logic V24.01 / build 3396210 全ツリーとCSVの12行。duplicated rowをまとめると9 methods。全callsiteはkiring/client/apigateway.py、serenity/eveguard/client/eveguard_service.py、characterSelectionUtils.pyにある。

## 登録・共通wire

CoreServices.cs:60はKiringMgrServiceを登録する。client callsは全てpositional argsで、machoVersion=1はtransport kwargs。文字列はPyStr、aid/goods IDs/counts/modeはclientの数値、headersはdictionary。結果はauth tuple / config dict / integer / bool / None / UserErrorとなる。

## 各methodとclient consumer

| メソッド | 全call / consumer | Host behavior・exact error |
|---|---|---|
| GetKiringConfiguration | apigateway.py:192-203。mode, client_id, endpoints.mpay/billing/redirect_uri, channels.login/pay/appを各attributeへ保存。 | nested dict。modeはPyInt、endpoint/channel/client fieldsはstr。 |
| PerformKiringServerSideAuthenticationFromCode | apigateway.py:256。3 strings (code,device_id,unique_client_id)、5-tupleをaid,user_id,token,account,nameへunpackし設定保存 (:257-263)。 | KIRING_AUTH_UNAVAILABLE UserError。偽auth情報なし。 |
| GetSAuthAid | apigateway.py:282。user_id, account, deviceIDの3 strings、replyをintへ変換。 | KIRING_AUTH_UNAVAILABLE UserError。 |
| GetAntiAddictionCode | apigateway.py:379-380。netease_account string。no-token時はclient local ANTI_ADDICTION_PASS_CODE、otherwise remote codeを返す。 | PyInt(201)。 |
| GetEveGuardServerAddress | eveguard_service.py:89。replyをremote addressへ、None/emptyならlocal eveGuardServerUrlへfallback。 | whitespace設定はNone、設定値はtrim済みPyStr。 |
| IsMonochromeStyleEnabled | characterSelectionUtils.py:72-75。Chinese serverに限ってreplyをstyle boolに使う。 | PyBool。 |
| ShouldKickCheater | eveguard_service.py:197-201。Trueならclientがcheaterをquitさせる。remote errorはfalseとして扱う。 | configurable PyBool。 |
| PlaceKiringOrder | apigateway.py:388-404。13 positional values: device/client IDs, aid, goodsid/count, user/netease IDs, URLs, three channels, headers map, app_mode。Noneならreturn、order IDならHTTP payment initとwatcherを開始。 | KIRING_PURCHASE_UNAVAILABLE UserError。支払order成功を作らない。 |
| ActivateRedeemingCode | apigateway.py:425。code, account, aid, user_id, name, current IP, deviceID positional; raw result is returned to redeem caller. | PyBool.False; codeを受理したふりはしない。 |

## EveJS比較、state、notifications

EveJS v0.12.9 server/src/services/newEdenStore/kiringMgrService.jsは同じcall shapesを持つが、auth tuple/aid/order ID/redeem trueをplaceholder valuesとして返す。Hostはauthとpurchaseのexact unavailable UserErrorsへfail closedし、redeem false。意図的なcompatibility deviationはdocs/reference/compat/DEVIATIONS.mdのKiring entryに記録されている。Anti-addiction code 201、config map、settings、EveGuard addressを照合した。これらのRPCはHost wallet/account persistenceやclient notificationsを生まない。Config/settingsはHost optionsから読む。

## Host wire check

KiringMgrDeepCheckHostWireTests.cs:Kiring_manager_calls_keep_client_arguments_and_fail_closed_replies_over_host_wireは実Host packet loopで9 methods、machoVersion kwarg除去、nested dict/types、13 positional argsとexact UserError IDsを検査する。結果: 1 passed、0 skipped。実外部Kiring/EveGuard service、実GUI payment/refund/redeem flowは使っていない。


## 引数型
全9 methodsはclient sourceの順でpositional argumentsを送る。GetKiringConfiguration, GetEveGuardServerAddress, IsMonochromeStyleEnabled, ShouldKickCheaterは引数なし。AuthFromCodeは3 strings、GetSAuthAidは3 strings、AntiAddictionはaccount string、PlaceKiringOrderは13 values (strings, integral IDs/counts, headers dict, numeric app_mode)、ActivateRedeemingCodeはcode/account/user/name/IP/device stringsとaid integer。machoVersion=1のみtransport kwargs。
