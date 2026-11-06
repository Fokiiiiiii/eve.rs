using System.Globalization;
using System.Text.Json;
using Eve.Data;
using Eve.Characters;
using Eve.Content;
using Eve.Events;
using Eve.Inventory;
using Eve.Sde;
using Eve.Services.Space;
using Eve.Ships;
using Eve.Space;
using Npgsql;

namespace Eve.Host;

public sealed record AbyssalFilamentConsumedEvent(string ContentId, long CharacterId,
    Item Before, Item After, int GroupId, int CategoryId);
public sealed record AbyssalTracePublishRequestedEvent(SimAbyssalTrace Trace, AbyssalFilamentConsumedEvent? Consumption = null);

public sealed record AbyssalTraceRemovalRequestedEvent(
    long InstanceId,
    string ContentId,
    int SolarSystemId,
    long TraceItemId);

public sealed record AbyssalRunAdmissionRequest(
    long CharacterId,
    long ShipId,
    long FilamentItemId,
    AbyssalGameMode GameModeId,
    long? FleetId,
    int SolarSystemId,
    AbyssalPosition SourceShipPosition,
    AbyssalPosition SourceShipDirection);

public sealed record AbyssalRunAdmissionResult(AbyssalRunState Run, SimAbyssalTrace Trace)
{
    public AbyssalFilamentConsumedEvent? Consumption { get; init; }
}

