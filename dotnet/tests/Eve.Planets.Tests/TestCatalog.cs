using Eve.Planets;

namespace Eve.Planets.Tests;

/// <summary>
/// The static facts the tests use, read from the pinned SDE build 3396210 (tables types, typeDogma, dogmaAttributes,
/// planetSchematics). Attributes missing from a type take the attribute default, as the client does.
/// </summary>
internal static class TestCatalog
{
    public const int Barren = 2016;
    public const int CommandCenter = 2524;
    public const int Storage = 2541;
    public const int Launchpad = 2544;
    public const int TemperateCommandCenter = 2254;
    public const int Ecu = 2848;
    public const int Processor = 2473;
    public const int Link = 2280;
    public const int AqueousLiquids = 2268;
    public const int Water = 3645;
    public const int Schematic = 121;
    public const int Consolidation = 2495;
    public const int CommandCenterUpgrade = 2505;

    public const long ColonyOwner = 90000001;
    public const long PlanetId = 40000011;
    /// <summary>A planet radius of 3000 km (a test value, not the SDE's mapPlanets radius).</summary>
    public const double Radius = 3_000_000.0;

    public static PlanetCatalog Build()
    {
        var defaults = new Dictionary<int, double> { [1683] = 0.012, [1684] = 9.2, [1685] = 0.5, [1687] = 0.8, [1689] = 0.3 };
        var types = new Dictionary<int, TypeInfo>
        {
            [CommandCenter] = Type(CommandCenter, 1027, 1000, 90000, 500, new() { [11] = 6000, [48] = 1675, [1632] = Barren }),
            [TemperateCommandCenter] = Type(TemperateCommandCenter, 1027, 1000, 90000, 500, new() { [11] = 6000, [48] = 1675, [1632] = 11 }),
            [Storage] = Type(Storage, 1029, 0, 250000, 12000, new() { [15] = 700, [49] = 500, [1632] = Barren }),
            [Launchpad] = Type(Launchpad, 1030, 0, 900000, 10000, new() { [15] = 700, [49] = 3600, [1632] = Barren }),
            [Ecu] = Type(Ecu, 1063, 0, 45000, null, new() { [15] = 2600, [49] = 400, [1632] = Barren, [1642] = 1000, [1643] = 300, [1690] = 110, [1691] = 550 }, defaults),
            [Processor] = Type(Processor, 1028, 0, 75000, null, new() { [15] = 800, [49] = 200, [1632] = Barren }),
            [Link] = Type(Link, 1036, 0, 0, null, new() { [15] = 10, [49] = 15, [1631] = 1250, [1633] = 0.15, [1634] = 0.2, [1635] = 1.4, [1636] = 1.2 }),
            [AqueousLiquids] = Type(AqueousLiquids, 1033, 0.005, 0, null, new()),
            [Water] = Type(Water, 1042, 0.19, 0, null, new()),
            [Consolidation] = Type(Consolidation, 1, 0, 0, null, new()),
            [CommandCenterUpgrade] = Type(CommandCenterUpgrade, 1, 0, 0, null, new()),
        };
        var schematics = new Dictionary<int, Schematic>
        {
            [Schematic] = new(Schematic, 1800, new Dictionary<int, int> { [AqueousLiquids] = 3000 }, new Dictionary<int, int> { [Water] = 20 },
                new HashSet<int> { 2469, 2471, Processor, 2481, 2483, 2490, 2492, 2493 }),
        };
        return new PlanetCatalog(types, schematics);
    }

    private static TypeInfo Type(int id, int group, double volume, long price, double? capacity, Dictionary<int, double> attrs, Dictionary<int, double>? defaults = null)
    {
        if (defaults is not null)
            foreach (var (attr, value) in defaults)
                attrs.TryAdd(attr, value);
        return new TypeInfo(id, group, volume, price, capacity, attrs);
    }

    /// <summary>A resource field of one constant value: a head yields value * maxVolume.</summary>
    public sealed class ConstantField(double value) : IResourceField
    {
        public double ValueAt(long planetId, int resourceTypeId, double theta, double phi) => value;
    }
}
