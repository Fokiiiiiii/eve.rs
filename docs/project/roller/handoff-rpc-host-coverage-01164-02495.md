# RPC Host coverage handoff: 01164–02495

## Client and EveJS evidence

The 25 rows represent 15 distinct methods. Whole-tree V24.01 caller and consumer scans found:

- `ValidateNameEx`: new creator's `CheckAvailability` compares the integer result with `VALID`, records the matching label, and accepts the name only on equality (`eve/client/script/ui/login/charcreation_new/steps/sections/chooseNameSection.py:133-156,184-194`). Legacy creation follows the same flow (`eve/client/script/ui/login/charcreation/steps/sections/chooseNameSection.py:147-170,197-207`). EveJS `server/src/services/character/charService.js:1904-1912` returns the numeric validation code.
- `GetMyEnergyState`: `eve/client/script/ui/services/characterEnergySvc.py:13-37` caches the Ex1 state for `CalculateStateAtTime` and refreshes on notifications. EveJS `characterEnergyMgrService.js:102-113,128-150,296-309` returns the Ex1 state and computes recharge.
- `GetCharacterLogs`: `eve/client/script/ui/shared/ledger/ledgerSvc.py:20-30` consumes a record list, memoizes it briefly, and clears it on session reset. EveJS `characterMiningLedgerService.js:42-53`.
- FW: `eve/client/script/ui/services/fwEnlistmentSvc.py:79-123` consumes enlistment tuple, faction lists, cooldown and mutation replies; other consumers are `modularHangarBehaviours.py:157-160`, `corp_ui_direct_enlistment_mgt.py:80,113`, `corp_ui_home.py:412`, `infosvc.py:1232`, `factionwarfare/client/enlistmentChecks.py:71`, and `enlistmentUtil.py:137`. EveJS `fwCharacterEnlistmentMgrService.js:70-143`.
- Clone: `eve/client/script/util/eveMisc.py:134` reads the structure clone count; `clonejumpsvc.py:95-99,382-400` consumes count and validation labels; `jumpClonesPanel.py:636-643` displays errors. EveJS `jumpCloneService.js:57-70`, `jumpCloneRuntime.js:2577-2584,1517-1559`.
- Skills: `eve/client/script/ui/services/skillsvc.py:760-805,858-892,975-984` consumes training, free-SP and injection-validation results; `skillInjectorWindow.py:156-181` displays injection errors. EveJS `skillMgrService.js:404-415,506-513,527-531,543-552`.
- Transport adds `machoVersion=1` when absent from the cache: `carbon/common/script/net/ServiceCallGPCS.py:221-225`, `ObjectCallGPCS.py:403-407`.

For the structure live flow, the client requires `InShipInSpace()` and reads `session.solarsystemid` before deployment (`eve/client/script/ui/services/structure/structureDeployment.py:85-111)); it calls `Anchor` with the selected item ID (`:172`). Client `SessionChanged` applies the server change map (`carbon/common/script/net/SessionChangeGPCS.py:77-110`). EveJS-compatible undock wire shape remains station/location/system; server-only `ClientSession.Set` records the already-active ship for the subsequent Host RPC.

## Change and checks

The target methods already had active Host registrations. `CharacterEnergyMgrRpcService` now allows the transport `machoVersion` keyword and rejects other unexpected keywords. Real loopback Host socket facts cover the Ex1 energy reply, all six FW calls and notifications, numeric name validation, character-less ledger list, clone count and docked error, plus skill replies/rejection. Concurrent upstream work added the composed DB-backed skill/injector Host fact.

The six no-database Host tests passed locally and on Actions run `40094928454` (0 failures, 0 skips). Selected inventory test `40099269497`, composed skill/injector Host test `40099745169`, and undock service regression run `40101176984` each passed (0 failures, 0 skips). The service project compile-check passed with 0 warnings and 0 errors. A full interim CI on `2fd39d9e` exposed that adding `shipid` to undock's SessionChange altered EveJS-observed wire shape: two undock fixture cases and one movement case failed, and structure Anchor still lacked authoritative server session state. The fix now uses silent server-side `Set`, preserving the wire contract while making shipid available to Anchor.

Commit `48d0dbf8` is on `main` at `550cf006`. The focused regression run `40102628138` passed (1 passed, 0 failed, 0 skipped). Push CI `40102612197` failed in the structure live scenario: undock and session checks passed, but the subsequent `CmdWarpToStuff` returned MachoError 2 before Anchor. The raw pytest output omitted the error payload, so the headless scenario now reports its decoded refusal and payload on rerun. The server-only `Set` change preserves the recorded undock SessionChange shape; no graphical-client verification has been completed.
