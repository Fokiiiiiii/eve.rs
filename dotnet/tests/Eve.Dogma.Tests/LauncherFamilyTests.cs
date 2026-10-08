namespace Eve.Dogma.Tests;

/// <summary>
/// The rule that puts a launcher group in the missile family, on synthetic candidates (no database). The SDE build of the same rule is checked against the real
/// static data in <c>Eve.Services.Space.Tests/LauncherFamilyTests.cs</c>.
/// </summary>
public class LauncherFamilyTests
{
    private const int ShipModule = 7, Structure = 66;
    private const int Cruise = 506, Defender = 512, Bomb = 862, Festival = 501, FieldGenerator = 899;
    private const int CruiseCharges = 386, DefenderCharges = 88, BombCharges = 90, NeutralCharges = 84, FofCharges = 396, Structures = 1546;

    private static ChargeCandidate Charge(int group, string? effect, bool published = true, int type = 0) =>
        new(type == 0 ? group * 10 : type, group, published, effect);

    private static LauncherCandidate Launcher(int group, int category = ShipModule, bool published = true, params int[] chargeGroups) =>
        new(group * 100, group, published, category, chargeGroups);

    [Fact]
    public void A_published_ship_launcher_whose_charges_fire_missiles_is_in_the_family()
    {
        var groups = LauncherFamilies.MissileGroups(
            [Launcher(Cruise, chargeGroups: [CruiseCharges, FofCharges])],
            [Charge(CruiseCharges, "missileLaunching"), Charge(FofCharges, "fofMissileLaunching")]);
        Assert.Equal(new[] { Cruise }, groups.ToArray());
    }

    [Fact]
    public void A_structure_launcher_with_missiles_is_not_in_the_family_because_its_category_is_not_a_ship_module()
    {
        // the category gate: a structure launcher fires missileLaunching too, and must not be served by the ship's handler
        var groups = LauncherFamilies.MissileGroups(
            [Launcher(Structure, category: Structure, chargeGroups: [Structures])],
            [Charge(Structures, "missileLaunching")]);
        Assert.Empty(groups);
    }

    [Fact]
    public void A_launcher_of_a_defender_bomb_or_festival_charge_is_not_in_the_family()
    {
        var groups = LauncherFamilies.MissileGroups(
            [Launcher(Defender, chargeGroups: [DefenderCharges]), Launcher(Bomb, chargeGroups: [BombCharges]), Launcher(Festival, chargeGroups: [500])],
            [Charge(DefenderCharges, "defenderMissileLaunching"), Charge(BombCharges, "bombLaunching"), Charge(500, "snowBallLaunching")]);
        Assert.Empty(groups);
    }

    [Fact]
    public void A_mixed_launcher_that_takes_a_defender_charge_and_a_missile_is_not_in_the_family()
    {
        // the countermeasure launcher (308) takes both kinds: the family needs every firing charge to be a missile
        var groups = LauncherFamilies.MissileGroups(
            [Launcher(308, chargeGroups: [DefenderCharges, CruiseCharges])],
            [Charge(DefenderCharges, "defenderMissileLaunching"), Charge(CruiseCharges, "missileLaunching")]);
        Assert.Empty(groups);
    }

    [Fact]
    public void A_launcher_whose_charges_are_only_fof_missiles_is_not_in_the_family_because_none_is_a_plain_missile()
    {
        var groups = LauncherFamilies.MissileGroups([Launcher(Cruise, chargeGroups: [FofCharges])], [Charge(FofCharges, "fofMissileLaunching")]);
        Assert.Empty(groups);
    }

    [Fact]
    public void A_charge_group_without_a_firing_effect_is_neutral_and_an_unpublished_charge_is_ignored()
    {
        var groups = LauncherFamilies.MissileGroups(
            [Launcher(Cruise, chargeGroups: [NeutralCharges, CruiseCharges])],
            [Charge(NeutralCharges, null), Charge(CruiseCharges, "missileLaunching"), Charge(CruiseCharges, "defenderMissileLaunching", published: false)]);
        Assert.Equal(new[] { Cruise }, groups.ToArray());
    }

    [Fact]
    public void An_unpublished_launcher_and_a_launcher_with_no_charge_group_are_not_in_the_family()
    {
        var groups = LauncherFamilies.MissileGroups(
            [Launcher(Cruise, published: false, chargeGroups: [CruiseCharges]), Launcher(FieldGenerator, chargeGroups: [])],
            [Charge(CruiseCharges, "missileLaunching")]);
        Assert.Empty(groups);
    }
}
