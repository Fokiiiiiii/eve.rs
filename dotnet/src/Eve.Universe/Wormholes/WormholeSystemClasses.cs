using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Eve.Universe;

/// <summary>
/// The class of each J-space solar system, from the V24.01 client's static data (<c>EVE_CLIENT_SDE_DIR/native/systems.jsonl.gz</c>,
/// build 3396210, field <c>wormholeClassID</c>). The pinned SDE export has no such field for the C1 to C6 systems. Values:
/// 1 to 6 = C1 to C6, 12 Thera, 13 shattered small-ship systems, 14 to 18 Drifter systems. Nothing is copied into the repository.
/// </summary>
public static class WormholeSystemClasses
{
    private const int FirstSystemId = 31_000_000;
    private const int SystemIdLimit = 32_000_000;

    /// <summary>The wormhole class of each J-space system (system ID to class). Empty when no directory is given.</summary>
    public static Task<IReadOnlyDictionary<int, int>> LoadAsync(string directory, CancellationToken cancellationToken = default) =>
        ReadAsync(directory, allSystems: false, cancellationToken);

    /// <summary>
    /// The wormhole class of every system that has one: the J-space systems (all of them) and the K-space systems with
    /// <c>wormholeClassID</c> (7 high-sec, 8 low-sec, 9 null-sec, and the others). K-space systems without a class are left out.
    /// </summary>
    public static Task<IReadOnlyDictionary<int, int>> LoadAllAsync(string directory, CancellationToken cancellationToken = default) =>
        ReadAsync(directory, allSystems: true, cancellationToken);

    private static async Task<IReadOnlyDictionary<int, int>> ReadAsync(string directory, bool allSystems, CancellationToken cancellationToken)
    {
        var classes = new Dictionary<int, int>();
        if (string.IsNullOrWhiteSpace(directory))
        {
            return classes;
        }
        await using var file = File.OpenRead(Path.Combine(directory, "native", "systems.jsonl.gz"));
        await foreach (var row in RowsAsync(file, cancellationToken))
        {
            if (!row.TryGetProperty("solarSystemID", out var id) || id.ValueKind != JsonValueKind.Number)
            {
                continue;
            }
            var systemId = id.GetInt32();
            var isJSpace = systemId is >= FirstSystemId and < SystemIdLimit;
            if (!isJSpace && !allSystems)
            {
                continue;
            }
            if (!row.TryGetProperty("wormholeClassID", out var wormholeClass) || wormholeClass.ValueKind != JsonValueKind.Number)
            {
                if (isJSpace)
                {
                    throw new InvalidDataException($"J-space system {systemId} has no wormholeClassID in the client data");
                }
                continue;
            }
            classes[systemId] = wormholeClass.GetInt32();
        }
        return classes;
    }

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
            yield return document.RootElement.Clone();
        }
    }
}
