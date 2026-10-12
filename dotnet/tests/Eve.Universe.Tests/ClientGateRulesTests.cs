using System.IO.Compression;
using System.Text;
using Eve.Universe;

namespace Eve.Universe.Tests;

/// <summary>The client's stargate lists and alignment tolls, read from a synthetic export (no client data is used).</summary>
public sealed class ClientGateRulesTests
{
    private static MemoryStream Gzip(params string[] lines)
    {
        var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, Encoding.UTF8))
        {
            foreach (var line in lines)
            {
                writer.WriteLine(line);
            }
        }
        buffer.Position = 0;
        return buffer;
    }

    [Fact]
    public async Task Each_stargate_with_a_list_is_read_and_a_gate_without_one_is_not()
    {
        using var file = Gzip(
            """{"_key": 1, "stargates": {"50016566": {"destination": 50016562, "typeID": 77921, "allowedShipsTypeListID": 579}, "50016567": {"typeID": 77921}}}""",
            """{"_key": 2, "stargates": {"60000001": {"allowedShipsTypeListID": 12}}}""");

        var directory = DirectoryWith(file);
        try
        {
            var lists = await ClientGateRules.AllowedShipListsAsync(directory);

            Assert.Equal(new Dictionary<long, int> { [50016566] = 579, [60000001] = 12 }, lists);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_type_with_an_alignment_toll_is_read_with_its_attributes_and_factions()
    {
        using var file = Gzip(
            """{"_key": 77918, "alignmentBasedToll": {"pricePerKilo": 0.015, "exponent": 1.1, "fixedAddition": -100000, "minCost": 10000, "alignedDiscountMultiplier": 0.0, "alignmentRestrictionList": [500011, 500010]}}""",
            """{"_key": 35, "deployBlocker": {}}""");

        var directory = DirectoryWith(file);
        try
        {
            var tolls = await ClientGateRules.TollsAsync(directory);

            var toll = Assert.Single(tolls).Value;
            Assert.Equal((0.015, 1.1, -100_000d, 10_000d, 0.0), (toll.PricePerKilo, toll.Exponent, toll.FixedAddition, toll.MinCost, toll.AlignedDiscountMultiplier));
            Assert.Equal(new long[] { 500011, 500010 }, toll.AlignmentRestrictionList);
            Assert.False(tolls.ContainsKey(35));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Without_a_directory_the_embedded_copy_gives_the_gate_lists_tolls_and_blockers_of_the_build()
    {
        var lists = await ClientGateRules.AllowedShipListsAsync("");
        var tolls = await ClientGateRules.TollsAsync("");
        var blockers = await ClientGateRules.DeployBlockersAsync("");

        Assert.Equal(8, lists.Count);
        Assert.All(lists.Values, list => Assert.Equal(579, list));
        Assert.Equal(new[] { 77918, 78264, 78265, 78266 }, tolls.Keys.OrderBy(type => type));
        Assert.Equal(new[] { 77876, 77918, 77953, 77954, 77955, 78264, 78265, 78266, 90666 }, blockers.Keys.OrderBy(type => type));
        Assert.Equal(new DeployBlocker(100_000_000, 100_000_000), blockers[77876]);
    }

    /// <summary>Writes the stream as <c>native/solarsystemcontent.jsonl.gz</c> and <c>native/spacecomponentsbytype.jsonl.gz</c> of a new directory.</summary>
    private static string DirectoryWith(Stream gzipped)
    {
        var directory = Path.Combine(Path.GetTempPath(), "eve-gate-rules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "native"));
        using (var target = File.Create(Path.Combine(directory, "native", "solarsystemcontent.jsonl.gz")))
        {
            gzipped.CopyTo(target);
        }
        using (var target = File.Create(Path.Combine(directory, "native", "spacecomponentsbytype.jsonl.gz")))
        {
            gzipped.Position = 0;
            gzipped.CopyTo(target);
        }
        gzipped.Position = 0;
        return directory;
    }
}
