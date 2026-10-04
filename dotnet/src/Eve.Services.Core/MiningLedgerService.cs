using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Marshal;

namespace Eve.Services.Core;

/// <summary>Read-only EVE.js characterMiningLedger RPCs over the persisted miningLedger table.</summary>
public sealed class CharacterMiningLedgerService(
    Func<CancellationToken, Task<JsonObject>> readMiningLedger,
    Func<long>? nowFileTime = null) : IService
{
    private readonly Func<CancellationToken, Task<JsonObject>> _readMiningLedger =
        readMiningLedger ?? throw new ArgumentNullException(nameof(readMiningLedger));
    private readonly Func<long> _nowFileTime = nowFileTime ?? (() => DateTime.UtcNow.ToFileTimeUtc());

    public string Name => "characterMiningLedger";

    public bool HasMethod(string method) => method == "GetCharacterLogs";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;

        call.Cancellation.ThrowIfCancellationRequested();
        var characterId = call.Session.GetInt("characterID") ?? 0;
        if (characterId <= 0)
            characterId = call.Session.GetInt("charid") ?? 0;
        characterId = Math.Max(0, characterId);
        if (characterId == 0)
            return new PyList([]);

        var state = await _readMiningLedger(call.Cancellation);
        call.Cancellation.ThrowIfCancellationRequested();
        var bucket = (state["characters"] as JsonObject)?[MiningLedgerData.Key(characterId)] as JsonObject;
        return MiningLedgerData.Entries(bucket, "characterVisibleAfter", _nowFileTime());
    }
}

/// <summary>Read-only EVE.js corpMiningLedger RPCs over the persisted miningLedger table.</summary>
public sealed class CorpMiningLedgerService(
    Func<CancellationToken, Task<JsonObject>> readMiningLedger,
    Func<long>? nowFileTime = null) : IService
{
    // Matches Eve.Corporations.CorporationRoles and EVE.js CORP_ROLE_* values.
    private const long LedgerRoleMask = 1L | 256L | (1L << 52);

    private readonly Func<CancellationToken, Task<JsonObject>> _readMiningLedger =
        readMiningLedger ?? throw new ArgumentNullException(nameof(readMiningLedger));
    private readonly Func<long> _nowFileTime = nowFileTime ?? (() => DateTime.UtcNow.ToFileTimeUtc());

    public string Name => "corpMiningLedger";

    public bool HasMethod(string method) => method is "GetObserversWithMiningEvents" or "GetObserverLedger";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;

        call.Cancellation.ThrowIfCancellationRequested();
        var corporationId = MiningLedgerData.Positive(call.Session.GetInt("corporationID") ?? 0);
        if (corporationId == 0)
            corporationId = MiningLedgerData.Positive(call.Session.GetInt("corpid") ?? 0);
        var roles = call.Session.GetInt("corprole") ?? 0;
        if (corporationId == 0 || (roles & LedgerRoleMask) == 0)
            return new PyList([]);

        var observerId = call.Method == "GetObserverLedger" ? MiningLedgerData.Positive(call.Arg(0)) : 0;
        if (call.Method == "GetObserverLedger" && observerId == 0)
            return new PyList([]);

        var state = await _readMiningLedger(call.Cancellation);
        call.Cancellation.ThrowIfCancellationRequested();
        var observers = state["observers"] as JsonObject;
        var now = _nowFileTime();

        if (call.Method == "GetObserverLedger")
        {
            var bucket = observers?[MiningLedgerData.Key(observerId)] as JsonObject;
            if (MiningLedgerData.Integer(bucket?["corporationID"]) != corporationId)
                return new PyList([]);
            return MiningLedgerData.Entries(bucket, "observerVisibleAfter", now);
        }

        if (observers is null)
            return new PyList([]);

        var headers = new List<MiningLedgerData.ObserverHeader>();
        foreach (var (key, node) in observers)
        {
            if (node is not JsonObject bucket || MiningLedgerData.Integer(bucket["corporationID"]) != corporationId)
                continue;
            if (bucket["entries"] is not JsonArray entries || entries.Count == 0 ||
                !entries.Any(entry => entry is JsonObject row && MiningLedgerData.IsRetained(row, now) && MiningLedgerData.IsVisible(row, "observerVisibleAfter", now)))
                continue;

            var itemId = MiningLedgerData.Positive(MiningLedgerData.Integer(bucket["itemID"]));
            if (itemId == 0)
                itemId = MiningLedgerData.Positive(MiningLedgerData.Integer(key));
            if (itemId == 0)
                continue;

            headers.Add(new MiningLedgerData.ObserverHeader(
                itemId,
                MiningLedgerData.Positive(MiningLedgerData.Integer(bucket["solarSystemID"])),
                MiningLedgerData.Text(bucket["itemName"]) is { Length: > 0 } name ? name : $"Observer {itemId}"));
        }

        var comparer = StringComparer.CurrentCulture;
        return new PyList([.. headers.OrderBy(header => header.ItemName, comparer)
            .ThenBy(header => header.ItemId)
            .Select(MiningLedgerData.Header)]);
    }

}

