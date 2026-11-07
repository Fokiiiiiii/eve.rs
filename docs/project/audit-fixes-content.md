# Content audit r14 fixes

Input: `/home/user/reports/90-audit-eve-content-r14.md` (audit dated 2026-11-07). The report's three findings were confirmed against the current `Eve.Content` code and tests; none was a false positive.

## Confirmed findings and changes

### P2: saved objective and action registrations were not checked on restart

Before the fix, `AttachAsync` validated current definitions and restored saved objective topics, but it did not verify the saved active objective types or the actions copied into active instances. The event path indexes `_types` in `ContentRuntime.OnEventAsync`; `FinishAsync` fails when a saved action is missing. This could leave a persisted active instance unable to advance or finish.

`AttachAsync` now checks every waiting/active objective and both saved action lists for each active instance before attaching the event subscriber. Startup fails with the instance, objective/action name and definition in the error. It checks saved rows even when they have no event topic.

Regression tests: `Restart_refuses_active_content_when_its_saved_objective_type_is_not_registered` and `Restart_refuses_active_content_when_its_saved_action_is_not_registered`. Both failed before the fix because `AttachAsync` returned successfully; both pass after the fix.

### P2: non-finite targets and deltas could corrupt objective progress

Before the fix, definition targets were only constrained to be greater than zero in SQL, and `ProgressAsync` accepted NaN/infinity/negative deltas. Custom objective event effects also reached progress application without validation. A NaN could persist permanently; positive infinity could complete an objective.

`Define` now requires finite positive targets. `ProgressAsync`, the shared progress mutation and event-effect application require finite non-negative deltas. Migration `0002_finite_objective_values.sql` adds PostgreSQL checks that reject invalid target and progress values even for writers that bypass the runtime.

Regression tests: `Definitions_reject_objective_targets_that_are_not_finite_and_positive`, `Progress_rejects_invalid_deltas_without_mutating_the_objective`, `Objective_progress_constraints_reject_nonfinite_values_in_postgres`, and `Objective_event_effects_cannot_complete_content_with_infinite_progress`. The API and target tests failed before the fix by accepting invalid values; the DB test verifies the named constraints, and the event test verifies that an infinite custom effect cannot complete content.

### P3: parallel content with no required objectives did not complete at start

`ObjectiveOrder.Parallel` is documented to complete when all required objectives are done (`ContentTypes.cs`). With no required objectives that condition is already true, but the prior implementation evaluated it only after progress. `StartAsync` now evaluates the shared completion logic immediately for parallel definitions with no required objectives, including an empty objective list. Sequential content with no objectives stays active until its deadline; resource-bearing wormhole sites can have no objectives, and completing them at start removes their rocks or clouds from the active site list. The normal parallel completion path skips optional objectives, runs end actions, cancels its deadline and publishes `completed` after `started` in the same transaction.

For active parallel rows created by an older version, `AttachAsync` schedules a durable `content.reconcile` job only when such rows exist. Sequential rows with no objectives are excluded. `Foundations.StartAsync` starts the EventBus before the scheduler; therefore the completion event is published after subscribers establish their `StartAt.Now` cursors. The job uses the normal finish path and is safe to retry under the scheduler transaction.

Regression tests: `Parallel_content_with_only_optional_objectives_completes_at_start` verifies terminal state, skipped objective, action, deadline cancellation and `started` then `completed` notification order. `Restart_completes_saved_parallel_content_with_no_required_objectives` recreates an old active row, initializes a new subscriber cursor, runs the scheduled reconciliation and verifies persisted completion plus delivery of the completion event. Both tests failed before their fixes by observing `active` after start/restart. `Sequential_content_with_no_objectives_remains_active_until_its_deadline` failed after the initial implementation because it observed `completed`; the condition was narrowed and the test now passes. The existing `A_gas_or_ore_site_that_expires_takes_its_resources_out_of_the_world_and_the_others_do_not` and `A_wormhole_system_gets_the_sites_of_its_plan_and_its_probe_targets_need_no_strength` tests exposed the same regression in site flows; both pass with the narrowed condition.

## V24.01 client and EveJS comparison

The client-wide symbol search found the Abyssal interaction at `eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:568-579`; `abyss/client/abyssal_service.py:75-94,177-183` consumes the location dictionary and the finished notification. Other consumers of `OnAbyssalContentFinished` were checked at `eve/client/script/ui/crimewatch/crimewatchTimers.py:517-522`, `nodegraph/client/events/location.py:21-27`, and `eveaudio/musicDebuggerSvc.py:16-32`.

The audited generic `ContentRuntime` rules do not define a client RPC or wire notification. The relevant client-facing Abyssal lifecycle is handled separately: EveJS `abyssalEndGateRequest.js:517-524` routes a transfer/final exit, and `abyssalDungeonNotifications.js:411-450` sends `OnAbyssalContentFinished` on `charid` with `[contentID, timerType]`. The current Host adapter sends the same notification and two values in `AbyssalNotificationAdapter.cs:208-226`; that wire path was not changed. EveJS dungeon objectives are loaded from its static `objectiveChainsByID` and `objectiveTypesByID` tables (`dungeonAuthority.js:370-384,565-573`) and its dungeon runtime stores a `currentObjectiveTypeID` (`dungeonRuntime.js:698-712`); there is no direct generic persisted objective/action registry to execute for these three internal `Eve.Content` cases.

No client-visible behavior difference from EveJS was introduced. The relevant client/EveJS code was compared, but the graphical client and an EveJS end-to-end scenario for these generic runtime-only conditions were not run.

## Verification

- Baseline regression run against the unfixed implementation: five new focused cases failed as expected (missing objective type, missing action, invalid target, invalid progress delta, and optional-only parallel content). The persisted optional-only restart case was then added and also failed before the reconciliation change.
- `Sequential_content_with_no_objectives_remains_active_until_its_deadline` failed before narrowing the completion condition because the new start-time logic ended it immediately.
- `Eve.Content.Tests`: 50 passed, 0 failed, 0 skipped after the correction, using `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p3`.
- Selected `Eve.Services.Space.Tests` classes (`InsurgencyDirectorTests`, `CosmicSiteDirectorTests`, `ContainerSiteEndToEndTests`, `DungeonWaveTests`, `DungeonWaveSpaceTests`, `SiteNpcTests`, `IncursionDirectorTests`): 50 passed, 0 failed, 0 skipped after the correction.
- Selected `Eve.Host.Tests` classes (`AbyssalHostFlowTests`, `AbyssalRunAdmissionTests`, `DailyGoalTests`): 24 passed, 0 failed, 20 skipped. The skipped cases require `EVE_ABYSSAL_SIM_BINARY` and `EVE_ABYSSAL_SIM_SDE` (`RealAbyssalSimPgFactAttribute`); they are not counted as passes.
- `dotnet build dotnet/src/Eve.Content/Eve.Content.csproj --configuration Release --no-restore -warnaserror`: succeeded after the correction, 0 warnings.
- `dotnet build dotnet/tests/Eve.Content.Tests/Eve.Content.Tests.csproj --configuration Release --no-restore -warnaserror`: succeeded after the correction, 0 warnings.
- The first push CI run (`42408158965`, commit `a2afa7cc`) caught the overly broad empty-objective completion in both resource-site expiry and wormhole-site-count tests. The same regression reproduced locally; it is fixed by this correction. A post-correction CI run has not yet completed.

Not checked: graphical-client behavior, the 20 skipped real-simulation Host cases, a production database containing values previously corrupted by NaN/infinity, or a successful post-correction main CI run. The EveJS checkout did not provide a matching generic runtime scenario; no EveJS command was run for these internal cases.
