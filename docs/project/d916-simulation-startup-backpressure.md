# d916d77 simulation startup backpressure

## 原因

- main CI run 1683（`d916d77b670d`）のlive undock walkが失敗した。Host logには `the simulation link: outbound queue is full (64 frames)` があり、undock RPCは `CustomNotify` を返し、後続のballpark / dock通知が届かなかった。
- 最初の修正ではESS配置をbounded simulation queueの空きまで待つようにしたが、run 1684では3,285個のnullsec ESSを起動時に全て送るため、Host listenerが60秒の接続期限に間に合わなかった。起動時の全系一括配置が回帰の原因だった。

## 現在の動作

- main commit `7988cf92c` で、ESS配置を系へのentry前に移した。`SpaceComposition` は `PrepareSolarSystemForEntryAsync` を `PlaceEssBallAsync` に接続する（`dotnet/src/Eve.Host/Composition/SpaceComposition.cs:337-338`）。`SpaceSessions` はentry前にこの準備callbackを呼ぶ（`dotnet/src/Eve.Services.Space/SpaceSessions.cs:524-528`）。login/undockは `SpaceSessions.Ships.cs:100`、relocationは `SpaceSessions.Relocation.cs:163,432`、gateは `SpaceSessions.Movement.cs:367`、wormholeは `SpaceWormholes.cs:198`、cynosural jumpは `SpaceSessions.CynoJump.cs:78,129,209`。
- `PlaceEssBallAsync` は宛先系の永続ESSを最大1件読み、存在しない場合はstructureを送らない。存在すればpinned SDEからgeometryと決定的なanchorを導き、entry commandより前に配置する（`dotnet/src/Eve.Host/Composition/EssComposition.cs:220-245`）。simulation clientは配置通知を送るためにoutbound queueが空くまで待つ（`dotnet/src/Eve.Space/SimClient.cs:1175`）。
- 先行commit `681246e13` の `SystemEntered` subscriptionはentry後の配置だったため、現在のentry前callbackに置き換わった。現在の全体説明は `docs/project/ess-startup-entry-placement.md` に記録した。

## 検証

- run 1685は最初のsystem-entry版の後にgreen。run 1686はentry前の現行実装を含み、必須job全て成功した。Host test 6 shards、Windows、server-stack/headless-client E2E（22.6分）を含む。
- 回帰テストは満杯のoutbound queue（`dotnet/tests/Eve.Space.Tests/SimClientTests.cs:910`）とsimulation entry前の準備順（`dotnet/tests/Eve.Services.Space.Tests/RelocationTests.cs:20`）を検証する。live headless ESS scenarioとclient期待値の根拠は `docs/project/ess-startup-entry-placement.md` に記録した。

## 未確認

- graphical V24.01 clientでは確認していない。CI run 1686はheadless clientを使った。
