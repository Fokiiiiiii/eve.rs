namespace Eve.Ships.Tests;

/// <summary>The fighter tube rules as EVE.js has them (fighterInventory.js): the tube a squadron takes, the classes, the slots and the squadron size.</summary>
public class FighterTubesTests
{
    private static Func<string, double> Attributes(params (string Name, double Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value);
        return name => map.GetValueOrDefault(name, 0);
    }

    private static readonly Func<string, double> Carrier = Attributes(
        ("fighterTubes", 5), ("fighterLightSlots", 3), ("fighterSupportSlots", 2), ("fighterHeavySlots", 2));

    private static Func<string, double> Light => Attributes(("fighterSquadronIsLight", 1), ("fighterSquadronMaxSize", 6));

    private static Func<string, double> Heavy => Attributes(("fighterSquadronIsHeavy", 1), ("fighterSquadronMaxSize", 6));

    [Theory]
    [InlineData(159, 1)]
    [InlineData(160, 2)]
    [InlineData(161, 3)]
    [InlineData(162, 4)]
    [InlineData(163, 5)]
    [InlineData(158, 0)]
    [InlineData(164, 0)]
    public void Each_launch_tube_flag_has_its_ordinal_and_the_bay_is_not_a_tube(int flag, int ordinal)
    {
        Assert.Equal(ordinal, FighterTubes.Ordinal(flag));
        Assert.Equal(ordinal > 0, FighterTubes.IsTube(flag));
    }

    [Fact]
    public void A_type_is_in_the_first_class_its_attribute_is_above_zero_for_and_none_for_a_non_squadron()
    {
        Assert.Equal("light", FighterTubes.ClassOf(Light)!.Name);
        Assert.Equal(FighterBroadClass.Heavy, FighterTubes.ClassOf(Heavy)!.Broad);
        Assert.Null(FighterTubes.ClassOf(Attributes(("fighterSquadronMaxSize", 6))));
        Assert.Equal("standupLight", FighterTubes.ClassOf(Attributes(("fighterSquadronIsStandupLight", 1)))!.Name);
        Assert.Equal(FighterBroadClass.Light, FighterTubes.ClassOf(Attributes(("fighterSquadronIsStandupLight", 1)))!.Broad);
    }

    [Fact]
    public void The_squadron_size_is_the_max_size_when_set_else_the_size_and_one_singleton_is_one_squadron()
    {
        Assert.Equal(6, FighterTubes.MaxSquadronSize(Attributes(("fighterSquadronMaxSize", 6), ("fighterSquadronSize", 4))));
        Assert.Equal(4, FighterTubes.MaxSquadronSize(Attributes(("fighterSquadronSize", 4))));
        Assert.Equal(0, FighterTubes.MaxSquadronSize(Attributes()));
        Assert.Equal((1L, 5L, 0L), (FighterTubes.InventorySize(true, 1), FighterTubes.InventorySize(false, 5), FighterTubes.InventorySize(false, -3)));
    }

    [Fact]
    public void A_light_squadron_goes_into_any_tube_while_the_host_has_light_slots_and_tubes()
    {
        Assert.Null(FighterTubes.RefusalOf(Carrier, Light, 159, []));
        Assert.Null(FighterTubes.RefusalOf(Carrier, Light, 163, [FighterBroadClass.Light, FighterBroadClass.Light]));
        Assert.True(FighterTubes.CanLoad(Carrier, Light, 163, [FighterBroadClass.Heavy]));
    }

    [Fact]
    public void A_tube_beyond_the_hosts_count_or_not_a_tube_is_refused_as_no_such_tube()
    {
        var twoTubes = Attributes(("fighterTubes", 2), ("fighterLightSlots", 3));
        Assert.Equal(FighterTubeRefusal.NoSuchTube, FighterTubes.RefusalOf(twoTubes, Light, 161, []));
        Assert.Equal(FighterTubeRefusal.NoSuchTube, FighterTubes.RefusalOf(twoTubes, Light, 158, []));
        Assert.Equal(FighterTubeRefusal.NoSuchTube, FighterTubes.RefusalOf(Attributes(), Light, 159, []));
    }

    [Fact]
    public void A_squadron_with_no_class_is_not_a_squadron_and_a_host_with_no_slot_for_its_class_refuses_it()
    {
        Assert.Equal(FighterTubeRefusal.NotASquadron, FighterTubes.RefusalOf(Carrier, Attributes(("fighterSquadronMaxSize", 6)), 159, []));
        var noSupport = Attributes(("fighterTubes", 5), ("fighterLightSlots", 3));
        Assert.Equal(FighterTubeRefusal.NoSlotForClass, FighterTubes.RefusalOf(noSupport, Attributes(("fighterSquadronIsSupport", 1)), 159, []));
    }

    [Fact]
    public void The_class_slots_count_only_their_own_broad_class_and_standup_classes_share_it()
    {
        // a standup light squadron has its own slot count, but it is light too: three light squadrons in the tubes fill its three slots
        var standupCarrier = Attributes(("fighterTubes", 5), ("fighterLightSlots", 3), ("fighterStandupLightSlots", 3), ("fighterHeavySlots", 2));
        var standupLight = Attributes(("fighterSquadronIsStandupLight", 1));
        var full = new[] { FighterBroadClass.Light, FighterBroadClass.Light, FighterBroadClass.Light };
        Assert.Equal(FighterTubeRefusal.ClassSlotsFull, FighterTubes.RefusalOf(standupCarrier, standupLight, 162, full));
        Assert.Null(FighterTubes.RefusalOf(standupCarrier, Heavy, 162, full));
    }

    [Fact]
    public void A_host_with_all_its_tubes_full_refuses_whatever_class_comes()
    {
        var five = new[] { FighterBroadClass.Support, FighterBroadClass.Support, FighterBroadClass.Heavy, FighterBroadClass.Heavy, FighterBroadClass.Light };
        Assert.Equal(FighterTubeRefusal.TubesFull, FighterTubes.RefusalOf(Carrier, Light, 159, five));
    }
}
