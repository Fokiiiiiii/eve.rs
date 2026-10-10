using System.Text.Json;
using Eve.Data;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Sde;
using Npgsql;

namespace Eve.Ships.Tests;

/// <summary>
/// The published ships of the pinned SDE against the fitting rules (SF-0). Every ship offers the slots its static data gives; one module of each family
/// fits where the rules put it; the four subsystem classes fit a Strategic Cruiser of their hull and give it the slots they name. The exceptions are
/// listed here and asserted exactly, so an exception that stops being true fails this test until its entry is removed.
/// </summary>
public sealed class RealSdeShipSweepTests
{
    private const long CharId = 150_000_301, ShipBase = 9_988_900_000_000, Station = 60_015_249;
    private const int CharacterType = 1373, CharacterFlag = 57, HangarFlag = 4, SkillFlag = 7;
    private const int DamageControlI = 2046, Afterburner = 21857, EnergyNeutralizer = 533;
    private const int SmallRig = 26929, MediumRig = 4395, LargeRig = 4397, CapitalRig = 33303;
    private const int StrategicCruiserGroup = 963, ModeGroup = 1306;
    private const int PublishedShips = 423, PublishedSubsystems = 48, ModeItems = 18;
    private const int LowSlots = 12, MediumSlots = 13, HighSlots = 14, RigSlots = 1137, MaxSubsystems = 1367, SubsystemSlot = 1366, RigSize = 1547;
    private const int HighModifier = 1374, MediumModifier = 1375, LowModifier = 1376;

    /// <summary>The subsystem classes and the flag each one fits (the SDE's subSystemSlot values).</summary>
    private static readonly (int Flag, string Name)[] SubsystemClasses = [(125, "Core"), (126, "Defensive"), (127, "Offensive"), (128, "Propulsion")];

    /// <summary>
    /// The Strategic Cruisers that offer more subsystem flags than the four classes (the SDE's maxSubSystems is 5). Empty since SF-4: a ship offers at most
    /// the four classes (<c>Slots.Count</c>). The list is asserted exactly.
    /// </summary>
    private static readonly int[] OpenSubsystemHulls = [];

    /// <summary>The six hulls with a tactical mode (the client's shipmode table: T3D plus Anhinga), by their SDE names.</summary>
    private static readonly string[] ModeHulls = ["Confessor", "Svipul", "Jackdaw", "Hecate", "Skua", "Anhinga"];

