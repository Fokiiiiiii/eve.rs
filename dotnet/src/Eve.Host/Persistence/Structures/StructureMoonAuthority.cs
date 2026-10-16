using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Structures;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// The backend of the moon service's mutations (<c>moonExtractions</c>, EVE.js moonExtractionsService): <c>StartNewExtraction</c>, <c>FractureChunkWithStructure</c> and
/// <c>CancelExtraction</c>. Each requires the current control seat, moon service profile access and an online drill outside Pochven;
/// cancel also needs the Station Manager role. The moon's checks and the record are the domain's
/// (<see cref="MoonExtractions"/>), gated by persistent control ownership and the moon service profile.
/// </summary>
public sealed class StructureMoonAuthority
{
    private const long StationManagerRole = 2048;

    private readonly NpgsqlDataSource _source;
    private readonly StructureStore _structures;
    private readonly MoonExtractions _moons;
    private readonly MoonRulesSource _rules;
    private readonly StructurePilotStore _pilots;
    private readonly Func<long, long, CancellationToken, ValueTask<bool>> _hasMoonAccess;

    public StructureMoonAuthority(NpgsqlDataSource source, StructureStore structures, MoonExtractions moons, MoonRulesSource rules,
        StructurePilotStore pilots, Func<long, long, CancellationToken, ValueTask<bool>> hasMoonAccess)
    {
        _source = source;
        _structures = structures;
        _moons = moons;
        _rules = rules;
        _pilots = pilots;
        _hasMoonAccess = hasMoonAccess;
    }

    /// <summary>Answers a mutation of the moon service. A start replies the extraction (the client's KeyVal); a fracture and a cancel reply None, as EVE.js does.</summary>
    public ValueTask<PyValue> CallAsync(ServiceCall call) => call.Method switch
    {
        "StartNewExtraction" => StartAsync(call),
        "FractureChunkWithStructure" => FractureAsync(call),
        "CancelExtraction" => CancelAsync(call),
        _ => throw Refusal("Moon extraction is not available on this server."),
    };

    private async ValueTask<PyValue> StartAsync(ServiceCall call)
    {
        var structureId = PositiveId(call.Arg(0));
        var durationSeconds = (long)Math.Truncate(Number(call.Arg(1)));
        var addToCalendar = call.Arg(2) is PyBool { Value: true };
        if (durationSeconds <= 0)
            throw MachoErrors.ThrowUserError("MoonminingEventTooShort");

        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(call.Cancellation);
        await GateAsync(call, connection, transaction, structureId);
        var result = await _moons.StartAsync(connection, transaction, structureId, durationSeconds, addToCalendar, call.Cancellation, actorCharacterId: call.Session.CharacterId ?? 0);
        switch (result.Outcome)
        {
            case MoonExtractions.StartOutcome.NoStructure:
                throw StructureDefenseDenied();
            case MoonExtractions.StartOutcome.DrillOffline:
                throw DrillRequired();
            case MoonExtractions.StartOutcome.NoMoonResources:
                throw Refusal("This structure has no moon to mine.");
        }
        await transaction.CommitAsync();

        var payload = MoonExtractionsRpcService.ExtractionPayload(MoonExtractionStore.ToLegacy(result.Extraction!));
        if (result.Outcome == MoonExtractions.StartOutcome.Started)
            call.Session.SendNotification("OnMoonExtractionUpdate", "clientID", payload);
        return payload;
    }

    private async ValueTask<PyValue> FractureAsync(ServiceCall call)
    {
        var structureId = call.Session.GetInt("structureid") ?? 0;
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(call.Cancellation);
        await GateAsync(call, connection, transaction, structureId);
        if (await _moons.FractureAsync(connection, transaction, structureId, call.Cancellation, actorCharacterId: call.Session.CharacterId ?? 0) == MoonExtractions.FractureOutcome.NotReady)
            throw Refusal("The moon chunk is not ready to fracture.");
        await transaction.CommitAsync();
        return PyNone.Instance;
    }

    private async ValueTask<PyValue> CancelAsync(ServiceCall call)
    {
        var structureId = call.Session.GetInt("structureid") ?? 0;
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(call.Cancellation);
        var structure = await GateAsync(call, connection, transaction, structureId);
        var roles = call.Session.GetInt("corprole") ?? 0;
        if (call.Session.GetInt("corpid") != structure.OwnerCorporationId || (roles & (StationManagerRole | Eve.Corporations.CorporationRoles.Director)) == 0)
            throw Refusal("Station Manager role is required.");
        await _moons.CancelAsync(connection, transaction, structureId, call.Cancellation, actorCharacterId: call.Session.CharacterId ?? 0);
        await transaction.CommitAsync();
        return PyNone.Instance;
    }

    /// <summary>Every mutation requires the current controller and moon service access, as in validateMoonExtractionMutationAccess.</summary>
    private async Task<StructureRecord> GateAsync(ServiceCall call, NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId)
    {
        var record = structureId > 0 ? await _structures.GetAsync(connection, transaction, structureId, true, call.Cancellation) : null;
        if (record is null || record.Snapshot.DestroyedMs > 0)
            throw StructureDefenseDenied();
        if (_rules.IsPochven(record.SolarSystemId))
            throw Refusal("Moon mining is not available in Pochven.");
        // EVE.js isControllingStructureSession: the session is docked in this structure and its ship is the structure (else a named notice)
        if (call.Session.GetInt("structureid") != structureId || call.Session.GetInt("shipid") != structureId)
            throw Refusal("You must be controlling this structure.");
        if (call.Session.CharacterId is not { } characterId || call.Session.SessionId <= 0)
            throw StructureDefenseDenied();
        // the persistent pilot record (ST-5) must agree with the session: a forged or released session changes nothing
        var pilot = await _pilots.GetBySessionAsync(connection, transaction, call.Session.SessionId, call.Cancellation, forUpdate: true);
        if (pilot is null || pilot.CharacterId != characterId || pilot.StructureId != structureId ||
            !await _hasMoonAccess(characterId, structureId, call.Cancellation))
            throw StructureDefenseDenied();
        var services = await _structures.ServicesAsync(connection, transaction, structureId, call.Cancellation);
        if (services.GetValueOrDefault(Eve.Structures.StructureServiceIds.MoonMining) != ServiceState.Online)
            throw DrillRequired();
        return record;
    }

    private static Exception StructureDefenseDenied() => MachoErrors.ThrowUserError("StructureDefenseDenied");

    private static Exception DrillRequired() => Refusal("An online Standup Moon Drill is required.");

    /// <summary>The refusal the client shows: a named notice (the client reports success on any reply).</summary>
    public static Exception Refusal(string message) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(message)));

    private static long PositiveId(PyValue value)
    {
        var id = value switch
        {
            PyInt integer => integer.Value,
            PyLong integer => integer.Value,
            _ => 0L,
        };
        return id > 0 ? id : 0;
    }

    private static double Number(PyValue value) => value switch
    {
        PyReal real when double.IsFinite(real.Value) => real.Value,
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => 0,
    };
}
