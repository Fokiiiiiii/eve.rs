# Handoff: NPC combat timer (cx4)

## Status

The Host now connects pilot damage notifications to CrimewatchRuntime.RecordNpcTimerAsync. The shared timer path persists max(existing, now + 5 minutes) before syncing the client notification. SpaceSessions.RecordNpcCombatTimer handles attackType me with an NPC target and attackType otherPlayerWeapons with an NPC source and pilot target. JsSemantics.StringValue is required because Marshal string-table keys and values arrive as PyBytes.

Host composition assigns the callback and includes NpcUntilMs > now in safe-logoff blockers. The PostgreSQL Space test covers both combat directions, the persisted deadline, renewal, the notification's charid/(402, FILETIME) shape, reconnect GetClientStates, and the bound SafeLogoff route before and after expiry. The test uses the Space test rig's lifecycle blocker matching the Host condition; the full process-level Host composition was compiled but not started for this scenario.

## V24.01 client trace

- eve/client/script/ui/services/crimewatchSvc.py:94-114: ProcessSessionChange calls GetClientStates, unpacks five timer pairs, and stores the NPC timer from pair index 2. OnSubscriptionChanged (:87-92) and OnSessionChanged (:116-123) also call GetClientStates for safety-level refresh and ignore the timer pairs.
- eve/client/script/ui/services/crimewatchSvc.py:234-238: OnNpcTimerUpdate(state, expiryTime) stores both values. GetNpcTimer returns that pair at :323-325.
- eve/client/script/ui/crimewatch/crimewatchTimers.py:347-358: OnCombatTimersUpdated reads GetNpcTimer; :372-382 renders active/inherited state, an expiry, or removes the timer. crimewatch/const.py:24-25 defines active state 401 and timer state 402.
- All other OnNpcTimerUpdate consumers are the nodegraph events NpcCombatStateChanged, NpcCombatStarted, and NpcCombatEnded in nodegraph/client/events/crimewatch.py:10-36.
- The safe-logoff client call path reaches MenuSvc.SafeLogoff at eve/client/script/ui/services/menusvc.py:3210-3211 from the ship menu (same file:1649), SystemMenu.SafeLogoff (:445-454), and EveCommandService.CmdLogOff (eve/client/script/ui/eveCommands.py:1232). eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:222-228 calls shipAccess.SafeLogoff() and displays returned failures. eve/client/script/parklife/gameui.py:942-979 handles safe-logoff start, activation, abort, and failure notifications.

## EveJS comparison

EveJS 261028.1 initializes and retains npcTimerExpiresAtMs (src/server/src/services/security/crimewatchState.js:324-345,484-549) and checks it in safe logoff (src/server/src/services/ship/safeLogoffRuntime.js:64-98). The current state mutation path sets npcTimerMs only for the chat /crimewatch npc command (src/server/src/services/chat/commands/crimewatch.js:320-325); source search found no damage-event hook. The intentional difference is recorded in docs/reference/compat/DEVIATIONS.md. Gameplay source: https://wiki.eveuniversity.org/GCC (EVE University Timers).

## Checks completed

- After merging origin/main at 08bdb59d, builds of Eve.Services.Space.Tests, Eve.Host.Tests, and Eve.Services.Crimewatch.Tests passed with 0 warnings and 0 errors. These transitively build the changed Space service and Host project.
- With eve_p4 and tools/dev/db-lock.sh, the NPC combat Space test, the Host GetClientStates registered-route test, and Npc_and_disapproval_timers_persist_renew_and_expire each passed after the merge (1 passed, 0 skipped each).
- An initial Crimewatch PgFact invocation without EVE_PG_URL skipped; it was rerun with the required eve_p4 URL and passed.

## Remaining checks

The graphical V24.01 client has not been run. At the latest gate check, origin/main was a09f496b4d8e and CI run 1462 had failed `Rust tests` and `Fixtures match their generators`; other jobs were still running. Its fixture failure is in `tools/dev/gen-simlink-fixtures.py` (`ship` referenced before assignment), outside this change. The red gate blocks pushing this work until a newer completed main CI run is green. Then fetch/merge origin/main, rerun the touched builds and three DB-locked focused tests, push only HEAD:main, watch the Actions run to completion, and update this handoff with its result.