    private static async Task<(SdeReader Sde, DogmaStatic Statics, IReadOnlyList<TypeDef> Ships, IReadOnlyList<TypeDef> Subsystems)> LoadAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Rig.Url!));
        var sde = new SdeReader(source);
        Assert.True(await sde.BuildAsync() is not null, "no SDE is loaded: run `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`");
        var statics = await DogmaStatic.LoadAsync(sde);
        var ships = await statics.TypesInCategoryAsync("Ship");
        var subsystems = await statics.TypesInCategoryAsync("Subsystem");
        return (sde, statics, ships, subsystems);
    }

    private static int Attr(TypeDef type, int attribute) => type.Attributes.TryGetValue(attribute, out var value) ? (int)value : 0;

    private static int Clamped(int value) => Math.Clamp(value, 0, Slots.FlagsPerFamily);

    /// <summary>The slots a hull's static data gives per family (a Strategic Cruiser's subsystem flags are its four classes).</summary>
    private static int Expected(TypeDef ship, SlotFamily family) => family switch
    {
        SlotFamily.Low => Clamped(Attr(ship, LowSlots)),
        SlotFamily.Medium => Clamped(Attr(ship, MediumSlots)),
        SlotFamily.High => Clamped(Attr(ship, HighSlots)),
        SlotFamily.Rig => Clamped(Attr(ship, RigSlots)),
        _ => ship.GroupId == StrategicCruiserGroup ? SubsystemClasses.Length : Clamped(Attr(ship, MaxSubsystems)),
    };

    /// <summary>A pilot in a hangar with the skills the probes need (trained to the levels the static data asks).</summary>
    private static DogmaWorld Pilot(DogmaStatic statics, TypeDef ship, long shipId, IReadOnlyDictionary<int, int> skills)
    {
        var world = new DogmaWorld(statics, CharId, shipId);
        world.Add(new DogmaItem(CharId, CharacterType, CharId, shipId, CharacterFlag));
        world.Add(new DogmaItem(shipId, ship.TypeId, CharId, Station, HangarFlag));
        foreach (var (skill, level) in skills)
        {
            var id = CharId * 100_000 + skill;
            world.Add(new DogmaItem(id, skill, CharId, CharId, SkillFlag));
            world.SetValue(id, DogmaWorld.SkillLevel, level);
        }
        return world;
    }

    private static Dictionary<int, int> SkillsFor(IEnumerable<TypeDef> types)
    {
        var levels = new Dictionary<int, int>();
        foreach (var type in types)
        {
            foreach (var (skill, level) in FittingRules.RequiredSkills(type))
            {
                levels[skill] = Math.Max(levels.GetValueOrDefault(skill), level);
            }
        }
        return levels;
    }

    /// <summary>The rig of a hull's size (the SDE's rigSize 1 small, 2 medium, 3 large, 4 capital), or 0 when the size has no probe rig.</summary>
    private static int RigFor(TypeDef ship) => Attr(ship, RigSize) switch
    {
        1 => SmallRig,
        2 => MediumRig,
        3 => LargeRig,
        4 => CapitalRig,
        _ => 0,
    };

    [PgFact]
    public async Task Every_published_ship_offers_the_slots_its_static_data_gives()
    {
        var (_, statics, ships, _) = await LoadAsync();
        Assert.Equal(PublishedShips, ships.Count);

        var problems = new List<string>();
        var open = new List<int>();
        foreach (var ship in ships)
        {
            var shipId = ShipBase + ship.TypeId;
            var world = Pilot(statics, ship, shipId, new Dictionary<int, int>());
            foreach (var family in Enum.GetValues<SlotFamily>())
            {
                var count = Slots.Count(world, shipId, family);
                var expected = Expected(ship, family);
                if (count == expected)
                {
                    continue;
                }
                if (family == SlotFamily.Subsystem && ship.GroupId == StrategicCruiserGroup)
                {
                    open.Add(ship.TypeId);
                }
                else
                {
                    problems.Add($"{ship.TypeId} {ship.Name}: {family} offers {count}, the static data gives {expected}");
                }
            }
        }

        Assert.Equal(OpenSubsystemHulls.Order(), open.Order());
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(40)));
    }

    [PgFact]
    public async Task Every_published_ship_takes_one_module_of_each_family_where_it_has_the_slot()
    {
        var (_, statics, ships, subsystems) = await LoadAsync();
        int[] modules = [DamageControlI, Afterburner, EnergyNeutralizer, SmallRig, MediumRig, LargeRig, CapitalRig];
        await statics.EnsureTypesAsync(modules);
        var skills = SkillsFor(modules.Select(statics.Type).OfType<TypeDef>().Concat(subsystems));
        await statics.EnsureTypesAsync(skills.Keys);

        var problems = new List<string>();
        foreach (var ship in ships)
        {
            var shipId = ShipBase + ship.TypeId;
            var world = Pilot(statics, ship, shipId, skills);
            long next = shipId;
            long Next() => ++next;

            // a Strategic Cruiser's subsystems first: they decide how many slots the hull has (EveJS fittingOperations.js:185-187)
            if (ship.GroupId == StrategicCruiserGroup)
            {
                var (hi, med, low) = (Attr(ship, HighSlots), Attr(ship, MediumSlots), Attr(ship, LowSlots));
                foreach (var (flag, className) in SubsystemClasses)
                {
                    var candidate = subsystems.FirstOrDefault(s => s.Attributes.TryGetValue(SubsystemSlot, out var slot) && (int)slot == flag
                        && s.Name.StartsWith($"{ship.Name} {className} - ", StringComparison.Ordinal));
                    if (candidate is null)
                    {
                        problems.Add($"{ship.TypeId} {ship.Name}: no {className} subsystem names this hull");
                        continue;
                    }
                    var probe = new DogmaItem(Next(), candidate.TypeId, CharId, Station, HangarFlag);
                    world.Add(probe);
                    var failure = FittingRules.Check(world, statics, shipId, probe, flag);
                    if (failure is not null)
                    {
                        problems.Add($"{ship.TypeId} {ship.Name}: {candidate.Name} refused at {flag}: {failure.Error}");
                        continue;
                    }
                    world.Add(new DogmaItem(Next(), candidate.TypeId, CharId, shipId, flag, ItemState.Online));
                    hi += Attr(candidate, HighModifier);
                    med += Attr(candidate, MediumModifier);
                    low += Attr(candidate, LowModifier);
                }
                // the subsystems' slot modifiers, once fitted, add to the hull's slots
                foreach (var (family, expected) in new[] { (SlotFamily.High, hi), (SlotFamily.Medium, med), (SlotFamily.Low, low) })
                {
                    var count = Slots.Count(world, shipId, family);
                    if (count != Clamped(expected))
                    {
                        problems.Add($"{ship.TypeId} {ship.Name}: {family} offers {count} with four subsystems, the modifiers give {Clamped(expected)}");
                    }
                }
            }

            foreach (var family in new[] { SlotFamily.Low, SlotFamily.Medium, SlotFamily.High, SlotFamily.Rig })
            {
                var count = Slots.Count(world, shipId, family);
                var rig = RigFor(ship);
                var type = family switch
                {
                    SlotFamily.Low => DamageControlI,
                    SlotFamily.Medium => Afterburner,
                    SlotFamily.High => EnergyNeutralizer,
                    _ => rig != 0 ? rig : MediumRig, // a hull without a rig size is probed with a medium rig, and reported below
                };
                if (family == SlotFamily.Rig && count > 0 && rig == 0)
                {
                    problems.Add($"{ship.TypeId} {ship.Name}: {Attr(ship, RigSize)} rig size has no probe rig");
                    continue;
                }
                var flag = Slots.FirstFlag(family);
                var probe = new DogmaItem(Next(), type, CharId, Station, HangarFlag);
                world.Add(probe);
                var error = FittingRules.Check(world, statics, shipId, probe, flag)?.Error;
                if (count == 0)
                {
                    if (error != FitError.InvalidSlot)
                    {
                        problems.Add($"{ship.TypeId} {ship.Name}: no {family} slot, but a {type} at its first flag was {error?.ToString() ?? "accepted"}");
                    }
                    continue;
                }
                if (error is not null)
                {
                    problems.Add($"{ship.TypeId} {ship.Name}: {type} refused in {family}: {error}");
                    continue;
                }
                world.Add(new DogmaItem(Next(), type, CharId, shipId, flag, ItemState.Online));
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(40)));
    }

    [PgFact]
    public async Task The_static_data_has_the_four_subsystem_classes_and_the_tactical_mode_items()
    {
        var (sde, _, _, subsystems) = await LoadAsync();
        Assert.Equal(PublishedSubsystems, subsystems.Count);
        foreach (var (flag, className) in SubsystemClasses)
        {
            Assert.Equal(12, subsystems.Count(s => Attr(s, SubsystemSlot) == flag));
        }

        var modes = (await sde.ReadByFieldAsync("types", "groupID", [(long)ModeGroup]))
            .Select(row => SdeReader.English(row.Doc.GetProperty("name")))
            .ToList();
        Assert.Equal(ModeItems, modes.Count);
        foreach (var hull in ModeHulls)
        {
            Assert.Equal(3, modes.Count(name => name.StartsWith($"{hull} ", StringComparison.Ordinal)));
        }
    }
}
