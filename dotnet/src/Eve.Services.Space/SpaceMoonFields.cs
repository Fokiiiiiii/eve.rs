using System.Globalization;
using Eve.Dogma;
using Eve.Events;
using Eve.Inventory;
using Eve.Ships;
using Eve.Structures;
using Npgsql;

namespace Eve.Services.Space;

public sealed record MoonFieldMaterialized(long ExtractionId, int SolarSystemId);
public sealed record MoonFieldRocksRemoved(long ExtractionId, int SolarSystemId, long[] AsteroidIds);

/// <summary>Persists mineable rocks from a completed moon extraction and projects them to the simulation.</summary>
public sealed class SpaceMoonFields(
    MoonExtractionStore extractions,
    StructureStore structures,
    PgItemStore items,
    PgSpaceItemStore spaceItems,
    DogmaStatic statics,
    IMoonRulesSource rules,
    SpaceContainers containers)
{
    public const string FieldMaterializedTopic = "moon.field_materialized";
    public const string FieldRocksRemovedTopic = "moon.field_rocks_removed";
    public const string SubscriberName = "space.moon_fields";

    private const double BaseFieldRadius = 15_000d;
    private const double ReferenceVolumeM3 = 4_320_000d;
    private const int ReferenceRockCount = 30;
    private const int MaximumRockCount = 200;
    private const double RockSizeSpread = 0.4d;
    private const double ImprovedChance = 0.15d;
    private const double JackpotChance = 0.1d;
    private const double AsteroidExponentScale = 4e-5;

    private readonly MoonExtractionStore _extractions = extractions;
    private readonly StructureStore _structures = structures;
    private readonly PgItemStore _items = items;
    private readonly PgSpaceItemStore _spaceItems = spaceItems;
    private readonly DogmaStatic _statics = statics;
    private readonly IMoonRulesSource _rules = rules;
    private readonly SpaceContainers _containers = containers;

    private sealed record Ore(int TypeId, double UnitVolumeM3, long Units);
    private sealed record Rock(int Ordinal, int TypeId, long Units, double Radius, double[] Position);
    private sealed record MoonPosition(int SolarSystemId, double[] Position);

    private struct Grades
    {
        public int ImprovedTypeId;
        public int JackpotTypeId;
    }

    public void Subscribe(EventBus events) => events.Subscribe(
        SubscriberName,
        [MoonExtractions.EndedTopic, FieldMaterializedTopic, MoonExtractions.FieldDecayedTopic, FieldRocksRemovedTopic],
        DispatchAsync);

    private Task DispatchAsync(EventContext context, CancellationToken cancellationToken) => context.Event.Topic switch
    {
        MoonExtractions.EndedTopic => EndedAsync(context, cancellationToken),
        FieldMaterializedTopic => MaterializedAsync(context, cancellationToken),
        MoonExtractions.FieldDecayedTopic => DecayedAsync(context, cancellationToken),
        FieldRocksRemovedTopic => RocksRemovedAsync(context, cancellationToken),
        _ => Task.CompletedTask,
    };

    private async Task EndedAsync(EventContext context, CancellationToken cancellationToken)
    {
        var ended = context.Event.Payload<MoonExtractionEnded>()
            ?? throw new InvalidDataException($"{MoonExtractions.EndedTopic} event {context.Event.Id} has no payload");
        if (ended.Reason is not (MoonExtractions.Fractured or MoonExtractions.Decayed))
        {
            return;
        }

        if (await SeedInTransactionAsync(context.Connection, context.Transaction, ended, cancellationToken) is { } field)
        {
            await context.PublishAsync(FieldMaterializedTopic, field, $"moon-field:{field.ExtractionId}", cancellationToken);
        }
    }

    internal async Task<MoonFieldMaterialized?> SeedInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MoonExtractionEnded ended,
        CancellationToken cancellationToken = default)
    {
        if (ended.ExtractionId <= 0 || ended.StructureId <= 0 || ended.OwnerCorporationId <= 0 || ended.SolarSystemId is <= 0 or > int.MaxValue)
        {
            throw new InvalidDataException("moon extraction event has invalid identities");
        }

        await using (var advisory = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction))
        {
            advisory.Parameters.AddWithValue($"eve.rs:moon-field:{ended.ExtractionId}");
            await advisory.ExecuteNonQueryAsync(cancellationToken);
        }

        if ((await _spaceItems.MoonFieldAsync(connection, transaction, ended.ExtractionId, cancellationToken)).Count > 0)
        {
            return null;
        }

        var extraction = await _extractions.GetAsync(connection, transaction, ended.ExtractionId, forUpdate: false, cancellationToken)
            ?? throw new InvalidDataException($"moon extraction {ended.ExtractionId} is missing");
        if (extraction.StructureId != ended.StructureId || extraction.SolarSystemId != ended.SolarSystemId
            || extraction.OwnerCorporationId != ended.OwnerCorporationId || extraction.CompletedReason != ended.Reason)
        {
            throw new InvalidDataException($"moon extraction {ended.ExtractionId} does not match its ended event");
        }
        if (extraction.FieldDecayedMs > 0)
        {
            return null;
        }

        var structure = await _structures.GetAsync(connection, transaction, extraction.StructureId, forUpdate: false, cancellationToken)
            ?? throw new InvalidDataException($"moon extraction {ended.ExtractionId} has no refinery");
        if (structure.SolarSystemId != extraction.SolarSystemId || structure.OwnerCorporationId <= 0)
        {
            throw new InvalidDataException($"moon extraction {ended.ExtractionId} has an invalid refinery");
        }
        if (extraction.OreVolumeM3.Count == 0)
        {
            throw new InvalidDataException($"moon extraction {ended.ExtractionId} has no ore volumes");
        }

        var oreTypes = extraction.OreVolumeM3.Keys
            .Where(typeId => typeId > 0 && typeId <= int.MaxValue)
            .Select(typeId => (int)typeId)
            .Distinct()
            .ToArray();
        if (oreTypes.Length != extraction.OreVolumeM3.Count)
        {
            throw new InvalidDataException($"moon extraction {ended.ExtractionId} has an invalid ore type ID");
        }
        await _statics.EnsureTypesAsync(oreTypes, cancellationToken);
        var volumeAttribute = _statics.AttributeId("volume")
            ?? throw new InvalidDataException("SDE has no volume attribute");
        var ores = new List<Ore>();
        foreach (var (rawTypeId, rawVolume) in extraction.OreVolumeM3.OrderBy(pair => pair.Key))
        {
            if (!double.IsFinite(rawVolume) || rawVolume < 0)
            {
                throw new InvalidDataException($"moon extraction {ended.ExtractionId} has an invalid ore volume");
            }
            if (rawVolume == 0)
            {
                continue;
            }
            var type = _statics.Type((int)rawTypeId);
            if (type is null || !IsMoonOreGroup(type.GroupId) || !MiningTargets.IsMineable(_statics, type))
            {
                throw new InvalidDataException($"SDE type {rawTypeId} is not a published moon ore type");
            }
            var unitVolume = type.Attributes.GetValueOrDefault(volumeAttribute);
            if (!double.IsFinite(unitVolume) || unitVolume < 1d)
            {
                throw new InvalidDataException($"SDE moon ore type {rawTypeId} has no positive volume");
            }
            var rawUnits = rawVolume / unitVolume;
            if (!double.IsFinite(rawUnits) || rawUnits > long.MaxValue - 1d)
            {
                throw new InvalidDataException($"moon extraction {ended.ExtractionId} has an unrepresentable ore quantity");
            }
            var units = (long)Math.Floor(rawUnits + 1e-9d);
            if (units > 0)
            {
                ores.Add(new Ore(type.TypeId, unitVolume, units));
            }
        }
        if (ores.Count == 0)
        {
            throw new InvalidDataException($"moon extraction {ended.ExtractionId} contains less than one mineable unit");
        }

        var grades = await MoonOreGradesAsync(cancellationToken);
        var shellGraphics = await ShellGraphicsAsync(connection, transaction, cancellationToken);
        var center = await FieldCenterAsync(connection, transaction, extraction.MoonId, structure, cancellationToken);
        var radiusMultiplier = await FieldRadiusMultiplierAsync(connection, transaction, structure, cancellationToken);
        var planned = PlanRocks(extraction, ores, grades, center, radiusMultiplier);
        var fieldLifetimeMs = extraction.FieldDecayMs >= 0
            ? extraction.FieldDecayMs
            : _rules.RulesOf(structure.TypeId).FieldDecayMs;
        if (fieldLifetimeMs <= 0)
        {
            throw new InvalidDataException($"refinery type {structure.TypeId} has no positive moon field lifetime");
        }
        var endMs = extraction.CompletedMs > 0 ? extraction.CompletedMs : ended.EndedMs;
        var expiresAtMs = checked(endMs + fieldLifetimeMs);
        foreach (var rock in planned)
        {
            var type = _statics.Type(rock.TypeId)
                ?? throw new InvalidDataException($"SDE type {rock.TypeId} is missing");
            var itemId = await _items.CreateAsync(connection, transaction,
                new NewItem(rock.TypeId, 0, extraction.SolarSystemId, CharacterShips.InSpaceFlag, rock.Units, true), cancellationToken);
            var shellIndex = (int)(HashUnit($"{rock.TypeId}:{itemId}:shell") * shellGraphics.Length);
            var spaceItem = new SpaceItem(
                itemId, (int)extraction.SolarSystemId, rock.Position, [1d, 0d, 0d], rock.Radius, type.GroupId,
                0, 0, 0, 0, 0, expiresAtMs,
                extraction.ExtractionId, extraction.MoonId, extraction.StructureId, extraction.OwnerCorporationId, rock.Ordinal,
                shellGraphics[Math.Clamp(shellIndex, 0, shellGraphics.Length - 1)]);
            await _spaceItems.SaveAsync(connection, transaction, spaceItem, cancellationToken);
        }

        return new MoonFieldMaterialized(extraction.ExtractionId, (int)extraction.SolarSystemId);
    }

    private async Task MaterializedAsync(EventContext context, CancellationToken cancellationToken)
    {
        var field = context.Event.Payload<MoonFieldMaterialized>()
            ?? throw new InvalidDataException($"{FieldMaterializedTopic} event {context.Event.Id} has no payload");
        await _containers.RestoreMoonFieldAsync(field.SolarSystemId, field.ExtractionId, cancellationToken);
    }

    private async Task DecayedAsync(EventContext context, CancellationToken cancellationToken)
    {
        var decayed = context.Event.Payload<MoonFieldDecayed>()
            ?? throw new InvalidDataException($"{MoonExtractions.FieldDecayedTopic} event {context.Event.Id} has no payload");
        var rocks = await _spaceItems.MoonFieldAsync(context.Connection, context.Transaction, decayed.ExtractionId, cancellationToken);
        if (rocks.Count == 0)
        {
            return;
        }
        foreach (var rock in rocks)
        {
            if (rock.SolarSystemId != decayed.SolarSystemId || rock.MoonStructureId != decayed.StructureId)
            {
                throw new InvalidDataException($"moon field {decayed.ExtractionId} has mismatched asteroid metadata");
            }
            await _items.DeleteAsync(context.Connection, context.Transaction, rock.ItemId, cancellationToken);
            await _spaceItems.RemoveAsync(context.Connection, context.Transaction, rock.ItemId, cancellationToken);
        }
        await context.PublishAsync(
            FieldRocksRemovedTopic,
            new MoonFieldRocksRemoved(decayed.ExtractionId, (int)decayed.SolarSystemId, rocks.Select(rock => rock.ItemId).ToArray()),
            $"moon-field:{decayed.ExtractionId}", cancellationToken);
    }

    private Task RocksRemovedAsync(EventContext context, CancellationToken cancellationToken)
    {
        var removed = context.Event.Payload<MoonFieldRocksRemoved>()
            ?? throw new InvalidDataException($"{FieldRocksRemovedTopic} event {context.Event.Id} has no payload");
        _containers.RemoveMoonFieldRocks(removed.AsteroidIds);
        return Task.CompletedTask;
    }

    private async Task<double[]> FieldCenterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long moonId,
        StructureRecord structure,
        CancellationToken cancellationToken)
    {
        var moon = await MoonPositionAsync(connection, transaction, moonId, cancellationToken);
        if (moon is null)
        {
            throw new InvalidDataException($"SDE moon position {moonId} was not found");
        }
        var from = new[] { structure.X, structure.Y, structure.Z };
        if (moon.SolarSystemId != structure.SolarSystemId || from.Any(value => !double.IsFinite(value)))
        {
            throw new InvalidDataException($"refinery {structure.Snapshot.StructureId} has invalid moon coordinates");
        }
        var vector = new[] { moon.Position[0] - from[0], moon.Position[1] - from[1], moon.Position[2] - from[2] };
        var length = Math.Sqrt(vector.Sum(value => value * value));
        if (!double.IsFinite(length) || length <= 0)
        {
            throw new InvalidDataException($"refinery {structure.Snapshot.StructureId} has no direction to its moon");
        }
        var radiusId = _statics.AttributeId("radius");
        var structureRadius = radiusId is { } radiusAttribute && _statics.Type(structure.TypeId) is { } type
            ? Math.Max(0d, type.Attributes.GetValueOrDefault(radiusAttribute))
            : 0d;
        var distance = structureRadius + 100_000d;
        return [
            from[0] + vector[0] / length * distance,
            from[1] + vector[1] / length * distance,
            from[2] + vector[2] / length * distance,
        ];
    }

    private static async Task<MoonPosition?> MoonPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long moonId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT (doc->'position'->>'x')::double precision, (doc->'position'->>'y')::double precision, " +
            "(doc->'position'->>'z')::double precision, (doc->>'solarSystemID')::integer " +
            "FROM sde.\"mapMoons\" WHERE key = $1",
            connection, transaction);
        command.Parameters.AddWithValue(moonId.ToString(CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new MoonPosition(reader.GetInt32(3), [reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2)])
            : null;
    }

    internal async Task<double> FieldRadiusMultiplierAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StructureRecord structure,
        CancellationToken cancellationToken)
    {
        if (_statics.AttributeId("moonAsteroidFieldRadius") is not { } radiusId)
            return 1d;
        var contents = await _items.InContainerAsync(connection, transaction, structure.Snapshot.StructureId, cancellationToken);
        var candidates = contents.Where(item => item.OwnerId == structure.OwnerCorporationId && item.Flag is >= 92 and <= 99).ToArray();
        await _statics.EnsureTypesAsync(candidates.Select(item => item.TypeId).Append(structure.TypeId), cancellationToken);
        var structureType = _statics.Type(structure.TypeId)
            ?? throw new InvalidDataException($"SDE refinery type {structure.TypeId} is missing");
        var sizeId = _statics.AttributeId("rigSize");
        var slotsId = _statics.AttributeId("rigSlots");
        var moduleCategory = _statics.CategoryId("Structure Module");
        var size = sizeId is { } rigSize ? structureType.Attributes.GetValueOrDefault(rigSize) : 0d;
        var slots = slotsId is { } rigSlots ? structureType.Attributes.GetValueOrDefault(rigSlots) : 0d;
        var world = new DogmaWorld(_statics, structureId: structure.Snapshot.StructureId);
        world.Add(new DogmaItem(structure.Snapshot.StructureId, structure.TypeId, structure.OwnerCorporationId,
            structure.SolarSystemId, 0, ItemState.Online));
        foreach (var rig in candidates)
        {
            if (!double.IsFinite(size) || size <= 0 || !double.IsFinite(slots) || rig.Flag - 92 >= Math.Clamp(Math.Truncate(slots), 0, 8)
                || sizeId is not { } sizeAttribute || moduleCategory is not { } category
                || _statics.Type(rig.TypeId) is not { } type || type.CategoryId != category
                || type.Attributes.GetValueOrDefault(sizeAttribute) != size)
                continue;
            world.Add(new DogmaItem(rig.Id, rig.TypeId, rig.OwnerId, structure.Snapshot.StructureId, rig.Flag, ItemState.Online));
        }
        var value = world.Get(structure.Snapshot.StructureId, radiusId);
        return double.IsFinite(value) ? Math.Max(0.1d, value) : 1d;
    }

    private async Task<Dictionary<int, Grades>> MoonOreGradesAsync(CancellationToken cancellationToken)
    {
        var metaLevelId = _statics.AttributeId("asteroidMetaLevel")
            ?? throw new InvalidDataException("SDE has no asteroidMetaLevel attribute");
        var baseOreId = _statics.AttributeId("oreBasicType")
            ?? throw new InvalidDataException("SDE has no oreBasicType attribute");
        var volumeId = _statics.AttributeId("volume")
            ?? throw new InvalidDataException("SDE has no volume attribute");
        var types = await _statics.TypesInGroupsAsync(
            ["Ubiquitous Moon Asteroids", "Common Moon Asteroids", "Uncommon Moon Asteroids", "Rare Moon Asteroids", "Exceptional Moon Asteroids"],
            cancellationToken);
        var grades = new Dictionary<int, Grades>();
        foreach (var type in types.OrderBy(type => type.TypeId))
        {
            if (!IsMoonOreGroup(type.GroupId)
                || type.Attributes.GetValueOrDefault(volumeId) < 1d
                || !type.Attributes.TryGetValue(baseOreId, out var rawBase)
                || !type.Attributes.TryGetValue(metaLevelId, out var rawLevel)
                || !double.IsFinite(rawBase) || rawBase <= 0 || rawBase > int.MaxValue)
            {
                continue;
            }
            var baseTypeId = (int)rawBase;
            var grade = grades.GetValueOrDefault(baseTypeId);
            if (rawLevel == 3d)
            {
                grade.ImprovedTypeId = type.TypeId;
            }
            else if (rawLevel == 5d)
            {
                grade.JackpotTypeId = type.TypeId;
            }
            grades[baseTypeId] = grade;
        }
        return grades;
    }

    private static async Task<int[]> ShellGraphicsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT (doc->>'graphicID')::integer FROM sde.types " +
            "WHERE doc->'name'->>'en' LIKE 'Asteroid Set 2 Xsmall %' AND doc->>'graphicID' IS NOT NULL ORDER BY key::bigint",
            connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var graphics = new List<int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            graphics.Add(reader.GetInt32(0));
        }
        return graphics.Count > 0 ? [.. graphics] : throw new InvalidDataException("SDE has no moon asteroid shell graphics");
    }

    private List<Rock> PlanRocks(
        MoonExtraction extraction,
        List<Ore> ores,
        Dictionary<int, Grades> grades,
        double[] center,
        double radiusMultiplier)
    {
        var totalUnits = ores.Sum(ore => ore.Units);
        var totalVolume = extraction.OreVolumeM3.Values.Sum();
        if (totalUnits <= 0 || !double.IsFinite(totalVolume) || totalVolume <= 0)
        {
            throw new InvalidDataException($"moon extraction {extraction.ExtractionId} has no field volume");
        }

        var rockCount = Math.Min(MaximumRockCount, Math.Max(ores.Count,
            (int)Math.Floor(ReferenceRockCount * Math.Sqrt(Math.Max(1d, totalVolume / ReferenceVolumeM3)) + 0.5d)));
        var extra = Math.Max(0, rockCount - ores.Count);
        var exact = ores.Select(ore => extra * (double)ore.Units / totalUnits).ToArray();
        var counts = exact.Select(value => 1 + (int)Math.Floor(value)).ToArray();
        var leftover = rockCount - counts.Sum();
        foreach (var index in Enumerable.Range(0, ores.Count)
                     .OrderByDescending(index => exact[index] - Math.Floor(exact[index]))
                     .ThenBy(index => index).Take(leftover))
        {
            counts[index]++;
        }

        var seed = $"moon-field:{extraction.ExtractionId}";
        var jackpot = HashUnit($"{seed}:jackpot") < JackpotChance;
        var fieldRadius = BaseFieldRadius * Math.Max(0.1d, radiusMultiplier);
        var rocks = new List<Rock>(rockCount);
        for (var oreIndex = 0; oreIndex < ores.Count; oreIndex++)
        {
            var ore = ores[oreIndex];
            var count = Math.Min(counts[oreIndex], checked((int)Math.Min(int.MaxValue, ore.Units)));
            var weights = Enumerable.Range(0, count)
                .Select(index => 1d + RockSizeSpread * (HashUnit($"{seed}:size:{ore.TypeId}:{index}") * 2d - 1d))
                .ToArray();
            var weightTotal = weights.Sum();
            long assigned = 0;
            for (var index = 0; index < count; index++)
            {
                var ordinal = rocks.Count + 1;
                var units = index == count - 1
                    ? ore.Units - assigned
                    : Math.Max(1L, (long)Math.Floor(ore.Units * weights[index] / weightTotal));
                assigned += units;
                var grade = grades.GetValueOrDefault(ore.TypeId);
                var typeId = jackpot && grade.JackpotTypeId > 0
                    ? grade.JackpotTypeId
                    : HashUnit($"{seed}:grade:{ordinal}") < ImprovedChance && grade.ImprovedTypeId > 0
                        ? grade.ImprovedTypeId
                        : ore.TypeId;
                var distance = fieldRadius * Math.Cbrt(HashUnit($"{seed}:r:{ordinal}"));
                var theta = 2d * Math.PI * HashUnit($"{seed}:theta:{ordinal}");
                var cosPhi = 2d * HashUnit($"{seed}:phi:{ordinal}") - 1d;
                var sinPhi = Math.Sqrt(Math.Max(0d, 1d - cosPhi * cosPhi));
                var position = new[]
                {
                    center[0] + distance * sinPhi * Math.Cos(theta),
                    center[1] + distance * cosPhi,
                    center[2] + distance * sinPhi * Math.Sin(theta),
                };
                var type = _statics.Type(typeId)
                    ?? throw new InvalidDataException($"SDE moon ore grade {typeId} is missing");
                rocks.Add(new Rock(ordinal, typeId, units, AsteroidRadius(type, units, ore.UnitVolumeM3), position));
            }
        }
        return rocks;
    }

    private double AsteroidRadius(TypeDef type, long quantity, double unitVolume)
    {
        var unitSizeId = _statics.AttributeId("asteroidRadiusUnitSize");
        var growthId = _statics.AttributeId("asteroidRadiusGrowthFactor");
        var maxRadiusId = _statics.AttributeId("asteroidMaxRadius") ?? _statics.AttributeId("asteroidMaxRadius ");
        if (unitSizeId is { } unitId && growthId is { } growthAttr && maxRadiusId is { } maxId
            && type.Attributes.GetValueOrDefault(unitId) is > 0 and var unitSize
            && type.Attributes.GetValueOrDefault(growthAttr) is > 0 and var growth
            && type.Attributes.GetValueOrDefault(maxId) is > 0 and var maxRadius
            && maxRadius >= unitSize)
        {
            var raw = unitSize * Math.Exp(AsteroidExponentScale * (Math.Max(1L, quantity) - 1L) * growth);
            return Math.Clamp(raw, unitSize, maxRadius);
        }

        // shortcut: SDE types without all three radius attributes use the EveJS visual fallback until the matching SDE publishes them.
        return Math.Clamp(Math.Sqrt(Math.Max(0d, quantity * unitVolume) / AsteroidExponentScale), 250d, 25_000d);
    }

    private static bool IsMoonOreGroup(int groupId) => groupId is 1884 or 1920 or 1921 or 1922 or 1923;

    private static double HashUnit(string seed)
    {
        uint hash = 2_166_136_261;
        foreach (var character in seed)
        {
            hash ^= character;
            hash = unchecked(hash * 16_777_619);
        }
        hash ^= hash >> 13;
        hash = unchecked(hash * 0x5bd1e995);
        hash ^= hash >> 15;
        return hash / 4_294_967_296d;
    }
}
