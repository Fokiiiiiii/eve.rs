# 2026-11-17 main CI 赤の追跡

## 対象

基準コミット `c07d3c9acfd9e40dfaeea5bbd902a07157f1c634` の main CI run 1830 (`44012308329`) が赤だった。失敗した Rust の scene/bootstrap 期待値、Space と Skills の通知 fixture、dogma・fitting・undock・movement の headless 比較を同じ挙動として再現し、原因を分けて直した。

## 確認済みの根因と変更

### Destiny の束ね方と初回 bind

- EveJS は Destiny の action をtickの一つの履歴へ積み、deliver時に Observer ごと、またはGrid bubbleごとのbatchへ分ける (`server/src/space/destiny/batching/destinyHistory.js:3-44`)。Grid bubbleに追加されたshipは `PackagedAction(AddBalls2)` の後ろにmode/primeを同stampで続ける (`server/src/space/runtime/scene/visibility.js:653-676`)。V24.01は `DoDestinyUpdate` 内の `PackagedAction` を展開してから `FlushState` へ渡し、ひとつのupdateに複数stampがあればdesyncとして扱う (`eve/client/script/remote/michelle.py:482-511`)。
- そのため、既にbind済みのobserverに対するship追加とmovement primeを一つのbubble updateにまとめた。Rustでは `crates/eve-space/src/crowd/ships.rs:215-238` を変更し、`crowd` の回帰テストはaction順とwait flagを検査する。fresh undock中のpilot自身は別扱いで、EveJSは最初のbubble entryにprimeを付けず、primeの後のtickにSetStateを送る (`visibility.js:559-596`, `runtime/scene/presentation.js:723-787`)。
- station undockのcollision grace `leaving_station` がSetState bindより先に終了することがあるため、fresh undockの判定を `!jumped_in && !bound` にした (`crates/eve-space/src/scene/session.rs:306-360`)。V24はballpark未作成中のDestiny更新を捨て (`michelle.py:482-485`)、SetStateでfull stateとslimを再構築する (`michelle.py:968-1005`)。SetState後にだけ初期状態を要求するRust regressionは `npc/tests.rs` と `sim/tests.rs` にある。
- 最初のpush後のCI run 1831 (`44020477344`) では、さらに `scene::tests::a_ship_lining_up_for_warp_or_in_it_takes_no_orders` と `scene::tests::binding_a_ship_that_follows_restores_its_state_without_replaying_the_movement_command` が旧 `SetState + SetBall*` 列を期待して落ちた。EveJSはundockのSetStateを単独送信し、それ以前のprimeとSetStateがball/modeを持つと明記している (`runtime/scene/presentation.js:944-947`)。2 testを `SetState` 単独の期待に更新し、warp中とFollow中のego ball bytesがSetState bufferにあることを検証する (`crates/eve-space/src/scene/tests.rs:1470-1498,1832-1849`)。
- gate jumpのJumpInはEveJSが到着したshipのowner sessionを除外して他observerへbroadcastする (`server/src/space/runtime/spaceRuntime/stargateJump.js:241-249`)。Rustのjump arrival testはownerへJumpInを送らず、SetStateのfull stateを一つのDestiny updateで確認する形へ更新した。V24側で `effects.JumpIn` が登録されていることは `eve/client/script/environment/effects/Jump.py:617-625`、SetStateの状態再構築は `michelle.py:968-1005` で確認した。

### Space のtarget通知fixture

- `OnTarget` は `what, targetID, reason` を直接受け取る。V24 `eve/client/script/parklife/targetMgr.py:580-598` はOnTargetsの要素を展開してこの形で処理し、EveJSは `OnTarget` をdirect payloadで送る (`server/src/space/runtime/scene/targeting.js:230-249`)。Rustも `crates/eve-space/src/targeting/mod.rs:177-184` でdirect argsを出している。
- `HackingMgrServiceTests` と `SpaceServiceTests` のsimulation通知fixtureが古いMacho envelopeを誤って含めていたため、実際のdirect argsをmarshalするよう修正した。server handlerは変更していない。

### Skills の通知fixture

- EveJSは `OnServerSkillsChanged`、必要なqueue snapshot、必要なpaused通知をserverから送り、`OnSkillLevelsTrained` と `OnSkillsChanged` はclient側が前者からscatterすると明記している (`server/src/services/skills/training/skillQueueNotifications.js:246-284`)。V24も `skillsvc.py:159-170` で2つのclient-local eventをscatterし、`skillQueueSvc.py:749-765` でqueue saved/pausedを処理する。
- `/giveskill` testをsent件数だけの確認から通知名と宛先型の完全一致へ変更した。期待するwire通知は `OnServerSkillsChanged(charid)`, `OnNewSkillQueueSaved(charid)`, `OnSkillQueuePausedServer(clientID)`。これは現在のEveJS `skillQueueNotifications.js:278-284` およびC# `SkillNotifications.cs:37-52` と一致する。

