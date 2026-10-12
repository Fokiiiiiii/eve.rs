using System.Text.Json;

namespace Eve.Insurgency;

/// <summary>
/// A pirate warzone: the pirate faction, the pirate NPC corporation (the FOB's owner), the pirate militia corporation (the loyalty points of a pirate win),
/// the NPC pool of its sites, the FOB its insurgencies spawn (SDE group 4644, "Pirate Forward Operating Base"), and the Empire militia the Empire side's loyalty
/// points come from when the FOB's system names no Empire faction (<c>EmpireFallbackMilitiaId</c>). The warzone ids and their systems are the client's Faction
/// Warfare warzone map (<c>warzonespositiondata</c>: warzone 1 Angel, 2 Guristas).
/// </summary>
public sealed record Warzone(long WarzoneId, int PirateFactionId, long PirateCorporationId, long MilitiaCorporationId, string SpawnPool, int FobTypeId, long EmpireFallbackMilitiaId);

public static class InsurgencyWarzones
{
    /// <summary>The Angel Cartel warzone (SDE faction 500011, corporation 1000138, militia 1000436; the client's warzone 1, Amarr-Minmatar: the Amarr militia falls back).</summary>
    public static readonly Warzone Angel = new(1, 500011, 1000138, 1000436, "angels_deadspace_pve", 78260, 1000179);

    /// <summary>The Guristas warzone (SDE faction 500010, corporation 1000127, militia 1000437; the client's warzone 2, Caldari-Gallente: the Caldari militia falls back).</summary>
    public static readonly Warzone Guristas = new(2, 500010, 1000127, 1000437, "guristas_deadspace_pve", 79172, 1000180);

    /// <summary>The militia corporation of each Faction Warfare Empire faction (the client's factions table: <c>militiaCorporationID</c>).</summary>
    private static readonly IReadOnlyDictionary<int, long> EmpireMilitia = new Dictionary<int, long>
    {
        [500001] = 1000180, // Caldari State
        [500002] = 1000182, // Minmatar Republic
        [500003] = 1000179, // Amarr Empire
        [500004] = 1000181, // Gallente Federation
    };

    /// <summary>The Empire militia whose loyalty points an Empire win pays: that of the Empire faction that owns the FOB's system, else the warzone's fallback.</summary>
    public static long EmpireMilitiaOf(Warzone warzone, int systemFactionId) =>
        EmpireMilitia.TryGetValue(systemFactionId, out var militia) ? militia : warzone.EmpireFallbackMilitiaId;

    public static IReadOnlyList<Warzone> All { get; } = [Angel, Guristas];

    private static readonly IReadOnlyDictionary<long, IReadOnlySet<int>> Systems = LoadSystems();

    /// <summary>The solar systems of a warzone on the client's warzone map (none for an unknown warzone).</summary>
    public static IReadOnlySet<int> SystemsOf(long warzoneId) => Systems.GetValueOrDefault(warzoneId) ?? new HashSet<int>();

    private static IReadOnlyDictionary<long, IReadOnlySet<int>> LoadSystems()
    {
        using var stream = typeof(InsurgencyWarzones).Assembly.GetManifestResourceStream("warzone-systems.json")
            ?? throw new InvalidOperationException("warzone-systems.json is not embedded in Eve.Insurgency");
        var raw = JsonSerializer.Deserialize<Dictionary<string, int[]>>(stream)!;
        return raw.ToDictionary(pair => long.Parse(pair.Key), pair => (IReadOnlySet<int>)pair.Value.ToHashSet());
    }
}
