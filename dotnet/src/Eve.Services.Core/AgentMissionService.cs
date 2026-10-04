using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

// Readers return persisted character mission state, character-owned bookmark records,
// and the character's already-resolved seven-field research rows. They never write.
public sealed class AgentMissionService(
    Func<long, CancellationToken, ValueTask<JsonObject?>> readMissionState,
    Func<long, IReadOnlyList<long>, CancellationToken, ValueTask<IReadOnlyDictionary<long, JsonObject>>> readBookmarks,
    Func<long, CancellationToken, ValueTask<JsonArray?>> readResearchRows,
    Func<long, long, ClientSession, bool>? isAtAgentLocation = null) : IService
{
    public string Name => "agentMgr";

    public bool HasMethod(string method) => method is "GetMyJournalDetails";

    public ValueTask<PyValue> CallAsync(ServiceCall call) => call.Method switch
    {
        "GetMyJournalDetails" => ReadAsync(call),
        _ => ValueTask.FromResult<PyValue>(PyNone.Instance),
    };

    private async ValueTask<PyValue> ReadAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var characterId = Positive(call.Session.Get("charid"));
        if (characterId == 0) return EmptyJournal();

        var state = await readMissionState(characterId, call.Cancellation);
        call.Cancellation.ThrowIfCancellationRequested();
        var missions = state?["missionsByAgentID"] as JsonObject;
        var entries = missions is null
            ? []
            : missions
                .Select(pair => (AgentId: PositiveString(pair.Key), Record: pair.Value as JsonObject))
                .Where(entry => entry.AgentId > 0 && entry.Record is not null)
                .OrderBy(entry => entry.AgentId)
                .ToArray();

        var bookmarkIds = entries
            .SelectMany(entry => BookmarkIds(entry.Record!))
            .ToArray();
        var bookmarkRecords = bookmarkIds.Length == 0
            ? new Dictionary<long, JsonObject>()
            : await readBookmarks(characterId, bookmarkIds.Distinct().ToArray(), call.Cancellation);
        call.Cancellation.ThrowIfCancellationRequested();

        var missionRows = new List<PyValue>(entries.Length);
        foreach (var entry in entries)
        {
            if (MissionRow(entry.AgentId, entry.Record!, call.Session, characterId, bookmarkRecords) is { } row)
                missionRows.Add(row);
        }

        var research = await readResearchRows(characterId, call.Cancellation);
        call.Cancellation.ThrowIfCancellationRequested();
        var researchRows = research is null
            ? []
            : research.Select(row => (PyValue)Tuple(row as JsonArray)).ToArray();
        return new PyTuple([new PyList(missionRows), new PyList(researchRows)]);
    }

    private static PyTuple EmptyJournal() => new([new PyList([]), new PyList([])]);

    private PyValue? MissionRow(
        long agentId,
        JsonObject record,
        ClientSession session,
        long characterId,
        IReadOnlyDictionary<long, JsonObject> bookmarkRecords)
    {
        var status = Text(record["runtimeStatus"]).ToLowerInvariant() switch
        {
            "accepted" => 2,
            "completed" => 4,
            _ => 1,
        };
        var missionName = Positive(record["missionNameID"]) is > 0 and var nameId
            ? (PyValue)new PyInt(nameId)
            : Text(record["missionTitle"]) is { Length: > 0 } title
                ? new PyStr(title)
                : null;
        var missionType = Text(record["missionTypeLabel"]);
        if (missionName is null || missionType.Length == 0) return null;

        var clientMission = record["clientMissionRecord"] as JsonObject;
        var objectiveMode = Text(record["objectiveMode"]);
        var explicitOfferable = BooleanValue(clientMission?["remoteOfferable"]);
        var remoteOfferable = explicitOfferable ??
            (objectiveMode != "agent" && Positive(clientMission?["initialAgentGiftTypeID"]) == 0 && objectiveMode != "transport");
        var explicitCompletable = BooleanValue(record["remoteCompletable"]) ?? BooleanValue(clientMission?["remoteCompletable"]) ?? false;
        var remoteCompletable = explicitCompletable &&
            (isAtAgentLocation?.Invoke(characterId, agentId, session) != true);
        var bookmarks = BookmarkIds(record)
            .Where(bookmarkRecords.ContainsKey)
            .Select(id => (PyValue)Bookmark(bookmarkRecords[id]))
            .ToArray();

        return Tuple(
            new PyInt(status),
            new PyInt(BooleanValue(record["importantMission"]) == true ? 1 : 0),
            new PyStr(missionType),
            missionName,
            new PyInt(agentId),
            Py.FileTimeLong(Integer(record["expiresAtFileTime"])),
            new PyList(bookmarks),
            new PyInt(remoteOfferable ? 1 : 0),
            new PyInt(remoteCompletable ? 1 : 0),
            MissionContentId(record["contentID"]));
    }

    private static PyValue Bookmark(JsonObject record)
    {
        var metadata = record["metadata"] as JsonObject;
        var role = Text(metadata?["role"]).ToLowerInvariant();
        var x = 0d;
        var y = 0d;
        var z = 0d;
        var coordinates = TryNumber(record["x"], out x) &&
            TryNumber(record["y"], out y) &&
            TryNumber(record["z"], out z);
        if (!coordinates) (x, y, z) = (0, 0, 0);
        var fields = new List<(string, PyValue)>
        {
            ("itemID", OptionalPositive(record["itemID"])),
            ("typeID", new PyInt(Positive(record["typeID"]) is > 0 and var typeId ? typeId : 5)),
            ("agentID", OptionalPositive(metadata?["agentID"])),
            ("hint", new PyStr(Text(metadata?["hint"]))),
            ("locationType", new PyStr(Text(metadata?["locationType"]))),
            ("memo", new PyStr("")),
            ("isAgentBase", new PyInt(BooleanValue(metadata?["isAgentBase"]) == true ? 1 : 0)),
            ("locationNumber", new PyInt(role == "agenthomebase" ? -1 : 0)),
        };
        if (BooleanValue(metadata?["deadspace"]) == true) fields.Add(("deadspace", PyBool.True));
        fields.Add(("created", Py.FileTimeLong(Integer(record["created"]))));
        fields.Add(("flag", PyNone.Instance));
        fields.Add(("locationID", new PyInt(Positive(record["locationID"]))));
        fields.Add(("ownerID", OptionalPositive(record["creatorID"])));
        fields.Add(("y", coordinates ? new PyReal(y) : new PyInt(0)));
        fields.Add(("x", coordinates ? new PyReal(x) : new PyInt(0)));
        fields.Add(("solarsystemID", new PyInt(Positive(metadata?["solarsystemID"]) is > 0 and var systemId
            ? systemId : Positive(record["locationID"]))));
        fields.Add(("z", coordinates ? new PyReal(z) : new PyInt(0)));
        return Py.KeyVal([.. fields]);
    }

    private static IEnumerable<long> BookmarkIds(JsonObject record) =>
        record["bookmarkIDsByRole"] is JsonObject roles
            ? roles.Select(pair => Positive(pair.Value)).Where(id => id > 0)
            : [];

    private static PyTuple Tuple(JsonArray? row) =>
        new(row is null ? [] : [.. row.Select(Payload)]);

    private static PyTuple Tuple(params PyValue[] values) => new(values);

    private static PyValue Payload(JsonNode? value) => value switch
    {
        null => PyNone.Instance,
        JsonObject obj => new PyDict([.. obj.Select(pair => new KeyValuePair<PyValue, PyValue>(new PyStr(pair.Key), Payload(pair.Value)))]),
        JsonArray array => new PyList([.. array.Select(Payload)]),
        _ => value.GetValueKind() switch
        {
            JsonValueKind.String => new PyStr(value.GetValue<string>()),
            JsonValueKind.True => PyBool.True,
            JsonValueKind.False => PyBool.False,
            JsonValueKind.Number => TryInteger(value, out var number) ? new PyInt(number) : new PyReal(Number(value)),
            _ => PyNone.Instance,
        },
    };

    private static PyValue MissionContentId(JsonNode? value) =>
        value is JsonValue json && json.GetValueKind() == JsonValueKind.String &&
        long.TryParse(json.GetValue<string>(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? new PyInt(id)
            : Payload(value);

    private static PyValue OptionalPositive(JsonNode? value) =>
        Positive(value) is > 0 and var id ? new PyInt(id) : PyNone.Instance;

    private static long Positive(PyValue? value) =>
        value switch
        {
            PyInt integer when integer.Value > 0 => integer.Value,
            PyLong integer when integer.Value > 0 => integer.Value,
            _ => 0,
        };

    private static long Positive(JsonNode? value) =>
        Integer(value) is > 0 and var number ? number : 0;

    private static long PositiveString(string value) =>
        long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0
            ? id : 0;

    private static long Integer(JsonNode? value) => TryInteger(value, out var number) ? number : 0;

    private static bool TryInteger(JsonNode? value, out long number) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out number);

    private static bool TryNumber(JsonNode? value, out double number)
    {
        if (double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) && double.IsFinite(number))
            return true;
        number = 0;
        return false;
    }

    private static double Number(JsonNode? value) => TryNumber(value, out var number) ? number : 0;

    private static bool? BooleanValue(JsonNode? value) => value?.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when Integer(value) is 1 => true,
        JsonValueKind.Number when Integer(value) is 0 => false,
        _ => null,
    };

    private static string Text(JsonNode? value) =>
        value is JsonValue json && json.GetValueKind() == JsonValueKind.String ? json.GetValue<string>().Trim() : "";

}
