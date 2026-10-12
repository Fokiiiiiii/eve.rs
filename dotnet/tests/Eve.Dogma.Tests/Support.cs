using System.Text.Json;
using Eve.Data;
using Eve.Sde;
using Npgsql;

namespace Eve.Dogma.Tests;

/// <summary>Skips a test, loudly, when there is no PostgreSQL with the SDE loaded (see tools/dev/pg18.sh).</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (Url is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }

    public static string? Url => Environment.GetEnvironmentVariable("EVE_PG_URL");

    public static async Task<SdeReader> SdeAsync()
    {
        var reader = new SdeReader(NpgsqlDataSource.Create(PgConnection.ForTest(Url!)));
        Assert.True(await reader.BuildAsync() is not null, "no SDE is loaded: run `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`");
        return reader;
    }
}

/// <summary>Reads what the reference sent, as the recorder wrote it (<c>{"$tuple": …}</c>, <c>{"$dict": [[k, v], …]}</c>, <c>{"$long": n}</c>).</summary>
public static class Recorded
{
    public static JsonElement Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "scenarios", name);
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    public static IEnumerable<(JsonElement Key, JsonElement Value)> Dict(JsonElement value) =>
        value.GetProperty("$dict").EnumerateArray().Select(pair => (pair[0], pair[1]));

    public static JsonElement[] Tuple(JsonElement value) => [.. value.GetProperty("$tuple").EnumerateArray()];

    /// <summary>The fields of a <c>util.KeyVal</c>.</summary>
    public static Dictionary<string, JsonElement> KeyVal(JsonElement value) =>
        Dict(value.GetProperty("args")).ToDictionary(p => p.Key.GetString()!, p => p.Value);

    public static double Number(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object ? value.GetProperty("$long").GetDouble() : value.GetDouble();

    public static long Long(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object ? value.GetProperty("$long").GetInt64() : value.GetInt64();

    public static Dictionary<int, double> Attributes(JsonElement dict) =>
        Dict(dict).ToDictionary(p => (int)Number(p.Key), p => Number(p.Value));
}
