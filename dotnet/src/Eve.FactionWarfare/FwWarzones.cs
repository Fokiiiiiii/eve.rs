using System.Text.Json;

namespace Eve.FactionWarfare;

/// <summary>
/// The two Faction Warfare warzones and their solar systems. The map is the client's <c>warzonespositiondata</c> (warzone 1: Amarr and Minmatar,
/// 103 systems; warzone 2: Caldari and Gallente, 149 systems; the same embedded file Eve.Insurgency reads). The initial occupier of a system is
/// its SDE faction (<c>mapSolarSystems.factionID</c>) when that faction is one of the warzone's two; a system with another or no faction starts
/// unoccupied (DEVIATIONS "Factional warfare").
/// </summary>
public static class FwWarzones
{
    public const long AmarrMinmatar = 1;
    public const long CaldariGallente = 2;

    private static readonly IReadOnlyDictionary<long, long[]> FactionsByWarzone = new Dictionary<long, long[]>
    {
        [AmarrMinmatar] = [FwFactions.AmarrEmpire, FwFactions.MinmatarRepublic],
        [CaldariGallente] = [FwFactions.CaldariState, FwFactions.GallenteFederation],
    };

    private static readonly Lazy<(IReadOnlyDictionary<long, long[]> Systems, IReadOnlyDictionary<long, long> WarzoneBySystem)> Map = new(Load);

    /// <summary>The warzone ids, in order.</summary>
    public static IReadOnlyList<long> Ids { get; } = [AmarrMinmatar, CaldariGallente];

    /// <summary>The two factions of a warzone (empty for an unknown warzone).</summary>
    public static IReadOnlyList<long> FactionsOf(long warzoneId) => FactionsByWarzone.GetValueOrDefault(warzoneId) ?? [];

    /// <summary>The solar systems of a warzone on the client's map (empty for an unknown warzone).</summary>
    public static IReadOnlyList<long> SystemsOf(long warzoneId) => Map.Value.Systems.GetValueOrDefault(warzoneId) ?? [];

    /// <summary>The warzone a solar system belongs to, or 0 when it is not on any warzone map.</summary>
    public static long WarzoneOf(long solarSystemId) => Map.Value.WarzoneBySystem.GetValueOrDefault(solarSystemId, 0);

    /// <summary>
    /// The occupier a system starts with, from its SDE faction: the faction when it is one of the warzone's two factions, else 0 (unoccupied).
    /// </summary>
    public static long InitialOccupier(long warzoneId, long systemFactionId) =>
        FactionsOf(warzoneId).Contains(systemFactionId) ? systemFactionId : 0;

    private static (IReadOnlyDictionary<long, long[]> Systems, IReadOnlyDictionary<long, long> WarzoneBySystem) Load()
    {
        using var stream = typeof(FwWarzones).Assembly.GetManifestResourceStream("fw-warzone-systems.json")
            ?? throw new InvalidOperationException("fw-warzone-systems.json is not embedded in Eve.FactionWarfare");
        var raw = JsonSerializer.Deserialize<Dictionary<string, long[]>>(stream)
            ?? throw new InvalidOperationException("fw-warzone-systems.json is empty");
        var systems = raw.ToDictionary(pair => long.Parse(pair.Key), pair => pair.Value.Distinct().OrderBy(id => id).ToArray());
        var warzoneBySystem = new Dictionary<long, long>();
        foreach (var (warzoneId, ids) in systems)
        {
            foreach (var id in ids)
                warzoneBySystem[id] = warzoneId;
        }
        return (systems, warzoneBySystem);
    }
}
