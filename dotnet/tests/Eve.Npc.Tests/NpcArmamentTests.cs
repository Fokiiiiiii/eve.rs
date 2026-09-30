using Eve.Dogma;
using Eve.Npc;

namespace Eve.Npc.Tests;

public sealed class NpcArmamentTests
{
    private const long ShipId = 4_300_000_000_000_001;

    private static EffectDef Effect(int id, string name, string guid, int category) => new(id, name, category, []) { Guid = guid };

    private static DogmaStatic Statics(params EffectDef[] effects) =>
        new([new AttributeDef(247, "entityAttackRange", 15_000, true, false, 0)], effects, []);

    private static TypeDef Hull(IEnumerable<(int Attribute, double Value)> attributes, params int[] effects) =>
        new(23245, 25, 11, attributes.ToDictionary(a => a.Attribute, a => a.Value), effects, "Blood Visionary");

    private static readonly (int, double)[] LaserHull =
    [
        (51, 4_000), (54, 20_000), (158, 10_000), (160, 0.4), (620, 40), (117, 6), (116, 4), (64, 1.5),
    ];

    private static readonly EffectDef TargetAttack = Effect(10, "targetAttack", "effects.Laser", EffectDef.Target);

    [Fact]
    public void A_hull_with_a_laser_effect_carries_a_laser_of_its_own_attributes()
    {
        var weapon = NpcArmament.HullWeapon(Hull(LaserHull, 10), ShipId, Statics(TargetAttack));

        Assert.NotNull(weapon);
        Assert.Equal(ShipId, weapon.ModuleId);
        Assert.Equal(10, weapon.EffectId);
        Assert.Equal(23245, weapon.WeaponTypeId);
        Assert.Equal("effects.Laser", weapon.Fx);
        Assert.Equal(4_000, weapon.CycleMs);
        Assert.Equal(0, weapon.CapacitorNeed);
        Assert.Equal(20_000, weapon.OptimalRange);
        Assert.Equal(10_000, weapon.Falloff);
        Assert.Equal(0.4, weapon.TrackingSpeed);
        Assert.Equal(40, weapon.SignatureResolution);
        // em, thermal, kinetic, explosive each times the multiplier
        Assert.Equal([0, 0, 9, 6], weapon.RawDamage);
    }

    [Fact]
    public void The_order_of_the_damage_is_em_thermal_kinetic_explosive()
    {
        var weapon = NpcArmament.HullWeapon(Hull([(114, 1), (118, 2), (117, 3), (116, 4)], 10), ShipId, Statics(TargetAttack));

        Assert.Equal([1, 2, 3, 4], weapon!.RawDamage);
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(6, 0.0)]
    public void A_hull_that_deals_no_damage_has_no_weapon(double damage, double multiplier)
    {
        var hull = Hull([(117, damage), (64, multiplier)], 10);

        Assert.Null(NpcArmament.HullWeapon(hull, ShipId, Statics(TargetAttack)));
        Assert.False(NpcArmament.HasTargetAttackDamage(hull));
    }

    [Fact]
    public void A_hull_without_a_damage_multiplier_multiplies_by_one()
    {
        var weapon = NpcArmament.HullWeapon(Hull([(117, 6)], 10), ShipId, Statics(TargetAttack));

        Assert.Equal([0, 0, 6, 0], weapon!.RawDamage);
    }

    [Fact]
    public void What_the_hull_does_not_say_has_the_reference_defaults()
    {
        var weapon = NpcArmament.HullWeapon(Hull([(117, 6)], 10), ShipId, Statics(TargetAttack))!;

        Assert.Equal(1_000, weapon.CycleMs);
        Assert.Equal(40_000, weapon.SignatureResolution);
        Assert.Equal(0, weapon.OptimalRange);
        Assert.Equal(0, weapon.Falloff);
        Assert.Equal(0, weapon.TrackingSpeed);
    }

    [Fact]
    public void A_cycle_is_at_least_a_millisecond_and_a_signature_at_least_one()
    {
        var weapon = NpcArmament.HullWeapon(Hull([(117, 6), (51, 0.2), (620, 0)], 10), ShipId, Statics(TargetAttack))!;

        Assert.Equal(1, weapon.CycleMs);
        Assert.Equal(1, weapon.SignatureResolution);
    }

    [Fact]
    public void Numbers_are_rounded_to_six_places_as_the_reference_does()
    {
        var weapon = NpcArmament.HullWeapon(Hull([(117, 1.23456789), (64, 1)], 10), ShipId, Statics(TargetAttack))!;

        Assert.Equal(1.234568, weapon.RawDamage[2]);
    }

    [Theory]
    [InlineData(6.4453125, 6.445313)]
    [InlineData(7.8515625, 7.851563)]
    [InlineData(-6.4453125, -6.445313)]
    [InlineData(2.9296875, 2.929688)]
    [InlineData(0.9765625, 0.976563)]
    [InlineData(4.296875, 4.296875)]
    [InlineData(1.23456789, 1.234568)]
    [InlineData(1.0000005, 1.000001)]
    [InlineData(1.0000004999999999, 1.0)]
    [InlineData(0, 0)]
    [InlineData(1e300, 1e300)]
    public void Round6_rounds_as_toFixed_does_a_tie_to_the_larger_magnitude(double value, double expected)
    {
        Assert.Equal(expected, NpcArmament.Round6(value));
    }

