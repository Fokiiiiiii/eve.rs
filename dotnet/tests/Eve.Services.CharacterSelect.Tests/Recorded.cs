using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.CharacterSelect.Tests;

/// <summary>What the reference server was recorded doing (oracle/fixtures/scenarios).</summary>
internal static class Recorded
{
    public static readonly JsonObject Login = (JsonObject)JsonNode.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "scenarios", "login_to_character.json")))!;

    /// <summary>The recorded steps that call this method, in order.</summary>
    public static List<JsonObject> Steps(string method) =>
        [.. Login["steps"]!.AsArray().Select(s => (JsonObject)s!).Where(s => s["call"]![1]!.GetValue<string>() == method)];

    /// <summary>The kind of a recorded scalar or of a value we produce: int, long, real, str or none.</summary>
    public static string Kind(JsonNode? node) => node switch
    {
        null => "none",
        JsonObject o when o.ContainsKey("$long") => "long",
        JsonValue v when v.TryGetValue<string>(out _) => "str",
        JsonValue v when v.ToJsonString().Contains('.', StringComparison.Ordinal) => "real",
        JsonValue => "int",
        _ => "other",
    };

    public static string Kind(PyValue value) => value switch
    {
        PyNone => "none",
        PyInt => "int",
        PyLong => "long",
        PyReal => "real",
        PyStr or PyBytes or PyWString => "str",
        _ => "other",
    };

    /// <summary>Attribute names and values of a <c>util.KeyVal</c>, in order.</summary>
    public static List<KeyValuePair<string, PyValue>> Attributes(PyValue keyVal)
    {
        var dict = (PyDict)((PyObject)keyVal).Args;
        return [.. dict.Entries.Select(e => new KeyValuePair<string, PyValue>(JsSemantics.StringValue(e.Key)!, e.Value))];
    }

    /// <summary>Attribute names and kinds of a recorded <c>util.KeyVal</c>, in order.</summary>
    public static List<KeyValuePair<string, string>> RecordedKinds(JsonNode keyVal) =>
        [.. keyVal["args"]!["$dict"]!.AsArray().Select(pair => new KeyValuePair<string, string>(pair![0]!.GetValue<string>(), Kind(pair[1])))];
}
