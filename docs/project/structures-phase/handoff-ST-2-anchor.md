# ST-2 Anchor authority handoff

**Status:** Anchor-side rules and authority work is present in the owned files. Sovereignty duration is calculated, but its persistence through the 15-minute transition, timer replay, and restart is **still open**. Do not treat the existing base 24-hour path as completion of the sovereignty-delay requirement.

## Current Anchor contract

~~~text
StructureAnchorAuthority(
    ...,
    profiles: Func<long, CancellationToken, ValueTask<IReadOnlyList<StructureProfileRecord>>>,
    sov: Func<CancellationToken, ValueTask<(SovFwReadSnapshot, IReadOnlyList<DevelopmentIndexRecord>)>>,
    anchorWithDuration: Func<NpgsqlConnection, NpgsqlTransaction, NewStructure, long,
        CancellationToken, Task<StructureTransition?>>
)
~~~

The authority locks and validates the pilot's ship item, SDE type/system, deployment location, trade-hub and corporation-age rules, then resolves the profile and sovereignty duration **before** deleting the item. The item delete, structure creation, initial transition, scheduler write, and structure.anchored event are in one PostgreSQL transaction. Notifications/space placement happen after commit. A thrown domain error or duplicate structure id rolls the item delete back.

- An explicitly requested profile is used only if it belongs to the corporation; otherwise the current corporation default is selected. PersistenceStructureProfilesRpcBackend.GetProfilesAsync ensures a persistent default when the corporation has none.
- SDE mapSolarSystems.disallowedAnchorCategories and disallowedAnchorGroups now drive generic refused-system checks. The Authority also requires SDE category Structure and SDE rig-size data; rigged non-structure items are refused.
- The five existing trade-hub names remain explicit (Jita, Amarr, Dodixie, Rens, Hek); the SDE hub flag is broader than that list.
- Null-sec sovereignty reads fail closed if required data is unavailable. A foreign alliance may not anchor a medium Upwell structure when the official ADM rule is strictly greater than 4.0. A foreign-alliance IHub adds 24 hours per Strategic Index level. The duration currently reaches only the optional adapter; no row/snapshot field persists it yet.

The current delayed-deployment guard refuses the foreign-IHub case before item deletion when no duration adapter is supplied. The no-bonus base path can still use the old timer entry point, but that does not verify persistence of a calculated sovereignty delay.

## Authoritative data and rule evidence

