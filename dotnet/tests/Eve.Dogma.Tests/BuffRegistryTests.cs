namespace Eve.Dogma.Tests;

/// <summary>
/// <see cref="BuffRegistry"/>: the timed inputs of a buff collection on an item, aggregated as the reference aggregates them (the strongest of a Maximum
/// collection, the weakest of a Minimum one), active up to and including the expiry, dropped on a restart, and made the dogma world's buffs.
/// </summary>
public class BuffRegistryTests
{
    private const long Owner = 1, Ship = 100;
    private const int ShipType = 1, Resistance = 271;
    private const int ShieldResistance = 10, Speed = 9;

    private static readonly BuffCollection Strongest = new(20, "strongest", false, [new Modifier(ModifierFunc.Item, "itemID", Operation.PostPercent, Speed, 0)]);
    private static readonly BuffCollection Weakest = new(ShieldResistance, "shield resistance", true,
        [new Modifier(ModifierFunc.Item, "itemID", Operation.PostPercent, Resistance, 0)]);

    private static DogmaStatic Statics() => new(
        [],
        [],
        [new TypeDef(ShipType, 1, 6, new Dictionary<int, double> { [Resistance] = 100, [Speed] = 100 }, [])]);

    [Fact]
    public void A_maximum_collection_takes_the_strongest_active_input_and_a_later_weaker_one_does_not_stack()
    {
        var registry = new BuffRegistry();
        registry.Apply(Ship, Strongest, 8, nowMs: 0, durationMs: 60_000);
        registry.Apply(Ship, Strongest, 20, nowMs: 10_000, durationMs: 60_000);
        Assert.Equal(20, registry.ValuesOf(Ship, 20_000)[Strongest.Id]);

        registry.Apply(Ship, Strongest, 5, nowMs: 30_000, durationMs: 60_000);
        Assert.Equal(20, registry.ValuesOf(Ship, 30_000)[Strongest.Id]);
        // the 20 ran out at 70 000 ms; the 5 (to 90 000 ms) is the strongest left
        Assert.Equal(5, registry.ValuesOf(Ship, 75_000)[Strongest.Id]);
    }

    [Fact]
    public void A_minimum_collection_takes_the_weakest_value_so_a_bigger_negative_one_wins()
    {
        var registry = new BuffRegistry();
        registry.Apply(Ship, Weakest, -8, nowMs: 0, durationMs: 60_000);
        registry.Apply(Ship, Weakest, -10, nowMs: 0, durationMs: 60_000);
        Assert.Equal(-10, registry.ValuesOf(Ship, 1_000)[ShieldResistance]);
    }

    [Fact]
    public void An_input_is_active_up_to_and_including_its_expiry_and_a_refresh_extends_it()
    {
        var registry = new BuffRegistry();
        registry.Apply(Ship, Strongest, 8, nowMs: 0, durationMs: 1_000);
        Assert.Equal(8, registry.ValuesOf(Ship, 1_000)[Strongest.Id]);
        Assert.False(registry.ValuesOf(Ship, 1_001).ContainsKey(Strongest.Id));

        registry.Apply(Ship, Strongest, 8, nowMs: 900, durationMs: 1_000);
        Assert.Equal(8, registry.ValuesOf(Ship, 1_500)[Strongest.Id]);
    }

    [Fact]
    public void A_value_that_is_not_finite_or_a_duration_that_is_not_positive_applies_nothing()
    {
        var registry = new BuffRegistry();
        registry.Apply(Ship, Strongest, double.NaN, nowMs: 0, durationMs: 1_000);
        registry.Apply(Ship, Strongest, 8, nowMs: 0, durationMs: 0);
        registry.Apply(Ship, Strongest, 8, nowMs: 0, durationMs: double.PositiveInfinity);
        Assert.Empty(registry.ValuesOf(Ship, 0));
    }

    [Fact]
    public void Prune_drops_the_expired_inputs_and_a_new_registry_drops_every_buff()
    {
        var registry = new BuffRegistry();
        registry.Apply(Ship, Strongest, 8, nowMs: 0, durationMs: 1_000);
        registry.Prune(1_001);
        Assert.Empty(registry.ValuesOf(Ship, 0));

        registry.Apply(Ship, Strongest, 8, nowMs: 0, durationMs: 60_000);
        var restarted = new BuffRegistry();
        Assert.Empty(restarted.ValuesOf(Ship, 1));
    }

    [Fact]
    public void Sync_makes_the_items_buffs_the_worlds_numbers_until_they_run_out()
    {
        var world = new DogmaWorld(Statics(), 0, Ship);
        world.Add(new DogmaItem(Ship, ShipType, Owner, 0, 0, ItemState.Online));
        Assert.Equal(100, world.Get(Ship, Resistance));

        var registry = new BuffRegistry();
        registry.Apply(Ship, Weakest, -10, nowMs: 0, durationMs: 60_000);
        registry.Sync(world, Ship, 1_000);
        Assert.Equal(90, world.Get(Ship, Resistance), 9);

        registry.Sync(world, Ship, 60_001);
        Assert.Equal(100, world.Get(Ship, Resistance), 9);
    }

