using System.Globalization;
using Eve.Dogma;
using Eve.Events;
using Eve.Inventory;
using Eve.Ships;
using Eve.Space;
using Eve.Structures;
using Npgsql;

namespace Eve.Services.Space;

/// <summary>Projects persisted moon extractions to the system scene as a chunk and the V24 manual moon-mining effect.</summary>
public sealed class SpaceMoonChunks
{
    public const string SubscriberName = "space.moon_chunks";

    private const long UnixEpochFileTime = 116_444_736_000_000_000L;
    private const int MoonDrillTypeId = 45_009;
    private const int RefineryGroupId = 1_406;
    private const double ChunkArrivalDistance = 100_000d;
    private const double YieldPerDayM3 = 720_000d;

    private readonly ISimulation _sim;
    private readonly SpaceSessions _space;
    private readonly NpgsqlDataSource _source;
    private readonly StructureStore _structures;
    private readonly MoonExtractionStore _extractions;
    private readonly PgItemStore _items;
    private readonly PgSpaceItemStore _spaceItems;
    private readonly DogmaStatic _statics;
    private readonly Func<long> _nowUnixMs;
    private readonly SpaceStructures? _spaceStructures;
    private readonly Action<string>? _log;

    private sealed record MoonPosition(int SolarSystemId, double[] Position, double Radius);
    private sealed record ChunkProjection(SimUpsertMoonChunk Chunk, SimSetStructureMoonMiningState StructureState);

    public SpaceMoonChunks(
        ISimulation sim,
        SpaceSessions space,
        NpgsqlDataSource source,
        StructureStore structures,
        MoonExtractionStore extractions,
        PgItemStore items,
        PgSpaceItemStore spaceItems,
        DogmaStatic statics,
        Func<long> nowUnixMs,
        SpaceStructures? spaceStructures = null,
        Action<string>? log = null)
    {
        _sim = sim;
        _space = space;
        _source = source;
        _structures = structures;
        _extractions = extractions;
        _items = items;
        _spaceItems = spaceItems;
        _statics = statics;
        _nowUnixMs = nowUnixMs;
        _spaceStructures = spaceStructures;
        _log = log;
        _space.SystemEntered += SystemEntered;
    }

    public void Subscribe(EventBus events) => events.Subscribe(
        SubscriberName,
        [MoonExtractions.StartedTopic, MoonExtractions.ChunkArrivedTopic,
            MoonExtractions.FractureStartedTopic, MoonExtractions.ChunkExplodedTopic, MoonExtractions.EndedTopic],
        DispatchAsync);

    private async Task DispatchAsync(EventContext context, CancellationToken cancellationToken)
    {
        switch (context.Event.Topic)
        {
            case MoonExtractions.StartedTopic:
            case MoonExtractions.ChunkArrivedTopic:
            case MoonExtractions.FractureStartedTopic:
            {
                var extractionId = context.Event.Topic switch
                {
                    MoonExtractions.StartedTopic => context.Event.Payload<MoonExtractionStarted>()?.ExtractionId,
                    MoonExtractions.ChunkArrivedTopic => context.Event.Payload<MoonChunkArrived>()?.ExtractionId,
                    MoonExtractions.FractureStartedTopic => context.Event.Payload<MoonFractureStarted>()?.ExtractionId,
                    _ => null,
                } ?? throw new InvalidDataException($"{context.Event.Topic} event {context.Event.Id} has no extraction ID");
                var projection = await UpsertInTransactionAsync(
                    context.Connection, context.Transaction, extractionId, _nowUnixMs(), cancellationToken);
                if (projection is not null)
                    AfterCommitUpsert(context, projection);
                break;
            }
            case MoonExtractions.ChunkExplodedTopic:
            {
                var exploded = context.Event.Payload<MoonChunkExploded>()
                    ?? throw new InvalidDataException($"{MoonExtractions.ChunkExplodedTopic} event {context.Event.Id} has no payload");
                await RemoveInTransactionAsync(context.Connection, context.Transaction, exploded.ExtractionId, 3, context, cancellationToken);
                break;
            }
            case MoonExtractions.EndedTopic:
            {
                var ended = context.Event.Payload<MoonExtractionEnded>()
                    ?? throw new InvalidDataException($"{MoonExtractions.EndedTopic} event {context.Event.Id} has no payload");
                var effect = ended.Reason is MoonExtractions.Fractured or MoonExtractions.Decayed ? 3 : 0;
                await RemoveInTransactionAsync(context.Connection, context.Transaction, ended.ExtractionId, effect, context, cancellationToken);
                break;
            }
        }
    }

