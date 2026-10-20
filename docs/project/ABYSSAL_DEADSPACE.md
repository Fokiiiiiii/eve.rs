# Abyssal Deadspace Reference Contract

**Status: PARTLY — protocol79 attempt12 passed its recorded headless combat/Origin and cleanup gates, while other fits/tiers, all-four acceptance, full JS/RS parity, and GUI acceptance remain open (2026-10-18).** This document records source contracts, verified component checks, executed headless captures, intentional differences, and unresolved acceptance gates. It does not claim feature completion. GUI/native-client launch remains prohibited; all reported execution is headless.

## Source pins and confidence

- V24.01 client logic: `Fokiiiiiii/eve-logic` commit `1dbb5711a072a146e1d228365668ac05655eefe1`. The decompiled Abyss packages identify the embedded release as V24.01, build 3396210.
- Executable EveJS reference: local `EveJS repository`, HEAD `828823a75f211fdecb945b087a26c225f111491f`. This is the canonical executable reference for the headless baseline, combat scenarios, and catalog batch comparisons.
- Supplemental GitHub main snapshot: `Fokiiiiiii/evejs-v0.12.9` commit `7cc6f323c7c5c92c2738476a62768318799d8077`. It is not the local executable checkout. The selected Abyssal and dungeon files in the comparison table below have identical Git blob IDs at both commits, so their source text is shared.
- The transition-profile source records `TQTranquilAbyssal5.txt`, segment `2026-07-15T23:20:22--2026-07-15T23:28:56`, but that raw file was not found in the repository or local captures. The profile is evidence recorded in EveJS, not a fresh packet capture in this investigation.
- `docs/design/REFERENCE-TRACES.md` requires same-scenario EveJS/eve.rs capture and `mitm diff --ignore-volatile`. The final JS three-room combat/Origin capture and the RS reconnect/entry baseline now pass. Full same-scenario EveJS/eve.rs combat parity and RS all-four acceptance remain pending; see the evidence table below.

## Game mode fields

The client uses two distinct numeric fields: `GameModeID` and Abyss `content_type`. Equal numeric values across those fields do not mean the same mode.

| Client mode | GameModeID sent as `game_mode_id` | content_type scalar | Allowed typeList | max_players | key_stack_size | should_jump_on_deploy |
|---|---:|---:|---:|---:|---:|---|
| Solo cruiser | 1 | `CRUISER_SOLO_CONTENT = 1` | 57 | 1 | 1 | true |
| Three frigates | 2 | `FRIGATE_FLEET_CONTENT = 2` | 92 | 3 | 3 | false |
| Two destroyers | 3 | `TWO_PLAYER_DESTROYERS = 4` | 233 | 2 | 2 | false |
| Cruiser PvP | not in this PvE flow | `CRUISER_PVP_CONTENT = 3` | out of scope | — | — | — |

`MAX_PLAYERS = 3` is a global maximum, not the participant count for every mode. The solo/co-op values above come from the scalar assignments and `AbyssSoloGameMode`, `AbyssCoOpGameMode`, and `AbyssTwoPlayerGameMode` field assignments in the client. The decompiled list-style constants such as `ABYSS_REGIONS` are not used as evidence.

