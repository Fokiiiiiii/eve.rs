using System.Text.Json;

namespace Eve.Services.Space.Sites;

/// <summary>One group of NPCs of a site: the pool the NPC selector rolls them from, and how many come (the wiki's table of the site).</summary>
public sealed record SiteWave(string Query, int Amount);

/// <summary>A container of a site: what it is for (<c>data</c> or <c>relic</c> are opened with the matching analyzer), how many there are and the type names it may have.</summary>
public sealed record SiteContainer(string Role, string? Analyzer, int Count, IReadOnlyList<string> Types);

/// <summary>
/// A site the host can spawn: a dungeon of the static data with the NPCs it holds. <see cref="Kind"/> is <c>anomaly</c> (seen in the system scanner without probes) or <c>signature</c>
/// (found with probes); <see cref="Family"/> is <c>combat</c>, <c>data</c> or <c>relic</c>. The table is made by <c>tools/dev/extract-site-templates.py</c>.
/// </summary>
public sealed record SiteTemplate(
    string Id, int DungeonId, string Name, string Kind, string Family, int Difficulty, IReadOnlyList<string> Bands, int? ArchetypeId, int? FactionId,
    int? DungeonNameId, int? EntryObjectTypeId, IReadOnlyList<SiteWave> Waves, IReadOnlyList<SiteContainer> Containers)
{
    public const string Anomaly = "anomaly";
    public const string Signature = "signature";

    public bool IsAnomaly => Kind == Anomaly;

    /// <summary>The level of a signature, one to five (the wiki: levels I to III need a probe strength of 42, IV of 72, V of 104).</summary>
    public int Level => Math.Clamp(Difficulty, 1, 5);

    /// <summary>The ids of the content definition of the site and of the objectives its NPCs are counted by.</summary>
    public string DefinitionId => $"site.{Id}";

    public int NpcCount => Waves.Sum(wave => wave.Amount);

    public int ContainerCount => Containers.Sum(container => container.Count);

    /// <summary>The sensor strength attribute that finds the site (SDE 208 radar for data, 210 magnetometric for relic, 1136 for the all-strength combat sites).</summary>
    public int StrengthAttribute => Family switch
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
        using var stream = typeof(SiteTemplates).Assembly.GetManifestResourceStream("site-templates.json")
            ?? throw new InvalidOperationException("site-templates.json is not in the assembly");
        var file = JsonSerializer.Deserialize<File>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("site-templates.json is empty");
        return new SiteTemplates(file.Templates);
    }
}
