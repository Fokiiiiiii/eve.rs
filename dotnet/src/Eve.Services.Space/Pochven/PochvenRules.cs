namespace Eve.Services.Space.Pochven;

/// <summary>
/// The rules of Pochven (SDE region 10000070, the Triglavian space of the Final Liminality systems) that the server states from published data: the
/// region and faction ids, the capital ships a stargate refuses, and how the four NPC factions of Pochven treat each other and a pilot. Pure: no state and
/// no PostgreSQL. The sources and the numbers are in <c>docs/project/POCHVEN_DESIGN.md</c>.
/// </summary>
public static class PochvenRules
{
    /// <summary>The Pochven region (SDE <c>mapRegions</c> 10000070).</summary>
    public const int RegionId = 10_000_070;

    /// <summary>The factions of Pochven's NPCs (SDE <c>factions</c>): Drifters 500024, Rogue Drones 500025, Triglavian Collective 500026, EDENCOM 500027.</summary>
    public const int DrifterFactionId = 500_024, RogueDroneFactionId = 500_025, TriglavianFactionId = 500_026, EdencomFactionId = 500_027;

    /// <summary>The NPC corporation each faction names in the SDE (<c>factions.corporationID</c>).</summary>
    public const int DrifterCorporationId = 1_000_274, RogueDroneCorporationId = 1_000_287, TriglavianCorporationId = 1_000_298, EdencomCorporationId = 1_000_297;

    /// <summary>The capital ship groups (SDE): Titan 30, Dreadnought 485, Carrier 547, Supercarrier 659, Force Auxiliary 1538, Capital Industrial Ship 883.</summary>
    public const int TitanGroupId = 30, DreadnoughtGroupId = 485, CarrierGroupId = 547, SupercarrierGroupId = 659, ForceAuxiliaryGroupId = 1_538, CapitalIndustrialGroupId = 883;

    /// <summary>The refusal of a capital at a stargate: the text the stargate lists already use (the wiki gives no text for Pochven).</summary>
    public const string CapitalStargateRefusal = "Capital ships cannot use this stargate.";

    /// <summary>NPC bounty payouts are not made in Pochven (owner decision 2026-10-19).</summary>
    public static bool PaysNpcBounty(int regionId) => !IsPochvenRegion(regionId);

    public static bool IsPochvenRegion(int regionId) => regionId == RegionId;

    public static bool IsCapitalGroup(int groupId) =>
        groupId is TitanGroupId or DreadnoughtGroupId or CarrierGroupId or SupercarrierGroupId or ForceAuxiliaryGroupId or CapitalIndustrialGroupId;

    /// <summary>The refusal of a stargate jump by a ship of this group in this region, or null when this rule allows it. Capital ships cannot use stargates
    /// in Pochven (wiki, "Pochven").</summary>
    public static string? StargateRefusal(int regionId, int shipGroupId) =>
        IsPochvenRegion(regionId) && IsCapitalGroup(shipGroupId) ? CapitalStargateRefusal : null;

    public static bool IsPochvenNpcFaction(int factionId) =>
        factionId is DrifterFactionId or RogueDroneFactionId or TriglavianFactionId or EdencomFactionId;

    /// <summary>Whether two NPC factions of Pochven attack each other on sight: the wiki's "the five main factions attack each other on sight", taken for the
    /// four factions with SDE ids here (the Sleepers have no id in the sources read, so they are not modelled). A faction is not hostile to itself, and a
    /// faction outside the four (CONCORD, a pirate, an empire) is not part of this rule.</summary>
    public static bool FactionsAreHostile(int factionA, int factionB) =>
        factionA != factionB && IsPochvenNpcFaction(factionA) && IsPochvenNpcFaction(factionB);

    /// <summary>Whether an NPC of a Pochven faction is hostile to a pilot of this standing (wiki, "Triglavian Invasion": Triglavian rats hostile at 0.00 and
    /// below, EDENCOM rats below 0.00; Rogue Drones and Drifters hostile to every pilot, wiki "Pochven"). False for any other faction.</summary>
    public static bool NpcHostileToPilot(int factionId, double standing) => factionId switch
    {
        TriglavianFactionId => standing <= 0.0,
        EdencomFactionId => standing < 0.0,
        RogueDroneFactionId or DrifterFactionId => true,
        _ => false,
    };
}
