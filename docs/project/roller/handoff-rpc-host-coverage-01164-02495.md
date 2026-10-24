# RPC Host coverage handoff: 01164–02495

## Scope and client trace

The request covers 25 call-matrix rows and 15 distinct methods. Duplicate row IDs have the same method and signature. The full V24.01 client tree was searched for every method name; direct calls and consumers are:

- ValidateNameEx — eve/client/script/ui/login/charcreation_new/steps/sections/chooseNameSection.py:182-193 caches and returns the reply as a boolean; the legacy creator also calls it at charcreation/steps/sections/chooseNameSection.py:195-205.
- GetMyEnergyState — eve/client/script/ui/services/characterEnergySvc.py:13-37 caches the state, clears it on character change, and passes it to CalculateStateAtTime; energy-change notifications replace that cached state.
- GetCharacterLogs — eve/client/script/ui/shared/ledger/ledgerSvc.py:20-30 returns the list paired with a local data version; the service memoizes it for five seconds and clears it on session reset.
- FW methods — eve/client/script/ui/services/fwEnlistmentSvc.py:79-123 consumes the three-element enlistment result, faction lists, cooldown timestamp and mutation replies. Other callers are eve/client/script/ui/view/hangarBehaviours/modularHangarBehaviours.py:157-160 (first enlistment element), ui/shared/neocom/corporation/corp_ui_direct_enlistment_mgt.py:80,113, ui/shared/neocom/corporation/corp_ui_home.py:412, ui/shared/info/infosvc.py:1232, factionwarfare/client/enlistmentChecks.py:71, and ui/shared/factionalWarfare/enlistmentFlow/enlistmentUtil.py:137.
- Clone methods — eve/client/script/util/eveMisc.py:134 displays the structure clone count; eve/client/script/ui/services/clonejumpsvc.py:95-99,382-400 forwards both replies; eve/client/script/ui/shared/neocom/charsheet/jumpClonesPanel.py:636-643 converts validation labels to displayed errors.
- Skill methods — eve/client/script/ui/services/skillsvc.py:771-796 ignores start/abort replies; skillsvc.py:866-892 updates the displayed free-point balance from the reply; skillsvc.py:981-984 forwards injection validation errors. eve/client/script/ui/skilltrading/skillInjectorWindow.py:156-181 disables the action and displays the returned user error.

## EveJS comparison

EveJS v0.12.9 provides matching behavior in server/src/services/character/charService.js:1904-1912, character/characterEnergyMgrService.js:296-309, mining/characterMiningLedgerService.js:42-53, corporation/fwCharacterEnlistmentMgrService.js:70-143, station/jumpCloneService.js:57-70 plus station/jumpCloneRuntime.js:2577-2584,1517-1559, and skills/skillMgrService.js:404-415,506-513,527-531,543-552. The inspected eve.rs routes are present; no source-level change was identified in this increment. No deviation was recorded.

## Changes and checks

Added socket-level Host registration tests in dotnet/tests/Eve.Host.Tests/CharacterFactionRpcHostTests.cs for Character Energy response shape and all six FW methods. The FW test uses the concrete service with test callbacks as its backing state provider and checks read-after-write state plus session-change notifications; it does not claim a PostgreSQL restart check.

Local checks (no database): each focused dotnet test invocation passed 1/1, skipped 0:

- Faction_warfare_enlistment_reads_and_writes_persisted_state_through_host_routes
- Character_energy_state_reaches_the_client_through_the_registered_host_route

The remaining distinct methods in this batch still need focused socket-level Host coverage before this handoff can be closed. No graphical-client check was performed.
