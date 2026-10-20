using System.Text.Json;

namespace Eve.Services.Space.Sites;

public sealed record SiteWave(string Query, int Amount);

/// <summary>A rock of a deposit site: its ore type, its quantity, and its offset from the site's origin (metres).</summary>
public sealed record SiteRock(int TypeId, long Quantity, double[] Offset);

/// <summary>A container of a site: what it is for (<c>data</c> or <c>relic</c> are opened with the matching analyzer), how many there are and the type names it may have.</summary>
public sealed record SiteContainer(string Role, string? Analyzer, int Count, IReadOnlyList<string> Types);

/// <summary>
/// A site the host can spawn: a dungeon of the static data with the NPCs it holds. <see cref="Kind"/> is <c>anomaly</c> (seen in the system scanner without probes) or <c>signature</c>
/// (found with probes); <see cref="Family"/> is <c>combat</c>, <c>data</c> or <c>relic</c>. The table is made by <c>tools/dev/extract-site-templates.py</c>.
/// </summary>
public sealed record SiteTemplate(
    string Id, int DungeonId, string Name, string Kind, string Family, int Difficulty, IReadOnlyList<string> Bands, int? ArchetypeId, int? FactionId,
    int? DungeonNameId, int? EntryObjectTypeId, IReadOnlyList<SiteWave> Waves, IReadOnlyList<SiteContainer> Containers,
    IReadOnlyList<SiteRock>? Rocks = null, IReadOnlyList<int>? Clouds = null)
{
    public const string Anomaly = "anomaly";
    public const string Signature = "signature";

    /// <summary>The band of the J-space sites (the wormhole templates): their sites are the plan of <c>WormholeSites</c>, never a draw of a band.</summary>
    public const string WormholeBand = "wormhole";

    public bool IsWormhole => Bands.Contains(WormholeBand);

    public bool IsAnomaly => Kind == Anomaly;

    public int Level => Math.Clamp(Difficulty, 1, 5);

    /// <summary>The ids of the content definition of the site and of the objectives its NPCs are counted by.</summary>
    public string DefinitionId => $"site.{Id}";

    public int NpcCount => Waves.Sum(wave => wave.Amount);

    public int ContainerCount => Containers.Sum(container => container.Count);

    /// <summary>Whether the site places resources when it starts: the rocks of a deposit, or a reservoir's cloud (one of <see cref="Clouds"/>).</summary>
    public bool HasResources => Rocks is { Count: > 0 } || Clouds is { Count: > 0 };

    /// <summary>The sensor strength attribute that finds the site (SDE 208 radar for data, 210 magnetometric for relic, 1136 for the all-strength combat sites; 1908 for a wormhole signature).</summary>
    public int StrengthAttribute => IsWormhole ? 1908 : Family switch
    {
        "data" => 208,
        "relic" => 210,
        _ => 1136,
    };
}

/// <summary>The sites the host can spawn, by the security band they appear in.</summary>
public sealed class SiteTemplates
{
    private readonly IReadOnlyList<SiteTemplate> _all;
    private readonly Dictionary<string, SiteTemplate> _byId;

    public SiteTemplates(IEnumerable<SiteTemplate> templates)
    {
        _all = [.. templates];
        _byId = _all.ToDictionary(template => template.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<SiteTemplate> All => _all;

    public SiteTemplate? ById(string id) => _byId.GetValueOrDefault(id);

    /// <summary>The sites of a kind and family that appear in a band (<c>highsec</c>, <c>lowsec</c>, <c>nullsec</c>), in the table's order.</summary>
    public IReadOnlyList<SiteTemplate> For(string kind, string family, string band) =>
        [.. _all.Where(template => template.Kind == kind && template.Family == family && template.Bands.Contains(band))];

    private sealed record File(List<SiteTemplate> Templates);

    public static SiteTemplates Load()
    {
        return new SiteTemplates(Read("site-templates.json").Templates.Concat(Read("wormhole-site-templates.json").Templates));
    }

    private static File Read(string resource)
    {
        using var stream = typeof(SiteTemplates).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"{resource} is not in the assembly");
        return JsonSerializer.Deserialize<File>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException($"{resource} is empty");
    }
}
