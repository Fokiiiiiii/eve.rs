using Eve.Inventory;

namespace Eve.Characters;

/// <summary>A module a new character's ship comes with, in the slot it is fitted to.</summary>
public sealed record StarterModule(int TypeId, int Flag);

/// <summary>
/// What each rookie ship is fitted with when a character is made: an afterburner in the first medium slot, the civilian weapon of
/// the race's damage type in the first high slot and a civilian miner in the second (the reference's starter fittings).
/// </summary>
public static class StarterKits
{
    private const int CivilianAfterburner = 21857;
    private const int CivilianMiner = 3651;
    private const int CivilianGatlingAutocannon = 3636;
    private const int CivilianGatlingPulseLaser = 3634;
    private const int CivilianGatlingRailgun = 3638;

    private static StarterModule[] Kit(int weapon) =>
    [
        new(CivilianAfterburner, ItemFlags.MedSlot0),
        new(weapon, ItemFlags.HiSlot0),
        new(CivilianMiner, ItemFlags.HiSlot0 + 1),
    ];

    private static readonly Dictionary<int, StarterModule[]> ByShipType = new()
    {
        [588] = Kit(CivilianGatlingAutocannon), // Reaper (Minmatar)
        [596] = Kit(CivilianGatlingPulseLaser), // Impairor (Amarr)
        [601] = Kit(CivilianGatlingRailgun),    // Ibis (Caldari)
        [606] = Kit(CivilianGatlingRailgun),    // Velator (Gallente)
    };

    /// <summary>The modules to fit to a new ship of this type, in the order they are made (a ship of a type with no kit starts empty).</summary>
    public static IReadOnlyList<StarterModule> For(int shipTypeId) => ByShipType.GetValueOrDefault(shipTypeId) ?? [];

    public static IEnumerable<int> ShipTypeIds => ByShipType.Keys;
}
