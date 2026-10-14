# ST-2 Anchor Duration Persistence Handoff

## Status

The canonical anchoring duration is now stored on `StructureSnapshot` and in PostgreSQL. `StructureAnchorAuthority` carries the computed SOV duration into `NewStructure`; timer replay and scheduler reconstruction use the stored snapshot value. Parent composition wiring and the parent's serial .NET verification are still required before calling the Anchor delay complete. No .NET command or Git command was run in this worker.

## Rule and Persistence Contract

- The current base rule is `StructureRules.AnchoringMs`, which defaults to `StructureTimes.AnchoringMs` (86,400,000 ms / 24 hours). `StructureRulesSource.RulesOf` returns that base rule for the deployed type/system.
- For a foreign alliance's IHub in null-sec, `StructureAnchorAuthority.ResolveSovereigntyAnchoringMsAsync` adds one day per Strategic Index level from `DevelopmentIndexRecord.ClaimedFor`. At the pinned rule boundary, `ClaimedFor=21` is level 2, so the deployed duration is 72 hours. Missing sovereignty/index data fails closed before the inventory item is deleted.
- `NewStructure.AnchoringMs` and `StructureSnapshot.AnchoringMs` are the canonical deployed value. Both default to the base rule for old call sites and legacy state. Migration `0007_anchoring_ms.sql` adds `anchoring_ms bigint NOT NULL DEFAULT 86400000 CHECK (anchoring_ms > 0)`, so pre-existing rows retain the standard duration.
- `StructureStore.CreateAsync`, `SaveAsync`, and row decoding write/read the field. `StructureAnchorAuthority` sets `AnchoringMs: anchoringMs` when building the request structure.
- `StructureTimers.ApplyAsync` and `EnsureScheduledAsync` override the static/default rule with `record.Snapshot.AnchoringMs` before `StructureStateMachine.Apply` or `NextDue`. The 15-minute `AnchorVulnerable` transition is unchanged. When its TimerDue arrives, the ensuing `Anchoring` deadline uses the persisted duration. `TimerJob` remains the structure ID; restarts re-read the snapshot instead of reconstructing a base delay from a job payload.

## Parent Composition Wiring Required

`StructureComposition.cs` is parent-owned and was not edited by this worker. Serena readback of `CharacterServicesComposition.Structures()` shows the current constructor call still supplies only the original callbacks. It must pass the existing profile/SOV owners and the duration adapter so foreign-IHub deployments can use the persisted field:

```csharp
structureAnchor = new StructureAnchorAuthority(
    source, structureStore, structureTimers, rules, foundations.Scheduler, foundations.Events, items, dogma,
    CorporationCreatedMsAsync, ShipPositionForAnchorAsync,
    session => spaceSessions is not null && spaceSessions.IsWarping(session),
    async structureId => { if (spaceStructures is not null) await spaceStructures.PlaceAsync(structureId); },
    profiles: structureProfilesRpcBackend.GetProfilesAsync,
    sov: ReadStructureAnchorSovereigntyAsync,
    anchorWithDuration: (connection, transaction, structure, anchoringMs, ct) =>
        structureTimers.AnchorAsync(connection, transaction,
            structure with { AnchoringMs = anchoringMs }, ct));
```

`StructureAnchorWiring.cs` owns `ReadStructureAnchorSovereigntyAsync` and should continue using the existing SOV read model and development-index owner. With no duration adapter, the authority intentionally refuses a deployment requiring a bonus before consuming the item.

## Regression Coverage Added

- `StructureAnchorDelayTests` checks the authority's level-2 result (base 24h + 48h), the no-foreign-IHub base path, and fail-closed behavior when a foreign IHub has no development-index row.
- `StructureAnchorTests.Foreign_IHub_deployment_persists_the_computed_duration_on_the_structure_row` exercises the real Host Authority route with the duration adapter, checks the stored 72-hour snapshot, preserves the initial 15-minute vulnerability, and checks the initial job deadline. The existing `Route.CreateAsync()` leaves the adapter disabled by default so its fail-closed test remains meaningful.
- `StructureTimerAnchoringDelayTests` uses the PostgreSQL timer/store path and a controlled clock to check the 15-minute boundary, the persisted 72-hour Anchoring deadline, restart plus `EnsureScheduledAsync`, stale TimerDue replay, base-duration legacy default, and rollback of state/job/outbox when a transaction fails before commit.

Run with `EVE_PG_URL` pointed at the pinned SDE database, in the parent's serial .NET pass:

```powershell
dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --filter "FullyQualifiedName~StructureAnchorDelayTests"
dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --filter "FullyQualifiedName~Foreign_IHub_deployment_persists_the_computed_duration_on_the_structure_row"
dotnet test dotnet/tests/Eve.Structures.Tests/Eve.Structures.Tests.csproj --filter "FullyQualifiedName~StructureTimerAnchoringDelayTests"
```

## Source Priority and Open Validation

The existing rule source and migration use the current 24-hour base. The SOV extension follows CCP's [Upwell Structure Deployment and Unanchoring](https://support.eveonline.com/hc/en-us/articles/208289335-Upwell-Structure-Deployment-and-Unanchoring), which describes the foreign-IHub Strategic Index delay; official static data is build-pinned as described in [CCP Static Data](https://developers.eveonline.com/docs/services/static-data/). EveJS v0.12.9 commit `828823a75f211fdecb945b087a26c225f111491f` and `eve-logic@1dbb` remain secondary behavior/client references.

The source changes and test diagnostics are ready for parent review. This worker has not run .NET tests, the actual Host composition callback is still missing, and the end-to-end deployed Anchor → 15-minute TimerDue → restart/replay path must pass in the parent's serial run before completion is claimed.