**High-security filament activation (0.9):** CCP’s V24.01 patch note for 2026-06-16.1 says, “Fixed a bug that had allowed the activation of Abyssal Filaments beyond tier 0 in 0.9 security status systems.” Accordingly, Tier 0 is the maximum allowed tier in a 0.9 system; Tier 1+ activation is disallowed. The Tier 0 allowance is inferred from the correction wording, while the patch explicitly identifies higher-tier activation as the bug. [CCP V24.01 patch notes](https://www.eveonline.com/news/view/patch-notes-version-24-01).

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
| `OnAbyssalLocationExited` | `charid`; `[]` | EveJS publishes it on room/final exit. A matching handler was not found in `AbyssalService`; its full client-side effect remains not confirmed. |
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

The runtime stages commit the source room, move the activating participant, move the other eligible participants, clean the source room, prepare/publish the destination, activate its controllers, and commit the transition. The stored order is a capture-derived profile whose named raw trace file remains unavailable. A final JS three-room Origin capture and an RS reconnect/entry baseline are listed below; full same-scenario EveJS/eve.rs combat parity remains not confirmed.

The simulator grants 60 seconds of protection when a participant enters an Abyssal room, including each room transfer. The focused regression `abyssal_room_transfer_preserves_the_ship_and_rebinds_a_new_session_before_origin_return` asserts the destination protection deadline; protection is applied only when the destination is Abyssal, so returning to known space does not grant it.

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

## Implementation and catalog evidence

The following implementation results and test gates were supplied by the coordinating agent on 2026-10-16. This documentation pass inspected the listed baseline/combat result summaries and artifact locations; it did not rerun the server, tests, or client.

- The default catalog uses the EveJS default procedural main and side content selection. The comparison matched 210 JavaScript cases / 630 rooms.
- Resolver coverage contains 477 objects across 105 rooms, including 105 weather objects and 7 functional objects. The final catalog gate passed 6 tests, 0 failed, 0 skipped, exit 0. Node comparisons passed for all 477 objects at both allowed weather stages; the real EveJS batch test ports completed 477 create / 477 spawn operations.
- Local mechanics use 13 SDE types, source HP, and source-declared disabled decoration. Saved weather TypeId is authoritative. Rehydration no longer rerolls seedContentId.
- Room.Loot remains empty; saved loot belongs to each individual artifact. This avoids creating a second room-level reward source.
- MechanicsEnabled is a boolean defaulting to true. PresentationRadiusMeters is nullable and preserves the source presentation radius independently of functional and damage boundaries. These fields are declared in dotnet/src/Eve.Content/AbyssalTypes.cs; catalog/runtime authority is in AbyssalStaticCatalog.cs and AbyssalContentRuntime.cs.
- Host entry, room materialization/transfer, origin return, notifications, and death settlement use ContentRuntime and the existing Event Bus. Saved run/object identities and SDE data supply materialization. Duplicate EndGate draining and presentation-radius contracts are included in the latest focused Host gate.

## Test and runtime status

### Latest accepted component gates

| Scope | Accepted result | Evidence and limit |
|---|---|---|
| Domain / full Eve.Content.Tests after presentation radius | 41 PASS, 0 FAIL, 0 SKIP; exit 0; database eve_rs_test | External artifact directory: abyssal-verification/eve-content-tests-radius-20261016-fc57070844684cc88491882eca6e83ef. This supersedes the earlier 39-test and 24-test snapshots. |
| Catalog final | 6 PASS, 0 FAIL, 0 SKIP; exit 0 | Includes the final 477-object comparisons and EveJS batch create/spawn evidence above. Keep this named gate distinct from the full Domain gate; do not assume disjoint test totals. |
| Host focused, earlier named non-realSim gate | 52 PASS, 0 SKIP | Historical broader focused gate; latest integrated build and specified PG 22 are recorded below. Do not sum overlapping suites. |
| Host latest integrated build | 0 warnings, 0 errors; 43.23 s | Latest supplied integration build; supersedes the earlier 13.22-second direction/radius snapshot. |
| Prior Host private-frame boundary snapshot (2026-10-16; superseded) | 3 targeted PG checks PASS; Release Host build 0 warnings / 0 errors; package SHA256 E91E488D0DF62C2BE485FE5CA33E5790384E350493561619DB3464EE14CF91 | Historical behavior used new origin zero and preserved legacy null fallback to the SDE center. The origin choice is superseded by the grid-cell root cause and correction below. Package: abyssal-verification/rs-private-scene-frame-20261016-host-boundary/host-package. |
| Host private-scene grid-origin correction (2026-10-17) | 3 focused Host PG tests PASS; Domain start/idempotency/JSON test 1 PASS; Release build 0 warnings / 0 errors; Release publish exit 0; `eve-host.dll` SHA256 AD48A3B4640556038E333C2A6ACA2B21F5B863354DF7745321FD9D0D69F51212 | Red reproduced two reserved NPCs at negative X/Y, positive Z: with new origin zero they occupied (-1,-1,0) while the player was in (0,0,0). New starts now save the visibility-grid midpoint. Explicit saved origins and null legacy SDE fallback are unchanged. Package: abyssal-verification/rs-private-scene-grid-origin-20261017-host-boundary/host-package. Focused headless evidence only; no full sim/client run is claimed. |
| Host specified integration PG gate | 22 PASS, 0 SKIP | Eligibility 19 + post-drain actual RPC 2 + materialization 1; no source changes during validation; Host writer stopped. |
| Host materialization subset at the earlier Cloud source direction/radius snapshot | 1 PASS, 0 FAIL, 0 SKIP; 6.078 s | This test is part of the 46 focused tests: do not report 47 unique tests. The final direction is (0,1,0); this was a source-facing parity correction. OriginDirection had already prevented the zero-direction exception. |
| Services.Core focused Abyssal RPC | 5 PASS | Component result supplied with the final Host gate. |
| Abyssal reconnect/session-attribute fix | Red→Green 1 PASS; Host build 0 warnings / 0 errors; diff check PASS | `Hidden_system_entry_and_relogin_set_both_system_ids_and_allow_real_ballpark_bind` preserves the last-station docked fallback and restores an active/disconnected in-space Abyssal ship with both system IDs and a real ballpark bind. The saved RS reconnect baseline is listed below. |
| Source integration format / diff check | PASS, supplied by source owners | cargo fmt --all --check / diff check passed; source writers stopped. This documentation pass does not substitute for runtime acceptance. |
| Space final component integration | Rust 857/857 and all integrations PASS; shared golden 10/10; C# Space/codec 411/411; PG 20/20 | Supersedes intermediate Space snapshots. No command-14 golden exemption. These component gates do not establish RS end-to-end success. |

### Private-scene NPC visibility root cause (2026-10-17)

Rust `visibility::grid_of` assigns cells with `floor(position / GRID_BOX)`, where `GRID_BOX` is 7,864,320 m; ordinary ships in different cells are not visible to each other. The former new-run origin `(0,0,0)` put the player in cell `(0,0,0)`, while the Host-reserved NPCs with negative X/Y offsets landed in `(-1,-1,0)`. The reservation and NPC data were present; the cell mismatch excluded them from the player's view. This supersedes the earlier missing-NPC snapshot/rebind diagnosis. New runs now persist `(3,932,160, 3,932,160, 3,932,160)`, the grid midpoint, keeping these local offsets in cell `(0,0,0)`. This value is a private coordinate frame, not an SDE position. Existing `ResolvePrivateSceneOriginAsync` callers remain shared by ship handoff, room materialization, and boundaries; explicit saved origins are honored and a null legacy origin still falls back to the SDE hidden-system center. The public return origin is unchanged.

### Component integration evidence (recorded gates)

The protocol-74 and protocol-75 component snapshots below are historical. The latest protocol79 attempt12 headless run is recorded in the capture table: one five-gun fit, three-room combat/Origin, four integration checks, and cleanup all passed. It does not establish RS all-four, other fit/tier, full same-scenario JS/RS parity, or GUI acceptance. Overall feature status remains PARTLY.

- Earlier protocol-74 component snapshot (Carson): Rust 857/857 PASS and all integration suites PASS; shared golden 10/10 PASS with no exemption for the previously fixed command-14 baseline; C# Space/codec 411/411 PASS; Space PostgreSQL 20/20 PASS.
- Earlier protocol-74 mapping and protocol75 component snapshots are historical: public kinds90..93 and Abyssal94..120, SDK 10.0.401 build0 warnings/errors, codec/golden/SimClient412/412 PASS. Current protocol79 mapping preserves public kinds90..97 and uses Abyssal98..124. Attempt12 validates one five-gun fit/scenario only; component checks and this run do not establish RS all4, other tiers, or full same-scenario parity.
- Earlier Host integration snapshot (Hilbert): build 0 warnings / 0 errors (43.23 s); specified PostgreSQL 22 PASS, 0 skipped (eligibility 19 + post-drain RPC 2 + materialization 1). The protocol-75 Host/PG/binary gates were still pending at that historical snapshot; protocol79 attempt12 is recorded below and covers one fit/scenario.
- Earlier protocol-74 real-simulator cleanup regression: cargo build passed (46.22 s); simulator SHA-256 450a13f4fb5bcafbcbbb1deb47b5b177211e5b925a8f81f18720c0975669096c. Real_sim_cleanup... with --no-build passed 1 / 0 failed / 0 skipped (16.36 s), covering all three scenes plus trace cleanup, one committed participant receipt, zero remaining items, retention of the other run, and successful replay. It remains a named historical cleanup regression and does not establish RS all4 end-to-end acceptance.
- Keep these named suites distinct. Do not add older focused suites or overlapping materialization/partial subsets into a unique test count.
- The final JS three-room Origin exit now passes. RS reconnect/entry also passes; RS all-four and same-scenario combat parity remain not confirmed. Feature status is PARTLY.

### Later shared-owner and Host integration evidence

The coordinating agent supplied these additional results after the earlier 46-test Host gate. These are earlier named snapshots, superseded where noted by the final component integration gate above. JS combat and RS all4 acceptance remain incomplete. Named suites and subsets may overlap and must not be added into a unique test total.

- Shared owners: Eve.Inventory.Tests 55/55 PASS, 0 SKIP; Eve.Ships.Tests 234/234 PASS, 0 SKIP; focused Ships 6/6 PASS, 0 SKIP.
- Typed creation preserves provenance and passes CustomInfo through. FindRunSpaceItems uses exact run scope. Recursive RemoveContainer runs in the same transaction, recovers remaining corporate/other-participant contents, and preserves claimed contents.
- Latest non-realSim Host gate: 52 PASS, 0 SKIP. Latest integrated Host build and specified PG 22 now passed; see the final integration gate above.
- Real Rust simulator + PostgreSQL, own TCP0, source 339: 1 PASS, 0 SKIP, 17.8325 s. It exercised live NPC/environment state in both runs, rejected legacy empty-remove behavior, acknowledged exact settled no-death cleanup, and retained the other run.
- Partial-scope check: 1 PASS, 5.97 s. After the late-partial-terminal change requiring all three acknowledgements, the two affected tests passed (2 PASS, 8.02 s). These are named checks, not additional unique tests inferred from the 52-test suite.
- Intermediate Space results: PostgreSQL 38 PASS (Abyssal 20 + containers 18), C# SimLink 400 PASS, Rust 844 library tests plus integration suites of 4 / 5 / 2 / 7 PASS. Golden 9/10 and baseline-command 14 are also pre-integration snapshots. These counts are historical; the final Rust 857 / golden 10 / C# 411 / PG 20 results above supersede them.



- Shared-owner/file-size audit (2026-10-18): `HeatProfileOf` reuses `Slots.FamilyOfFlag`; NPC cleanup reuses the per-scene spawn receipt, `launcher_id`, and `remove_wreck`; gate updates reuse `AbyssalRoomSpawnReservation.ResolvePrivateSceneOriginAsync`; `ClientIsReady` reuses `AbyssalContentRuntime.ReconnectAsync`. No duplicate owner helper, persistent store, timer, wire kind, or public API was added. The largest related implementation file remains the existing `AbyssalContentRuntime.cs` at 1,526 physical lines (PowerShell `Measure-Object -Line`); no split is justified by these deltas.

### Deployment eligibility and POS compatibility boundary

EveJS findPlacementConflict uses typeList 50, excludes the activating ship, deduplicates candidates, and rejects a candidate at center distance <= 1,000,000 m with Error 8. The missing Host connection was repaired during integration; the final Host eligibility 19, post-drain RPC 2, and materialization 1 PostgreSQL checks passed. The implementation uses the existing per-system pg_advisory_xact_lock, a fresh snapshot from the same link, and persisted trace checks inside the same transaction before consuming filaments. The five source-defined state gates, session/tether/self-destruct, existing dungeon, Crimewatch, and system conditions are connected and covered by the received Host gate. Full RS end-to-end acceptance remains pending.

The latest origin/main source is 235ee85a882f2a5a36067192874d842f1ec22828 (the deployable.rs / SimMessages changes arrived in 6142d86b). Its representation has no forcefield owner information. ControlTower Active represents anchored state and hull radius only; fuel and modules are explicitly not modelled. A reachable POS forcefield therefore has no authoritative runtime representation for this eligibility check. Record this as a compatibility boundary. Do not fabricate InsideForceField=false or use placement Error 8 to conceal a missing forcefield Error 13 check, and do not add a new POS subsystem under this feature.

### Executed headless captures

Capture root: external abyssal verification workspace. Raw packets, logs, credentials, and tool reasoning are kept outside this document.

| Scenario / run | Observed result | Interpretation |
|---|---|---|
| JS baseline: js-baseline-1792079082441 | 1 PASS; 97.85 s; runner exit 0; cleanup_ok=true | Capture: js-abyssal.jsonl. This is the accepted reference baseline, not a combat-completion result. |
| RS baseline: rs-baseline-1792079121868 | 0 PASS, 1 FAIL; 119.06 s; exit 1; cleanup_ok=true; remaining_own_schemas=0 | Stopped at Cloud unsupported because the role was not connected in this captured run. The previous cache mismatch did not recur. Subsequent Host materialization passed after the final source-direction/radius update. The historical failure does not describe an unresolved zero-direction exception; it has not yet been superseded by an accepted winning RS capture. |
| Invalid-entry error comparison | Error-envelope tuple fully matches | Evidence: rs-baseline-1792079121868/comparison/invalid-entry-error-contract.json. Error parity does not establish happy-path sequence parity. |
| JS combat first run: js-combat-1792123136221 | 0 PASS, 1 FAIL; 705.54 s; exit 1; cleanup_ok=true | Rooms 1/2 completed real kills, cache wreck creation, and transfer. Room 3 ran out of ammunition and ended in real ship/pod destruction and clone handoff. This is failure-path evidence; it is not a successful combat acceptance run. |
| JS combat replenishment run: js-combat-1792124296256 | 0 PASS, 1 FAIL; 15.03 s; runner exit 1; cleanup_ok=true | Failed during setup because List returned None through the source-confirmed JS BigInt binding bug described below. It did not verify replenishment or loot. |
| Later JS combat, fifth run (run ID not supplied) | 3 FAIL reported; no three-room Origin win | Auxiliary inactive-repair and fleet-proximity failures remain; captured placement against the same planet was 252.2907 m, correctly producing Error 8. Real reload, cache loot, and room-1 transfer have partial PASS evidence only. |
| JS current five-gun reference attempt: `combat-fit5-web2-reload-20261018-01/js` | Generator exit 0; reference server exit 1 with uncaught `TypeError: Cannot read properties of null (reading 'kind')`; pytest exit 1; cleanup verified | Fatal is at `targetDamageApplication.js:415` during missile resolution (`attackerEntity` is null). This is a reference-server failure record, not a JS success or same-fit parity result. Evidence: `result.json`, `uncaught-summary-98072.json`, `server-stderr.log`. |
| JS valid-placement attempt: js-valid-placement-1792128817856 | Last received as in progress; no accepted final combat PASS | Existing normalized JS Inventory binding is used for setup/loot. A different planet / 100,000,000 m offset follows the source typeList-50 and 1,000,000 m minimum-clearance rule. |
| JS manual modes: js-manual-diagnostic-1792131021538 | 2 PASS, 0 FAIL, 0 SKIP; 181.23 s; runner exit 0; cleanup_ok=true | Mode 2 uses a Tristan and 3 filaments; Mode 3 uses a Catalyst and 2 filaments. Real fleet/deploy stays outside the Abyss and has no expiry during the 5-second observation. EntranceGateActivation, bind/ready, the 20-minute deadline starting at first entrance activation, unchanged deadline after relogin, and consumption notifications passed. Coordinator verified all owned processes exited, ports=0, and temporary runtime removal. This does not verify EndGate wire execution or combat's final Origin exit. |
| JS combat Origin: `js-combat-origin-1792142413084` | 1 PASS, 0 FAIL, 0 SKIP; 485.13 s; exit 0; cleanup_ok=true | Three real-combat rooms, cache loot and Origin return passed. Raw order: `OnExitingDungeon` → `OnAbyssalLocationExited` → `$sessionchange` → public `beyonce` bind → `UpdateStateRequest` → `OnAbyssalContentFinished` (content UUID, type 11). Headless only. |
| RS StationId-fix baseline: `rs-stationid-fix-baseline-1792144800043` | 1 FAIL; 122.00 s; cleanup_ok=true | Initial entry, room and expiry passed; relog did not restore the private system. Superseded by the reconnect/session-attribute fix below. |
| RS reconnect/login baseline: `rs-reconnect-login-baseline-1792147356535` | 1 PASS, 0 FAIL, 0 SKIP; 91.11 s; exit 0; cleanup_ok=true | Invalid-item Error 3 matches the JS capture. Valid deployment, hidden-system entry, room/expiry, relogin with both system IDs, real bind and ClientIsReady passed; the expiry FILETIME remained unchanged. This is one baseline scenario, not RS all4 or combat/loot/Origin acceptance. |
| RS attempt11: `rs-fit5-web2-reload-20261018-11` | Four gameplay checks passed; runner exit 1; `cleanup_verified=false` | Cleanup failed with retained native NPC wrecks. Keep this attempt as a separate failed cleanup result; attempt12 does not erase it. |
| RS attempt12 protocol79: `rs-fit5-web2-reload-20261018-12` | Four integration tests PASS, 0 SKIP; pytest/test/runner exit 0; cleanup verified | One five-gun fit with three-room combat and Origin return. Room 1–3 scene acknowledgements and trace removal passed; terminal receipt count 1, validated owned IDs remaining 0 (34 IDs, then 33 on idempotent retry); all 15 claim checks matched and the parent item was preserved. Exact typed scopes for rooms 1–3 had no rows, private residue 0, identity mismatches 0, simulator cleanup diagnostics 0, owned schemas 0. Evidence: `external abyssal verification workspace/rs-fit5-web2-reload-20261018-12/attempt12-evidence-summary.json` and `binary-manifest.json`; source commit `b9c435a76b21b8c27a272a131e20fca16359d7ef`, protocol79. This is one fit/scenario run, not all-four or other-tier acceptance. |
| RS all4 / other fit tiers | not exercised | Attempt12 covers one five-gun fit/scenario. RS all4, other tiers/fits and full same-scenario JS/RS parity remain open. |

**Auxiliary Inventory binding difference (source-confirmed):** This is separate from the five Abyssal action/readiness RPC contracts. In pinned EveJS server/src/services/inventory/invBrokerService.js, GetInventoryFromId retains a raw itemid BigInt in boundContext.inventoryID. List/Add/MultiAdd evaluate a debug JSON.stringify(boundContext), which throws Do not know how to serialize a BigInt; the catch path returns null. The coordinating agent matched this source behavior to js-combat-1792124296256/server.log lines 814–822. The reference source is unchanged. JS test setup/loot uses its existing GetInventory(itemID) path, whose _normalizeInventoryId converts the ID to Number. RS uses its working GetInventoryFromId; RS GetInventory serves hangar access. These source-supported auxiliary binding paths differ, and the captures must record that difference. No claim of complete equality of all MITM traffic is made; the JS serialization bug is not ported to RS.

The recipe in docs/design/REFERENCE-TRACES.md still requires the same meaningful EveJS/eve.rs scenario and mitm diff --ignore-volatile with explanations for retained differences. Captures have been executed; a successful full happy-path/combat comparison has not yet been accepted. The historical retail profile's named TQTranquilAbyssal5.txt raw file remains unavailable, independently of these new captures.

### Integration and compatibility gates still open

- RS all-four-scenario execution at the final fixed source SHA and full same-scenario JS/RS combat parity remain pending. The reconnect baseline passes but is not the all-four run.
- The POS forcefield representation boundary and committed FOLLOW-approach compatibility gap remain explicit.
- RS all-four-scenario capture, including RS combat, loot transfer and final Origin return; JS combat Origin now passes on the reference run.
- Full transition/notification-order comparison, plus the remaining co-op, reconnect, expiry/death-race, and restart acceptance scenarios where not already covered by a named component gate.
- The merged local source integration uses protocol75: public kinds90..93 remain, and Abyssal kinds are94..120. Peirce reports SDK10.0.401 Eve.Space build0 warnings/errors; codec/golden/SimClient412/412 PASS; Rust eve-space890 + eve-net31 =921/0 ignored; eve-simlink5/5 PASS. origin/main235ee contributes public PLACE_DEADSPACE_POCKET kind93, retained by the local remapping. Final Rust/PG/Host/binary gate remains in progress. No wire renumbering is made in this documentation update. JS combat Origin and RS reconnect baseline pass; RS all4 and same-scenario capture parity remain open.
- GUI/native-client behavior remains not confirmed. No GUI/client launch was performed or authorized.

### Intentional differences and gameplay boundaries

See docs/reference/compat/DEVIATIONS.md, section "Abyssal Deadspace: intentional differences and pending acceptance (2026-10-16)".

- The accepted JS baseline observed keeper.GetCurrentDungeonForCharacter returning NULL; eve.rs requires an active-run four-value dungeon tuple so the V24.01 dungeonTracking callsite can reconstruct the room. This is an explicit, evidence-based difference.
- EveJS startup administrative return selects one owner ship per run. eve.rs returns all eligible participants while preserving committed terminal/death outcomes; this follows the per-pilot downtime return rule and the shared participant authority. Multi-participant restart acceptance remains a named pending gate.
- The 2,500 m gate surface range is enforced and covered by dotnet/tests/Eve.Host.Tests/AbyssalTraceRangeTests.cs. EveJS's committed FOLLOW-approach reconciliation (server/src/abyssal/runtime/abyssalGateActivationRange.js) remains unconnected in the Space pose API. Keep this as a compatibility gap in approach reconciliation, not as an unsupported/no-op gate and not as an assumed EveJS workaround.
- SDE types, source HP, disabled decoration, saved weather TypeId, stable content selection, artifact-owned loot, and independent presentation radius provide authoritative gameplay/state values. Differences from a reference fallback require source/SDE justification; none authorizes fake success or permanent implementation-readiness refusals.

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

## Not confirmed items

- Feature status remains PARTLY: JS three-room combat/final Origin and the single-scenario RS reconnect baseline now pass; RS all4 and full same-scenario JS/RS capture parity remain not confirmed.
- The latest RS reconnect/entry baseline passes, but it does not establish RS all-four or same-scenario combat/loot/Origin parity with EveJS.
- Historical JS failures remain recorded in the captures above (ammo exhaustion/death path, BigInt Inventory setup failure, and fifth-run repair/proximity failures). Those partial runs are superseded for the solo three-room path by js-combat-origin-1792142413084 (1 PASS, 485.13 s, final Origin/Finished); RS reconnect baseline also passes. RS all4, RS combat/loot/Origin and full same-scenario parity remain open.
- The old retail transition profile has not been independently reproduced as a complete successful same-scenario sequence. New headless captures exist and are listed separately.
- A matching handler for OnAbyssalLocationExited was not found in AbyssalService; its full application elsewhere in V24.01 remains not confirmed.
- Source confirms the EveJS startup path selects one owner ship. The all-participant eve.rs recovery policy is intentional; live multi-participant restart/downtime acceptance remains pending.
- GUI/native-client acceptance is not confirmed; GUI/client launch is prohibited in this task.
- Final component/capture results must be supplied with scenario identity and cleanup result. Preserve failure history and distinguish overlapping subsets from unique test counts.
