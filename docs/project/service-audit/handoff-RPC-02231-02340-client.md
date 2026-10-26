# P1 client verification batch 7 handoff

## Current state

- Worktree: `/home/user/wt-cx4`; base pushed commit is `299b1111`.
- Updated `docs/project/service-audit/CLIENT_VERIFIED.md` with all 30 row IDs and per-row client, handler, and EveJS comparison.
- Modified `dotnet/tests/Eve.Host.Tests/PublicQaAndPvpHostWireTests.cs`: the registered-Host delayed-rewards test now also calls `rewardMgr.GetRewardLPLogs` and asserts the empty PyList response.
- Compile check passed: `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false` (0 warnings/errors).
- Focused test passed: `Delayed_incursion_rewards_return_the_rows_consumed_by_the_journal_through_host_dispatch` (1 passed, 0 skipped).
- No PostgreSQL database or graphical client was used.

## Remaining work

1. Root-fix `RepairItemsInStructure`: current handler rejects it while EveJS implements repair. Do not replace it with an empty success.
2. Add registered-Host success-shape tests for station repair, shared-setting persistence, ship cosmetics mutations and successful free-skill-point application. Current skill wire test covers no-character None, not the integer the client consumes after a successful `ApplyFreeSkillPoints`.
3. Dispatch and inspect the focused GitHub Actions run and push-triggered CI, then commit/fetch/merge/push with the required identity. This is a partial audit increment; the above behavior gaps are open.

## Key source evidence

- eve-logic repair caller: `eve/client/script/ui/station/repairshop/base_repairshop.py:66-69`.
- eve-logic reward consumers: `incursionSvc.py:254-259`, `journal.py:782,832-833`, `lpIncursionsPanel.py:73-74`.
- eve-logic shared-settings consumers: `eveEditPlainText.py:169-171`, `filterSvc.py:449-451`.
- eve-logic cosmetics callers: `shipUtil.py:27-28`, `cosmeticsSvc.py:161`, `devMenuFunctions.py:8-36,77-84`.
- eve-logic skill consumers: `skillsvc.py:770-772,795,885,972,982,1016`.
- Detailed row-by-row citations and verdicts are in `CLIENT_VERIFIED.md`.
