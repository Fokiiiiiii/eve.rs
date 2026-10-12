namespace Eve.Insurgency;

/// <summary>
/// A pirate warzone: the pirate faction, the regions its insurgencies happen in and the NPC pool of its sites. The regions are the SDE's (CCP: Angel Cartel
/// insurgencies in Curse and Fountain, Guristas in Venal; the wiki's Insurgency page). The warzone ids are EVE.js's.
/// </summary>
public sealed record Warzone(long WarzoneId, int PirateFactionId, long PirateCorporationId, IReadOnlyList<int> RegionIds, string SpawnPool);

public static class InsurgencyWarzones
{
    /// <summary>The Angel Cartel warzone (SDE faction 500011, corporation 1000138; regions Curse 10000012 and Fountain 10000058).</summary>
    public static readonly Warzone Angel = new(1, 500011, 1000138, [10000012, 10000058], "angels_deadspace_pve");

    /// <summary>The Guristas warzone (SDE faction 500010, corporation 1000127; region Venal 10000015).</summary>
    public static readonly Warzone Guristas = new(2, 500010, 1000127, [10000015], "guristas_deadspace_pve");

    public static IReadOnlyList<Warzone> All { get; } = [Angel, Guristas];
}
