# scanMgr deep check (EVE V24.01)

調査日: 2026-10-27

## 範囲と結論

Serenaで `/home/user/eve-logic` 全体を検索。clientからのremote entrypointは `sensorSuiteService.py:172` の `SignalTrackerRegister` と `scanSvc.py:114` の `GetSystemScanMgr`。返るbound managerの全呼び出しは次の9件だけだった: `GetFullState`, `RequestScans`, `DestroyProbe`, `ReconnectToLostProbes`, `RecoverProbes`, `QAOverrideProbeExpiry`, `SetActivityState`, `QAScanSites`, `ConeScan`。呼出し箇所は以下の各項目に記載。追加のclient service alias / monikerは見つからなかった。

`GetScanTargetID` は `eve/client` からの直接呼び出しなし。whole-source検索では共有server側 `explorationscanner/signalTracker.py:203,223,251` が `scanMgr` を呼ぶ。clientのnearest pathは `scanSvc.GetScanMan()` が返す同じbound manager (`scanSvc.py:110-115`)。

本変更でHostの登録、probe QA RPC、署名情報、signal tracker更新通知を接続した。Hostが供給するauthoritativeなstatic-site / structure site sourceは見つからず、`GetFullState` の3番目・4番目のmapは空のまま。EveJSは両方を構築するため、この2区分とそれらのtarget ID lookupは残る互換差分である。根拠のないsiteを作らないためmapを埋めていない。

## メソッド別

### GetSystemScanMgr
- Client: `scanSvc.py:110-115` が `RemoteSvc('scanMgr')` から一度取得して再利用する。
- Host: `DungeonRpc.cs` が `scanMgr` を登録し、`IncursionAnomalyService` の `GetSystemScanMgr` がsession内のbound object IDを再利用してPySubStructを返す。Host wire testはunbound service callから得たIDでbound callを行う。
- 引数/結果/状態: 引数なし。結果はbound proxy用PySubStruct。sessionのbound ID/stateを保持し、追加通知なし。EveJS: `scanMgrService.js:2319-2323`。

### SignalTrackerRegister
- Client: `sensorSuiteService.py:164-178`。引数なし、結果は使わず、`UserError('UnMachoDestination')` のみ捕捉する。
- Host: `SignalTrackerRegistrationProvider` が現session・character・systemを検証し、`AfterResponse` で `OnSignalTrackerFullState(charid, systemID, fullState)` を送信。5秒周期snapshot refreshから、anomaly/signature/structureのdelta通知を送る。Client consumerは `sensorSuiteService.py:501-541`。
- state/error: 登録はHostメモリ上のsession状態。通常のclient callで汎用failureを返さない。EveJS: `scanMgrService.js:2325-2352`。

### GetFullState
- Client: `sensorSuiteService.py:717-721` はstateをfull refreshに使い、`sensorSuiteService.py:501-528` は `[anomalies, signatures, staticSites, structures]` の順に4 mapとして読む。
- Host: 4つのPyDictを返す。anomaly dataはread provider、signaturesはactive cosmic-site provider由来。staticSitesとstructuresは現在空。mapの順序と型はwire testで確認。
- read-only、追加通知なし。EveJSも4つのmapを同順に返す (`scanMgrService.js:716-726`)。EveJSの各map builderは `:632-654`。

### RequestScans(probes)
- Client: `scanSvc.py:194-197` はprobe mapまたはNoneをpositionalで渡し、戻り値は使わずclient stateをscanning/movingにする。Void-space拒否はRPC前のclient UserError (`:185-197`)。
- Host: bound routeから `SpaceProbeScans`。None/DictとPyInt/PyLong IDを読み、scan state・positionをprobe storeに保存し、durable start/stop jobを作る。移動/scan時は `OnProbeWarpStart`, `OnProbeWarpEnd`, `OnSystemScanStarted`, `OnSystemScanStopped` を送る。
- 返値None。EveJS: `scanMgrService.js:2592-2776`。Host testはNoneとDictのwire引数を確認。

### DestroyProbe(probeID)
- Client: `scanSvc.py:265` はtracker callbackとして呼び、`scanSvc.py:118-123` の `OnRemoveProbe` がprobeを消す。
- Host: int/long IDを実probe recallに渡し、成功時 `OnRemoveProbe` を送る。返値None、永続probe stateを削除。wire testはcharacter付きrouteでrecall callbackを確認。
- EveJS: `scanMgrService.js:2806-2827`。追加のclient-visible UserErrorなし。

