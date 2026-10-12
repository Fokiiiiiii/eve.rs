using System.IO.Compression;
using System.Text;
using Eve.Universe;

namespace Eve.Universe.Tests;

/// <summary>The client's disallowCyno and disallowScanning flags, read from a synthetic export (no client data is used).</summary>
public sealed class ClientSystemFlagsTests
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
    public async Task Only_the_two_flags_are_read_and_only_where_one_is_true()
    {
        using var file = Gzip(
            """{"_key": 1, "security": 0.5, "disallowCyno": true}""",
            """{"_key": 2, "disallowScanning": true, "disallowCyno": false}""",
            """{"_key": 3, "sunTypeID": 3796}""",
            """{"_key": 4, "disallowCyno": false, "disallowScanning": false}""",
            "");

        var flags = await ClientSystemFlags.ReadAsync(file);

        Assert.Equal(2, flags.Count);
        Assert.Equal(new ClientSystemFlags(DisallowCyno: true, DisallowScanning: false), flags[1]);
        Assert.Equal(new ClientSystemFlags(DisallowCyno: false, DisallowScanning: true), flags[2]);
        Assert.False(flags.ContainsKey(3));
        Assert.False(flags.ContainsKey(4));
    }

    [Fact]
    public async Task A_blank_directory_loads_nothing()
    {
        Assert.Empty(await ClientSystemFlags.ReadDirectoryAsync(""));
    }

    [Fact]
    public void A_system_has_no_flags_unless_the_export_sets_them()
    {
        var system = new SolarSystemInfo(30_000_001, "Test", 1, 1, 0.5);

        Assert.False(system.DisallowCyno);
        Assert.False(system.DisallowScanning);
    }
}
