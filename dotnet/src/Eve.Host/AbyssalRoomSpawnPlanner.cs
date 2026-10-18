using Eve.Content;

namespace Eve.Host;

/// <summary>
/// Reserves stable positions for the persisted room plan. Room scenes are isolated by RoomIndex,
/// so the same hidden-system center can be reused across rooms without EveJS's 12,000km same-scene spacing workaround.
/// The generic fallback layout follows EveJS's 35km conduit and 10km NPC orbit distances.
/// </summary>
public static class AbyssalRoomSpawnPlanner
{
    public const int TransferConduitTypeId = 47685;
    public const int OriginConduitTypeId = 47686;
    public const double ConduitDistanceMeters = 35_000;
    public const double NpcOrbitRadiusMeters = 10_000;
    public const double CacheFromConduitMeters = 8_000;

    public static IReadOnlyList<AbyssalSpaceEntity> Plan(
        long instanceId,
        string contentId,
        int spaceLocationId,
        int roomOrdinal,
        AbyssalRoomPlan room,
        AbyssalPosition roomCenter,
        AbyssalPosition shipDirection,
        IReadOnlyList<long> npcEntityIds,
        long gateEntityId,
        long containerEntityId,
        int containerTypeId,
        IReadOnlyDictionary<string, long>? plannedEntityIds = null)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(roomCenter);
        ArgumentNullException.ThrowIfNull(shipDirection);
        ArgumentNullException.ThrowIfNull(npcEntityIds);
        if (instanceId <= 0 || !Guid.TryParse(contentId, out _) ||
            spaceLocationId <= 0 || roomOrdinal is < 1 or > 3 || room.LocationId != roomOrdinal ||
            gateEntityId <= 0 || containerEntityId <= 0 || containerTypeId <= 0 ||
            npcEntityIds.Any(id => id <= 0) ||
            npcEntityIds.Distinct().Count() != npcEntityIds.Count ||
            npcEntityIds.Contains(gateEntityId) || npcEntityIds.Contains(containerEntityId) ||
            gateEntityId == containerEntityId)
        {
            throw new ArgumentException("Abyssal room spawn reservation has an invalid identity.");
        }

        ValidateFinite(roomCenter, nameof(roomCenter));
        ValidateFinite(shipDirection, nameof(shipDirection));
        var direction = Normalize(shipDirection);
        var lateral = Lateral(direction);
        var conduit = Add(roomCenter, Scale(direction, ConduitDistanceMeters));
        var npcCount = room.NpcSpawns.Sum(spawn => spawn.Count);
        if (npcCount != npcEntityIds.Count ||
            room.NpcSpawns.Any(spawn => spawn.TypeId <= 0 || spawn.Count < 0))
        {
            throw new InvalidDataException("The reserved Abyssal NPC IDs do not match the saved room plan.");
        }

        var gateTypeId = roomOrdinal == 3 ? OriginConduitTypeId : TransferConduitTypeId;
        var entities = new List<AbyssalSpaceEntity>(npcCount + 2)
        {
            new(gateEntityId, gateTypeId, AbyssalEntityRole.Gate, conduit),
        };

        if (room.Artifacts.Count == 0)
            entities.Add(new(containerEntityId, containerTypeId, AbyssalEntityRole.Container,
                Add(conduit, Scale(direction, CacheFromConduitMeters))));
        foreach (var artifact in room.Artifacts)
            entities.Add(new AbyssalSpaceEntity(plannedEntityIds![artifact.Key], artifact.TypeId,
                AbyssalEntityRole.Container, LocalToWorld(roomCenter, shipDirection, artifact.LocalPosition))
                { PlanKey = artifact.Key });
        foreach (var environmentalObject in room.EnvironmentObjects)
            entities.Add(new AbyssalSpaceEntity(plannedEntityIds![environmentalObject.Key], environmentalObject.TypeId,
                AbyssalEntityRole.Cloud, LocalToWorld(roomCenter, shipDirection, environmentalObject.LocalPosition))
                { PlanKey = environmentalObject.Key });

        var npcIndex = 0;
        for (var spawnIndex = 0; spawnIndex < room.NpcSpawns.Count; spawnIndex++)
        {
            var spawn = room.NpcSpawns[spawnIndex];
            for (var copy = 0; copy < spawn.Count; copy++)
            {
                var ordinal = npcIndex++;
                var angle = 2 * Math.PI * ordinal / Math.Max(1, npcCount);
                var position = Add(
                    Add(conduit, Scale(direction, Math.Cos(angle) * NpcOrbitRadiusMeters)),
                    Scale(lateral, Math.Sin(angle) * NpcOrbitRadiusMeters));
                entities.Add(new AbyssalSpaceEntity(
                    npcEntityIds[ordinal],
                    spawn.TypeId,
                    AbyssalEntityRole.Npc,
                    position));
            }
        }

        return Array.AsReadOnly(entities.ToArray());
    }

    public static AbyssalPosition LocalToWorld(AbyssalPosition center, AbyssalPosition forward, AbyssalPosition local)
    {
        ValidateFinite(local, nameof(local));
        var x = Normalize(forward);
        var y = Lateral(x);
        var z = new[] { x[1] * y[2] - x[2] * y[1], x[2] * y[0] - x[0] * y[2], x[0] * y[1] - x[1] * y[0] };
        return new(center.X + x[0] * local.X + y[0] * local.Y + z[0] * local.Z,
            center.Y + x[1] * local.X + y[1] * local.Y + z[1] * local.Z,
            center.Z + x[2] * local.X + y[2] * local.Y + z[2] * local.Z);
    }

    public static double[] DirectionToward(AbyssalPosition source, AbyssalPosition target) =>
        Normalize(new AbyssalPosition(target.X - source.X, target.Y - source.Y, target.Z - source.Z));

    private static double[] Lateral(IReadOnlyList<double> direction)
    {
        var candidate = new[] { -direction[1], direction[0], 0d };
        var length = Math.Sqrt(candidate.Sum(value => value * value));
        return length > 0
            ? [candidate[0] / length, candidate[1] / length, candidate[2] / length]
            : [0, 1, 0];
    }

    private static double[] Normalize(AbyssalPosition value)
    {
        var vector = new[] { value.X, value.Y, value.Z };
        var length = Math.Sqrt(vector.Sum(component => component * component));
        if (!double.IsFinite(length) || length <= 0)
            throw new ArgumentException("Abyssal room direction must be a finite non-zero vector.");
        return [vector[0] / length, vector[1] / length, vector[2] / length];
    }

    private static double[] Normalize(IReadOnlyList<double> value) =>
        Normalize(new AbyssalPosition(value[0], value[1], value[2]));

    private static void ValidateFinite(AbyssalPosition value, string parameterName)
    {
        if (!double.IsFinite(value.X) || !double.IsFinite(value.Y) || !double.IsFinite(value.Z))
            throw new ArgumentException("Abyssal room coordinates must be finite.", parameterName);
    }

    private static AbyssalPosition Add(AbyssalPosition origin, IReadOnlyList<double> offset) =>
        new(origin.X + offset[0], origin.Y + offset[1], origin.Z + offset[2]);

    private static double[] Scale(IReadOnlyList<double> vector, double scale) =>
        [vector[0] * scale, vector[1] * scale, vector[2] * scale];
}