    /// <summary>Rebuilds active chunks from extraction rows when a solar-system scene opens or is restored.</summary>
    public async Task RestoreAsync(int solarSystemId, CancellationToken cancellationToken = default)
    {
        if (solarSystemId <= 0)
            return;
        try
        {
            if (_spaceStructures is not null)
                await _spaceStructures.RestoreAsync(solarSystemId, cancellationToken);
            await using var connection = await _source.OpenConnectionAsync(cancellationToken);
            var open = await _extractions.OpenAsync(connection, null, cancellationToken: cancellationToken);
            foreach (var extraction in open.Where(row => row.SolarSystemId == solarSystemId))
            {
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                var projection = await UpsertInTransactionAsync(connection, transaction, extraction.ExtractionId, _nowUnixMs(), cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                if (projection is not null)
                    SendUpsert(projection);
            }
        }
        catch (Exception error)
        {
            _log?.Invoke($"moon chunks in system {solarSystemId} were not restored: {error.Message}");
        }
    }

    private async Task<ChunkProjection?> UpsertInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long extractionId,
        long nowMs,
        CancellationToken cancellationToken)
    {
        var extraction = await _extractions.GetAsync(connection, transaction, extractionId, forUpdate: true, cancellationToken);
        if (extraction is null || extraction.State == "completed" || extraction.ChunkExplodedMs > 0)
            return null;
        if (extraction.ExtractionId <= 0 || extraction.StructureId <= 0 || extraction.MoonId <= 0 ||
            extraction.SolarSystemId is <= 0 or > int.MaxValue || extraction.OwnerCorporationId <= 0 ||
            extraction.ChunkAvailableMs <= extraction.StartMs || extraction.OreVolumeM3.Count == 0)
            throw new InvalidDataException($"moon extraction {extractionId} has incomplete chunk presentation data");

        var structure = await _structures.GetAsync(connection, transaction, extraction.StructureId, forUpdate: false, cancellationToken)
            ?? throw new InvalidDataException($"moon extraction {extractionId} has no refinery");
        if (structure.SolarSystemId != extraction.SolarSystemId)
            throw new InvalidDataException($"moon extraction {extractionId} is in another system than its refinery");

        var moonMiningState = await StructureMoonMiningSlim.ReadAsync(
            connection, _items, _extractions, _statics, structure.Snapshot.StructureId, cancellationToken);
        var drill = moonMiningState.Modules.FirstOrDefault(module => module.TypeId == MoonDrillTypeId && module.FlagId is >= 164 and <= 171)
            ?? throw new InvalidDataException($"moon extraction {extractionId} has no fitted Standup Moon Drill item");
        if (moonMiningState.ClosestMoonId > 0 && moonMiningState.ClosestMoonId != extraction.MoonId)
            throw new InvalidDataException($"moon extraction {extractionId} targets another moon than the refinery profile");

        var volume = extraction.OreVolumeM3.Values.Sum();
        if (!double.IsFinite(volume) || volume <= 0)
            throw new InvalidDataException($"moon extraction {extractionId} has an invalid chunk volume");
        var shellTypeId = ShellTypeId(volume);
        await _statics.EnsureTypesAsync([structure.TypeId, shellTypeId], cancellationToken);
        var shell = _statics.Type(shellTypeId)
            ?? throw new InvalidDataException($"SDE moon chunk type {shellTypeId} is missing");
        var radiusAttributeId = _statics.AttributeId("radius")
            ?? throw new InvalidDataException("SDE has no radius attribute");
        var shellRadius = shell.Attributes.GetValueOrDefault(radiusAttributeId);
        if (!double.IsFinite(shellRadius) || shellRadius <= 0 || shell.GraphicId <= 0 || shell.CategoryId <= 0 || shell.GroupId != 1_940)
            throw new InvalidDataException($"SDE moon chunk type {shellTypeId} has invalid render data");

        var moon = await ReadMoonAsync(connection, transaction, extraction.MoonId, cancellationToken)
            ?? throw new InvalidDataException($"SDE moon {extraction.MoonId} is missing");
        if (moon.SolarSystemId != extraction.SolarSystemId)
            throw new InvalidDataException($"moon extraction {extractionId} targets a moon in another system");
        var structureType = _statics.Type(structure.TypeId)
            ?? throw new InvalidDataException($"SDE refinery type {structure.TypeId} is missing");
        var structureRadius = structureType.Attributes.GetValueOrDefault(radiusAttributeId);
        if (!double.IsFinite(structureRadius) || structureRadius <= 0)
            throw new InvalidDataException($"SDE refinery type {structure.TypeId} has no positive radius");

        var pose = ChunkPose(extraction, structure, moon, structureRadius, nowMs);
        var existing = await _spaceItems.MoonChunkAsync(connection, transaction, extractionId, cancellationToken);
        long itemId;
        if (existing is null)
        {
            itemId = await _items.CreateAsync(connection, transaction,
                new NewItem(shellTypeId, extraction.OwnerCorporationId, extraction.SolarSystemId, CharacterShips.InSpaceFlag, 1, true), cancellationToken);
        }
        else
        {
            if (existing.SolarSystemId != extraction.SolarSystemId || existing.MoonId != extraction.MoonId ||
                existing.MoonStructureId != extraction.StructureId || existing.MoonRockOrdinal != 0)
                throw new InvalidDataException($"moon chunk item {existing.ItemId} has mismatched extraction metadata");
            itemId = existing.ItemId;
            if (await _items.LockAndGetAsync(connection, transaction, itemId, cancellationToken) is null)
                throw new InvalidDataException($"moon chunk item {itemId} has no inventory row");
        }

        var spaceItem = new SpaceItem(
            itemId, (int)extraction.SolarSystemId, pose.Position, pose.Direction, shellRadius, shell.GroupId,
            structure.Snapshot.StructureId, 0, extraction.OwnerCorporationId, 0, 0, long.MaxValue,
            extraction.ExtractionId, extraction.MoonId, extraction.StructureId, extraction.OwnerCorporationId, 0, shell.GraphicId,
            SpaceItemKind.MoonChunk);
        await _spaceItems.SaveAsync(connection, transaction, spaceItem, cancellationToken);

        var effectStartedFileTime = FileTime(pose.PhaseStartedMs);
        var chunk = new SimUpsertMoonChunk(
            itemId, (int)extraction.SolarSystemId, shell.Name, shell.TypeId, shell.GroupId, shell.CategoryId, extraction.OwnerCorporationId, shell.GraphicId,
            pose.Position, pose.Direction, shellRadius, structure.Snapshot.StructureId, drill.ItemId, drill.TypeId, extraction.MoonId,
            pose.Phase, effectStartedFileTime);
        var structureState = new SimSetStructureMoonMiningState((int)extraction.SolarSystemId, structure.Snapshot.StructureId, extraction.MoonId, moonMiningState.Modules);
        return new ChunkProjection(chunk, structureState);
    }

