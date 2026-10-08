namespace Eve.Dogma.Tests;

/// <summary>
/// <see cref="DogmaWorld.Assume"/>: what an effect is told before it starts. The world is worked out as if the item were in the assumed state and ran the
/// assumed modifiers, with the same precedence and pins as the world, and it is the same world again when the scope ends.
/// </summary>
public class AssumeTests
{
    private const long Owner = 1, Ship = 100, Module = 101, Pin = 102;
    private const int ShipType = 1, ModuleType = 2, PinType = 3;
    private const int Speed = 20, Gain = 21, Pinned = 22;
    private const int ActivationEffect = 1, PassiveEffect = 2;

    private static DogmaStatic Statics() => new(
        [],
        [
            new EffectDef(ActivationEffect, "gain", EffectDef.Activation, [new Modifier(ModifierFunc.Item, "shipID", Operation.PostPercent, Speed, Gain)]),
            new EffectDef(PassiveEffect, "pin", EffectDef.Passive, [new Modifier(ModifierFunc.Item, "shipID", Operation.PostAssign, Speed, Pinned)]),
        ],
        [
            new TypeDef(ShipType, 1, 6, new Dictionary<int, double> { [Speed] = 100 }, []),
            new TypeDef(ModuleType, 1, 7, new Dictionary<int, double> { [Gain] = -50 }, [ActivationEffect]),
            new TypeDef(PinType, 1, 7, new Dictionary<int, double> { [Pinned] = 1 }, [PassiveEffect]),
        ]);

    private static DogmaWorld WorldOf(bool pinned)
    {
        var world = new DogmaWorld(Statics(), 0, Ship);
        world.Add(new DogmaItem(Ship, ShipType, Owner, 0, 0, ItemState.Online));
        world.Add(new DogmaItem(Module, ModuleType, Owner, Ship, 11, ItemState.Offline));
        if (pinned)
        {
            world.Add(new DogmaItem(Pin, PinType, Owner, Ship, 12, ItemState.Online));
        }
        return world;
    }

    [Fact]
    public void An_assumed_state_runs_the_item_modifiers_only_until_its_scope_ends()
    {
        var world = WorldOf(pinned: false);
        Assert.Equal(100, world.Get(Ship, Speed));

        using (world.Assume(Module, ItemState.Active))
        {
            Assert.Equal(50, world.Get(Ship, Speed));
            Assert.Equal(ItemState.Active, world.Item(Module)!.State);
        }

        Assert.Equal(100, world.Get(Ship, Speed));
        Assert.Equal(ItemState.Offline, world.Item(Module)!.State);
    }

    [Fact]
    public void An_unknown_item_assumes_nothing_and_a_second_dispose_changes_nothing()
    {
        var world = WorldOf(pinned: false);
        var scope = world.Assume(Module, ItemState.Active);
        scope.Dispose();
        scope.Dispose();
        Assert.Equal(100, world.Get(Ship, Speed));

        using (world.Assume(999, ItemState.Active))
        {
            Assert.Equal(100, world.Get(Ship, Speed));
        }
    }

    [Fact]
    public void An_assumed_modifier_is_applied_by_the_world_and_cannot_lift_a_pin()
    {
        // without a pin the assumed PostMul (by the module's -50) is what the world gives
        var unpinned = WorldOf(pinned: false);
        using (unpinned.Assume(Module, ItemState.Online, [new Modifier(ModifierFunc.Item, "shipID", Operation.PostMul, Speed, Gain)]))
        {
            Assert.Equal(-5000, unpinned.Get(Ship, Speed));
        }
        Assert.Equal(100, unpinned.Get(Ship, Speed));

        // with the pin the assignment comes last in the order, so the same assumption leaves the pinned value
        var pinned = WorldOf(pinned: true);
        Assert.Equal(1, pinned.Get(Ship, Speed));
        using (pinned.Assume(Module, ItemState.Active, [new Modifier(ModifierFunc.Item, "shipID", Operation.PostMul, Speed, Gain)]))
        {
            Assert.Equal(1, pinned.Get(Ship, Speed));
        }
        Assert.Equal(1, pinned.Get(Ship, Speed));
    }

    [Fact]
    public void Nested_scopes_end_in_reverse_order_and_restore_each_state()
    {
        var world = WorldOf(pinned: false);
        var outer = world.Assume(Module, ItemState.Active);
        var inner = world.Assume(Module, ItemState.Online);
        Assert.Equal(100, world.Get(Ship, Speed));

        inner.Dispose();
        Assert.Equal(ItemState.Active, world.Item(Module)!.State);
        Assert.Equal(50, world.Get(Ship, Speed));

        outer.Dispose();
        Assert.Equal(ItemState.Offline, world.Item(Module)!.State);
        Assert.Equal(100, world.Get(Ship, Speed));
    }
}
