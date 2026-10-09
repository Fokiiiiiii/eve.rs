namespace Eve.Services.Dogma.Tests;

/// <summary>
/// A script is a charge loaded in a module's slot: its effects reach the module it sits beside (the other item of the same slot, <c>otherID</c>). The
/// SDE rows used here: Tracking Computer II (1978) carries trackingSpeedBonus 767 = 15 and maxRangeBonus 351 = 7.5, and effect 4559 applies both to the
/// Gunnery turrets fitted to the ship. Tracking Speed Script (29001) carries +100 on the module's trackingSpeedBonusBonus and -100 on its maxRangeBonusBonus;
/// Optimal Range Script (28999) carries the mirror image.
/// </summary>
public class ModuleScriptTests
{
    private const int TrackingComputer = 1978;
    private const int Turret = 484; // 125mm Gatling AutoCannon I, group 55
    private const int TrackingSpeedScript = 29001;
    private const int OptimalRangeScript = 28999;
    private const int TrackingSpeed = 160;
    private const int MaxRange = 54;
    private const int TrackingSpeedBonus = 767;
    private const int MaxRangeBonus = 351;

    private const int EccmRadarI = 1947; // ECCM - Radar I: category 7 (a module), group 202
    private const int EccmScript = 41155; // ECCM Script: category 8 (a charge), group 910, loaded in the ECCM module's slot
    private const int ScanRadarStrengthPercent = 1030;
    private const int ScanRadarStrength = 208;
    private const int CapitalFlexArmorHardenerI = 41515;
    private const int CapitalFlexShieldHardenerI = 41516;
    private const int ArmorEmResistanceScript = 41517;
    private const int ShieldEmResistanceScript = 41521;
    private const int EmResistanceBonus = 984;
    private const int ExplosiveResistanceBonus = 985;
    private const int KineticResistanceBonus = 986;
    private const int ThermalResistanceBonus = 987;
    private const int ArmorEmResonance = 267;
    private const int ArmorExplosiveResonance = 268;
    private const int ShieldEmResonance = 271;
    private const int MissileGuidanceComputerI = 35788; // group 1396, its script group is 1400
    private const int MissileRangeScript = 35794;
    private const int MissilePrecisionScript = 35795;
    private const int MissileVelocityBonus = 547;
    private const int ExplosionDelayBonus = 596;
    private const int AoeVelocityBonus = 847;
    private const int AoeCloudSizeBonus = 848;
    private static readonly int[] ModuleScriptTypes =
        [EccmRadarI, EccmScript, CapitalFlexArmorHardenerI, CapitalFlexShieldHardenerI, ArmorEmResistanceScript, ShieldEmResistanceScript, MissileGuidanceComputerI, MissileRangeScript, MissilePrecisionScript];

    private static async Task<(Rig Rig, Eve.Dogma.DogmaWorld World, long ShipId, long CharacterId, Func<int, int, Eve.Dogma.ItemState, Eve.Dogma.DogmaItem> Add)> ShipAsync(params int[] more)
    {
        var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var snapshot = await rig.Ships.LoadAsync(character.CharacterId);
        Assert.NotNull(snapshot);
        await rig.Statics.EnsureTypesAsync([TrackingComputer, Turret, TrackingSpeedScript, OptimalRangeScript, .. more]);
        var shipId = snapshot.Ship.Id;
        var world = snapshot.World;
        var nextItemId = long.MaxValue - 2_000;
        Eve.Dogma.DogmaItem Add(int typeId, int flag, Eve.Dogma.ItemState state) =>
            world.Add(new Eve.Dogma.DogmaItem(nextItemId++, typeId, character.CharacterId, shipId, flag, state));
        return (rig, world, shipId, character.CharacterId, Add);
    }

    [PgFact]
    public async Task A_tracking_speed_script_loaded_in_a_tracking_computer_doubles_its_bonus_and_takes_the_range_bonus_away()
    {
        var (rig, world, _, _, add) = await ShipAsync();
        await using var _rig = rig;
        var turret = add(Turret, 27, Eve.Dogma.ItemState.Online);
        var baseTracking = world.Get(turret.Id, TrackingSpeed);
        var baseRange = world.Get(turret.Id, MaxRange);

        var computer = add(TrackingComputer, 11, Eve.Dogma.ItemState.Active); // an activated module: effect 4559 is an activation effect
        Assert.Equal(15, world.Get(computer.Id, TrackingSpeedBonus), 9);
        Assert.Equal(baseTracking * 1.15, world.Get(turret.Id, TrackingSpeed), 6);
        Assert.Equal(baseRange * 1.075, world.Get(turret.Id, MaxRange), 6);

        // the script sits beside the computer in its slot: it doubles the computer's tracking bonus and removes its range bonus
        add(TrackingSpeedScript, 11, Eve.Dogma.ItemState.Online);
        Assert.Equal(30, world.Get(computer.Id, TrackingSpeedBonus), 9);
        Assert.Equal(0, world.Get(computer.Id, MaxRangeBonus), 9);
        Assert.Equal(baseTracking * 1.30, world.Get(turret.Id, TrackingSpeed), 6);
        Assert.Equal(baseRange, world.Get(turret.Id, MaxRange), 6);
    }

