namespace Eve.Inventory;

/// <summary>
/// How much room an item takes in a hold. An assembled item (a ship, a fitted module) takes the volume of its type; a packaged one (a stack of goods) takes what the
/// client's <c>inventorycommon.const</c> says for its group or type, and the type's own volume where it says nothing. A port, as it is, of
/// <c>getInventoryItemUnitVolume</c> of EVE.js (ADR 0009), which mirrors those tables.
/// </summary>
public static class ItemVolumes
{
    /// <summary>The volume of a packaged ship or other hull by its group (<c>packagedVolumeOverridesPerGroup</c>).</summary>
    private static readonly Dictionary<int, double> PackagedByGroup = new()
    {
        [25] = 2500, [26] = 10000, [27] = 50000, [28] = 20000, [29] = 500, [30] = 10000000, [31] = 500, [324] = 2500, [358] = 10000, [380] = 20000, [381] = 50000,
        [419] = 15000, [420] = 5000, [463] = 3750, [485] = 1300000, [513] = 1300000, [540] = 15000, [541] = 5000, [543] = 3750, [547] = 1300000, [659] = 1300000,
        [830] = 2500, [831] = 2500, [832] = 10000, [833] = 10000, [834] = 2500, [883] = 1300000, [893] = 2500, [894] = 10000, [898] = 50000, [900] = 50000,
        [902] = 1300000, [906] = 10000, [941] = 500000, [963] = 5000, [1022] = 500, [1201] = 15000, [1202] = 20000, [1283] = 2500, [1305] = 5000, [1527] = 2500,
        [1534] = 5000, [1538] = 1300000, [1972] = 10000, [4594] = 1300000, [4902] = 50000, [5087] = 10000, [5120] = 1300000,
    };

    /// <summary>The same by type (<c>packagedVolumeOverridesPerType</c>), which wins over the group.</summary>
    private static readonly Dictionary<int, double> PackagedByType = new()
    {
        [3293] = 33, [3296] = 65, [3297] = 10, [3465] = 65, [3466] = 33, [3467] = 10, [11019] = 100, [11488] = 150, [11489] = 300, [17363] = 10, [17364] = 33,
        [17365] = 65, [17366] = 10000, [24445] = 1200, [33003] = 2500, [33005] = 500, [33007] = 100, [33009] = 50, [33011] = 10, [42244] = 50000,
    };

    /// <summary>The volume of one unit of a type once it is packaged.</summary>
    public static double Packaged(int typeId, int groupId, double volume) =>
        PackagedByType.TryGetValue(typeId, out var byType) ? byType : PackagedByGroup.TryGetValue(groupId, out var byGroup) ? byGroup : volume;

    /// <summary>The volume of one unit of an item: of its type when it is assembled (a singleton), packaged otherwise.</summary>
    public static double Unit(int typeId, int groupId, bool singleton, double volume) => singleton ? volume : Packaged(typeId, groupId, volume);

    /// <summary>The volume of the whole stack (a singleton is one unit whatever its quantity says).</summary>
    public static double Of(Item item, int groupId, double typeVolume) =>
        Unit(item.TypeId, groupId, item.Singleton, typeVolume) * (item.Singleton ? 1 : Math.Max(0, item.Quantity));
}
