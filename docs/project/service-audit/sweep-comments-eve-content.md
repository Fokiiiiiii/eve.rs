# Eve.Content comment and dead-code sweep

## Decisions

- Rewrote the class summary and the `StartAsync` and `ProgressAsync` summaries in `dotnet/src/Eve.Content/ContentRuntime.cs:9,298,485` to describe the implemented runtime and return behavior. Verified them against the method bodies: a non-null unique key reuses an active instance of the same kind, and `ProgressAsync` returns true only when it advances an active objective.
- Rewrote the `CounterObjective` summary in `dotnet/src/Eve.Content/ObjectiveType.cs:55` to describe its configured numeric amount field and default increment.
- Removed five section-divider comments from `ContentRuntime.cs`; they only repeated the next method group and explained no rationale.
- The source and test sweep found no stale TODO/FIXME notes. No private member or branch was removed: CBM caller/reference checks showed the candidates in use. For example, `EnvironmentalTypeIds` at `AbyssalStaticCatalog.cs:85` is used at lines 298, 502, and 512.
- Kept the Abyssal spawn-distance comment at `AbyssalStaticCatalog.cs:649`: current EveJS clamps the offset in `src/server/src/abyssal/runtime/abyssalSpawnGeometry.js:368-441`, and initializes the minimum to 15,000 meters in `abyssalManagerRuntimeGroup7.js:826-829`.
- No RPC, reply, notification, session, or persisted data contract changed. A search of the full indexed V24.01 client for `ProgressAsync` and `ContentRuntime` found no direct references.

## Checks

- `Eve.Content.Tests`: 41 passed, 0 failed, 0 skipped, using the pinned SDE build 3396210 loaded into the task database `eve_p7`.
- Referencing Host classes `AbyssalHostFlowTests`, `AbyssalRunAdmissionTests`, and `DailyGoalTests`: 24 passed, 0 failed, 20 skipped. The skips are excluded from the pass count.
- `Eve.Content.csproj` build: passed with 0 warnings and 0 errors.
