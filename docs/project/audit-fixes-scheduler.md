# Scheduler audit fix: wormhole slim-state retry (2026-11-10)

## Finding

The report's only finding, P2 "a deadline job is consumed after a failed slim-state update," is confirmed. Before the fix, `WormholeJobs.UpdateSlimStateAsync` registered the simulation update as an `AfterCommit` callback. `DurableScheduler` deleted the one-shot row and committed before running callbacks (`dotnet/src/Eve.Scheduler/DurableScheduler.cs:366-379`); `JobContext.CompleteAsync` logged callback exceptions and continued (`dotnet/src/Eve.Scheduler/JobContext.cs:56-62`). The Host callback also caught simulation errors (`dotnet/src/Eve.Services.Space/SpaceWormholes.cs:119-131`). A database read or simulation send failure therefore did not reach scheduler retry.

The V24.01 client uses these fields in two visible paths: the information window maps `slimItem.wormholeAge` and `wormholeSize` to age and stability text (`/home/user/eve-logic/eve/client/script/ui/shared/info/infoContentBase.py:1519-1536`), and the graphics controller maps them to `WH_Age` and `WH_Size` (`/home/user/eve-logic/evegraphics/controllers/clientControllerRegistry.py:153-165`). A slim update is applied to the live wormhole object by `OnSlimItemUpdated` (`/home/user/eve-logic/eve/client/script/environment/spaceObject/wormhole.py:42-45`). A whole-checkout source-text search for both field names found these three client files.

No audit finding was false or skipped.

## Reference behavior

In EveJS 261108.1, the generic durable scheduler awaits a handler and then acknowledges it; a thrown handler error calls `retry` with exponential backoff (`/home/user/evejs-261108.1/server/src/services/scheduler/durableScheduler.js:255-295`). Its wormhole runtime derives the age and size from the persisted pair (`server/src/services/exploration/wormholes/wormholeRuntime.js:603-635,693-699`), recomputes visible wormhole entities during scene ticks (`server/src/services/exploration/wormholes/wormholeRuntime.js:1334-1451,1647-1657`; `server/src/space/runtime/scene/tick.js:503-510`), and broadcasts changed slim fields. The EveJS source does not use its generic scheduler for this wormhole display path.

The actual EveJS dispatcher was exercised with a one-failure scenario through `createDurableScheduler`: the first thrown handler produced `validateLease` then `retry`; the next successful attempt produced `validateLease` then `ack`. The ordered-process client and logger were injected at the scheduler boundary, so this checked dispatcher behavior without EveJS production storage or a live wormhole scene.

## Fix

- `UpdateSlimStateAsync` now requests slim-state synchronization while the durable job transaction is active. Exceptions escape the handler, so `DurableScheduler` rolls the transaction back and schedules its existing retry. The field values are reread from `WormholeWorld` state and applied to both active ends; replaying a snapshot is idempotent.
- Host composition calls `SpaceSessions.BindWormholes`, which connects the registered world to the retryable callback (`dotnet/src/Eve.Host/Composition/SpaceComposition.cs:282-295`).
- The retryable path awaits `SimClient` writing each frame to the active simulation stream. Offline state or a full outbound queue returns failure; a stream write error faults the update. The scheduler therefore retains the work on those failures. The current sim-link protocol has no acknowledgement for `SetSlimField`, so the checked transport boundary is successful stream write, not confirmation that the simulation applied the frame.
- The accepted-jump state callback remains post-commit and best-effort. It is a separate path from the persisted deadline job.

## Regression coverage

- `WormholeJumpServiceTests.A_failed_age_bucket_slim_update_retries_after_a_scheduler_restart` injects a one-time failure, asserts the row stays pending with attempt 1, reconstructs the scheduler, and verifies both ends receive the authoritative age and size before the row is removed.
- `WormholeJumpServiceTests.A_slim_state_job_is_not_consumed_when_the_simulation_is_offline` checks an offline simulation does not consume the scheduled state update and that reconnecting allows the retry to finish.
- `SimClientTests` cover a disconnected link, successful delivery over an active link, and a stream write failure after queueing.

## Verification status

The regression test was added before the fix. To verify it detected the original bug, the `AfterCommit` implementation was temporarily restored and the focused PostgreSQL test was run: it failed at `Assert.NotNull(pending)` because the job row had already been deleted (1 failed, 0 skipped). The fixed implementation was restored immediately afterward.

Release builds with `-warnaserror` succeeded with 0 warnings and 0 errors for `Eve.Space`, `Eve.Services.Space`, `Eve.Host`, `Eve.Wormholes`, `Eve.Space.Tests`, `Eve.Services.Space.Tests`, `Eve.Host.Tests`, `Eve.Wormholes.Tests`, and `Eve.Scheduler.Tests`.

Focused tests actually run:

| Test selection | Database | Result |
| --- | --- | --- |
| `WormholeJumpServiceTests` | `eve_p3` | 13 passed, 0 skipped |
| `WormholeJumpServiceTests` | `eve_p11` | 13 passed, 0 skipped |
| `SchedulerTests` | `eve_p3` / `eve_p11` | 18 passed on each, 0 skipped |
| `WormholeLifecycleTests` + `TheraConnectionsTests` | `eve_p3` / `eve_p11` | 12 passed on each, 0 skipped |
| `SimClientTests` | none | 40 passed, 0 skipped |
| registered Host polarization RPC test | none | 1 passed, 0 skipped |

`eve_p11` was initially missing SDE data. The pinned SDE build 3396210 was loaded from `.cache/sde`, then the database-backed tests were rerun successfully. The EveJS scheduler module was also executed with a one-failure scenario using injected logger and ordered-process client dependencies; this checked the handler/retry/ack behavior without EveJS production storage or a live scene.

The V24.01 and EveJS source traces are complete, including whole-checkout search for the two slim-field names. A graphical client session and a production Host/simulation run were not performed. `SetSlimField` has no sim-link acknowledgement, so this fix confirms a successful stream write, not simulation-side application. Codebase-memory/Serena tools were unavailable; source reads and line checks used local shell/Python tooling instead.
