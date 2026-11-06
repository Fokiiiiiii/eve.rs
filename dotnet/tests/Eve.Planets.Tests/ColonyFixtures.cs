using Eve.Planets;

namespace Eve.Planets.Tests;

internal static class Fixtures
{
    public const long Now = 1_700_000_000L * 10_000_000L;
    public const long Lat = 0;

    public static Colony NewColony(IResourceField? field = null, PlanetCatalog? catalog = null)
    {
        var colony = new Colony(catalog ?? TestCatalog.Build(), field ?? new TestCatalog.ConstantField(1.0), TestCatalog.PlanetId, TestCatalog.Barren,
            TestCatalog.Radius, TestCatalog.ColonyOwner) { CurrentSimTime = Now };
        return colony;
    }

    public static ColonyContext Ctx(int planets = 0, int consolidation = -1, int ccSkill = 5, long balance = 1_000_000_000_000L) =>
        new(planets, consolidation, ccSkill, balance);

    /// <summary>A colony with a command center (1), a storage (2), an extraction control unit (3) and a processor (4), linked 2-3 and 2-4.</summary>
    public static Colony Chain(IResourceField? field = null)
    {
        var colony = NewColony(field);
        colony.ApplyCommands(
        [
            new CreatePinCommand(1, TestCatalog.CommandCenter, 0.5, 1.0),
            new CreatePinCommand(2, TestCatalog.Storage, 0.5, 1.01),
            new CreatePinCommand(3, TestCatalog.Ecu, 0.5, 1.0),
            new CreatePinCommand(4, TestCatalog.Processor, 0.52, 1.01),
            new CreateLinkCommand(2, 3, TestCatalog.Link, 0),
            new CreateLinkCommand(2, 4, TestCatalog.Link, 0),
            new AddExtractorHeadCommand(3, 0, 0.5, 1.0),
            new InstallProgramCommand(3, TestCatalog.AqueousLiquids, 0.01),
            new SetSchematicCommand(4, TestCatalog.Schematic),
        ], Ctx());
        return colony;
    }
}
