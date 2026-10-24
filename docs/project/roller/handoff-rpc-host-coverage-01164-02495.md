# RPC Host coverage handoff: 01164–02495

## Client and EveJS evidence

The 25 rows represent 15 distinct methods. Whole-tree V24.01 caller and consumer scans found:

- `ValidateNameEx`: new creator's `CheckAvailability` compares the integer result with `VALID`, records the corresponding label, and sets the accepted character name only on equality (`eve/client/script/ui/login/charcreation_new/steps/sections/chooseNameSection.py:133-156)); `IsNameValid` caches the service result (`:184-194`). Legacy creation does the same (`eve/client/script/ui/login/charcreation/steps/sections/chooseNameSection.py:147-170,197-207`). EveJS `server/src/services/character/charService.js:1904-1912` also returns the numeric validation code.
- `GetMyEnergyState`: `eve/client/script/ui/services/characterEnergySvc.py:13-37` caches the returned state for `CalculateStateAtTime` and clears/refreshes it on character and energy notifications. EveJS `characterEnergyMgrService.js:102-113,128-150,296-309` returns the Ex1 state and computes recharge.
- `GetCharacterLogs`: `eve/client/script/ui/shared/ledger/ledgerSvc.py:20-30` consumes a record list with local version, briefly memoizes it, and clears it on session reset. EveJS `characterMiningLedgerService.js:42-53`.
- FW: `eve/client/script/ui/services/fwEnlistmentSvc.py:79-123` consumes enlistment tuple, faction lists, cooldown and mutation replies; callers/consumers also include `eve/client/script/ui/view/hangarBehaviours/modularHangarBehaviours.py:157-160`, `eve/client/script/ui/shared/corporation/corp_ui_direct_enlistment_mgt.py:80,113`, `corp_ui_home.py:412`, `eve/client/script/ui/services/infosvc.py:1232`, `factionwarfare/client/enlistmentChecks.py:71`, and `enlistmentUtil.py:137`. EveJS `fwCharacterEnlistmentMgrService.js:70-143`.
- Clone: `eve/client/script/util/eveMisc.py:134` reads the structure clone count; `eve/client/script/ui/services/clonejumpsvc.py:95-99,382-400` consumes the count and validation labels; `jumpClonesPanel.py:636-643` displays errors. EveJS `jumpCloneService.js:57-70`, `jumpCloneRuntime.js:2577-2584,1517-1559`.
- Skills: `eve/client/script/ui/services/skillsvc.py:760-805,858-892,975-984` consumes training, free-skill-point and injection-validation replies; `skillInjectorWindow.py:156-181` displays and reacts to injection errors. EveJS `skillMgrService.js:404-415,506-513,527-531,543-552`.
- Transport adds `machoVersion=1` absent a cached version: `carbon/common/script/net/ServiceCallGPCS.py:221-225`, `ObjectCallGPCS.py:403-407`.

## Change and checks

The target methods already had active Host registrations. `CharacterEnergyMgrRpcService` rejected every kwargs dictionary; it now allows only transport `machoVersion`, while continuing to reject other keywords. Six loopback Host socket facts cover the Ex1 energy reply, all six FW calls and change notifications, numeric name-validation code, character-less ledger list, clone count and docked-state error label, and skill no-character replies plus injection rejection. A concurrent main update replaced the latter with a DB-backed skill/injector Host fact.

The six no-database Host tests passed locally and on selected Actions run `40094928454` for `d149e933` (0 failed, 0 skipped). Ledger and clone tests cover no-character/not-docked cases; they do not validate successful DB-backed ledger reads or clone persistence/valid installation. FW uses callback-backed state and does not test PostgreSQL restart durability. The concurrent skill test covers composed DB-backed behavior but has not yet passed a selected Action run.

CI run `40094922057` for `d149e933` failed in the live structure-board test and in a Host inventory test. The inventory fixture had no active ship in the character row; setting only session `shipid` was insufficient because `CharacterShips.LoadAsync` reads the persisted `Character.ShipId`. The fixture now persists that active ship before the inventory call. The structure refusal helper now retains unexpected payload text instead of raising `KeyError`, so a rerun can expose the server's actual refusal.

Selected Actions run `40098440261` confirmed the inventory test still returned `None` after the session-only change. The active-ship persistence fix was applied afterward and is awaiting a new selected run. Selected run `40097584871` over the batch tests failed without a per-test tail; inventory-only run `40098440261` identified the fixture issue. Push CI `40097565711` for `d1cdab23` remains in progress. No graphical-client verification was completed.