    [Fact]
    public void Round6_of_what_is_no_number_is_nothing()
    {
        Assert.Equal(0, NpcArmament.Round6(double.NaN));
        Assert.Equal(0, NpcArmament.Round6(double.PositiveInfinity));
    }

    [Theory]
    [InlineData("effects.Laser", "laserTurret")]
    [InlineData("effects.TriglavianBeam", "precursorTurret")]
    [InlineData("effects.HybridWeaponFire", "hybridTurret")]
    [InlineData("effects.Railgun", "hybridTurret")]
    [InlineData("effects.Blaster", "hybridTurret")]
    [InlineData("effects.ProjectileFired", "projectileTurret")]
    [InlineData("effects.Artillery", "projectileTurret")]
    [InlineData("effects.Autocannon", "projectileTurret")]
    [InlineData("effects.MissileDeployment", null)]
    [InlineData("", null)]
    public void The_family_of_a_turret_is_told_by_the_effect_it_plays(string guid, string? family)
    {
        Assert.Equal(family, NpcArmament.TurretFamily(Effect(10, "targetAttack", guid, EffectDef.Target)));
    }

    [Fact]
    public void An_effect_that_is_not_an_attack_is_no_turret()
    {
        Assert.Null(NpcArmament.TurretFamily(Effect(1, "entityChainLightning", "effects.Laser", EffectDef.Target)));
        Assert.Null(NpcArmament.TurretFamily(Effect(2, "shieldBoosting", "effects.Laser", EffectDef.Activation)));
    }

    [Fact]
    public void A_hull_whose_only_attack_is_no_turret_has_no_weapon()
    {
        var missiles = Effect(10, "targetAttack", "effects.MissileDeployment", EffectDef.Target);

        Assert.Null(NpcArmament.HullWeapon(Hull(LaserHull, 10), ShipId, Statics(missiles)));
    }

    [Fact]
    public void The_weapon_is_the_first_turret_effect_and_the_fx_the_first_effect_a_pilot_can_run()
    {
        var online = Effect(16, "online", "", EffectDef.Online);
        var passive = Effect(3, "turretFitted", "effects.Nothing", EffectDef.Activation);
        var repair = Effect(5, "entityArmorRepair", "effects.ArmorRepair", EffectDef.Activation);
        var disintegrator = Effect(11, "targetDisintegratorAttack", "effects.Laser", EffectDef.Target);

        var weapon = NpcArmament.HullWeapon(Hull(LaserHull, 16, 3, 5, 11, 10), ShipId, Statics(online, passive, repair, disintegrator, TargetAttack))!;

        Assert.Equal(11, weapon.EffectId);
        Assert.Equal("effects.ArmorRepair", weapon.Fx);
    }

    [Theory]
    [InlineData("online")]
    [InlineData("onlineForStructures")]
    [InlineData("hiPower")]
    [InlineData("turretFitted")]
    [InlineData("launcherFitted")]
    [InlineData("medPower")]
    [InlineData("loPower")]
    [InlineData("rigSlot")]
    [InlineData("subSystem")]
    [InlineData("anchorDropForStructures")]
    [InlineData("anchorLiftForStructures")]
    public void An_effect_every_module_has_is_not_the_one_the_fx_comes_from(string name)
    {
        var slot = Effect(3, name, "effects.Slot", EffectDef.Activation);

        var weapon = NpcArmament.HullWeapon(Hull(LaserHull, 3, 10), ShipId, Statics(slot, TargetAttack))!;

        Assert.Equal("effects.Laser", weapon.Fx);
    }

    [Theory]
    [InlineData(EffectDef.Activation, true)]
    [InlineData(EffectDef.Target, true)]
    [InlineData(EffectDef.Area, true)]
    [InlineData(EffectDef.Passive, false)]
    [InlineData(EffectDef.Online, false)]
    [InlineData(EffectDef.Overload, false)]
    public void Only_effects_of_the_categories_a_pilot_runs_give_the_fx(int category, bool gives)
    {
        var first = Effect(5, "entityShieldBoosting", "effects.First", category);

        var weapon = NpcArmament.HullWeapon(Hull(LaserHull, 5, 10), ShipId, Statics(first, TargetAttack))!;

        Assert.Equal(gives ? "effects.First" : "effects.Laser", weapon.Fx);
    }

    [Fact]
    public void A_guid_of_none_is_no_fx()
    {
        // the laser family is told by the guid, so the hull plays its laser through an effect of another name first
        var none = Effect(5, "entityShieldBoosting", "None", EffectDef.Activation);

        var weapon = NpcArmament.HullWeapon(Hull(LaserHull, 5, 10), ShipId, Statics(none, TargetAttack))!;

        Assert.Equal("", weapon.Fx);
    }

    [Theory]
    [InlineData(32_500.0, 32_500.0)]
    [InlineData(-5.0, 0.0)]
    public void An_npc_attacks_at_the_range_its_hull_gives(double range, double expected)
    {
        Assert.Equal(expected, NpcArmament.AggressionRange(Hull([(247, range)]), Statics()));
    }

    [Fact]
    public void A_hull_with_no_attack_range_has_the_default_of_the_attribute()
    {
        Assert.Equal(15_000, NpcArmament.AggressionRange(Hull([]), Statics()));
        Assert.Equal(0, NpcArmament.AggressionRange(Hull([]), new DogmaStatic([], [], [])));
    }
}