    [PgFact]
    public async Task An_optimal_range_script_loaded_in_a_tracking_computer_removes_its_tracking_bonus_and_doubles_its_range_bonus()
    {
        var (rig, world, _, _, add) = await ShipAsync();
        await using var _rig = rig;
        var turret = add(Turret, 27, Eve.Dogma.ItemState.Online);
        var baseTracking = world.Get(turret.Id, TrackingSpeed);
        var baseRange = world.Get(turret.Id, MaxRange);

        var computer = add(TrackingComputer, 11, Eve.Dogma.ItemState.Active); // an activated module: effect 4559 is an activation effect
        add(OptimalRangeScript, 11, Eve.Dogma.ItemState.Online);
        Assert.Equal(0, world.Get(computer.Id, TrackingSpeedBonus), 9);
        Assert.Equal(15, world.Get(computer.Id, MaxRangeBonus), 9);
        Assert.Equal(baseTracking, world.Get(turret.Id, TrackingSpeed), 6);
        Assert.Equal(baseRange * 1.15, world.Get(turret.Id, MaxRange), 6);
    }

    [PgFact]
    public async Task An_ECCM_script_loaded_in_an_ECCM_module_doubles_its_radar_strength_and_the_ship_takes_the_doubled_strength_when_it_runs()
    {
        // the SDE: the ECCM script carries sensorStrengthBonusBonus +100 (2282) on the module's four strengths; the wiki (EVE University, Electronic warfare)
        // says the script "doubles the sensor strength effect". The ECCM module itself is category 7, the script category 8: they pair through otherID.
        var (rig, world, shipId, _, add) = await ShipAsync(ModuleScriptTypes);
        await using var _rig = rig;
        var baseShipRadar = world.Get(shipId, ScanRadarStrength);
        // the pairing is by slot (flag): the low slot is empty on the starting ship, a medium slot may not be
        var eccm = add(EccmRadarI, 11, Eve.Dogma.ItemState.Online);
        Assert.Equal(80, world.Get(eccm.Id, ScanRadarStrengthPercent), 9);

        var script = add(EccmScript, 11, Eve.Dogma.ItemState.Online);
        Assert.Equal(eccm.Id, world.Partner(script)?.Id);
        Assert.Equal(160, world.Get(eccm.Id, ScanRadarStrengthPercent), 9);

        // running, the ECCM's activation effect (2231) adds its percent to the ship's radar strength as PostPercent: base * (1 + 1.60)
        using (world.Assume(eccm.Id, Eve.Dogma.ItemState.Active))
        {
            Assert.Equal(baseShipRadar * 2.60, world.Get(shipId, ScanRadarStrength), 6);
        }
        Assert.Equal(baseShipRadar, world.Get(shipId, ScanRadarStrength), 6);
    }

    [PgFact]
    public async Task A_flex_armour_script_loaded_in_a_capital_flex_armour_hardener_shifts_its_resistances_and_the_ship_resonance_it_sets()
    {
        // the SDE: Armor EM Resistance Script carries +200 on the hardener's EM bonus and -100 on the other three (2402 to 2405, op 6)
        var (rig, world, shipId, _, add) = await ShipAsync(ModuleScriptTypes);
        await using var _rig = rig;
        var baseEmResonance = world.Get(shipId, ArmorEmResonance);
        var baseExplosiveResonance = world.Get(shipId, ArmorExplosiveResonance);
        var hardener = add(CapitalFlexArmorHardenerI, 11, Eve.Dogma.ItemState.Online);
        Assert.Equal(-16, world.Get(hardener.Id, EmResistanceBonus), 9);

        add(ArmorEmResistanceScript, 11, Eve.Dogma.ItemState.Online);
        Assert.Equal(-48, world.Get(hardener.Id, EmResistanceBonus), 9);
        Assert.Equal(0, world.Get(hardener.Id, ExplosiveResistanceBonus), 9);
        Assert.Equal(0, world.Get(hardener.Id, KineticResistanceBonus), 9);
        Assert.Equal(0, world.Get(hardener.Id, ThermalResistanceBonus), 9);

        // the hardener's activation sets the armour resonances from its bonuses (modifyActiveArmorResonancePostPercent, 5231)
        using (world.Assume(hardener.Id, Eve.Dogma.ItemState.Active))
        {
            Assert.Equal(baseEmResonance * 0.52, world.Get(shipId, ArmorEmResonance), 6);
            Assert.Equal(baseExplosiveResonance, world.Get(shipId, ArmorExplosiveResonance), 6);
        }
    }