/// <summary>Commits filament consumption, the trace item, and the ContentRuntime run in one transaction.</summary>
public sealed class AbyssalRunAdmission(
    NpgsqlDataSource source,
    PgCharacterStore characters,
    PgItemStore items,
    ItemTypeCatalog itemTypes,
    AbyssalStaticCatalog catalog,
    AbyssalContentRuntime runtime,
    EventBus bus,
    AbyssalFilamentConsumer filamentConsumer,
    AbyssalDeploymentEligibility eligibility,
    Func<AbyssalRunAdmissionRequest, CancellationToken, Task<AbyssalParticipantPoseResult>> inspectDeployment,
    int blueprintCategoryId,
    double traceRadius,
    Func<long> nowUnixMs)
{
    public const string TracePublishRequestedTopic = "abyssal.host.trace_publish_requested";
    public const string TraceRemovalRequestedTopic = "abyssal.host.trace_removal_requested";
    public const long UnusedTraceLifetimeMs = 30 * 60 * 1000;
    private const int TraceTypeId = AbyssalTraceFactory.TraceTypeId;
    private static readonly JsonSerializerOptions TraceMetadataJson = new(JsonSerializerDefaults.Web);

    public async Task<AbyssalRunAdmissionResult> DeployAsync(
        AbyssalRunAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var character = await characters.GetAsync(request.CharacterId, cancellationToken).ConfigureAwait(false)
            ?? throw AbyssalErrorAdapter.ClientError(7); // KEY_NOT_FOUND
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await Sql.ScalarAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))",
            cancellationToken, $"abyssal:placement:system:{request.SolarSystemId}").ConfigureAwait(false);
        var committed = await FindCommittedInTransactionAsync(connection, transaction, request.CharacterId, request.FilamentItemId,
            request.GameModeId, request.FleetId, rejectConflict: true, cancellationToken).ConfigureAwait(false);
        if (committed is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return committed;
        }
        var ship = await items.LockAndGetAsync(connection, transaction, request.ShipId, cancellationToken).ConfigureAwait(false)
            ?? throw AbyssalErrorAdapter.ClientError(7);
        if (character.ShipId != request.ShipId || character.SolarSystemId != request.SolarSystemId)
        {
            Console.Error.WriteLine($"eve-host: abyssal.admission authority_refused character={character.Id} ship={character.ShipId} system={character.SolarSystemId} station={character.StationId} request_ship={request.ShipId} request_system={request.SolarSystemId}");
            throw AbyssalErrorAdapter.ClientError(22);
        }
        if (ship.OwnerId != request.CharacterId ||
            ship.LocationId != request.SolarSystemId ||
            ship.Flag != CharacterShips.InSpaceFlag ||
            !catalog.IsShipAllowed(request.GameModeId, ship.TypeId))
        {
            throw AbyssalErrorAdapter.FromRule(
                new AbyssalRuleException(AbyssalRuleCode.InvalidShipClass,
                    "the active ship is not an eligible in-space hull for this game mode"),
                isGateActivation: false);
        }

        var filamentRequest = new AbyssalFilamentConsumptionRequest(
            request.FilamentItemId,
            request.CharacterId,
            character.CorporationId,
            request.ShipId,
            catalog.RequiredFilamentsFor(request.GameModeId));
        var filament = await filamentConsumer.ValidateAsync(
            connection, transaction, filamentRequest, cancellationToken).ConfigureAwait(false);
        if (!catalog.FilamentsByTypeId.TryGetValue(filament.TypeId, out var filamentDefinition))
            throw AbyssalErrorAdapter.ClientError(3); // INVALID_KEY_TYPE

        var pose = await inspectDeployment(request, cancellationToken).ConfigureAwait(false);
        AbyssalDeploymentEligibility.ValidateProbe(request, pose);
        AbyssalDeploymentEligibility.ValidateSessionState(pose);
        AbyssalDeploymentEligibility.ValidateShipEffects(pose);
        await eligibility.ValidateNearbyAsync(request.ShipId, pose.Deployment!, cancellationToken).ConfigureAwait(false);
        request = request with
        {
            SourceShipPosition = new AbyssalPosition(pose.Position[0], pose.Position[1], pose.Position[2]),
            SourceShipDirection = new AbyssalPosition(pose.Direction[0], pose.Direction[1], pose.Direction[2]),
        };
        await eligibility.ValidatePersistedTracesAsync(items, connection, transaction, request, cancellationToken).ConfigureAwait(false);
        var traceType = await itemTypes.GetAsync(TraceTypeId, cancellationToken).ConfigureAwait(false);
        if (traceType is null ||
            traceType.GroupId != AbyssalTraceFactory.TraceGroupId ||
            traceType.CategoryId != AbyssalTraceFactory.TraceCategoryId ||
            !double.IsFinite(traceRadius) ||
            traceRadius <= 0)
        {
            throw new InvalidDataException("The pinned SDE trace type or radius is invalid.");
        }

        var traceItemId = await items.CreateAsync(
            connection,
            transaction,
            new NewItem(
                TraceTypeId,
                AbyssalTraceFactory.TraceOwnerId,
                request.SolarSystemId,
                CharacterShips.InSpaceFlag,
                Quantity: 1,
                Singleton: true),
            cancellationToken).ConfigureAwait(false);

        var conflict = await runtime.FindAdmissionConflictAsync(
            connection,
            transaction,
            request.CharacterId,
            request.FleetId,
            traceItemId,
            cancellationToken).ConfigureAwait(false);
        if (conflict is not null)
        {
            throw new AbyssalRuleException(
                AbyssalRuleCode.AdmissionConflict,
                "the creator or cooperative fleet already has an active Abyssal deployment");
        }

        await filamentConsumer.ConsumeAsync(
            connection, transaction, filamentRequest, cancellationToken).ConfigureAwait(false);

        var runId = Guid.NewGuid().ToString("D");
        var spaceLocationId = SelectSpaceLocationId(traceItemId);
        var plan = catalog.CreatePlan(
            filament.TypeId,
            request.GameModeId,
            traceItemId,
            runId,
            spaceLocationId);
        plan = await AbyssalLootPlanResolver.FreezeBlueprintCopiesAsync(
            plan,
            itemTypes,
            blueprintCategoryId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var now = nowUnixMs();
        var traceExpiresAtMs = checked(now + UnusedTraceLifetimeMs);
        var trace = AbyssalTraceFactory.Create(
            request.SolarSystemId,
            traceItemId,
            (int)request.GameModeId,
            filament.TypeId,
            filamentDefinition.Tier,
            traceExpiresAtMs,
            traceRadius,
            ToVector(request.SourceShipPosition),
            ToVector(request.SourceShipDirection));

        var filamentType = await itemTypes.GetAsync(filament.TypeId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The consumed filament SDE type is missing.");
        var remaining = filament.Quantity - filamentRequest.RequiredQuantity;
        var consumption = new AbyssalFilamentConsumedEvent(runId, request.CharacterId, filament,
            remaining == 0 ? filament with { LocationId = 6 } : filament with { Quantity = remaining },
            filamentType.GroupId, filamentType.CategoryId);
        var traceMetadata = JsonSerializer.Serialize(
            new AbyssalTracePoseSnapshot(1, trace.Position, trace.Direction) { Trace = trace, Consumption = consumption },
            TraceMetadataJson);
        await items.SetCustomInfoAsync(
            connection, transaction, traceItemId, traceMetadata, cancellationToken).ConfigureAwait(false);

        var started = await runtime.StartAsync(
            connection,
            transaction,
            new AbyssalStartRequest(
                traceItemId,
                request.CharacterId,
                request.ShipId,
                request.FleetId,
                request.GameModeId,
                filament.TypeId,
                request.SolarSystemId,
                ship.LocationId,
                request.SourceShipPosition,
                traceExpiresAtMs,
                plan),
            cancellationToken).ConfigureAwait(false);

        if (started.Status is not (AbyssalOperationStatus.Applied or AbyssalOperationStatus.AlreadyApplied))
            throw new InvalidOperationException($"Abyssal ContentRuntime returned unexpected start status {started.Status}.");

        await bus.PublishAsync(
            connection,
            transaction,
            TracePublishRequestedTopic,
            new AbyssalTracePublishRequestedEvent(trace, consumption),
            $"abyssal:{traceItemId.ToString(CultureInfo.InvariantCulture)}:trace-publish",
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AbyssalRunAdmissionResult(started.State, trace) { Consumption = consumption };
    }

    public async Task<AbyssalRunAdmissionResult?> FindCommittedAsync(long characterId, long filamentItemId,
        AbyssalGameMode mode, long? fleetId, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        return await FindCommittedInTransactionAsync(c, tx, characterId, filamentItemId, mode, fleetId, rejectConflict: false, ct).ConfigureAwait(false);
    }

    private async Task<AbyssalRunAdmissionResult?> FindCommittedInTransactionAsync(NpgsqlConnection c, NpgsqlTransaction tx,
        long characterId, long filamentItemId, AbyssalGameMode mode, long? fleetId, bool rejectConflict, CancellationToken ct)
    {
        var run = await runtime.FindAdmissionConflictAsync(c, tx, characterId, fleetId, filamentItemId, ct).ConfigureAwait(false);
        if (run is null) return null;
        if (run.Data.CreatorCharacterId == characterId && run.Data.GameModeId == mode)
        {
            var item = await items.LockAndGetAsync(c, tx, run.Data.TraceItemId, ct).ConfigureAwait(false);
            var saved = item?.CustomInfo is { } json ? JsonSerializer.Deserialize<AbyssalTracePoseSnapshot>(json, TraceMetadataJson) : null;
            if (saved?.Consumption is { } consumed && consumed.Before.Id == filamentItemId && saved.Trace is not null)
                return new AbyssalRunAdmissionResult(run, saved.Trace) { Consumption = consumed };
        }
        if (rejectConflict)
            throw new AbyssalRuleException(AbyssalRuleCode.AdmissionConflict, "the creator or cooperative fleet already has an active Abyssal deployment");
        return null;
    }

    private int SelectSpaceLocationId(long traceItemId)
    {
        var systems = catalog.AbyssalSystemsById.Keys.Order().ToArray();
        if (systems.Length == 0)
            throw new InvalidDataException("The SDE catalog has no Abyssal hidden systems.");
        return systems[(int)(traceItemId % systems.Length)];
    }

    private static IReadOnlyList<double> ToVector(AbyssalPosition value) =>
        Array.AsReadOnly(new[] { value.X, value.Y, value.Z });

    private static void ValidateRequest(AbyssalRunAdmissionRequest request)
    {
        if (request.CharacterId <= 0 || request.ShipId <= 0 || request.FilamentItemId <= 0 ||
            request.SolarSystemId <= 0 || request.FleetId is <= 0 ||
            !Enum.IsDefined(request.GameModeId))
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        ValidatePosition(request.SourceShipPosition, nameof(request.SourceShipPosition));
        ValidatePosition(request.SourceShipDirection, nameof(request.SourceShipDirection));
        var direction = request.SourceShipDirection;
        if (direction.X == 0 && direction.Y == 0 && direction.Z == 0)
            throw new ArgumentException("The authoritative ship direction must be non-zero.", nameof(request));
    }

    private static void ValidatePosition(AbyssalPosition position, string parameterName)
    {
        if (position is null ||
            !double.IsFinite(position.X) ||
            !double.IsFinite(position.Y) ||
            !double.IsFinite(position.Z))
        {
            throw new ArgumentException("Abyssal ship pose must contain finite coordinates.", parameterName);
        }
    }

}