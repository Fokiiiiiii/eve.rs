using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Structures;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// The backend of the moon service's mutations (<c>moonExtractions</c>, EVE.js moonExtractionsService): <c>StartNewExtraction</c>, <c>FractureChunkWithStructure</c> and
/// <c>CancelExtraction</c>. Each one passes the same gate in EVE.js's order: the structure exists and the pilot is docked at it, it belongs to the pilot's corporation,
/// it is not in Pochven, and its moon drill is online; the cancel also needs the Station Manager role. The moon's checks and the record are the domain's
/// (<see cref="MoonExtractions"/>). The shortcut is the gate: EVE.js also needs the pilot to control the structure and the profile's access setting, which wait for ST-3 and ST-5.
/// </summary>
public sealed class StructureMoonAuthority
{
    private const long StationManagerRole = 2048;

    private readonly NpgsqlDataSource _source;
    private readonly StructureStore _structures;
    private readonly MoonExtractions _moons;
    private readonly MoonRulesSource _rules;

    public StructureMoonAuthority(NpgsqlDataSource source, StructureStore structures, MoonExtractions moons, MoonRulesSource rules)
    {
        _source = source;
        _structures = structures;
        _moons = moons;
        _rules = rules;
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
        await GateAsync(call, connection, structureId);
        await using var transaction = await connection.BeginTransactionAsync();
        var result = await _moons.StartAsync(connection, transaction, structureId, durationSeconds, addToCalendar);
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
        await GateAsync(call, connection, structureId);
        await using var transaction = await connection.BeginTransactionAsync();
        if (await _moons.FractureAsync(connection, transaction, structureId) == MoonExtractions.FractureOutcome.NotReady)
            throw Refusal("The moon chunk is not ready to fracture.");
        await transaction.CommitAsync();
        return PyNone.Instance;
    }

    private async ValueTask<PyValue> CancelAsync(ServiceCall call)
    {
        var structureId = call.Session.GetInt("structureid") ?? 0;
        await using var connection = await _source.OpenConnectionAsync();
        await GateAsync(call, connection, structureId);
        if (((call.Session.GetInt("corprole") ?? 0) & StationManagerRole) != StationManagerRole)
            throw Refusal("Station Manager role is required.");
        await using var transaction = await connection.BeginTransactionAsync();
        await _moons.CancelAsync(connection, transaction, structureId);
        await transaction.CommitAsync();
        return PyNone.Instance;
    }

    /// <summary>The gate of every mutation (EVE.js validateMoonExtractionMutationAccess, without the control and access checks that wait for ST-3 and ST-5).</summary>
    private async Task GateAsync(ServiceCall call, NpgsqlConnection connection, long structureId)
    {
        var record = structureId > 0 ? await _structures.GetAsync(connection, null, structureId, false) : null;
        if (record is null || record.Snapshot.DestroyedMs > 0 || call.Session.GetInt("structureid") != structureId || call.Session.GetInt("corpid") != record.OwnerCorporationId)
            throw StructureDefenseDenied();
        if (_rules.IsPochven(record.SolarSystemId))
            throw Refusal("Moon mining is not available in Pochven.");
        var services = await _structures.ServicesAsync(connection, null, structureId);
        if (services.GetValueOrDefault(Eve.Structures.StructureServiceIds.MoonMining) != ServiceState.Online)
            throw DrillRequired();
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
