# Abyssal Deadspace Reference Contract

**Status:** investigation handoff; this file records observed contracts and unresolved parity checks. It does not claim the feature is complete.

## Source pins and confidence

- V24.01 client logic: `Fokiiiiiii/eve-logic` commit `1dbb5711a072a146e1d228365668ac05655eefe1`. The decompiled Abyss packages identify the embedded release as V24.01, build 3396210.
- Executable EveJS reference: local `C:/Users/yutai/evejs-v0.12.9`, HEAD `828823a75f211fdecb945b087a26c225f111491f`. This is the runtime source used for local test discovery.
- Supplemental GitHub main snapshot: `Fokiiiiiii/evejs-v0.12.9` commit `7cc6f323c7c5c92c2738476a62768318799d8077`. It is not the local executable checkout. The selected Abyssal and dungeon files in the comparison table below have identical Git blob IDs at both commits, so their source text is shared.
- The transition-profile source records `TQTranquilAbyssal5.txt`, segment `2026-07-15T23:20:22--2026-07-15T23:28:56`, but that raw file was not found in the repository or local captures. The profile is evidence recorded in EveJS, not a fresh packet capture in this investigation.
- `docs/design/REFERENCE-TRACES.md` requires a same-scenario EveJS/eve.rs MITM capture and `mitm diff --ignore-volatile` before completion. No Abyssal MITM run was made; the existing procedure takes the shared heavy-run lock.

## Game mode fields

The client uses two distinct numeric fields: `GameModeID` and Abyss `content_type`. Equal numeric values across those fields do not mean the same mode.

| Client mode | GameModeID sent as `game_mode_id` | content_type scalar | Allowed typeList | max_players | key_stack_size | should_jump_on_deploy |
|---|---:|---:|---:|---:|---:|---|
| Solo cruiser | 1 | `CRUISER_SOLO_CONTENT = 1` | 57 | 1 | 1 | true |
| Three frigates | 2 | `FRIGATE_FLEET_CONTENT = 2` | 92 | 3 | 3 | false |
| Two destroyers | 3 | `TWO_PLAYER_DESTROYERS = 4` | 233 | 2 | 2 | false |
| Cruiser PvP | not in this PvE flow | `CRUISER_PVP_CONTENT = 3` | out of scope | — | — | — |

`MAX_PLAYERS = 3` is a global maximum, not the participant count for every mode. The solo/co-op values above come from the scalar assignments and `AbyssSoloGameMode`, `AbyssCoOpGameMode`, and `AbyssTwoPlayerGameMode` field assignments in the client. The decompiled list-style constants such as `ABYSS_REGIONS` are not used as evidence.

## Client RPC callsites

| RPC / keeper method | Client callsite and trigger | Normal EveJS return |
|---|---|---|
| `AbyssalEntranceDeployment(itemID, gameModeID)` | `keyActivationWindow.py` calls `abyss.deploy_entrance_gate(itemID, gameMode.id)`. Solo uses `PerformSessionChange('JumpToAbyss', ...)`; fleet modes deploy the trace without jumping so fleet members can enter. | JS `null`; the client does not consume a result payload. Validation failures use notifications/Abyss errors. |
| `AbyssalEntranceGateActivation(itemID)` | `AbyssJumpWindow.py` calls `abyss.activate_entrance_gate(itemID)` through `PerformSessionChange('JumpToAbyss', ...)`. The entrance gate menu first opens this window. | JS `null` on normal completion/deferred movement; failures use the reference error/notify path. |
| `AbyssalGateActivation(itemID)` | `movementFunctions.RealActivateAbyssalGate`, selected for `typeAbyssEncounterGate`. Calls `PerformSessionChange('abyssToAbyssJump', sm.RemoteSvc('abyssalMgr').AbyssalGateActivation, itemID, violateSafetyTimer=True)`. | Normal room transfer returns JS `null`; valid and specifically routed cases delegate to `AbyssalEndGateActivation`. Rejections are errors, not success payloads. |
| `AbyssalEndGateActivation(itemID)` | `movementFunctions.RealActivateAbyssalEndGate`, selected for `typeAbyssExitGate`. Calls `PerformSessionChange('abyssToKnownJump', sm.RemoteSvc('abyssalMgr').AbyssalEndGateActivation, itemID)`. | Normal transfer/final-exit scheduling returns JS `null`; failed transition/validation raises the reference error. |
| `ClientIsReady()` | `abyssal_service.py` sends it after `OnSessionChanged` when `solarsystemid2` changes from `None` into an Abyssal system. | JS `null`; it reconciles pending entry/environment work and, when appropriate, republishes a source trace. |
| `keeper.GetCurrentDungeonForCharacter()` | `dungeonTracking.py` calls this after character session change. | EveJS returns `null` or `[dungeonID, roomID, instanceID, dungeonValues]`. It is the observed current-dungeon read; no dedicated `GetCurrentAbyss` call was found in the Abyss client service. Abyss timer delivery is via notification below. |

