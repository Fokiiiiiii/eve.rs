using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Eve.Universe;

/// <summary>The alignment-based toll of a stargate type: the client's <c>alignmentBasedToll</c> attributes (spacecomponentsbytype).</summary>
public sealed record AlignmentToll(double PricePerKilo, double Exponent, double FixedAddition, double MinCost, double AlignedDiscountMultiplier,
    IReadOnlyList<long> AlignmentRestrictionList);

/// <summary>
/// The stargate data the V24.01 client keeps locally and the server reads at start-up from <c>EVE_CLIENT_SDE_DIR</c> (nothing is copied):
/// each stargate's <c>allowedShipsTypeListID</c> (solarsystemcontent) and each stargate type's <c>alignmentBasedToll</c> (spacecomponentsbytype).
/// </summary>
public static class ClientGateRules
{
    /// <summary>The allowed-ship type list of each stargate that has one (gate ID to list ID).</summary>
    public static async Task<IReadOnlyDictionary<long, int>> AllowedShipListsAsync(string directory, CancellationToken cancellationToken = default)
    {
        var lists = new Dictionary<long, int>();
        if (string.IsNullOrWhiteSpace(directory))
        {
            return lists;
        }
        await using var file = File.OpenRead(Path.Combine(directory, "native", "solarsystemcontent.jsonl.gz"));
        await foreach (var row in RowsAsync(file, cancellationToken))
        {
            if (!row.TryGetProperty("stargates", out var gates) || gates.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            foreach (var gate in gates.EnumerateObject())
            {
                if (long.TryParse(gate.Name, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var gateId)
                    && gate.Value.TryGetProperty("allowedShipsTypeListID", out var list) && list.ValueKind == JsonValueKind.Number)
                {
                    lists[gateId] = list.GetInt32();
                }
            }
        }
        return lists;
    }

    /// <summary>The alignment-based toll of each stargate type that has one (type ID to toll).</summary>
    public static async Task<IReadOnlyDictionary<int, AlignmentToll>> TollsAsync(string directory, CancellationToken cancellationToken = default)
    {
        var tolls = new Dictionary<int, AlignmentToll>();
        if (string.IsNullOrWhiteSpace(directory))
        {
            return tolls;
        }
        await using var file = File.OpenRead(Path.Combine(directory, "native", "spacecomponentsbytype.jsonl.gz"));
        await foreach (var row in RowsAsync(file, cancellationToken))
        {
            if (!row.TryGetProperty("_key", out var key) || key.ValueKind != JsonValueKind.Number
                || !row.TryGetProperty("alignmentBasedToll", out var toll) || toll.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            tolls[key.GetInt32()] = new AlignmentToll(
                Number(toll, "pricePerKilo"), Number(toll, "exponent"), Number(toll, "fixedAddition"), Number(toll, "minCost"),
                Number(toll, "alignedDiscountMultiplier"),
                toll.TryGetProperty("alignmentRestrictionList", out var factions) && factions.ValueKind == JsonValueKind.Array
                    ? factions.EnumerateArray().Select(faction => faction.GetInt64()).ToArray()
                    : []);
        }
        return tolls;
    }

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0d;

    /// <summary>The rows of a gzipped JSONL export, one at a time (each row is valid until the next one is read).</summary>
    private static async IAsyncEnumerable<JsonElement> RowsAsync(Stream gzipped, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(new GZipStream(gzipped, CompressionMode.Decompress), leaveOpen: false);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }
            using var document = JsonDocument.Parse(line);
            yield return document.RootElement;
        }
    }
}
