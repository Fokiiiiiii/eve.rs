using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Characters;
using Eve.Events;
using Eve.Host.Persistence.Mail;
using Eve.Structures;
using Npgsql;

namespace Eve.Host.Persistence.Structures;

internal sealed class MoonOwnerNotifications
{
    private const int ExtractionStartedTypeId = 202;
    private const int ExtractionCancelledTypeId = 203;
    private const int ExtractionFinishedTypeId = 204;
    private const int LaserFiredTypeId = 205;
    private const int AutomaticFractureTypeId = 206;
    private const int StructuresGroupId = 7;

    private readonly NpgsqlDataSource _source;
    private readonly StructureStore _structures;
    private readonly MoonExtractionStore _extractions;
    private readonly PgCharacterStore _characters;
    private readonly PersistenceTransactionMailbox _mailbox;

    public MoonOwnerNotifications(
        NpgsqlDataSource source,
        StructureStore structures,
        MoonExtractionStore extractions,
        PgCharacterStore characters,
        PersistenceTransactionMailbox mailbox)
    {
        _source = source;
        _structures = structures;
        _extractions = extractions;
        _characters = characters;
        _mailbox = mailbox;
    }

    public void Subscribe(EventBus events) => events.Subscribe(
        "moon.owner_notifications",
        [MoonExtractions.StartedTopic, MoonExtractions.FractureStartedTopic, MoonExtractions.ChunkArrivedTopic, MoonExtractions.EndedTopic],
        async (context, token) =>
        {
            _ = context.Event.Topic switch
            {
                MoonExtractions.StartedTopic => await OnStartedAsync(context.Event.Payload<MoonExtractionStarted>()!, token, context),
                MoonExtractions.FractureStartedTopic => await OnFractureStartedAsync(context.Event.Payload<MoonFractureStarted>()!, token, context),
                MoonExtractions.ChunkArrivedTopic => await OnChunkArrivedAsync(context.Event.Payload<MoonChunkArrived>()!, token, context),
                MoonExtractions.EndedTopic => await OnEndedAsync(context.Event.Payload<MoonExtractionEnded>()!, token, context),
                _ => 0,
            };
        });

    public Task<int> OnStartedAsync(MoonExtractionStarted started, CancellationToken cancellationToken = default, EventContext? context = null) =>
        NotifyAsync(started.ExtractionId, started.StructureId, started.SolarSystemId, started.OwnerCorporationId,
            ExtractionStartedTypeId, started.ActorCharacterId, cancellationToken, context);

    public Task<int> OnChunkArrivedAsync(MoonChunkArrived arrived, CancellationToken cancellationToken = default, EventContext? context = null) =>
        NotifyAsync(arrived.ExtractionId, arrived.StructureId, arrived.SolarSystemId, arrived.OwnerCorporationId,
            ExtractionFinishedTypeId, 0, cancellationToken, context);

    public Task<int> OnFractureStartedAsync(MoonFractureStarted started, CancellationToken cancellationToken = default, EventContext? context = null) =>
        NotifyAsync(started.ExtractionId, started.StructureId, started.SolarSystemId, started.OwnerCorporationId,
            LaserFiredTypeId, started.ActorCharacterId, cancellationToken, context);

    public Task<int> OnEndedAsync(MoonExtractionEnded ended, CancellationToken cancellationToken = default, EventContext? context = null) =>
        ended.Reason switch
        {
            MoonExtractions.Cancelled or "manualcancel" => NotifyAsync(
                ended.ExtractionId, ended.StructureId, ended.SolarSystemId, ended.OwnerCorporationId,
                ExtractionCancelledTypeId, ended.ActorCharacterId, cancellationToken, context),
            MoonExtractions.Decayed => NotifyAsync(
                ended.ExtractionId, ended.StructureId, ended.SolarSystemId, ended.OwnerCorporationId,
                AutomaticFractureTypeId, 0, cancellationToken, context),
            _ => Task.FromResult(0),
        };

    private async Task<int> NotifyAsync(
        long extractionId,
        long structureId,
        long solarSystemId,
        long ownerCorporationId,
        int notificationTypeId,
        long actorCharacterId,
        CancellationToken cancellationToken,
        EventContext? context)
    {
        var (extraction, structure) = await ReadContextAsync(
            extractionId, structureId, solarSystemId, ownerCorporationId, cancellationToken, context);
        var recipients = (await _characters.ForCorporationAsync(extraction.OwnerCorporationId, cancellationToken))
            .Select(character => character.Id)
            .Distinct()
            .ToArray();
        var actor = actorCharacterId > 0
            ? await _characters.GetAsync(actorCharacterId, cancellationToken)
                ?? throw new InvalidOperationException($"Moon notification actor {actorCharacterId} does not exist.")
            : null;

        if ((notificationTypeId == ExtractionStartedTypeId || notificationTypeId == LaserFiredTypeId) && actor is null)
            throw new InvalidOperationException("This moon notification requires a real actor character.");

        var data = BuildData(extraction, structure, notificationTypeId, actor);
        var idempotencyKey = $"moon-extraction:{extraction.ExtractionId}:{notificationTypeId}";
        return await _mailbox.WriteNotificationsAsync(
            extraction.OwnerCorporationId,
            notificationTypeId,
            StructuresGroupId,
            recipients,
            idempotencyKey,
            data,
            cancellationToken, context);
    }

