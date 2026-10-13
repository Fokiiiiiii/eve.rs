using System.IO.Compression;
using System.Text.Json;
using Eve.Universe;

namespace Eve.Universe.Tests;

/// <summary>A fact that needs the V24.01 client export (<c>EVE_CLIENT_SDE_DIR</c>); skipped, not passed, without it.</summary>
public sealed class ClientSdeFactAttribute : FactAttribute
{
    public ClientSdeFactAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ClientStaticData.DirectoryVariable))) Skip = "EVE_CLIENT_SDE_DIR is not set"; }
}

/// <summary>
/// The embedded copies of the client data (<c>Data/client-*.jsonl.gz</c>): each one holds the rows its header records, of the build the
/// header records. When the export is configured, the embedded copies give the same answers as the export.
/// </summary>
public sealed class ClientStaticDataTests
{
    private const int Build = 3396210;

    [Theory]
    [InlineData("client-solarsystemcontent.jsonl.gz", 5)]
    [InlineData("client-spacecomponentsbytype.jsonl.gz", 9)]
    [InlineData("client-systems.jsonl.gz", 8113)]
    public async Task Each_embedded_copy_holds_the_rows_its_header_records_for_the_build(string resource, int expectedRows)
    {
        await using var stream = typeof(ClientStaticData).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"{resource} is not embedded");
        using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress));
        var header = JsonDocument.Parse(await reader.ReadLineAsync() ?? "").RootElement.GetProperty("_header");
        var rows = 0;
        while (await reader.ReadLineAsync() is { Length: > 0 })
        {
            rows++;
        }

        Assert.Equal(Build, header.GetProperty("build").GetInt32());
        Assert.Equal(expectedRows, header.GetProperty("rows").GetInt32());
        Assert.Equal(expectedRows, rows);
    }

    [ClientSdeFact]
    public async Task The_export_and_the_embedded_copies_give_the_same_answers()
    {
        var directory = Environment.GetEnvironmentVariable(ClientStaticData.DirectoryVariable);

        // the rows in key order, as text: the export's own row order is not the copy's
        Assert.Equal(Pairs(await ClientSystemFlags.LoadAsync("")), Pairs(await ClientSystemFlags.LoadAsync(directory)));
        Assert.Equal(Pairs(await ClientGateRules.AllowedShipListsAsync("")), Pairs(await ClientGateRules.AllowedShipListsAsync(directory)));
        Assert.Equal(Pairs(await ClientGateRules.DeployBlockersAsync("")), Pairs(await ClientGateRules.DeployBlockersAsync(directory)));
        Assert.Equal(Pairs(await WormholeSystemClasses.LoadAllAsync("")), Pairs(await WormholeSystemClasses.LoadAllAsync(directory)));
        Assert.Equal(Tolls(await ClientGateRules.TollsAsync("")), Tolls(await ClientGateRules.TollsAsync(directory)));
    }

    private static string Pairs<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> values) where TKey : notnull =>
        string.Join(";", values.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));

    private static string Tolls(IReadOnlyDictionary<int, AlignmentToll> tolls) => string.Join(";", tolls.OrderBy(pair => pair.Key).Select(pair =>
        $"{pair.Key}={pair.Value.PricePerKilo},{pair.Value.Exponent},{pair.Value.FixedAddition},{pair.Value.MinCost},{pair.Value.AlignedDiscountMultiplier},"
        + string.Join("/", pair.Value.AlignmentRestrictionList)));
}