    private async Task RemoveInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long extractionId,
        int effect,
        EventContext context,
        CancellationToken cancellationToken)
    {
        var chunk = await _spaceItems.MoonChunkAsync(connection, transaction, extractionId, cancellationToken);
        if (chunk is null)
            return;
        await _items.DeleteAsync(connection, transaction, chunk.ItemId, cancellationToken);
        await _spaceItems.RemoveAsync(connection, transaction, chunk.ItemId, cancellationToken);
        context.AfterCommit(_ =>
        {
            try
            {
                _sim.RemoveMoonChunk(new SimRemoveMoonChunk(chunk.SolarSystemId, chunk.ItemId, effect));
            }
            catch (Exception error)
            {
                _log?.Invoke($"moon chunk {chunk.ItemId} was removed from storage but not the simulation: {error.Message}");
            }
            return Task.CompletedTask;
        });
    }

    private void AfterCommitUpsert(EventContext context, ChunkProjection projection) => context.AfterCommit(async cancellationToken =>
    {
        try
        {
            if (_spaceStructures is not null)
                await _spaceStructures.PlaceAsync(projection.Chunk.SourceStructureId, cancellationToken);
            _sim.SetStructureMoonMiningState(projection.StructureState);
            _sim.UpsertMoonChunk(projection.Chunk);
        }
        catch (Exception error)
        {
            _log?.Invoke($"moon chunk {projection.Chunk.ChunkId} was saved but not projected to the simulation: {error.Message}");
        }
    });

    private void SendUpsert(ChunkProjection projection)
    {
        try
        {
            _sim.SetStructureMoonMiningState(projection.StructureState);
            _sim.UpsertMoonChunk(projection.Chunk);
        }
        catch (Exception error)
        {
            _log?.Invoke($"moon chunk {projection.Chunk.ChunkId} was restored in storage but not projected to the simulation: {error.Message}");
        }
    }

    private void SystemEntered(int solarSystemId) => _space.Background(() => RestoreAsync(solarSystemId));

    private static int ShellTypeId(double volume) => volume <= YieldPerDayM3 * 14
        ? 46_030
        : volume <= YieldPerDayM3 * 28
            ? 45_008
            : volume <= YieldPerDayM3 * 42
                ? 46_031
                : 46_032;

    private static long FileTime(long unixMs) => checked(unixMs * 10_000 + UnixEpochFileTime);

    private static (double[] Position, double[] Direction, string Phase, long PhaseStartedMs) ChunkPose(
        MoonExtraction extraction,
        StructureRecord structure,
        MoonPosition moon,
        double structureRadius,
        long nowMs)
    {
        var towardMoon = Normalize(moon.Position[0] - structure.X, moon.Position[1] - structure.Y, moon.Position[2] - structure.Z);
        var start = new[]
        {
            moon.Position[0] - towardMoon[0] * moon.Radius,
            moon.Position[1] - towardMoon[1] * moon.Radius,
            moon.Position[2] - towardMoon[2] * moon.Radius,
        };
        var arrival = new[]
        {
            structure.X + towardMoon[0] * (structureRadius + ChunkArrivalDistance),
            structure.Y + towardMoon[1] * (structureRadius + ChunkArrivalDistance),
            structure.Z + towardMoon[2] * (structureRadius + ChunkArrivalDistance),
        };
        var progress = Math.Clamp((nowMs - extraction.StartMs) / (double)(extraction.ChunkAvailableMs - extraction.StartMs), 0d, 1d);
        var position = Enumerable.Range(0, 3).Select(index => start[index] + (arrival[index] - start[index]) * progress).ToArray();
        var phase = extraction.State == "fracturing"
            ? ("fracture_phase", extraction.FractureStartedMs)
            : nowMs >= extraction.ChunkAvailableMs
                ? ("stasis_phase", extraction.ChunkAvailableMs)
                : ("tractor_phase", extraction.StartMs);
        return (position, [-towardMoon[0], -towardMoon[1], -towardMoon[2]], phase.Item1, phase.Item2);
    }

    private static double[] Normalize(double x, double y, double z)
    {
        var length = Math.Sqrt(x * x + y * y + z * z);
        return length > 0 && double.IsFinite(length) ? [x / length, y / length, z / length] : [1d, 0d, 0d];
    }

    private static async Task<MoonPosition?> ReadMoonAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long moonId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT (doc->'position'->>'x')::double precision, (doc->'position'->>'y')::double precision, " +
            "(doc->'position'->>'z')::double precision, (doc->>'solarSystemID')::integer, (doc->>'radius')::double precision " +
            "FROM sde.\"mapMoons\" WHERE key = $1",
            connection, transaction);
        command.Parameters.AddWithValue(moonId.ToString(CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new MoonPosition(reader.GetInt32(3), [reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2)], reader.GetDouble(4))
            : null;
    }
}