Abyss gate item types are dispatched in `menusvc.GetAbyssalGateFunction`; they do not use the generic `keeper.ActivateAccelerationGate` path. `spacecomponents/client/components/activate.py` and its timer controller expose component activation presentation, not these Abyss RPCs. The `gatekeeper` package is an identity/cohort service; it is distinct from `RemoteSvc('keeper')`.

## Notification payloads

| Notification | Recipient / arguments | Client contract |
|---|---|---|
| `OnEnteringDungeonRoom` | `shipid`; `[cloudDungeonID, dungeonRoomID, (x,y,z), None, None]` | Client handlers accept `(dungeonID, roomID, roomPosition, instanceID, dungeonValues)`; `environmentSvc` installs the room environment and `dungeonTracking` updates the active room. |
| `OnAbyssalLocationEntered` | `charid`; one dictionary with `difficulty_tier`, `content_id`, `location_id`, `nebula_graphic_id` | `AbyssalService` updates the active content, room, nebula, and objective state. |
| `OnAddExpiryDurationForAbyssalContent` | `charid`; `[expiryFileTime]` (logged by EveJS as `int64`) | Client argument is `end_time`; the client schedules warnings at 300, 120, and 30 seconds before expiry. |
| `OnExitingDungeon` | `shipid`; `[dungeonID]` | Generic dungeon exit event. |
| `OnAbyssalLocationExited` | `charid`; `[]` | EveJS publishes it on room/final exit. A matching handler was not found in `AbyssalService`; its full client-side effect remains unverified. |
| `OnAbyssalContentFinished` | `charid`; `[contentID, ABYSSAL_TIMER_TYPE_NORMAL_PVE]` | Client clears expiry state only when `contentID` matches its active content. |
| `OnAddExpiryDurationForAbyssalPvpContent` | client handler accepts `end_time` | PvP is outside this PvE contract; no matching PvE publisher is assumed. |

## Observed sequence

### Entry and relog

For an isolated-scene entry, the captured transition profile records:

1. `SessionChange` after ship repositioning.
2. `OnEnteringDungeonRoom`.
3. `OnAbyssalLocationEntered`.

The readiness worker then executes environment delivery, location delivery, lifecycle notifications, arrival FX, and NPC activation in that order. In the `entry` and `relog` phases, lifecycle delivery sends `OnAddExpiryDurationForAbyssalContent` after the location notification. It can also send `OnExitingDungeon` when a prior dungeon ID is present. The expiry notification is not refreshed by the `transfer` phase.

### Room transfer

The T0 Dark transition profile records this packet/event sequence:

1. `OnEnteringDungeonRoom`.
2. `OnExitingDungeon`.
3. `RemoveBalls + AddBalls2`.
4. `SetBallPosition`.
5. `OnAbyssalLocationEntered`.
6. `OnAbyssalLocationExited`.

The runtime stages commit the source room, move the activating participant, move the other eligible participants, clean the source room, prepare/publish the destination, activate its controllers, and commit the transition. The stored order is a capture-derived profile; its named raw trace file was not present locally, and a new MITM replay has not been run.

### Final Origin exit

The transition profile records `OnExitingDungeon` then `OnAbyssalLocationExited`, followed by `SessionChange`. Runtime sends the two exit notifications before the return movement/session change. It commits each participant return receipt after that participant is attached in K-space; `OnAbyssalContentFinished` is sent at that receipt/final-arrival stage and is idempotently guarded by a run/content/timer receipt key.

