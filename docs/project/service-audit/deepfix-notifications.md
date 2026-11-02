# Notification and persistence deep check

Date: 2026-11-02

## Scope and result

Checked two high-frequency skill flows: a queue level finishing (early progression) and Daily Alpha Injector availability/use (daily progression). Queue notification order matched. Daily Alpha preview omitted the cooldown error and use returned the wrong error code; both are fixed below. This is a bounded audit, not a claim that every notification-producing service has been checked.

## Queue level completion

- The V24.01 client receives OnServerSkillsChanged in eve/client/script/ui/services/skillsvc.py:159-176; it refreshes skills and queue at lines 160-162, then emits trained-level callbacks at 163-169, followed by event and OnSkillsChanged at 170-172. The tracked skill-plan service listens to OnSkillLevelsTrained and checks completion at skills/skillplan/skillPlanService.py:33-40,110-115. Queue cache consumers receive OnNewSkillQueueSaved in eve/client/script/ui/services/skillQueueSvc.py:761-766, updating the cached queue before scattering OnSkillQueueChanged.
- Eve.rs TrainingAnnouncer.OnSettledAsync sends OnServerSkillsChanged, OnSkillLevelsTrained, OnSkillsChanged, dogma synchronization, then queue-saved and (when empty) queue-paused notifications: dotnet/src/Eve.Services.Skills/TrainingAnnouncer.cs:34-69. Wire arities are centralized in SkillNotifications.cs:24-57.
- EveJS 261028.1 follows the same order: settlement at server/src/services/skills/training/skillQueueRuntime.js:756-780, notification fanout at server/src/services/skills/training/skillQueueNotifications.js:289-409,484-500.

## Daily Alpha Injector

- The client invokes InjectSkillpoints at eve/client/script/ui/services/skillsvc.py:976-980 and asks alphaInjectorMgr.GetNextAvailableInjection at skillsvc.py:1004-1005. The cooldown result is queried through this path.
- Eve.rs commits injector consumption and the daily-use timestamp in the store transaction; PgSkillStore.TryUseDailyAlphaSkillInjectorAsync is at dotnet/src/Eve.Skills/PgSkillStore.cs:449-470. TrainingAnnouncer.AnnounceInjectedPoints sends the consumed item's OnItemChange before OnFreeSkillPointsChanged at TrainingAnnouncer.cs:91-101. The composed Host route, notification order, grant amount, consumption, and restart persistence are asserted by CharacterHostTests.Skill_injector_applies_sequential_awards_and_persists_points_and_inventory_through_host at dotnet/tests/Eve.Host.Tests/CharacterHostTests.cs:56-86.
- Error path: client SkillInjectorWindow.CheckConstraints calls SkillsSvc.CheckInjectionConstraints at eve/client/script/ui/skilltrading/skillInjectorWindow.py:162-173 and displays AlreadyInjectedToday with the injector type and remaining cooldown at lines 175-184; skillsvc.py:1004-1008 computes that remaining duration. EveJS checkInjectionConstraints returns AlreadyInjectedToday for an exhausted daily injector at server/src/services/skills/trading/skillTradingRuntime.js:737-745, and the same error is used on the actual injection path at lines 780-836. Before this fix, Eve.rs preview returned success and actual use returned CustomNotify, so the client could not render its localized cooldown message or duration. Eve.rs now checks eligibility in SkillHandlerService.Injection.cs:112-133 and returns the same user error in the transaction at lines 165-171. Omega users receive the client-supported InjectorSkillPointLimitReached error with typeID and limit=0, as in EveJS lines 729-735; Eve.rs applies that to both preview and use.
- Persistence evidence: SkillServiceTests.Daily_alpha_clock_answers_zero_then_the_next_downtime_after_a_use_and_keeps_it_across_a_restart at dotnet/tests/Eve.Services.Skills.Tests/SkillServiceTests.cs:681-720; atomic one-per-day coverage at lines 548-562 and Alpha-only coverage at 374-407.
- EVE University documents the 50,000 SP Daily Alpha Injector and skill injector SP thresholds: [Skillbooks](https://wiki.eveuniversity.org/Skillbooks), [Skill trading](https://wiki.eveuniversity.org/Skill_trading). Eve.rs resolves item type and amount from its pinned SDE catalog; this check found no captured-packet or fixed-item shortcut in the path.

## Verification

- EVE_PG_URL=...eve_p3 with dotnet/tests/Eve.Services.Skills.Tests filtered to TrainingServiceTests and the restart/cooldown test: passed, 16 passed, 0 skipped.
- Build dotnet/src/Eve.Services.Skills: passed, 0 warnings and 0 errors. Eve.Services.Skills.Tests filtered to SkillHandlerTests: 49 passed, 0 skipped on eve_p3.
- Host wire/persistence focused test (CharacterHostTests.Skill_injector_applies_sequential_awards_and_persists_points_and_inventory_through_host): passed, 1 passed, 0 skipped on eve_p3.
- Host regression for preview and use errors (CharacterHostTests.Daily_alpha_injector_preview_and_use_return_the_client_error_after_downtime_use): the test failed against the previous handler with “No exception was thrown” at the preview assertion; after the fix it passed, 1 passed, 0 skipped on eve_p3.
- Eve.Host.Tests filtered to CharacterHostTests: 23 passed, 0 skipped on eve_p3.
- No graphical client session was used.
- The special non-diminishing injection counter has a client cache/listener in eve/client/script/ui/services/nonDiminishingInjectionService.py:11-49; its authoritative server write/grant path was not established in this bounded audit, so no conclusion about that feature is made.
- Push is held by the RED GATE: main CI run 1418 (sha 4165d420fdcf) failed Eve.Services.Space.Tests.AbyssalSpaceSessionsTests.Disconnect_persists_the_domain_transition_but_keeps_the_sim_resident_ship with ObjectDisposedException in LocalTimers.Set. This is outside this change; recheck main CI before pushing.
- At 2026-11-02 10:16 UTC, the newer main CI run 1419 (sha 110960422657) is still in progress; its remaining headless-client job has not completed. The change is staged locally and not committed or pushed.
