namespace Eve.Planets;

/// <summary>Dogma attribute ids the planetary rules read (eve-logic dogma/const.py).</summary>
public static class PlanetAttr
{
    public const int Capacity = 38;
    public const int PowerOutput = 11;
    public const int PowerLoad = 15;
    public const int CpuOutput = 48;
    public const int CpuLoad = 49;
    public const int PinCycleTime = 1643;
    public const int PinExtractionQuantity = 1642;
    public const int PlanetRestriction = 1632;
    public const int LogisticalCapacity = 1631;
    public const int PowerLoadPerKm = 1633;
    public const int CpuLoadPerKm = 1634;
    public const int CpuLoadLevelModifier = 1635;
    public const int PowerLoadLevelModifier = 1636;
    public const int EcuMaxVolume = 1684;
    public const int EcuDecayFactor = 1683;
    public const int EcuOverlapFactor = 1685;
    public const int EcuNoiseFactor = 1687;
    public const int EcuAreaOfInfluence = 1689;
    public const int ExtractorHeadCpu = 1690;
    public const int ExtractorHeadPower = 1691;
    public const int ImportTax = 1638;
    public const int ExportTax = 1639;
    public const int ImportTaxMultiplier = 1640;
}

/// <summary>Type groups of the planetary pins and links (inventorycommon/const.py).</summary>
public static class PlanetGroups
{
    public const int CommandCenter = 1027;
    public const int Ecu = 1063;
    public const int Processor = 1028;
    public const int Storage = 1029;
    public const int Launchpad = 1030;
    public const int Link = 1036;
}

public enum PinKind { CommandCenter, Ecu, Processor, Storage, Launchpad }

/// <summary>The static facts of one item type the colony rules read.</summary>
public sealed record TypeInfo(int TypeId, int GroupId, double Volume, long BasePrice, double? Capacity, IReadOnlyDictionary<int, double> Attributes)
{
    public double Attr(int attributeId, double fallback = 0) => Attributes.TryGetValue(attributeId, out var v) ? v : fallback;

    public bool HasAttr(int attributeId) => Attributes.ContainsKey(attributeId);
}

/// <summary>A planetary schematic (SDE planetSchematics): the inputs and outputs of a processor, and its cycle in seconds.</summary>
public sealed record Schematic(int SchematicId, double CycleSeconds, IReadOnlyDictionary<int, int> Inputs, IReadOnlyDictionary<int, int> Outputs, IReadOnlySet<int> PinTypes);

/// <summary>Thrown for a refused change; the message is the client's error key (its UserError name).</summary>
public sealed class PlanetRefusal(string key, string? detail = null) : Exception(detail ?? key)
{
    public string Key { get; } = key;
}

/// <summary>
/// The static data the colony rules need: item types, schematics and the planet bodies. Loaded from the SDE by the host;
/// the tests build it in memory.
/// </summary>
public sealed class PlanetCatalog(IReadOnlyDictionary<int, TypeInfo> types, IReadOnlyDictionary<int, Schematic> schematics)
{
    public TypeInfo Type(int typeId) =>
        types.TryGetValue(typeId, out var t) ? t : throw new PlanetRefusal("InvalidPinType", $"unknown type {typeId}");

    public bool Exists(int typeId) => types.ContainsKey(typeId);

    /// <summary>The types of one group (the command centers are group 1027: one per planet type).</summary>
    public IEnumerable<TypeInfo> TypesInGroup(int groupId) => types.Values.Where(t => t.GroupId == groupId);

    public Schematic? Schematic(int schematicId) => schematics.TryGetValue(schematicId, out var s) ? s : null;

    /// <summary>The kind of pin a type is, or null when it is not a pin of this model (obsolete extractors, POCO, other).</summary>
    public PinKind? KindOf(int typeId)
    {
        if (!types.TryGetValue(typeId, out var t))
            return null;
        return t.GroupId switch
        {
            PlanetGroups.CommandCenter => PinKind.CommandCenter,
            PlanetGroups.Ecu => PinKind.Ecu,
            PlanetGroups.Processor => PinKind.Processor,
            PlanetGroups.Storage => PinKind.Storage,
            PlanetGroups.Launchpad => PinKind.Launchpad,
            _ => null,
        };
    }

    public bool IsLink(int typeId) => types.TryGetValue(typeId, out var t) && t.GroupId == PlanetGroups.Link;

    /// <summary>The link type of the planetary network (the client sends no type: there is one, group 1036).</summary>
    public int LinkTypeId => types.Values.First(t => t.GroupId == PlanetGroups.Link).TypeId;
}
