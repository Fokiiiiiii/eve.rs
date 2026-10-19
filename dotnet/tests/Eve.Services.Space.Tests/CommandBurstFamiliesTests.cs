using Eve.Dogma;
using Eve.Services.Space.Effects;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The command burst families on the static data (track C6): each family's module with its charge gives the buffs the charge names, for the buff duration of the
/// module. The expedition bursts (charge group 4905) and the named and presidential ones are included.
/// </summary>
public class CommandBurstFamiliesTests
{
    private const long Owner = 140_000_001, Ship = 9_988_500_000_000, Station = 60_003_760;
    private const long Burst = 9_988_500_000_001, Charge = 9_988_500_000_003;
    private const int Rifter = 587, MidSlot = 19;

    [PgFact]
    public async Task Every_burst_family_gives_the_buffs_of_its_charge_for_the_buff_duration_of_its_module()
    {
        await using var rig = await Rig.CreateAsync();
        var statics = rig.Statics;
        // the module, its charge and the buff duration (ms): the Mk I of each family, the expedition burst, and a named and a presidential one
        (int Module, int Charge, double Duration)[] families =
        [
            (42526, 42832, 60_000), // Armor
            (42527, 42835, 60_000), // Information
            (42528, 42829, 60_000), // Mining Foreman
            (42529, 42695, 60_000), // Shield
            (42530, 42838, 60_000), // Skirmish
            (89608, 89612, 60_000), // Expedition
            (88263, 42832, 80_000), // Tyrannos (a named armor burst)
            (92455, 42835, 80_000), // Presidential Information
        ];
        await statics.EnsureTypesAsync([Rifter, .. families.SelectMany(family => new[] { family.Module, family.Charge })]);
        foreach (var (module, charge, duration) in families)
        {
            var world = new DogmaWorld(statics, 0, Ship);
            world.Add(new DogmaItem(Ship, Rifter, Owner, Station, 0, ItemState.Online));
            world.Add(new DogmaItem(Burst, module, Owner, Ship, MidSlot, ItemState.Online));
            world.Add(new DogmaItem(Charge, charge, Owner, Ship, MidSlot, ItemState.Offline));

            var buffs = CommandBurstBuffs.Of(world, statics, Burst);
            Assert.True(buffs.Count > 0, $"module {module} with charge {charge} gives no buff");
            Assert.Equal(duration, CommandBurstBuffs.DurationOf(world, Burst));
        }
    }

    [PgFact]
    public async Task Every_published_burst_charge_type_resolves_its_nonzero_sde_buff_values()
    {
        await using var rig = await Rig.CreateAsync();
        var sde = new Eve.Sde.SdeReader(rig.Source);
        var statics = rig.Statics;
        var chargeGroupIds = new long[] { 1769, 1771, 1772, 1773, 1774, 4905 };
        var moduleRows = await sde.ReadByFieldAsync("types", "groupID", [1770]);
        var chargeRows = await sde.ReadByFieldAsync("types", "groupID", chargeGroupIds);
        static bool Published(System.Text.Json.JsonElement document) =>
            !document.TryGetProperty("published", out var published) || published.ValueKind != System.Text.Json.JsonValueKind.False;

        var moduleIds = moduleRows.Where(row => Published(row.Doc)).Select(row => (int)row.Key).ToArray();
        var chargeIds = chargeRows.Where(row => Published(row.Doc)).Select(row => (int)row.Key).ToArray();
        await statics.EnsureTypesAsync([Rifter, .. moduleIds, .. chargeIds]);
        var modules = moduleIds.Select(statics.Type).OfType<TypeDef>().ToArray();
        var charges = chargeIds.Select(statics.Type).OfType<TypeDef>().ToArray();
        var buffIdAttributes = new[] { 2468, 2470, 2472, 2536 };
        var buffValueAttributes = new[] { 2469, 2471, 2473, 2537 };

        Assert.NotEmpty(charges);
        foreach (var charge in charges)
        {
            var module = modules.FirstOrDefault(candidate =>
                candidate.Attributes.TryGetValue(604, out var chargeGroup)
                && chargeGroup == charge.GroupId);
            Assert.True(module is not null, $"charge {charge.TypeId} in group {charge.GroupId} has no burst module");

            var world = new DogmaWorld(statics, 0, Ship);
            world.Add(new DogmaItem(Ship, Rifter, Owner, Station, 0, ItemState.Online));
            world.Add(new DogmaItem(Burst, module!.TypeId, Owner, Ship, MidSlot, ItemState.Online));
            world.Add(new DogmaItem(Charge, charge.TypeId, Owner, Ship, MidSlot, ItemState.Offline));

            var expected = Enumerable.Range(0, buffIdAttributes.Length)
                .Where(slot => world.Get(Burst, buffIdAttributes[slot]) > 0)
                .Select(slot => world.Get(Burst, buffValueAttributes[slot]))
                .ToArray();
            var buffs = CommandBurstBuffs.Of(world, statics, Burst);
            Assert.NotEmpty(expected);
            Assert.Equal(expected.Length, buffs.Count);
            Assert.All(expected, value => Assert.True(double.IsFinite(value) && value != 0, $"charge {charge.TypeId} resolved a zero buff value"));
            Assert.All(buffs, buff => Assert.True(double.IsFinite(buff.Value) && buff.Value != 0, $"charge {charge.TypeId} produced a zero buff value"));
        }
    }
}
