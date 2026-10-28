# Deep check: dungeonInstanceCacheMgr

## Whole-client callers and replies

All 16 registered methods have no explicit arguments. Transport machoVersion is removed by PacketDispatcher before service dispatch.

| Method | V24 caller and consumed shape |
|---|---|
| GetCombatAnomalyInstances | evedungeons/client/anomalyTracker.py:234-235; systemID to dungeon-entry list. |
| GetCombatAnomaliesCount | anomalyTracker.py:237-238; systemID to count for unknown sites. |
| GetSignatureInstances | eve/client/script/ui/shared/agencyNew/contentProviders/contentProviderSignatures.py:19-30 and mapView/filters/mapFilterSignatures.py:11-12; systemID to count or dungeon list. The Agency accepts either int count or iterable entries. |
| GetIceBeltInstances / GetIceBeltsCount | anomalyTracker.py:245-248; per-system instance lists and per-system counts. |
| GetOreAnomalyInstances / GetOreAnomaliesCount / GetOreAnomaliesCountInRange | anomalyTracker.py:256-262; per-system entries/counts, with the range method feeding the tracker’s in-range count path. |
| GetBulwarkMiningSiteCounts | anomalyTracker.py:271-272; per-system count data, unpacked into a tracker map. |
| GetFactionWarfareInstances | anomalyTracker.py:292-300; per-system lists, then client filters entries by dungeon.factionID against GetSystemOccupier. |
| GetFactionWarfareBattlefieldInstances | eve/client/script/ui/services/fwWarzoneSvc.py:162-163; the service stores the returned per-system battlefield lists. |
| GetHomefrontSiteInstances | anomalyTracker.py:303-304; per-system site lists. |
| GetPirateInsurgencyInstances | anomalyTracker.py:313-314; per-system site lists. |
| GetPirateInsurgencyIceHeistInstances | eve/client/script/ui/services/insurgenceDashboardSvc.py:293-294; per-system site lists consumed by the ice-heist dashboard. |
| GetTriglavianSiteInstances | anomalyTracker.py:326-327; per-system lists; the client returns an empty map outside Triglavian systems. |

The anomaly tracker uses defaultdict(list, map) for site groups. The whole-tree search found the additional Agency and map consumers above; no other callers for these 16 RPC names.

## Host path, state and comparison

DungeonRpc.cs:61 registers DungeonInstanceCacheMgrService with DungeonReadModel.ReadCacheSnapshotAsync. The service reads one authoritative cache snapshot and filters by active lifecycle state and SDE archetype IDs in DungeonRpcServices.cs:126-220. Entry methods return system-keyed dictionaries of lists; count methods return system-keyed integer dictionaries. GetSignatureInstances deliberately supports the client’s mixed count/list representation. Ice sites additionally require authoritative system slot data. The snapshot is the shared dungeon cache owner; the RPC adds no per-client store or mutation.

EveJS v0.12.9 exposes the same 16 no-argument handlers in dungeonInstanceCacheMgrService.js:629-702 and groups its dungeon cache into the same client-facing map shapes. The eve.rs source uses the authoritative read model and pinned archetype IDs instead of copying EveJS’s process-local cache. Returned rows preserve the client’s factionID and location fields; the client performs faction ownership filtering itself.

## Regression evidence and remaining scope

The CSV marks all 32 coverage rows for this service IMPLEMENTED-TESTED. DungeonDynamicResourceHostWireTests.cs:44-88 tests a registered dispatcher route, and ClientVerifiedBatch28CdHostWireTests.cs:173 onward tests client-shaped anomaly tracker results over the Host socket. These exercise the real Host packet path with an injected snapshot. No separate service-level error path exists for valid no-argument reads; cancellation and read-model failures propagate through normal Host error handling.

No PostgreSQL database or graphical V24 client was used for this audit. This service group has no identified coverage gap requiring a code change.