    [PgFact]
    public async Task A_flex_shield_script_loaded_in_a_capital_flex_shield_hardener_shifts_the_shield_resonance_it_sets()
    {
        var (rig, world, shipId, _, add) = await ShipAsync(ModuleScriptTypes);
        await using var _rig = rig;
        var baseShieldEm = world.Get(shipId, ShieldEmResonance);
        var hardener = add(CapitalFlexShieldHardenerI, 11, Eve.Dogma.ItemState.Online);
        add(ShieldEmResistanceScript, 11, Eve.Dogma.ItemState.Online);
        Assert.Equal(-48, world.Get(hardener.Id, EmResistanceBonus), 9);
        using (world.Assume(hardener.Id, Eve.Dogma.ItemState.Active))
        {
            Assert.Equal(baseShieldEm * 0.52, world.Get(shipId, ShieldEmResonance), 6);
        }
    }

    [PgFact]
    public async Task A_missile_range_script_loaded_in_a_missile_guidance_computer_trades_the_cloud_and_the_velocity_for_missile_speed_and_delay()
    {
        // the SDE: Missile Range Script carries aoeCloudSize -100, aoeVelocity -100, missileVelocity +100 and explosionDelay +100 (2023 to 2026, op 6)
        var (rig, world, _, _, add) = await ShipAsync(ModuleScriptTypes);
        await using var _rig = rig;
        var computer = add(MissileGuidanceComputerI, 11, Eve.Dogma.ItemState.Online);
        var baseVelocity = world.Get(computer.Id, MissileVelocityBonus);
        var baseDelay = world.Get(computer.Id, ExplosionDelayBonus);
        var baseCloud = world.Get(computer.Id, AoeCloudSizeBonus);
        var baseAoeVelocity = world.Get(computer.Id, AoeVelocityBonus);

        add(MissileRangeScript, 11, Eve.Dogma.ItemState.Online);
        Assert.Equal(baseVelocity * 2, world.Get(computer.Id, MissileVelocityBonus), 9);
        Assert.Equal(baseDelay * 2, world.Get(computer.Id, ExplosionDelayBonus), 9);
        Assert.Equal(0, world.Get(computer.Id, AoeCloudSizeBonus), 9);
        Assert.Equal(0, world.Get(computer.Id, AoeVelocityBonus), 9);
        Assert.NotEqual(baseCloud, world.Get(computer.Id, AoeCloudSizeBonus));
        Assert.NotEqual(baseAoeVelocity, world.Get(computer.Id, AoeVelocityBonus));
    }

    [PgFact]
    public async Task A_missile_precision_script_loaded_in_a_missile_guidance_computer_is_the_mirror_of_the_range_script()
    {
        var (rig, world, _, _, add) = await ShipAsync(ModuleScriptTypes);
        await using var _rig = rig;
        var computer = add(MissileGuidanceComputerI, 11, Eve.Dogma.ItemState.Online);
        var baseVelocity = world.Get(computer.Id, MissileVelocityBonus);
        var baseDelay = world.Get(computer.Id, ExplosionDelayBonus);
        var baseCloud = world.Get(computer.Id, AoeCloudSizeBonus);
        var baseAoeVelocity = world.Get(computer.Id, AoeVelocityBonus);

        add(MissilePrecisionScript, 11, Eve.Dogma.ItemState.Online);
        Assert.Equal(0, world.Get(computer.Id, MissileVelocityBonus), 9);
        Assert.Equal(0, world.Get(computer.Id, ExplosionDelayBonus), 9);
        Assert.Equal(baseCloud * 2, world.Get(computer.Id, AoeCloudSizeBonus), 9);
        Assert.Equal(baseAoeVelocity * 2, world.Get(computer.Id, AoeVelocityBonus), 9);
        Assert.NotEqual(baseVelocity, world.Get(computer.Id, MissileVelocityBonus));
        Assert.NotEqual(baseDelay, world.Get(computer.Id, ExplosionDelayBonus));
    }
}