### ReconnectToLostProbes()
- Client: `scanSvc.py:278-286`、引数/返値なし。capsule/reconnect cooldownのUserErrorはRPC前にclientで判定。
- Host: sessionのsystem内のprobeを再通知し、probe idle通知を送る。永続stateを再読込する。返値None。EveJS: `scanMgrService.js:2778-2804`。

### RecoverProbes(probeIDs)
- Client: `scanSvc.py:323-342` はID listをpositionalで送り、`probescanning/probeTracker.py:190-196` は返る成功ID listをmovingにし、不足分があれば `NotAllProbesReturnedSuccessfully` をclient側で出す。
- Host: list/tupleのint/long IDを処理し、実際に回収できたIDだけPyListで返し、回収probeを永続storeから削除して `OnRemoveProbe` を送る。wire testは返値がID listであることを確認。
- EveJS: `scanMgrService.js:2829-2859`。

### QAOverrideProbeExpiry(duration)
- Client: `scanSvc.py:348-353`。durationをpositionalで渡し、RPC後にsim-time expiryを自分で更新。返値なし。
- Host: numeric int/long/realのmillisecond durationを、character + current systemの保存probeに適用。NoneはEveJS defaultの0として扱う。返値None、独自通知なし。EveJS: `scanMgrService.js:2881-2893`。
- Host testはlong durationとNoneのwire routeを確認。DB query/saveとrestart後の値はこのjobのDB禁止により実行確認していない。

### SetActivityState(probeIDs, active)
- Client: `scanSvc.py:425-433` はID listとboolをpositionalで送り、client側trackerも更新する。
- Host: list/tupleのint/long/real IDsとboolを読み、idle/inactive状態をprobe storeへ保存。返値None。Clientが即時更新するため追加notificationなし。EveJS: `scanMgrService.js:2861-2879`。

### QAScanSites(*siteIDs)
- Client: `scanSvc.py:783-786` はvarargsを一つのtupleとして送る。返るtruthy listをprobe scannerへinjectする。
- Host: tuple/listをflattenし、数値IDまたはsite codeでauthoritative active signaturesを絞り、EveJSと同じcertainty 1 result rowsのPyListを返す。状態変更・notificationなし。EveJS: `scanMgrService.js:2895-2939`, `probeScanRuntime.js:873-890`, result row `:797-835`。
- Wire testはnested tuple形状をHost routeへ渡し、method/arg記録を確認。

### ConeScan(angle, range, *direction)
- Client: `directionalScanSvc.py:46` は5 positional numericsを渡し、result rowsを使う。`:44-45` はUserErrorを捕捉して再送出する。
- Host: `DungeonRpc.cs` から `SpaceDirectionalScans`。通常はdirectional target rowsを返す。Abyssal内ではEveJSと同じ `CustomNotify` UserErrorと文面を返す。永続stateや後続notificationなし。
- EveJS: `scanMgrService.js:2470-2590`。wire testはbound routeの5数値を確認。実clientのGUI結果は未確認。

### GetScanTargetID(siteID, targetSeedID=...)
- Whole-source上の共有server collaboratorのみ: `explorationscanner/signalTracker.py:203,223` はkeyword `targetSeedID`、`:251` は省略形を使う。
- Host: `SpaceProbeScans` はsiteIDをpositional、targetSeedIDをkwargsから読み、None/missing seedをsiteIDへ戻す。active cosmic-site codeを返し、未登録siteはEveJS互換deterministic encoderを使う。返値はPython string (wire decode後PyBytes)、状態変更・通知なし。Host wire testはint/long site ID、keyword、machoVersion、Noneを確認。
- EveJSはsiteIDとtargetSeedIDを正規化しsignature/anomaly/static/structureを探索してからfallbackする (`scanMgrService.js:2459-2468`, `signatureRuntime.js:333-355`)。Hostのstatic-site/structure source不在によりその2種の既知コード探索は未対応。

## 検証

- `dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --filter FullyQualifiedName~ScanMgr_`: 2 passed, 0 failed, 0 skipped。buildも成功。
- PostgreSQL、DB-backed persistence/restart、Host.Tests全体、graphical EVE clientは実行していない。DB接続は行っていない。
