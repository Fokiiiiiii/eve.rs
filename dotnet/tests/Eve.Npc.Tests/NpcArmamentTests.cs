using Eve.Dogma;
using Eve.Data;
using Eve.Npc;
using Eve.Sde;
using Eve.Space;
using Npgsql;

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

    private static readonly EffectDef LaunchesMissiles = Effect(569, "missileLaunchingForEntity", "effects.MissileDeployment", EffectDef.Target);

    private static DogmaStatic WithMissile(params EffectDef[] effects) =>
        new([new AttributeDef(247, "entityAttackRange", 15_000, true, false, 0)], effects,
            [new TypeDef(266, 387, 8, new Dictionary<int, double> { [4] = 100, [9] = 90, [37] = 2_250, [70] = 5.22, [117] = 33, [162] = 300, [281] = 2_000, [653] = 150, [654] = 20, [1353] = 0.644 }, [], "Scourge Rocket")]);

    [Fact]
    public void A_hull_that_launches_missiles_carries_the_missile_with_its_own_multipliers_and_has_no_rounds_to_spend()
    {
        // damage 1.5 times, speed 1.1, flight 1.2, explosion velocity 0.9, explosion radius 2; a cycle of 6 s
        var hull = Hull([(507, 266), (506, 6_000), (212, 1.5), (645, 1.1), (646, 1.2), (859, 0.9), (858, 2)], 569);

        var weapon = NpcArmament.HullWeapon(hull, ShipId, WithMissile(LaunchesMissiles));

        Assert.NotNull(weapon);
        Assert.Equal((ShipId, 569, 6_000.0, 0.0, 23245, "effects.MissileDeployment"), (weapon.ModuleId, weapon.EffectId, weapon.CycleMs, weapon.CapacitorNeed, weapon.WeaponTypeId, weapon.Fx));
        Assert.Equal([0.0, 0.0, 49.5, 0.0], weapon.RawDamage);
        Assert.Null(weapon.Charge);
        var missile = weapon.Missile!;
        Assert.Equal((266, 387, "Scourge Rocket", 300.0, 100.0, 5.22, 90.0), (missile.ChargeTypeId, missile.GroupId, missile.Name, missile.Radius, missile.Mass, missile.Agility, missile.HitPoints));
        Assert.Equal((2_475.0, 2_400.0, 40.0, 135.0, 0.644, 5.5), (missile.MaxVelocity, missile.FlightTimeMs, missile.ExplosionRadius, missile.ExplosionVelocity, missile.DamageReductionFactor, missile.DamageReductionSensitivity));
        // how far it goes: its speed for as long as it flies
        Assert.Equal(5_940.0, weapon.OptimalRange);
    }

    [Fact]
    public void A_hull_that_launches_nothing_or_whose_missile_is_not_loaded_has_no_missile_weapon_and_the_cycle_falls_back_to_the_hulls_speed()
    {
        Assert.Null(NpcArmament.HullWeapon(Hull([(507, 266)], 569), ShipId, new DogmaStatic([], [LaunchesMissiles], [])));   // the missile type is not known
        Assert.Null(NpcArmament.HullWeapon(Hull([(507, 0)], 569), ShipId, WithMissile(LaunchesMissiles)));                 // it names none
        Assert.Null(NpcArmament.HullWeapon(Hull([(507, 266)]), ShipId, WithMissile(LaunchesMissiles)));                    // it does not launch
        Assert.Equal(4_500.0, NpcArmament.HullWeapon(Hull([(507, 266), (51, 4_500)], 569), ShipId, WithMissile(LaunchesMissiles))!.CycleMs);
    }

    [Fact]
    public void A_hull_with_a_turret_and_missiles_is_armed_with_its_turret()
    {
        var weapon = NpcArmament.HullWeapon(Hull([.. LaserHull, (507, 266)], 10, 569), ShipId, WithMissile(TargetAttack, LaunchesMissiles));

        Assert.Equal("effects.Laser", weapon!.Fx);
        Assert.Null(weapon.Missile);
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

    [PgFact]
    public async Task Native_NPC_self_repairs_are_resolved_from_the_SDE_type_effects_and_attributes()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(BountyRig.Url!));
        var sde = new SdeReader(source);
        Assert.NotNull(await sde.BuildAsync());
        var statics = await DogmaStatic.LoadAsync(sde);
        await statics.EnsureTypesAsync([56_138, 56_170]);

        foreach (var (typeId, effectId, amountName, delayName) in new[]
        {
            (56_138, 6_990, "behaviorShieldBoosterAmount", "entityShieldBoostDelayChance"),
            (56_170, 6_884, "behaviorArmorRepairerAmount", "entityArmorRepairDelayChance"),
        })
        {
            var type = Assert.IsType<TypeDef>(statics.Type(typeId));
            Assert.Contains(effectId, type.EffectIds);
            var effect = Assert.IsType<EffectDef>(statics.Effect(effectId));
            var repair = Assert.Single(NpcArmament.SelfRepairsOf(type, statics));
            Assert.Equal(effectId, repair.EffectId);
            Assert.Equal(Value(type, statics, amountName), repair.Amount);
            Assert.Equal(Value(type, statics, effect.DurationAttributeId), repair.CycleMs);
            Assert.Equal(Value(type, statics, effect.DischargeAttributeId), repair.CapacitorCost);
            Assert.Equal(Value(type, statics, delayName), repair.DelayChance);
            Assert.True(SimNpcSelfRepair.IsValid(repair));
        }
    }

    private static double Value(TypeDef type, DogmaStatic statics, string attributeName) =>
        Value(type, statics, Assert.IsType<int>(statics.AttributeId(attributeName)));

    private static double Value(TypeDef type, DogmaStatic statics, int attributeId)
    {
        var attribute = Assert.IsType<AttributeDef>(statics.Attribute(attributeId));
        return type.Attributes.GetValueOrDefault(attributeId, attribute.DefaultValue);
    }

    [Fact]
    public void A_hull_with_no_attack_range_has_the_default_of_the_attribute()
    {
        Assert.Equal(15_000, NpcArmament.AggressionRange(Hull([]), Statics()));
        Assert.Equal(0, NpcArmament.AggressionRange(Hull([]), new DogmaStatic([], [], [])));
    }
}