internal static class MiningLedgerData
{
    // EVE.js prunes records older than 90 days before producing any ledger view.
    private const long RetentionFileTimeTicks = 90L * 24 * 60 * 60 * 1000 * 10_000;

    private static readonly string[] EntryFields =
    [
        "entryID", "eventDate", "characterID", "corporationID", "solarSystemID", "solarsystemID",
        "typeID", "quantity", "quantityWasted", "quantityCritical", "shipTypeID", "moduleTypeID",
        "observerItemID", "yieldKind",
    ];

    internal readonly record struct ObserverHeader(long ItemId, long SolarSystemId, string ItemName);

    internal static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);

    internal static long Integer(JsonNode? value)
    {
        var text = value?.ToString();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return integer;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
            double.IsFinite(number) && number > long.MinValue && number < long.MaxValue)
            return (long)Math.Truncate(number);
        return 0;
    }

    internal static long Positive(long value) => Math.Max(0, value);

    internal static long Positive(PyValue value) => value switch
    {
        PyInt number => Positive(number.Value),
        PyLong number => Positive(number.Value),
        PyStr text => long.TryParse(text.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? Positive(parsed) : 0,
        _ => 0,
    };

    internal static bool IsVisible(JsonObject entry, string field, long nowFileTime) => Integer(entry[field]) <= nowFileTime;

    internal static bool IsRetained(JsonObject entry, long nowFileTime) =>
        Integer(entry["eventDate"]) >= nowFileTime - RetentionFileTimeTicks;

    internal static PyValue Entries(JsonObject? bucket, string visibilityField, long nowFileTime)
    {
        if (bucket?["entries"] is not JsonArray entries)
            return new PyList([]);

        var visible = entries.OfType<JsonObject>()
            .Where(entry => IsRetained(entry, nowFileTime) && IsVisible(entry, visibilityField, nowFileTime))
            .OrderByDescending(entry => Integer(entry["eventDate"]))
            .ThenByDescending(entry => Integer(entry["entryID"]))
            .Select(Entry);
        return new PyList([.. visible]);
    }

    internal static PyValue Header(ObserverHeader header) => Py.KeyVal(
        ("itemID", new PyInt(header.ItemId)),
        ("solarSystemID", new PyInt(header.SolarSystemId)),
        ("solarsystemID", new PyInt(header.SolarSystemId)),
        ("itemName", new PyStr(header.ItemName)));

    private static PyValue Entry(JsonObject entry)
    {
        var systemId = Integer(entry["solarSystemID"]);
        var values = EntryFields.Select(field => field switch
        {
            "eventDate" => (PyValue)new PyLong(Integer(entry[field])),
            "solarSystemID" or "solarsystemID" => new PyInt(systemId),
            "yieldKind" => new PyStr(Text(entry[field])),
            _ => new PyInt(Integer(entry[field])),
        });
        return Py.KeyVal([.. EntryFields.Zip(values, (field, value) => (field, value))]);
    }

    internal static string Text(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
}