### Reconnect

On character session change, the client asks `keeper.GetCurrentDungeonForCharacter()`; if present, it locally calls its `OnEnteringDungeonRoom` handler with the returned IDs/values and a null room position. Separately, `AbyssalService.OnSessionChanged` sends `ClientIsReady()` when entering an Abyssal system. EveJS waits for a primed ballpark, then replays pending environment/location/lifecycle work; fresh-entry uses the trigger `beyonce-post-setstate`, while subsequent bootstrap uses `client-ready-after-bootstrap`. The relative ordering of the client-side keeper call and the Abyss service callback is not established by source alone.

## Cooperative participant behavior

- One room-gate activation moves the activating ship, then iterates the shared participant roster and relocates every other `ACTIVE` or `DISCONNECTED` participant to the same next room. Disconnected participants receive persisted room/scope movement without a live client session.
- The Origin Gate path returns the activating participant and then iterates other `ACTIVE`, `DISCONNECTED`, `ADMITTING`, or `EXITING` participants, committing individual return receipts. Thus the normal final-gate path is group-wide.
- A ship destruction is participant-local when another `ACTIVE`, `DISCONNECTED`, or `ADMITTING` participant survives and no timeout is committed. The run continues; the destroyed member is settled separately. With no survivors, or after a committed timeout, the run becomes terminal.
- On disconnect from a multiplayer run, EveJS durably changes that participant to `DISCONNECTED`, clears session state, and retains the run. This is separate from the timeout path.