### Dogma / fitting のcapture差

- EveJSのGetAllInfoではpilot自身のshipState rowは空のinstance stateであり、status rowの既定 `online` はfalse (`server/src/services/dogma/dogmaService/itemInfo.js:1437-1448`, `dogmaService.js:997-1005`)。V24はGetAllInfoを処理後、そのshipStateを `MakeShipActive` に渡す (`eve/client/script/environment/godma.py:2399-2432`)。古いcaptureのcharacter rowだけを現在のstatusへ写像した値で比較するようにした。
- eve.rsは全fitted moduleへ無条件でDogma attribute 18 (`Charge`) を加えていたが、EveJSはmoduleStateがある場合にshieldCharge/charge等のstateを出す (`dogmaService.js:1844-1873`)。moduleStateを持たないstarter moduleへ属性18を捏造しないよう、`DogmaInfo.ModuleAttributes` の無条件defaultを削除した。Dogma unit testはattribute 18の不在を明示的に検証する (`dotnet/tests/Eve.Services.Dogma.Tests/DogmaServiceTests.cs:114-121`)。ship自身のcapacitor attributeは `ShipAttributes` に残している (`DogmaInfo.cs:72-89`)。
- current EveJSは `OnServerBrainUpdated` を `charid` で送り、V24 handlerも `session.charid` のbrainとして処理する (`characterBrainRuntime.js:277-283`, `eve/client/script/dogma/clientDogmaLocation.py:1313-1328`)。Dogma notificationの属性変更値は、属性2、805、キャラクター自身の164-168などの整数例外以外をwire上のrealに変換する (`server/src/network/attributeChangeWireValues.js:3-26,66-95`)。V24 `clientDogmaLocation.py:310-345` はこれらの値を属性へ反映する。テストは古いcapture側だけに現行wire typeとidTypeを適用し、値・action・event順の比較は維持した。
- `tools/headless-client/tests/test_login_to_character.py` のnormalizerはこれら3点を現在のEveJS wire仕様へ揃える。`test_movement.py` と `test_undock.py` は、ballpark bind前にV24が捨てるowner Destiny updatesだけを双方から落とし、最初のSetState以降は従来どおり厳密に比較する (`test_undock.py:317-325,681-688`; V24の早期returnは `michelle.py:482-485`)。

## 実行した確認

- Rust: `cargo +1.99.0 fmt --all -- --check` 成功、`cargo +1.99.0 check --release -p eve-space -p eve-behaviors` 成功、`cargo +1.99.0 clippy --release -p eve-space --all-targets -- -D warnings` 成功、`cargo +1.99.0 build --release -p eve-net` 成功。変更したRust test 11件、CI対象の2船目undock 1件、run 1831で新たに見つかったscene test 2件を個別実行し、計14 passed / 0 failed。
- .NET Release build: Dogma、Space、Skills各test projectが0 warning / 0 error。DBは専用の `eve_p4`。
- .NET focused DB tests: DogmaServiceTests + HealthTests 20 passed / 0 skipped、HackingMgrServiceTests + SpaceServiceTests 193 passed / 0 skipped、GiveSkillTests 5 passed / 0 skipped。
- Python: 変更した3ファイルの `py_compile` 成功。実Hostに対しDogma race 1/2/4/8、Fitting race 1/2、Undock race 1/4、Movement race 1の計9 headless E2Eが成功。DB `eve_p4`、pinned SDEを使用した。quiet出力は9 dotsと`[100%]`、runner終了code 0 (`/tmp/cx4-ci-red-e2e-2026-11-17/`)。
- 以前のnightly skipped 10 scenarioの結果は全件成功、skip 0。各case・環境・logは [`live-skipped-scenarios.md`](live-skipped-scenarios.md) に記録済み。

## 未確認

- グラフィカル版V24.01 clientを起動しての操作は未実施。E2E確認はreal Hostとheadless clientによる。
- このCI追跡ではEveJS serverを新たに起動して同じ9ケースを直接再生していない。現在のEveJS実装、V24.01全client source内の受信callsite、および既存wire captureと照合した。
- run 1831はこの記録更新時点で終了待ちだった。修正した2 scene testを含む次のmain CI runの最終結果は未確認。
