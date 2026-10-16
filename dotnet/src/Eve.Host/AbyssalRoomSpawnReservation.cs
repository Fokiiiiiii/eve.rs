using Eve.Content;
using Eve.Inventory;
using Eve.Sde;
using Eve.Ships;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// Reserves the persistent NPC, gate, and cache identities and freezes their room positions
/// before any space objects are published.
/// </summary>
public sealed class AbyssalRoomSpawnReservation(
    SdeReader sde,
    PgItemStore items,
    ItemTypeCatalog itemTypes,
    AbyssalStaticCatalog catalog,
    AbyssalContentRuntime runtime)
{
    public async Task<AbyssalRoomSpawnRequestedEvent> PrepareAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AbyssalRunState run,
        int locationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(run);
        var data = run.Data;
        if (run.InstanceId <= 0 || !Guid.TryParse(data.ContentId, out _) ||
            data.SpaceLocationId <= 0 || locationId is < 1 or > 3)
        {
            throw new InvalidDataException("The Abyssal room spawn request has an invalid run or room identity.");
        }

        var roomIndex = data.Rooms.ToList().FindIndex(room => room.LocationId == locationId);
        var planIndex = data.Plan.Rooms.ToList().FindIndex(room => room.LocationId == locationId);
        if (roomIndex < 0 || planIndex < 0)
            throw new InvalidDataException($"Abyssal run {run.InstanceId} has no plan/state for room {locationId}.");
        var room = data.Rooms[roomIndex];
        var roomPlan = data.Plan.Rooms[planIndex];

        if (!room.SpawnPrepared)
        {
            if (run.ContentState != "active" || locationId != data.CurrentRoomIndex || data.Participants.Count == 0)
                throw new InvalidDataException($"Abyssal room {locationId} cannot be prepared before an active participant enters it.");

            var roomCenter = await ReadSystemCenterAsync(data.SpaceLocationId, cancellationToken).ConfigureAwait(false);
            var anchorDirection = data.Participants[0].Origin.ShipDirection;
            var npcEntityIds = new List<long>();
            foreach (var spawn in roomPlan.NpcSpawns)
            {
                if (spawn.TypeId <= 0 || spawn.Count < 0 ||
                    !catalog.NpcTypesByTypeId.ContainsKey(spawn.TypeId))
                {
                    throw new InvalidDataException($"Abyssal NPC type {spawn.TypeId} is not present in the pinned SDE catalog.");
                }

                for (var count = 0; count < spawn.Count; count++)
                {
                    npcEntityIds.Add(await items.CreateAsync(
                        connection,
                        transaction,
                        new NewItem(
                            spawn.TypeId,
                            OwnerId: 0,
                            data.SpaceLocationId,
                            CharacterShips.InSpaceFlag,
                            Quantity: 1,
                            Singleton: true),
                        cancellationToken).ConfigureAwait(false));
                }
            }

            var gateTypeId = locationId == 3
                ? AbyssalRoomSpawnPlanner.OriginConduitTypeId
                : AbyssalRoomSpawnPlanner.TransferConduitTypeId;
            var gateType = await itemTypes.GetAsync(gateTypeId, cancellationToken).ConfigureAwait(false);
            if (gateType?.GroupId != 366)
                throw new InvalidDataException($"Abyssal conduit type {gateTypeId} is absent from SDE group 366.");

            var cacheType = catalog.MainCacheTypeFor(data.GameModeId);
            var cacheItemType = await itemTypes.GetAsync(cacheType.TypeId, cancellationToken).ConfigureAwait(false);
            if (cacheItemType?.GroupId != cacheType.GroupId || cacheType.GroupId != 2009)
                throw new InvalidDataException($"Abyssal cache type {cacheType.TypeId} disagrees with the pinned SDE group 2009.");

            var gateItemId = await items.CreateAsync(
                connection, transaction,
                new NewItem(gateTypeId, data.CreatorCharacterId, data.SpaceLocationId,
                    CharacterShips.InSpaceFlag, Quantity: 1, Singleton: true),
                cancellationToken).ConfigureAwait(false);
            var plannedIds = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var planned in roomPlan.EnvironmentObjects.Select(value => (value.Key, value.TypeId, Cache: false))
                .Concat(roomPlan.Artifacts.Select(value => (value.Key, value.TypeId, Cache: true))))
            {
                var type = await itemTypes.GetAsync(planned.TypeId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException($"Abyssal planned type {planned.TypeId} is absent from SDE.");
                var id = await items.CreateAsync(connection, transaction,
                    new NewItem(type.TypeId, AbyssalTraceFactory.TraceOwnerId, data.SpaceLocationId,
                        CharacterShips.InSpaceFlag, 1, Singleton: true), cancellationToken).ConfigureAwait(false);
                plannedIds.Add(planned.Key, id);
                if (planned.Cache)
                    await items.SetCustomInfoAsync(connection, transaction, id,
                        AbyssalCacheSettlementProcessor.InitialMetadata(data.ContentId, locationId), cancellationToken).ConfigureAwait(false);
            }
            var cacheItemId = roomPlan.Artifacts.Count > 0
                ? plannedIds[roomPlan.Artifacts[0].Key]
                : await items.CreateAsync(
                connection, transaction,
                new NewItem(cacheType.TypeId, AbyssalTraceFactory.TraceOwnerId, data.SpaceLocationId,
                    CharacterShips.InSpaceFlag, Quantity: 1, Singleton: true),
                cancellationToken).ConfigureAwait(false);

            var entities = AbyssalRoomSpawnPlanner.Plan(
                run.InstanceId,
                data.ContentId,
                data.SpaceLocationId,
                locationId,
                roomPlan,
                roomCenter,
                anchorDirection,
                npcEntityIds,
                gateItemId,
                cacheItemId,
                cacheType.TypeId,
                plannedIds);

            await items.SetCustomInfoAsync(
                connection,
                transaction,
                cacheItemId,
                AbyssalCacheSettlementProcessor.InitialMetadata(data.ContentId, locationId),
                cancellationToken).ConfigureAwait(false);

            var prepared = await runtime.PrepareRoomSpawnAsync(
                connection,
                transaction,
                new AbyssalRoomSpawnRequest(run.InstanceId, locationId, entities),
                cancellationToken).ConfigureAwait(false);
            if (prepared.State.Data.Rooms.Single(item => item.LocationId == locationId).SpawnPrepared is false)
                throw new InvalidDataException($"Abyssal room {locationId} spawn reservation was not persisted.");
            room = prepared.State.Data.Rooms.Single(item => item.LocationId == locationId);
            data = prepared.State.Data;
        }
        else
        {
            foreach (var entity in room.Entities)
            {
                var item = await items.LockAndGetAsync(connection, transaction, entity.EntityId, cancellationToken)
                    .ConfigureAwait(false);
                if (item is null || item.TypeId != entity.TypeId ||
                    item.LocationId != data.SpaceLocationId ||
                    item.Flag != CharacterShips.InSpaceFlag)
                {
                    throw new InvalidDataException($"Abyssal room entity {entity.EntityId} is missing or mismatched.");
                }
            }
        }

        return new AbyssalRoomSpawnRequestedEvent(
            run.InstanceId,
            data.ContentId,
            data.SpaceLocationId,
            locationId,
            data.Tier,
            data.WeatherType,
            roomPlan.EnvironmentProfileId,
            room.Entities,
            roomPlan.Loot)
        {
            EnvironmentObjects = roomPlan.EnvironmentObjects,
            Artifacts = roomPlan.Artifacts,
        };
    }

    internal async Task<AbyssalPosition> ReadSystemCenterAsync(int solarSystemId, CancellationToken cancellationToken)
    {
        var rows = await sde.ReadAsync("mapSolarSystems", [(long)solarSystemId], cancellationToken).ConfigureAwait(false);
        if (rows.Count != 1)
            throw new InvalidDataException($"Abyssal hidden system {solarSystemId} is absent from the pinned SDE.");
        var doc = rows[0].Doc;
        var position = doc.TryGetProperty("position", out var positionNode) ? positionNode : doc;
        if (!position.TryGetProperty("x", out var x) || !x.TryGetDouble(out var px) ||
            !position.TryGetProperty("y", out var y) || !y.TryGetDouble(out var py) ||
            !position.TryGetProperty("z", out var z) || !z.TryGetDouble(out var pz) ||
            !double.IsFinite(px) || !double.IsFinite(py) || !double.IsFinite(pz))
        {
            throw new InvalidDataException($"Abyssal hidden system {solarSystemId} has no finite SDE spatial center.");
        }
        return new AbyssalPosition(px, py, pz);
    }


}