CCP states that a disconnected/logged-off ship remains in the Abyss and continues to be attacked by NPCs; the Abyss timer can still destroy it. EVE University separately says server downtime returns the pilot to the system where the filament was activated. [CCP support](https://support.eveonline.com/hc/en-us/articles/360000852629-Abyssal-Deadspace) · [EVE University downtime](https://wiki.eveuniversity.org/Server_downtime) · [EVE University Abyssal Deadspace](https://wiki.eveuniversity.org/Abyssal_Deadspace).

## Startup, crash, and downtime recovery

The EveJS startup path rehydrates persisted Abyssal instances and checks, in order, pending cleanup, inferred/committed participant death, committed final exit, and persisted terminal failure before allowing an administrative return. Precommitted timeout/death failures are retained for terminal reconciliation and are not changed into a safe return.

For a nonterminal run, the administrative-return path records `cluster_shutdown_returned` with `rewardEligible: false` and `deathApplied: false`, then cleans the run. Its return target uses `sourceSolarSystemID` and a position derived from the source trace/ship position plus the trace profile's return policy. This is EveJS server-restart behavior; no restart/downtime run was executed here.

**Co-op limitation in the reference:** the startup rehydration record contains a participant roster, but the administrative evacuation context selects one `characterID`/`shipID`, and `moveAbyssalEvacuationShip` moves that single ship. The explicitly group-wide exit path is separate. Therefore, group-wide server-downtime return is not established by this source path; do not copy an owner-only restart behavior as complete. It needs a same-scenario runtime check against the unmodified client and the intended EveJS behavior.

## Expiry and timeout

EveJS sends the absolute expiry as a FILETIME-like `int64`; the client calculates the warning times locally. On a committed lethal timeout, EveJS suppresses pending rewards, records `FAILED_TIMEOUT`, destroys the required participants, commits those results, sends terminal notifications when a participant session is available, then cleans up. For a live participant with an available room dungeon ID, teardown order is `OnExitingDungeon(shipid, [dungeonID])` -> `OnAbyssalLocationExited(charid, [])` -> `OnAbyssalContentFinished(charid, [contentID, ABYSSAL_TIMER_TYPE_NORMAL_PVE])`. CCP confirms ship and capsule destruction when the Abyss timer expires. This timeout path is different from server-restart administrative return.

## Test and runtime status

- Local EveJS root `C:/Users/yutai/evejs-v0.12.9` exposes `npm run test:isolated -- server/tests/<file>.test.js`; the runner requires an explicit test target and creates a temporary isolated GameStore per test.
- No Abyssal-, dungeon-, gate-, or keeper-specific `server/tests/*.test.js` exists in the local clone. No unrelated EveJS test was run.
- No live client, process-restart, or MITM capture was run. The MITM recipe in `docs/design/REFERENCE-TRACES.md` uses the shared heavy-run lock; perform it after the competing Euler run completes and both headless scenarios are ready.
- The trace profile references `TQTranquilAbyssal5.txt`, but the raw file was not found locally. Packet order below is therefore the checked-in profile/runtime contract, not a new raw-capture verification.

## Source ledger

The EveJS entries were hash-checked locally against the GitHub main commit. **Each listed blob is identical between local HEAD 828823a and pinned GitHub commit 7cc6f32.** Hashes below are Git blob SHA-1s; use the commit pins above for full source identity.

| EveJS path | Blob SHA |
|---|---|
| `server/src/abyssal/transport/abyssalMgrService.js` | `74b2b86889b1c3969ffe2af66d4691ca4e2b8ce5` |
| `server/src/abyssal/runtime/abyssalDeploymentRequest.js` | `1bbe5229d4ee99b6d404e30c7afd89fd820395d2` |
| `server/src/abyssal/runtime/abyssalEntranceRequest.js` | `b76ab0e761678bee94f9bdf26c3644f661ecbeb9` |
| `server/src/abyssal/runtime/abyssalEndGateRequest.js` | `03e45e0a1686129466b49bd87c960adf57c2092c` |
| `server/src/abyssal/runtime/abyssalClientReadyRequest.js` | `96a130b72bd7ae3a4e576430f71b81dbcff421d9` |
| `server/src/abyssal/runtime/abyssalEntryReadiness.js` | `e96d217c351134637002963a0f2c608f9dc133c9` |
| `server/src/abyssal/runtime/abyssalDungeonNotifications.js` | `6650c59da3dda5a1fa2e4c4c64253d45c965615d` |
| `server/src/abyssal/runtime/abyssalExitValidation.js` | `ead4b433342eddfcf0699e60ad2cf917c7c1258d` |
| `server/src/abyssal/domain/abyssalGatePresentation.js` | `99615e130bee51733c34fa60997b2b17c1d363f3` |
| `server/src/abyssal/runtime/abyssalRoomTransferMovement.js` | `95a30538d129c35da8b06758c77af1e4df02fccf` |
| `server/src/abyssal/runtime/abyssalRoomProgression.js` | `93b96497dce7770407b44fe8b60743fb7b469999` |
| `server/src/abyssal/runtime/abyssalParticipantReturn.js` | `c974d7b3fd89e289c8911c7eddb6a3a71ad2635c` |
| `server/src/abyssal/runtime/abyssalDestructionCallback.js` | `d6509e011cb258dcc920672cfb1b680cbb64b1a9` |
| `server/src/abyssal/runtime/abyssalStartupReconciliation.js` | `b0a4ca6a533d40c9c29a6f4d02373a04b28cdf11` |
| `server/src/abyssal/runtime/abyssalStartupReturn.js` | `39330327a1b31669875bed1866b2ff3b0e6321e9` |
| `server/src/abyssal/runtime/abyssalSessionDisconnect.js` | `8f95ccaa30ef1361a5c89c4cce9ea451b4221fa9` |
| `server/src/abyssal/runtime/abyssalParticipantRoster.js` | `22862d765489ffdcfa09db447c0161fa6051c4cd` |
| `server/src/abyssal/runtime/abyssalTimeoutLifecycle.js` | `6ba4ad8838a11dc2fe6be61bccd0aea91891f209` |
| `server/src/abyssal/runtime/abyssalRunEvacuation.js` | `11338351669248e8f00b6dfbf0c99f4133ede45f` |
| `server/src/abyssal/runtime/abyssalSpawnGeometry.js` | `719bc216d670caa6daa73a28f942182dd1eb585d` |
| `server/src/abyssal/runtime/abyssalClientEnvironment.js` | `5c9ccd6d100484b6509cc65d7e29d998106898c7` |
| `server/src/abyssal/runtime/abyssalEntryBootstrap.js` | `f2daf346da2d1a7fd6cae72957b0603c790b1647` |
| `server/src/abyssal/runtime/abyssalFinalExitExecution.js` | `8e5142dc3d37a3f7f8ce3c0703599ea80efb22fc` |
| `server/src/abyssal/runtime/abyssalKSpaceArrival.js` | `7ad18ece35f6f4c07608e1c3f7b170bb743200ef` |
| `server/src/abyssal/runtime/abyssalRoomTransferExecution.js` | `17ae801742ebb8bc36dff0b91d796e9dd72b3080` |
| `server/src/abyssal/runtime/abyssalParticipantAdmission.js` | `fb161d231d481f8449f8d0ca5348df73e881cd91` |
| `server/src/abyssal/runtime/abyssalCompatibilityRecovery.js` | `4e7901f8a8fd4c6a7f5493e18858385e5892cedd` |
| `server/src/services/dungeon/keeperService.js` | `de0a6825bda733a036cf690f588f30e70c9f652b` |
| `server/src/services/dungeon/dungeonTrackingRuntime.js` | `a7a5af5beda4a0c0ce58af5997a0cd52dba70d2f` |
| `server/src/abyssal/catalog/abyssalTransitionProfiles.js` | `0f28f1c02ae14ee1f3f920074f4e16968eaf7410` |
| `server/src/abyssal/domain/abyssalTerminalTransitionPolicy.js` | `df2d99412ddcc5081a5f022b4c6a70fa38f72245` |
| `server/src/abyssal/runtime/abyssalDeadlinePolicy.js` | `40b1fb7a54bd13ee7b0565a06b9bfb4c3f637023` |

Client sources use the separate EveLogic commit and blob IDs below.

| EveLogic path | Blob SHA |
|---|---|
| `abyss/const.py` | `52bde9d010020af81b1aebf91f61dfb6fb5a2e3d` |
| `abyss/game_mode.py` | `8e65aa6381624d6f812e099cf8f5bf5af347526e` |
| `abyss/client/abyssal_service.py` | `a81dba34ca609f18c7ba9a39b7b24857cc6c07f8` |
| `eve/client/script/ui/shared/abyss/keyActivationWindow.py` | `ed84d21439ba7820fe414ab09a8fe57c78b317b0` |
| `eve/client/script/ui/shared/abyss/AbyssJumpWindow.py` | `d2b96cebb2e9bce92d48a79f4af81f8bb79b2caa` |
| `eve/client/script/ui/services/menuSvcExtras/movementFunctions.py` | `2bb1b6619561987c68d92fbbf72ba624ac17928b` |
| `eve/client/script/ui/services/menusvc.py` | `f9cc15829cb8d6700526966de9a86d366bd7e020` |
| `eve/client/script/parklife/dungeonTracking.py` | `6bfc5c3e97bd158960346330c8ff47956e5f9bef` |
| `eve/client/script/parklife/environmentSvc.py` | `f81fa11920c44ee3b34dbbe374dbbbad55c112e7` |
| `spacecomponents/client/components/activate.py` | `e4b259dccbf713f47ba16dcf0c10ec8cbd116ae0` |
| `eve/client/script/spacecomponents/activatecontroller.py` | `40560ad6bbd03eb13e379c716c90f5e9748e52d2` |

## Unverified items

- No Abyssal-specific EveJS Node test exists in local `server/tests`; `npm run test:isolated` requires an explicit `server/tests/*.test.js` path. No unrelated test was substituted.
- The stored transition profile says its evidence is captured retail, but the named raw trace file was not found; the sequence has not been independently replayed here.
- No live disconnect/reconnect, multi-participant gate, final exit, scheduled downtime, process-crash, or MITM capture was executed.
- A client handler for `OnAbyssalLocationExited` was not found in `AbyssalService`; its application elsewhere in the V24.01 client remains unchecked.
- Co-op startup/downtime evacuation may be incomplete in the reference: the administrative startup path selects and moves one run owner ship, while normal gate and death paths explicitly enumerate group participants. Verify the multi-participant case before reusing that policy in eve.rs.
- The local test-only status includes Serena's untracked `.serena/` directory. Do not use checkout/reset/cleanup operations on the reference clone.
