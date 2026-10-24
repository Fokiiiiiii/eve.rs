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

The target methods already had active Host registrations. `CharacterEnergyMgrRpcService` rejected every kwargs dictionary; it now allows only transport `machoVersion`, while continuing to reject other keywords. Six loopback Host socket facts cover the Ex1 energy reply, all six FW calls and change notifications, numeric name-validation code, character-less ledger list, clone count and docked-state error label, and the skill training/free-point no-character replies plus injection rejection. Every call supplies transport metadata through Host dispatch.

Focused no-database tests passed (0 skipped): `Character_energy_state_reaches_the_client_through_the_registered_host_route`, `Faction_warfare_enlistment_routes_read_write_and_change_session_state`, `Char_unbound_name_validation_returns_the_numeric_code_consumed_by_creation_ui`, `Character_mining_ledger_returns_empty_rows_for_a_session_without_a_character`, `Jump_clone_reads_return_count_and_install_validation_labels_through_host`, and `Skill_training_and_injection_calls_reach_the_registered_host_routes`.

Tests for ledger, clone and skills cover the no-character or not-docked paths; they do not validate successful database-backed ledger reads, clone persistence/valid installation, or skill training/injection state mutations. FW uses callback-backed state and does not test PostgreSQL restart durability. No graphical-client check was performed. Push `0956182b` Actions run `40094148735` succeeded with no failed tests or jobs; the current changes still need a push and Actions run.