- CCP's [Upwell Structure Deployment and Unanchoring](https://support.eveonline.com/hc/en-us/articles/208289335-Upwell-Structure-Deployment-and-Unanchoring) names Thera, shattered wormhole, starter/career-agent and trade-hub restrictions, describes the foreign-IHub Strategic Index delay, and states the strict > 4.0 medium-structure ADM restriction.
- sde/lock.json pins build 3396210 (client build 3396210). StructureRulesSource.LoadAsync reads the pinned mapSolarSystems restrictions; the SDE category ID for Structure is 65. The official [SDE service documentation](https://developers.eveonline.com/docs/services/static-data/) describes build-numbered static data.
- Representative current PG Anchor coverage includes Thera (31000005), shattered J-space (J015092, 31002505), starter/career systems (Kisogo, Amsen, Manifest), and trade hubs (Jita, Hek). The generic SDE restriction check covers other rows without a guessed hand-maintained list.
- EveJS v0.12.9 remains the executable secondary reference for index thresholds and profile fallback: server/src/services/structure/sovState.js (STRATEGIC_INDEX_DAY_THRESHOLDS, getStrategicIndexLevel, getSystemDefenseMultiplier), sovPlayerDeployment.js, and structureProfilesState.js (resolveUsableProfileIDForCorporation). EveJS uses >= 4 for the medium-structure gate; this implementation follows CCP's > 4.0 wording.
- Current eve.rs read owners are PersistenceSovFwReadModel.ReadSnapshotAsync and ReadAllDevelopmentIndicesAsync, and PersistenceStructureProfilesRpcBackend.GetProfilesAsync. No second sovereignty/profile store is introduced.

## Parent composition wiring

StructureComposition.cs is parent-owned. Its current StructureAnchorAuthority construction is in CharacterServicesComposition.Structures() around lines 277–281. Append the current owners there:

~~~csharp
profiles: structureProfilesRpcBackend.GetProfilesAsync,
sov: ReadStructureAnchorSovereigntyAsync,
anchorWithDuration: (connection, transaction, structure, anchoringMs, ct) =>
    structureTimers.AnchorAsync(connection, transaction,
        structure with { AnchoringMs = anchoringMs }, ct)
~~~

StructureAnchorWiring.cs supplies ReadStructureAnchorSovereigntyAsync by combining the existing read model's snapshot and development-index list. The last lambda becomes valid after the persistence addition below. Parent should preserve the current placement/event ordering and ensure a missing adapter cannot consume an item for a deployment whose calculated duration needs it.

## Minimal durable timer/store contract for the Drop owner

Keep one canonical duration on the structure row/snapshot. The timer job is only an identity and must re-read the row under its transaction.

1. Append long AnchoringMs = StructureTimes.AnchoringMs to NewStructure, and add long AnchoringMs to StructureSnapshot (init property with the base duration for legacy construction). Carry the computed value from the Authority adapter into NewStructure.
2. Add a migration column anchoring_ms bigint NOT NULL DEFAULT 86400000 for existing base-duration rows. Include it in StructureStore.Columns, row read/write, and CreateAsync mapping. Keep StructureRecord.Snapshot as the state-machine owner's persisted source.
3. In StructureTimers.ApplyAsync and EnsureScheduledAsync, derive rules from the persisted snapshot:
   _rules.RulesOf(record.TypeId, record.SolarSystemId) with { AnchoringMs = record.Snapshot.AnchoringMs }
   Use that same effective rule for StructureStateMachine.Apply and StructureStateMachine.NextDue. AnchorAsync already accepts NewStructure; its signature need not change if the field is carried through creation.
4. Leave TimerJob(long StructureId) unchanged. HandleAsync already reloads the locked structure via ApplyAsync. Adding duration only to a job payload is insufficient: EnsureScheduledAsync replaces jobs from stored snapshots and would recreate the base duration after restart.

The schema default is only a migration value for pre-existing rows whose state was created under the base rule; new Anchors must persist the computed duration. Do not silently substitute the base duration when sovereignty/index reads fail.

## Required end-to-end regression test after serial timer/store integration

Use the real Host PG Anchor route, real structure store, scheduler, and bus with a controlled clock. For a foreign-alliance IHub whose Strategic Index level is 2 (EveJS claimedFor=21 threshold), assert all of these:

1. Anchor persists AnchoringMs = StructureTimes.AnchoringMs + 2 * StructureTimes.Day (72 hours).
2. The initial AnchorPlaced state remains AnchorVulnerable and ends at Now + StructureTimes.RepairMs (15 minutes); its scheduled TimerDue is at that deadline.
3. At that first TimerDue, the transition to Anchoring ends at Now + RepairMs + 72 hours, not 24 hours.
4. Recreate the store/timer services after commit and call EnsureScheduledAsync; the replacement job retains the same persisted deadline. Change current SOV index input before the TimerDue and verify the already-deployed structure keeps its deployment-time duration.
5. Replay the first TimerDue/stale job and assert no duplicate transition, event, or shortened/recomputed deadline.
6. Control cases: no foreign IHub or index level 0 uses the base duration; foreign medium deployment at ADM 4.0 is allowed, above 4.0 is refused, and the claiming alliance is allowed.
7. Missing profile/SOV/index data and duplicate structure-id races fail before commit or roll back the item; no domain refusal consumes the item.

Existing focused authority tests cover representative restricted systems, the profile/default selection, ADM/alliance boundary, delay calculation, and refusal before consumption when the duration adapter is absent. They do not prove the durable 15-minute → TimerDue → restart/replay path.

## Owned-file boundary and remaining work

Anchor authority/rules/test changes: StructureAnchorAuthority.cs, StructureRulesSource.cs, StructureAnchorTests.cs, plus StructureAnchorWiring.cs.

Timer/store migration and the durable integration regression remain with the Drop owner and parent for serial integration: StructureTimers.cs, StructureStore.cs, StructureModel.cs, and the structures migration. This handoff records the required patch/test contract; it does not mark ST-2 delay complete.