    [Fact]
    public void Two_collections_on_a_non_stackable_attribute_take_the_stacking_penalty_as_EVE_js_does()
    {
        var first = new BuffCollection(10, "first", true, [new Modifier(ModifierFunc.Item, "itemID", Operation.PostPercent, Resistance, 0)]);
        var second = new BuffCollection(11, "second", true, [new Modifier(ModifierFunc.Item, "itemID", Operation.PostPercent, Resistance, 0)]);
        var statics = new DogmaStatic(
            [new AttributeDef(Resistance, "resistance", 0, Stackable: false, HighIsGood: false, DataType: 0)],
            [],
            [new TypeDef(ShipType, 1, 6, new Dictionary<int, double> { [Resistance] = 100 }, [])]);
        var world = new DogmaWorld(statics, 0, Ship);
        world.Add(new DogmaItem(Ship, ShipType, Owner, 0, 0, ItemState.Online));

        var registry = new BuffRegistry();
        registry.Apply(Ship, first, -10, nowMs: 0, durationMs: 60_000);
        registry.Apply(Ship, second, -10, nowMs: 0, durationMs: 60_000);
        registry.Sync(world, Ship, 1_000);

        // unpenalised, two -10 percents would give 81; the penalty takes the second one part of the way back
        var value = world.Get(Ship, Resistance);
        Assert.True(value > 81 && value < 90, $"the penalised value is {value}");
    }

    [Fact]
    public void A_collection_row_with_an_unknown_operation_is_not_a_buff()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"operationName":"PostSomething","aggregateMode":"Maximum","itemModifiers":[{"dogmaAttributeID":1}]}""");
        Assert.Null(BuffCollection.From(1, document.RootElement));
    }

    [Fact]
    public void A_collection_reads_each_SDE_modifier_scope_and_applies_location_modifiers_to_fitted_items()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            {
              "operationName":"PostPercent",
              "aggregateMode":"Maximum",
              "itemModifiers":[{"dogmaAttributeID":271}],
              "locationModifiers":[{"dogmaAttributeID":272}],
              "locationGroupModifiers":[{"dogmaAttributeID":273,"groupID":2}],
              "locationRequiredSkillModifiers":[{"dogmaAttributeID":274,"skillID":123}]
            }
            """);
        var collection = Assert.IsType<BuffCollection>(BuffCollection.From(22, document.RootElement));
        Assert.Equal(
            [ModifierFunc.Item, ModifierFunc.Location, ModifierFunc.LocationGroup, ModifierFunc.LocationRequiredSkill],
            collection.Shape.Select(modifier => modifier.Func));

        const long module = 200;
        const int moduleType = 2;
        var statics = new DogmaStatic(
            [], [],
            [
                new TypeDef(ShipType, 1, 6, new Dictionary<int, double> { [271] = 100 }, []),
                new TypeDef(moduleType, 2, 7, new Dictionary<int, double> { [272] = 100, [273] = 100, [274] = 100, [182] = 123 }, [])
            ],
            categoryOfGroup: new Dictionary<int, int> { [2] = 7 });
        var world = new DogmaWorld(statics, 0, Ship);
        world.Add(new DogmaItem(Ship, ShipType, Owner, 0, 0, ItemState.Online));
        world.Add(new DogmaItem(module, moduleType, Owner, Ship, 0, ItemState.Online));
        world.SetBuffs(Ship, [collection.EffectFor(-10)]);

        Assert.Equal(90, world.Get(Ship, 271), 9);
        Assert.Equal(90, world.Get(module, 272), 9);
        Assert.Equal(90, world.Get(module, 273), 9);
        Assert.Equal(90, world.Get(module, 274), 9);
    }

    [Fact]
    public void The_state_of_an_item_is_each_collections_value_with_the_latest_expiry_of_its_active_inputs()
    {
        var registry = new BuffRegistry();
        registry.Apply(Ship, Weakest, -8, nowMs: 0, durationMs: 60_000);
        registry.Apply(Ship, Weakest, -10, nowMs: 10_000, durationMs: 60_000);
        registry.Apply(Ship, Strongest, 20, nowMs: 0, durationMs: 30_000);

        var told = registry.StateOf(Ship, 20_000).OrderBy(state => state.CollectionId).ToArray();
        Assert.Equal(
            new[] { new BuffState(ShieldResistance, -10, 70_000), new BuffState(Strongest.Id, 20, 30_000) },
            told);
        // the strongest ran out at 30 000 ms: the state no longer names it
        Assert.Equal(new[] { new BuffState(ShieldResistance, -10, 70_000) }, registry.StateOf(Ship, 40_000));
    }

    [Fact]
    public void Prune_reports_the_items_that_lost_an_input_and_only_those()
    {
        var registry = new BuffRegistry();
        registry.Apply(Ship, Weakest, -8, nowMs: 0, durationMs: 60_000);
        registry.Apply(Ship + 1, Weakest, -8, nowMs: 0, durationMs: 120_000);

        Assert.Equal(new[] { Ship }, registry.Prune(60_001));
        Assert.Empty(registry.Prune(60_002));
        Assert.Equal(new[] { Ship + 1 }, registry.Prune(120_001));
    }

    [Fact]
    public void The_inputs_a_registry_keeps_restore_into_a_new_one_with_the_same_values_and_expiries()
    {
        var registry = new BuffRegistry();
        registry.Apply(Ship, Strongest, 8, nowMs: 0, durationMs: 60_000);
        registry.Apply(Ship, Strongest, 20, nowMs: 10_000, durationMs: 60_000);
        var kept = registry.InputsOf(Ship, 20_000);
        Assert.Equal(2, kept.Count);

        // a restart: the new registry knows only what the host kept
        var restarted = new BuffRegistry();
        foreach (var input in kept)
        {
            restarted.Restore(Ship, Strongest, input.Value, input.ExpiresAtMs);
        }
        Assert.Equal(registry.ValuesOf(Ship, 20_000)[Strongest.Id], restarted.ValuesOf(Ship, 20_000)[Strongest.Id]);
        Assert.Equal(registry.StateOf(Ship, 20_000), restarted.StateOf(Ship, 20_000));
        Assert.Equal(kept.Count, restarted.InputsOf(Ship, 20_000).Count);
        // an input at or after its expiry is what is kept: at 80 000 ms both have run out
        Assert.Empty(registry.InputsOf(Ship, 80_000));
    }
}
