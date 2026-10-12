using System.Text.Json;

namespace Eve.Insurgency;

/// <summary>
/// A pirate warzone: the pirate faction, the pirate corporation, the NPC pool of its sites and the FOB its insurgencies spawn (SDE group 4644, "Pirate Forward
/// Operating Base"). The warzone ids and their systems are the client's Faction Warfare warzone map (<c>warzonespositiondata</c>: warzone 1 Angel, 2 Guristas).
/// </summary>
public sealed record Warzone(long WarzoneId, int PirateFactionId, long PirateCorporationId, string SpawnPool, int FobTypeId);

public static class InsurgencyWarzones
{
    /// <summary>The Angel Cartel warzone (SDE faction 500011, corporation 1000138; the client's warzone 1).</summary>
    public static readonly Warzone Angel = new(1, 500011, 1000138, "angels_deadspace_pve", 78260);

    /// <summary>The Guristas warzone (SDE faction 500010, corporation 1000127; the client's warzone 2).</summary>
    public static readonly Warzone Guristas = new(2, 500010, 1000127, "guristas_deadspace_pve", 79172);

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
