using Eve.Data;
using Eve.Dogma;
using Eve.Sde;
using Eve.Skills;
using Npgsql;

namespace Eve.Fitting.Tests;

/// <summary>Skips a test, loudly, when there is no PostgreSQL with the SDE loaded (see tools/dev/pg18.sh).</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (Url is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }

    public static string? Url => Environment.GetEnvironmentVariable("EVE_PG_URL");
}

/// <summary>The rules against the real static data: a new Caldari character in its Ibis, fitted as the game fits it.</summary>
public class RealShipTests
{
    private const long CharId = 140_000_001, Ship = 9_988_400_000_000, Station = 60_015_249;
    private const int Ibis = 601, Afterburner = 21857, Railgun = 3638, Miner = 3651;

    private static async Task<(DogmaWorld World, DogmaStatic Statics)> ShipAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(PgFactAttribute.Url!));
        var sde = new SdeReader(source);
        Assert.True(await sde.BuildAsync() is not null, "no SDE is loaded: run `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`");
        var statics = await DogmaStatic.LoadAsync(sde);
        var skills = await SkillCatalog.LoadAsync(sde);
        var starting = skills.StartingSkills(1);
        await statics.EnsureTypesAsync([1373, Ibis, Afterburner, Railgun, Miner, .. starting.Select(s => s.TypeId)]);

        var world = new DogmaWorld(statics, CharId, Ship);
        world.Add(new DogmaItem(CharId, 1373, CharId, Ship, 57));
        world.Add(new DogmaItem(Ship, Ibis, CharId, Station, 4));
        foreach (var skill in starting)
        {
            var id = (CharId * 100_000) + skill.TypeId;
            world.Add(new DogmaItem(id, skill.TypeId, CharId, CharId, 7));
            world.SetValue(id, DogmaWorld.SkillLevel, skill.Level);
        }
        return (world, statics);
    }

    private static void Fit(DogmaWorld world, int type, int flag, long id) => world.Add(new DogmaItem(id, type, CharId, Ship, flag, ItemState.Online));

    [PgFact]
    public async Task The_starter_modules_of_an_Ibis_fit_where_the_game_puts_them()
    {
        var (world, statics) = await ShipAsync();
        var candidate = new DogmaItem(Ship + 1, Afterburner, CharId, Station, 4);
        var weapon = new DogmaItem(Ship + 2, Railgun, CharId, Station, 4);
        var miner = new DogmaItem(Ship + 3, Miner, CharId, Station, 4);
        world.Add(candidate);
        world.Add(weapon);
        world.Add(miner);
        Assert.Null(FittingRules.Check(world, statics, Ship, candidate, Slots.MediumFirst));
        Assert.Null(FittingRules.Check(world, statics, Ship, weapon, Slots.HighFirst));
        Assert.Null(FittingRules.Check(world, statics, Ship, miner, Slots.HighFirst + 1));
        Assert.Equal(Slots.MediumFirst, FittingRules.FirstFreeSlot(world, statics, Ship, candidate));
        Assert.Equal(Slots.HighFirst, FittingRules.FirstFreeSlot(world, statics, Ship, weapon));
    }

    [PgFact]
    public async Task An_Ibis_has_the_slots_and_hard_points_of_a_rookie_frigate()
    {
        var (world, statics) = await ShipAsync();
        Fit(world, Afterburner, Slots.MediumFirst, Ship + 1);
        Fit(world, Railgun, Slots.HighFirst, Ship + 2);
        Fit(world, Miner, Slots.HighFirst + 1, Ship + 3);

        // two medium slots: the first is taken, the second is free, and there is no third
        var second = new DogmaItem(Ship + 4, Afterburner, CharId, Station, 4);
        world.Add(second);
        Assert.Equal(FitError.SlotOccupied, FittingRules.Check(world, statics, Ship, second, Slots.MediumFirst)?.Error);
        Assert.Null(FittingRules.Check(world, statics, Ship, second, Slots.MediumFirst + 1));
        Assert.Equal(FitError.InvalidSlot, FittingRules.Check(world, statics, Ship, second, Slots.MediumFirst + 2)?.Error);
        Assert.Equal(Slots.MediumFirst + 1, FittingRules.FirstFreeSlot(world, statics, Ship, second));

        // two high slots, both taken by turrets: there is no third slot, and the hard points are gone too
        var third = new DogmaItem(Ship + 5, Railgun, CharId, Station, 4);
        world.Add(third);
        Assert.Equal(FitError.InvalidSlot, FittingRules.Check(world, statics, Ship, third, Slots.HighFirst + 2)?.Error);
        Assert.Equal(0, world.Get(Ship, BuiltInEffects.TurretSlotsLeft));
        Assert.Null(FittingRules.FirstFreeSlot(world, statics, Ship, third));
    }

    [PgFact]
    public async Task A_module_that_needs_a_skill_the_character_has_not_trained_is_refused_naming_it()
    {
        var (world, statics) = await ShipAsync();
        // 3 is a made-up level higher than any starting skill: the railgun needs Gunnery and Small Hybrid Turret at least at 1, so ask for more
        var railgunType = statics.Type(Railgun)!;
        var required = FittingRules.RequiredSkills(railgunType).ToList();
        Assert.NotEmpty(required);
        var weapon = new DogmaItem(Ship + 2, Railgun, CharId, Station, 4);
        world.Add(weapon);
        Assert.Null(FittingRules.Check(world, statics, Ship, weapon, Slots.HighFirst)); // the starting skills are enough

        // a character with none of its skills
        var bare = new DogmaWorld(statics, CharId, Ship);
        bare.Add(new DogmaItem(CharId, 1373, CharId, Ship, 57));
        bare.Add(new DogmaItem(Ship, Ibis, CharId, Station, 4));
        var loose = new DogmaItem(Ship + 2, Railgun, CharId, Station, 4);
        bare.Add(loose);
        var failure = FittingRules.Check(bare, statics, Ship, loose, Slots.HighFirst);
        Assert.Equal(FitError.SkillRequired, failure?.Error);
        Assert.Contains(required.Select(r => (double)r.Skill), s => s == failure!.Data["skillTypeID"]);
        Assert.Equal(0, failure!.Data["currentLevel"]);
    }
}
