using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Structures;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// The backend of the moon service's mutations (<c>moonExtractions</c>, EVE.js moonExtractionsService). <c>StartNewExtraction</c> is served here: the pilot is docked at
/// the structure, the structure belongs to the pilot's corporation and is not in Pochven, the duration is positive; the moon drill's and the moon's checks are the
/// domain's. The shortcut is the gate: EVE.js also needs the pilot to control the structure and the profile's access setting, which wait for ST-3 and ST-5.
/// The fracture and the cancel are named refusals until ST-6a (d).
/// </summary>
public sealed class StructureMoonAuthority
{
    private const string StartMethod = "StartNewExtraction";

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

    /// <summary>Answers a mutation of the moon service. The reply of a start is the extraction (the client's KeyVal); the other mutations are not served yet.</summary>
    public ValueTask<PyValue> CallAsync(ServiceCall call) => call.Method == StartMethod
        ? StartAsync(call)
        : throw Refusal("Moon extraction is not available on this server.");

    private async ValueTask<PyValue> StartAsync(ServiceCall call)
    {
        var structureId = PositiveId(call.Arg(0));
        var durationSeconds = (long)Math.Truncate(Number(call.Arg(1)));
        var addToCalendar = call.Arg(2) is PyBool { Value: true };
        if (durationSeconds <= 0)
            throw MachoErrors.ThrowUserError("MoonminingEventTooShort");

        await using var connection = await _source.OpenConnectionAsync();
        var record = structureId > 0 ? await _structures.GetAsync(connection, null, structureId, false) : null;
        if (record is null || call.Session.GetInt("structureid") != structureId || call.Session.GetInt("corpid") != record.OwnerCorporationId)
            throw StructureDefenseDenied();
        if (_rules.IsPochven(record.SolarSystemId))
            throw Refusal("Moon mining is not available in Pochven.");

        await using var transaction = await connection.BeginTransactionAsync();
        var result = await _moons.StartAsync(connection, transaction, structureId, durationSeconds, addToCalendar);
        switch (result.Outcome)
        {
            case MoonExtractions.StartOutcome.NoStructure:
                throw StructureDefenseDenied();
            case MoonExtractions.StartOutcome.DrillOffline:
                throw Refusal("An online Standup Moon Drill is required.");
            case MoonExtractions.StartOutcome.NoMoonResources:
                throw Refusal("This structure has no moon to mine.");
        }
        await transaction.CommitAsync();

        var payload = MoonExtractionsRpcService.ExtractionPayload(MoonExtractionStore.ToLegacy(result.Extraction!));
        if (result.Outcome == MoonExtractions.StartOutcome.Started)
            call.Session.SendNotification("OnMoonExtractionUpdate", "clientID", payload);
        return payload;
    }

    private static Exception StructureDefenseDenied() => MachoErrors.ThrowUserError("StructureDefenseDenied");

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