    private async Task<(MoonExtraction Extraction, StructureRecord Structure)> ReadContextAsync(
        long extractionId,
        long structureId,
        long solarSystemId,
        long ownerCorporationId,
        CancellationToken cancellationToken,
        EventContext? context)
    {
        await using var ownedConnection = context is null ? await _source.OpenConnectionAsync(cancellationToken) : null;
        var connection = context?.Connection ?? ownedConnection!;
        await using var ownedTransaction = context is null ? await connection.BeginTransactionAsync(cancellationToken) : null;
        var transaction = context?.Transaction ?? ownedTransaction!;
        var extraction = await _extractions.GetAsync(connection, transaction, extractionId, false, cancellationToken)
            ?? throw new InvalidOperationException($"Moon extraction {extractionId} does not exist.");
        var structure = await _structures.GetAsync(connection, transaction, structureId, false, cancellationToken)
            ?? throw new InvalidOperationException($"Moon extraction structure {structureId} does not exist.");
        if (context is null)
            await transaction.CommitAsync(cancellationToken);

        if (extraction.ExtractionId != extractionId || extraction.StructureId != structureId ||
            extraction.SolarSystemId != solarSystemId || extraction.OwnerCorporationId != ownerCorporationId ||
            structure.Snapshot.StructureId != structureId || structure.SolarSystemId != solarSystemId ||
            structure.TypeId <= 0 || string.IsNullOrWhiteSpace(structure.Name) ||
            extraction.MoonId <= 0)
            throw new InvalidOperationException("The moon notification event does not match its persisted extraction and structure.");

        return (extraction, structure);
    }

    private static JsonObject BuildData(
        MoonExtraction extraction,
        StructureRecord structure,
        int notificationTypeId,
        Character? actor)
    {
        var data = new JsonObject
        {
            ["structureLink"] = ShowInfoLink(structure.TypeId, structure.Name, extraction.StructureId),
            ["structureID"] = extraction.StructureId,
            ["structureName"] = structure.Name,
            ["structureTypeID"] = (long)structure.TypeId,
            ["solarSystemID"] = extraction.SolarSystemId,
            ["moonID"] = extraction.MoonId,
        };

        switch (notificationTypeId)
        {
            case ExtractionStartedTypeId:
                data["startedBy"] = actor!.Id;
                data["startedByLink"] = ShowInfoLink(actor.TypeId, actor.Name, actor.Id);
                data["readyTime"] = ToFileTime(extraction.ChunkAvailableMs);
                data["autoTime"] = ToFileTime(extraction.NaturalDecayMs);
                data["oreVolumeByType"] = OreVolumes(extraction);
                break;
            case ExtractionCancelledTypeId:
                data["cancelledBy"] = actor is null ? null : JsonValue.Create(actor.Id);
                data["cancelledByLink"] = actor is null ? string.Empty : ShowInfoLink(actor.TypeId, actor.Name, actor.Id);
                break;
            case ExtractionFinishedTypeId:
                data["autoTime"] = ToFileTime(extraction.NaturalDecayMs);
                data["oreVolumeByType"] = OreVolumes(extraction);
                break;
            case LaserFiredTypeId:
                data["firedBy"] = actor!.Id;
                data["firedByLink"] = ShowInfoLink(actor.TypeId, actor.Name, actor.Id);
                data["oreVolumeByType"] = OreVolumes(extraction);
                break;
            case AutomaticFractureTypeId:
                data["oreVolumeByType"] = OreVolumes(extraction);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(notificationTypeId));
        }
        return data;
    }

    private static JsonObject OreVolumes(MoonExtraction extraction)
    {
        var volumes = new JsonObject();
        foreach (var (typeId, volume) in extraction.OreVolumeM3.OrderBy(pair => pair.Key))
            volumes[typeId.ToString(CultureInfo.InvariantCulture)] = volume;
        return volumes;
    }

    private static string ShowInfoLink(int typeId, string name, long itemId) =>
        $"<a href=\"showinfo:{typeId.ToString(CultureInfo.InvariantCulture)}//{itemId.ToString(CultureInfo.InvariantCulture)}\">{name}</a>";

    private static long ToFileTime(long unixMs)
    {
        if (unixMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(unixMs));
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToFileTimeUtc();
    }
}
