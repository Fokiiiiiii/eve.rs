using Eve.Data;
using Eve.Dogma;
using Eve.Events;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Inventory.Mutation;
using Eve.Scheduler;
using Eve.Sde;
using Npgsql;

namespace Eve.Ships.Tests;

/// <summary>
/// An abyssal mutated module, end to end on the real stores and the real SDE (skipped without EVE_PG_URL; skipped is not passed): the authority makes
/// it from a mutaplasmid and an input module, the fitting path and the ship load build it through <see cref="ItemDogma"/>, and dogma reads it. The
/// worked example is mutaplasmid 47297 on 50MN Microwarpdrive I (12052), giving 50MN Abyssal Microwarpdrive (47408), with the rolls of ADR 0016.
/// </summary>
public class MutatedModuleTests
{
    private const long ClockMs = 1_700_000_000_123;
    private const int Mutaplasmid = 47297, Input = 12052, Result = 47408;

    /// <summary>The skill the result requires (attribute 182), at the level the result requires (277 = 4); the input requires the same skill at level 1.</summary>
    private const int RequiredSkill = 3454;

    /// <summary>The rolls of the worked example: the fraction of each range the roll takes (ADR 0016, D8).</summary>
    private static readonly Dictionary<int, double> Rolls = new() { [6] = 0.5, [20] = 0.25, [30] = 0.75, [50] = 0.0, [554] = 1.0 };

    /// <summary>The mutated values: the input's base times each rolled multiplier (6: 180 x 1.0, 20: 500 x 0.95, 30: 150 x 1.325, 50: 50 x 0.8, 554: 500 x 1.3).</summary>
    private static readonly Dictionary<int, double> Mutated = new() { [6] = 180.0, [20] = 475.0, [30] = 198.75, [50] = 40.0, [554] = 650.0 };

    private sealed class BusEvents(EventBus bus) : IItemMutationEvents
    {
        public async Task PublishMutatedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ItemMutationService.ItemMutated payload, string key, CancellationToken cancellationToken)
        {
            await bus.PublishAsync(connection, transaction, ItemMutationService.MutatedTopic, payload, key, cancellationToken);
        }

        public void Wake() => bus.Wake();
    }

    /// <summary>The authority over the rig's item stores, with its own scheduler and event bus in schemas of their own.</summary>
    private static async Task<ItemMutationService> ServiceAsync(Rig rig, MutationRoll roll)
    {
        var catalog = await MutatorCatalog.LoadAsync(new SdeReader(rig.Source));
        var id = Guid.NewGuid().ToString("N")[..12];
        rig.KeepSchemas($"shs_{id}", $"she_{id}");
        await DurableScheduler.MigrateAsync(rig.Source, $"shs_{id}");
        await EventBus.MigrateAsync(rig.Source, $"she_{id}");
        var bus = new EventBus(rig.Source, new DurableScheduler(rig.Source, () => ClockMs, new SchedulerOptions { Concurrency = 1 }, $"shs_{id}"), null, $"she_{id}");
        return new ItemMutationService(rig.Source, rig.Items, rig.Items.Mutations, catalog, rig.Statics, new BusEvents(bus), () => ClockMs, roll);
    }

    [PgFact]
    public async Task A_mutated_module_is_fitted_with_its_rolled_values_and_the_skill_of_its_result_and_is_built_the_same_on_every_load()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        // the starter afterburner takes the ship's only mid slot: it goes to the hangar so that the mutated module can be fitted there
        var afterburner = (await rig.Items.InLocationAsync(c.ShipId, c.CharacterId)).Single(i => i.Flag == ItemFlags.MedSlot0);
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, afterburner.Id, c.ShipId, Rig.Station, ItemFlags.Hangar, null));
        var service = await ServiceAsync(rig, attribute => Rolls[attribute]);
        var mutator = await rig.GiveAsync(c.CharacterId, Mutaplasmid);
        var source = await rig.GiveAsync(c.CharacterId, Input);
        var twin = await rig.GiveAsync(c.CharacterId, Input);

        var accepted = Assert.IsType<ItemMutationAccepted>(await service.MutateAsync(new ItemMutationRequest(c.CharacterId, Rig.Station, mutator, source)));
        var module = accepted.Receipt.ItemId;
        Assert.Equal(Result, accepted.Receipt.TypeId);

        // the result's skill is the requirement: a pilot at level 1 of it cannot fit the module, and one at level 4 can
        await rig.SetSkillAsync(c.CharacterId, RequiredSkill, 1);
        var refused = Assert.IsType<ShipRefused>(await rig.Ships.MoveAsync(c.CharacterId, module, Rig.Station, c.ShipId, ItemFlags.MedSlot0, null));
        Assert.Equal(ShipRefusal.CannotFit, refused.Reason);
        Assert.Equal(FitError.SkillRequired, refused.Fit!.Error);

        await rig.SetSkillAsync(c.CharacterId, RequiredSkill, 4);
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, module, Rig.Station, c.ShipId, ItemFlags.MedSlot0, null));

        // the ship as the server loads it: the module is typed as its result, takes its base from its input, and carries its rolled values
        var world = (await rig.Ships.LoadAsync(c.CharacterId))!.World;
        var item = world.Item(module)!;
        Assert.Equal(Result, item.TypeId);
        Assert.Equal(Input, item.BaseTypeId);
        Assert.Equal(Mutated.Count, item.OwnValues.Count);
        foreach (var (attribute, value) in Mutated)
        {
            Assert.Equal(value, item.OwnValues[attribute], 9);
        }
        Assert.Equal(3454.0, world.Get(module, 182), 9);
        Assert.Equal(4.0, world.Get(module, 277), 9);
        Assert.Equal(1.0, rig.Statics.Type(Input)!.Attributes[277], 9); // the input's own requirement, which the module does not keep
        Assert.Equal(40.0, world.Get(module, 9), 9);     // not mutated: the input's base
        Assert.Equal(10000.0, world.Get(module, 73), 9); // not mutated: the input's base

        // an unmutated module of the same input keeps the input's values in the same world (its own values are none)
        var untouched = (await rig.Items.GetAsync(twin))!;
        var reference = new DogmaWorld(rig.Statics);
        reference.Add(ItemDogma.Of(untouched, c.CharacterId, c.ShipId, 11));
        Assert.Empty(reference.Item(twin)!.OwnValues);
        Assert.Equal(50.0, reference.Get(twin, 50), 9);
        Assert.Equal(150.0, reference.Get(twin, 30), 9);

        // the same module on the next load: the stored values, not a new roll
        var again = (await rig.Ships.LoadAsync(c.CharacterId))!.World.Item(module)!;
        foreach (var (attribute, value) in Mutated)
        {
            Assert.Equal(value, again.OwnValues[attribute], 9);
        }
        Assert.Equal(accepted.Receipt.MutatedValues.OrderBy(v => v.Key), again.OwnValues.OrderBy(v => v.Key)); // the stored doubles, exactly
    }
}
