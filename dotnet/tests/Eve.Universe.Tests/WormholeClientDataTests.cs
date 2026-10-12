using System.IO.Compression;
using System.Text;

namespace Eve.Universe.Tests;

/// <summary>A fact that needs the V24.01 client data (<c>EVE_CLIENT_SDE_DIR</c>); skipped, not passed, without it.</summary>
public sealed class ClientSdeFactAttribute : FactAttribute
{
    public ClientSdeFactAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EVE_CLIENT_SDE_DIR"))) Skip = "EVE_CLIENT_SDE_DIR is not set"; }
}

/// <summary>The J-space classes of the client's system table: the rule on synthetic rows, and the real counts when the client data is given.</summary>
public sealed class WormholeClientDataTests
{
    private static string DirectoryWith(params string[] systemRows)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wh-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(directory, "native"));
        using var file = File.Create(Path.Combine(directory, "native", "systems.jsonl.gz"));
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new StreamWriter(gzip, Encoding.UTF8))
        {
            foreach (var row in systemRows) writer.WriteLine(row);
        }
        return directory;
    }

    [Fact]
    public async Task Only_the_j_space_systems_are_read_with_their_classes()
    {
        var directory = DirectoryWith(
            """{"solarSystemID": 31000001, "wormholeClassID": 14, "securityStatus": -0.99}""",
            """{"solarSystemID": 31000005, "wormholeClassID": 12, "securityStatus": -0.99}""",
            """{"solarSystemID": 31000100, "wormholeClassID": 2, "securityStatus": -0.99}""",
            """{"solarSystemID": 30000001, "wormholeClassID": 9, "securityStatus": 0.5}""");
        try
        {
            var classes = await WormholeSystemClasses.LoadAsync(directory);
            Assert.Equal(new Dictionary<int, int> { [31000001] = 14, [31000005] = 12, [31000100] = 2 }, classes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_j_space_system_without_a_class_is_an_error_and_not_a_guess()
    {
        var directory = DirectoryWith("""{"solarSystemID": 31000002, "securityStatus": -0.99}""");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => WormholeSystemClasses.LoadAsync(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task No_directory_loads_no_classes()
    {
        Assert.Empty(await WormholeSystemClasses.LoadAsync(""));
    }

    [ClientSdeFact]
    public async Task The_client_data_gives_a_class_for_all_2604_j_space_systems()
    {
        var classes = await WormholeSystemClasses.LoadAsync(Environment.GetEnvironmentVariable("EVE_CLIENT_SDE_DIR")!);

        Assert.Equal(2604, classes.Count);
        Assert.Equal(358, classes.Values.Count(c => c == 1));
        Assert.Equal(537, classes.Values.Count(c => c == 2));
        Assert.Equal(506, classes.Values.Count(c => c == 3));
        Assert.Equal(523, classes.Values.Count(c => c == 4));
        Assert.Equal(531, classes.Values.Count(c => c == 5));
        Assert.Equal(118, classes.Values.Count(c => c == 6));
        Assert.Equal(25, classes.Values.Count(c => c == 13));
        Assert.Equal(12, classes[31000005]);
        Assert.Equal(14, classes[31000001]);
    }
}
