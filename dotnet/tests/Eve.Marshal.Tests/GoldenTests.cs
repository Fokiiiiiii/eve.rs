using System.Text.Json.Nodes;

namespace Eve.Marshal.Tests;

/// <summary>
/// Replays oracle/fixtures/marshal against the C# codec. Encoding must reproduce the recorded bytes
/// exactly; decoding must produce the recorded value (compared in EJSON, where every number keeps
/// its exact IEEE-754 bits); failures must be the same class of failure.
/// </summary>
public class GoldenTests
{
    private static readonly JsonArray Cases = (JsonArray)JsonNode.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "marshal", "cases.json")))!;

    public static IEnumerable<object[]> Ids() => Cases.Select(c => new object[] { (string)c!["id"]! });

    private static JsonObject Case(string id) => (JsonObject)Cases.Single(c => (string)c!["id"]! == id)!;

    private static string Clip(string text) => text.Length > 300 ? text[..300] + "…" : text;

    [Fact]
    public void The_fixture_set_is_the_expected_size()
    {
        Assert.True(Cases.Count > 500, $"fixture set unexpectedly small: {Cases.Count}");
        Assert.Equal(Cases.Count, Cases.Select(c => (string)c!["id"]!).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Encoding_reproduces_the_recorded_bytes(string id)
    {
        var record = Case(id);
        if (record["input"] is null && !record.ContainsKey("input"))
        {
            return; // decode-only case built from raw bytes
        }
        var js = Ejson.From(record["input"]);
        var expected = (string?)record["encoded"];
        var expectedError = (string?)record["encodeError"];

        byte[]? actual = null;
        MarshalException? error = null;
        try
        {
            actual = MarshalCodec.Encode(JsAdapter.ToValue(js));
        }
        catch (MarshalException ex)
        {
            error = ex;
        }

        if (expected is not null)
        {
            Assert.True(error is null, $"{id}: encode failed: {error?.Message}");
            Assert.Equal(expected, Convert.ToHexStringLower(actual!));
        }
        else
        {
            Assert.NotNull(expectedError);
            Assert.True(error is not null, $"{id}: encode succeeded, expected {expectedError}");
            Assert.Equal(expectedError, error!.ReferenceClass);
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Decoding_produces_the_recorded_value_or_the_recorded_failure(string id)
    {
        var record = Case(id);
        if ((string?)record["encoded"] is not { } hex)
        {
            return; // an encode-only case that failed to encode
        }
        var bytes = Convert.FromHexString(hex);
        Check(id, "decode", () => MarshalCodec.Decode(bytes), record["decoded"], (string?)record["decodeError"]);
        Check(id, "decodeExact", () => MarshalCodec.DecodeExact(bytes), record["decodedExact"], (string?)record["decodeExactError"]);
    }

    private static void Check(string id, string label, Func<PyValue> decode, JsonNode? expected, string? expectedError)
    {
        PyValue? value = null;
        MarshalException? error = null;
        try
        {
            value = decode();
        }
        catch (MarshalException ex)
        {
            error = ex;
        }

        if (expectedError is null)
        {
            Assert.True(error is null, $"{id}: {label} failed: {error?.Message}");
            var actual = Ejson.To(JsAdapter.ToJs(value!));
            Assert.True(
                JsonNode.DeepEquals(expected, actual),
                $"{id}: {label} value differs\n  expected {Clip(expected?.ToJsonString() ?? "null")}\n  actual   {Clip(actual?.ToJsonString() ?? "null")}");
        }
        else
        {
            Assert.True(error is not null, $"{id}: {label} succeeded, expected {expectedError}");
            Assert.Equal(expectedError, error!.ReferenceClass);
        }
    }
}
