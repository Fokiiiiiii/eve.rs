using System.IO.Compression;
using System.Text.Json;

namespace Eve.Universe;

/// <summary>
/// The per-system flags the V24.01 client keeps in its own static data (<c>solarSystemContent</c>): <c>disallowCyno</c> and
/// <c>disallowScanning</c>. The client reads them locally (<c>cfg.mapSolarSystemContentCache</c>, loaded from its own
/// <c>res:/staticdata/solarSystemContent.static</c>), so the server cannot send them; it reads its embedded copy of the export
/// (<see cref="ClientStaticData"/>) and enforces them itself. Only the two flags are kept.
/// </summary>
public sealed record ClientSystemFlags(bool DisallowCyno, bool DisallowScanning)
{
    /// <summary>Reads the flags of a gzipped JSONL export. A system with neither flag set is not in the result.</summary>
    public static async Task<IReadOnlyDictionary<int, ClientSystemFlags>> ReadAsync(Stream gzipped, CancellationToken cancellationToken = default)
    {
        var flags = new Dictionary<int, ClientSystemFlags>();
        using var reader = new StreamReader(new GZipStream(gzipped, CompressionMode.Decompress), leaveOpen: false);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            if (!row.TryGetProperty("_key", out var key) || key.ValueKind != JsonValueKind.Number)
            {
                continue;
            }
            var cyno = Flag(row, "disallowCyno");
            var scanning = Flag(row, "disallowScanning");
            if (cyno || scanning)
            {
                flags[key.GetInt32()] = new ClientSystemFlags(cyno, scanning);
            }
        }
        return flags;
    }

    /// <summary>The flags of the client data: the export in <paramref name="directory"/> when it holds the file, else the embedded copy.</summary>
    public static async Task<IReadOnlyDictionary<int, ClientSystemFlags>> LoadAsync(string? directory, CancellationToken cancellationToken = default)
    {
        await using var file = ClientStaticData.OpenSolarSystemContent(directory);
        return await ReadAsync(file, cancellationToken);
    }

    private static bool Flag(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
