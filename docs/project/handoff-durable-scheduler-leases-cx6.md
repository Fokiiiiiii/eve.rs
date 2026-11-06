# Durable scheduler lease recovery handoff (cx6)

## Result

The scheduler no longer parks a recurring job after its last claimed lease expires. Recovery skips locked exhausted rows, so a handler transaction holding one row does not stall unrelated due work. A scheduler invocation claims at most `min(BatchSize, Concurrency)` jobs per pass, preventing jobs waiting for workers from accumulating attempts before their handler starts.

The completion transaction, lease ownership checks, error backoff, and replacement behavior were left intact. No host RPC or notification payload changed.

## Evidence and compatibility

- `docs/design/FOUNDATIONS.md:79` says recurring failures retry at their next occurrence and are never parked.
- EVE University describes Expert Systems as temporary skill access and says the granted skills are removed when the system expires: [Expert Systems](https://wiki.eveuniversity.org/Expert_Systems).
- EveJS 261028.1 `src/server/src/services/scheduler/durableSchedulerProcess.worker.js:172-211` resets expired running work to scheduled before dispatch. The current eve.rs scheduler additionally retains its existing exhausted one-shot failure behavior.
- Host registers `skills.expert-systems.expire` every ten seconds in `dotnet/src/Eve.Host/Composition/CharacterComposition.cs:151-155`. `ExpertSystemMgrService.ExpireDueAsync` removes due grants in a transaction and publishes the existing service update after commit (`dotnet/src/Eve.Services.Skills/ExpertSystemMgrService.cs:148-211`).
- Whole V24 client search found the cache and update order in `/home/user/eve-logic/expertSystems/client/service.py:40-53`: the update replaces the cached map before scattering `OnExpertSystemsUpdated`. The cache is fetched through `GetMyExpertSystems` lazily and resets on character session change.
- Other V24 consumers checked: `skills/client/skillController.py:57-58`; `eve/client/script/ui/shared/shipTree/shipTreeShipGroup.py:90-96`; `expertSystems/client/insider.py:14`; `expertSystems/client/util.py:21,64`; `expertSystems/client/ui/fanfare.py:75-83`; `expertSystems/client/ui/character_sheet/home/controller.py:122-123,143-145`; `expertSystems/client/ui/character_sheet/state_icon.py:16,32-33,63-65`; `expertSystems/client/ui/icon/associated.py:22-23,50-65`; and `eve/client/script/ui/control/typeList/type_list_window.py:38,94-95,169-179`. These consume the existing map and local update event; the wire shape remains unchanged.

## Verification

- `dotnet build dotnet/src/Eve.Scheduler/Eve.Scheduler.csproj -m:1 -p:UseSharedCompilation=false`: passed.
- `dotnet build dotnet/tests/Eve.Scheduler.Tests/Eve.Scheduler.Tests.csproj -m:1 -p:UseSharedCompilation=false`: passed.
- PostgreSQL `eve_p6`, through `tools/dev/db-lock.sh`: focused `SchedulerTests`, 18/18; neighboring Host, Content, Corporations, Insurgency, Market, Relations, Space, Structures, and Wormholes checks, 379/379. Total: 397 passed, none skipped.
- The existing single-job exhausted-lease test still confirms one-shot jobs can fail. New regressions confirm recurring recovery without restart, nonblocking cleanup with a held row lock, and no attempts for jobs waiting beyond concurrency.
- Comparison to EveJS 261028.1 worker behavior is complete. A graphical V24 client session was not exercised.
- Fix commit: `76b7129a`; merged current main at `4848bdec6b74b7fe97bf6edce4cace2c4f603d27`. After that merge, the same selected test groups passed again: 397 passed, none skipped.
- Origin main later advanced to `6330da834735fe7c0ed508f4c0d12b773fd2b22b`, including the SDE full-table cache and its `CatalogTests` updates. The resulting merge `af28d5414aa05526c92ec46a4adf0a040fdf3eeb` passed the scheduler and selected caller tests plus `CatalogTests`: 415 PostgreSQL tests passed, none skipped.
- Most recent completed main CI before push: run 1549 (`d7449b80886b`), all jobs and the required gate succeeded.
- Pushed to main as `ba22d82a8d974f1f6ba43cb346af7f03a6433767`. Selected agent test run 1349 (`42168394768`) and push CI run 1550 (`42168346076`) both completed successfully; `ci-status.py failures` reported no failed tests or jobs. The selected workflow skipped its optional headless-client installation step because `--live ''` was requested; this was a setup step, not a test. The full CI end-to-end job passed using the headless client. No graphical V24 client session was run.
- Main CI run 1551 for `6330da83` also completed successfully across all jobs, including the required gate.
