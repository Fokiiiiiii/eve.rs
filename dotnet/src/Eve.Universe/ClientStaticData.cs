using System.Reflection;

namespace Eve.Universe;

/// <summary>
/// The V24.01 client's static data that the server reads. The embedded copies (<c>Data/client-*.jsonl.gz</c>, built by
/// tools/dev/extract-client-sde.py) hold the export's rows with only the fields the server uses, in the export's own format.
/// <c>EVE_CLIENT_SDE_DIR</c>, when it is set and holds the file, is read instead: an override, or a refresh from a newer export.
/// </summary>
public static class ClientStaticData
{
    /// <summary>The environment variable that names the client export directory (optional: without it the embedded copies are read).</summary>
    public const string DirectoryVariable = "EVE_CLIENT_SDE_DIR";

    /// <summary>The solar system content (the flags and the stargate lists) of the directory, or of the embedded copy.</summary>
    public static Stream OpenSolarSystemContent(string? directory) =>
        Open(typeof(ClientStaticData).Assembly, directory, "native/solarsystemcontent.jsonl.gz", "client-solarsystemcontent.jsonl.gz");

    /// <summary>The space components by type (the alignment tolls and the deploy blockers) of the directory, or of the embedded copy.</summary>
    public static Stream OpenSpaceComponents(string? directory) =>
        Open(typeof(ClientStaticData).Assembly, directory, "native/spacecomponentsbytype.jsonl.gz", "client-spacecomponentsbytype.jsonl.gz");

    /// <summary>The wormhole class of each system of the directory, or of the embedded copy.</summary>
    public static Stream OpenSystems(string? directory) =>
        Open(typeof(ClientStaticData).Assembly, directory, "native/systems.jsonl.gz", "client-systems.jsonl.gz");

    /// <summary>
    /// The file <paramref name="relativePath"/> of <paramref name="directory"/> when that directory is set and holds it; otherwise the
    /// </summary>
    public static Stream Open(Assembly owner, string? directory, string relativePath, string resource)
    {
        if (ExportPath(directory, relativePath) is { } path)
        {
            return File.OpenRead(path);
        }
        return owner.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"{resource} is not embedded in {owner.GetName().Name}");
    }

    /// <summary>The export's file when <paramref name="directory"/> is set and holds it; null when the embedded copy is read.</summary>
    public static string? ExportPath(string? directory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }
        var path = Path.Combine(directory, relativePath);
        return File.Exists(path) ? path : null;
    }
}
