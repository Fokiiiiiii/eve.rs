using Eve.Characters;
using Eve.Dogma;
using Eve.Inventory;

namespace Eve.Ships.Tests;

/// <summary>
/// A running module's ship bonuses, fitted and started the way the pilot does it, against the static data (SF-10 and SF-11 module-level checks). Each module
/// is fitted with its skills at level five, started (the dogma world holds it Active, as space does), and the numbers it modifies are checked against the SDE:
/// a percentage modifier (operation 6) is the base times (1 + the module's value / 100), an assignment (operation 7) is the hull's value, and a product is the
/// base times the charge's multiplier. A value an SDE type does not give (a ship base or a module's modifier value) is the attribute's own default, as dogma has
/// it: the Bastion Module I states no sensor dampener bonus, so its modifier is the default 0.
/// </summary>
public class ModuleBonusTests
{
    private const int HighSlot = 27;
    private const int Vargur = 28665, Apostle = 37604, Rorqual = 28352, Bifrost = 37480, Redeemer = 22428, Buzzard = 11192;
    private const int BastionModuleI = 33400, TriageModuleI = 27951, CapitalIndustrialCoreI = 28583;
    private const int ShieldCommandBurstI = 42529, ShieldHarmonizing = 42695, CovertCloakII = 11578, CovertCynosuralGeneratorI = 28646;
    private const int CommandDestroyers = 37615;
    private const int Sleipnir = 22444, CommandShips = 23950, SleipnirRole = 33098, CommandShipStrength = 1924;

    private const int MaxVelocity = 37, WarpScrambleStatus = 104, DisallowDocking = 2354;
    private const int RemoteRepairImpedance = 2116, RemoteAssistanceImpedance = 2135, SensorDampenerResistance = 2112;

    // the module values the modifiers read (SDE: the modifyingAttributeID of each modifier, on the module's own type)
    private const int RemoteRepairImpedanceBonus = 2342, RemoteAssistanceImpedanceBonus = 2352, SensorDampenerResistanceBonus = 2351;
    private const int WarpScrambleModule = 852;

    // the command burst strength the Command Destroyer's hull states per level (SDE 2059), the buff and its charge's multiplier (SDE 2469 and 2596)
    private const int CommandStrength = 2059, BuffValue = 2469, BuffMultiplier = 2596;
    // the cloak's delay (SDE 669), the hull's cloak delay the Black Ops role assigns (SDE 1034), and the cyno field's duration (SDE 73) with the hull's percentage (66)
    private const int CloakDelay = 669, HullCloakDelay = 1034, CynoDuration = 73, CynoDurationBonus = 66;

    /// <summary>The value an attribute of a type starts from: the type's own, else the attribute's default (what dogma gives an attribute no type states).</summary>
    private static double Base(Rig rig, int typeId, int attribute) =>
        rig.Statics.Type(typeId)!.Attributes.TryGetValue(attribute, out var value) ? value : rig.Statics.Attribute(attribute)!.DefaultValue;

    private static double Percent(double value, double percent) => value * (1 + percent / 100);

    /// <summary>
    /// Fits the module to a new character's ship in the high slot, with the skills it needs and the given hull skills at level five, loads the charge if one is
    /// given (into the module's slot, as the ship holds it), and starts the module.
    /// </summary>
    private static async Task<(Created Character, long Module, ShipSnapshot Running)> FitAndStartAsync(Rig rig, int hullTypeId, int moduleTypeId, int chargeTypeId = 0, params int[] hullSkills)
    {
        var c = await rig.NewCharacterAsync(shipTypeId: hullTypeId);
        await rig.Statics.EnsureTypesAsync(chargeTypeId == 0 ? [hullTypeId, moduleTypeId] : [hullTypeId, moduleTypeId, chargeTypeId]);
        foreach (var skill in rig.Statics.Type(moduleTypeId)!.RequiredSkills.Concat(hullSkills))
        {
            await rig.SetSkillAsync(c.CharacterId, skill, 5);
        }
        var module = await rig.GiveAsync(c.CharacterId, moduleTypeId);
        var moved = await rig.Ships.MoveAsync(c.CharacterId, module, Rig.Station, c.ShipId, HighSlot, 1);
        Assert.True(moved is ShipChanged, $"fit refused: {moved}");
        if (chargeTypeId != 0)
        {
            await LoadChargeAsync(rig, c, module, chargeTypeId);
        }
        var running = await rig.Ships.LoadAsync(c.CharacterId);
        Assert.NotNull(running);
        running.World.SetState(module, ItemState.Active);
        return (c, module, running);
    }

    /// <summary>Loads two rounds of a charge into the module's slot (its flag), in the ship.</summary>
    private static async Task LoadChargeAsync(Rig rig, Created c, long module, int chargeTypeId)
    {
        var flag = (await rig.Items.GetAsync(module))!.Flag;
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Items.CreateAsync(connection, transaction, new NewItem(chargeTypeId, c.CharacterId, c.ShipId, flag, 2));
        await transaction.CommitAsync();
    }

    [PgFact]
    public async Task A_running_bastion_module_locks_its_marauder_as_the_static_data_says()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, _, running) = await FitAndStartAsync(rig, Vargur, BastionModuleI);
        var world = running.World;

        Assert.Equal(0, world.Get(c.ShipId, MaxVelocity), 9);
        Assert.Equal(Base(rig, Vargur, WarpScrambleStatus) + Base(rig, BastionModuleI, WarpScrambleModule), world.Get(c.ShipId, WarpScrambleStatus), 9);
        Assert.Equal(Base(rig, Vargur, DisallowDocking) + 1, world.Get(c.ShipId, DisallowDocking), 9);
        Assert.Equal(Percent(Base(rig, Vargur, RemoteRepairImpedance), Base(rig, BastionModuleI, RemoteRepairImpedanceBonus)), world.Get(c.ShipId, RemoteRepairImpedance), 9);
        Assert.Equal(Percent(Base(rig, Vargur, RemoteAssistanceImpedance), Base(rig, BastionModuleI, RemoteAssistanceImpedanceBonus)), world.Get(c.ShipId, RemoteAssistanceImpedance), 9);
        Assert.Equal(Percent(Base(rig, Vargur, SensorDampenerResistance), Base(rig, BastionModuleI, SensorDampenerResistanceBonus)), world.Get(c.ShipId, SensorDampenerResistance), 9);
    }

    [PgFact]
    public async Task A_running_triage_module_changes_its_force_auxiliary_as_the_static_data_says()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, _, running) = await FitAndStartAsync(rig, Apostle, TriageModuleI);
        var world = running.World;

        Assert.Equal(0, world.Get(c.ShipId, MaxVelocity), 9);
        Assert.Equal(Base(rig, Apostle, WarpScrambleStatus) + Base(rig, TriageModuleI, WarpScrambleModule), world.Get(c.ShipId, WarpScrambleStatus), 9);
        Assert.Equal(Percent(Base(rig, Apostle, RemoteRepairImpedance), Base(rig, TriageModuleI, RemoteRepairImpedanceBonus)), world.Get(c.ShipId, RemoteRepairImpedance), 9);
        Assert.Equal(Percent(Base(rig, Apostle, RemoteAssistanceImpedance), Base(rig, TriageModuleI, RemoteAssistanceImpedanceBonus)), world.Get(c.ShipId, RemoteAssistanceImpedance), 9);
        Assert.Equal(Percent(Base(rig, Apostle, SensorDampenerResistance), Base(rig, TriageModuleI, SensorDampenerResistanceBonus)), world.Get(c.ShipId, SensorDampenerResistance), 9);
    }

    [PgFact]
    public async Task A_running_capital_industrial_core_locks_its_rorqual_as_the_static_data_says()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, _, running) = await FitAndStartAsync(rig, Rorqual, CapitalIndustrialCoreI);
        var world = running.World;

        Assert.Equal(0, world.Get(c.ShipId, MaxVelocity), 9);
        Assert.Equal(Base(rig, Rorqual, WarpScrambleStatus) + Base(rig, CapitalIndustrialCoreI, WarpScrambleModule), world.Get(c.ShipId, WarpScrambleStatus), 9);
        Assert.Equal(Percent(Base(rig, Rorqual, RemoteRepairImpedance), Base(rig, CapitalIndustrialCoreI, RemoteRepairImpedanceBonus)), world.Get(c.ShipId, RemoteRepairImpedance), 9);
    }

    /// <summary>
    /// The Command Destroyer's strength (2 per level of Command Destroyers, skill 37615 scales it) reaches the burst: the burst's value (1.0) is raised by it
    /// (eliteBonusCommandDestroyerShield1, PostPercent, on the burst as it requires Shield Command), and the Shield Harmonizing charge multiplies it by -8.
    /// </summary>
    [PgFact]
    public async Task A_shield_command_burst_on_a_command_destroyer_carries_the_hulls_strength_into_its_buff()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, burst, running) = await FitAndStartAsync(rig, Bifrost, ShieldCommandBurstI, ShieldHarmonizing, CommandDestroyers);
        var world = running.World;

        var strength = Base(rig, Bifrost, CommandStrength) * 5;
        Assert.Equal(strength, world.Get(c.ShipId, CommandStrength), 9);
        Assert.Equal(Percent(Base(rig, ShieldCommandBurstI, BuffValue), strength) * Base(rig, ShieldHarmonizing, BuffMultiplier), world.Get(burst, BuffValue), 9);
    }

    /// <summary>
    /// A command ship's strength (SDE 1924: 4 per level for a Sleipnir, the attack ship, as the wiki's Command Ships page says) is scaled by the Command Ships
    /// skill (23950, effect 5571) and raises the burst it reaches (5573, the Shield Command burst, PostPercent): the buff is the base times (1 + strength %).
    /// </summary>
    [PgFact]
    public async Task A_shield_command_burst_on_a_command_ship_carries_the_ships_strength_into_its_buff()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, burst, running) = await FitAndStartAsync(rig, Sleipnir, ShieldCommandBurstI, ShieldHarmonizing, CommandShips, SleipnirRole);
        var world = running.World;

        var strength = Base(rig, Sleipnir, CommandShipStrength) * 5;
        Assert.Equal(strength, world.Get(c.ShipId, CommandShipStrength), 9);
        Assert.Equal(Percent(Base(rig, ShieldCommandBurstI, BuffValue), strength) * Base(rig, ShieldHarmonizing, BuffMultiplier), world.Get(burst, BuffValue), 9);
    }

    /// <summary>The Covert Ops and Recon role bonus (2252, PostAssign) gives a covert cloak the hull's cloak delay (1034) when it requires Covert Ops.</summary>
    [PgFact]
    public async Task A_covert_cloak_on_a_covert_ops_ship_takes_the_hulls_cloak_delay()
    {
        await using var rig = await Rig.CreateAsync();
        var (_, cloak, running) = await FitAndStartAsync(rig, Buzzard, CovertCloakII);

        Assert.Equal(Base(rig, Buzzard, HullCloakDelay), running.World.Get(cloak, CloakDelay), 9);
    }

    /// <summary>The Black Ops role bonus (2008) takes a cyno field's duration by the hull's percentage (66) for a generator that requires Cynosural Field Theory.</summary>
    [PgFact]
    public async Task A_covert_cyno_generator_on_a_black_ops_ship_lasts_as_the_hulls_bonus_says()
    {
        await using var rig = await Rig.CreateAsync();
        var (_, generator, running) = await FitAndStartAsync(rig, Redeemer, CovertCynosuralGeneratorI);

        Assert.Equal(Percent(Base(rig, CovertCynosuralGeneratorI, CynoDuration), Base(rig, Redeemer, CynoDurationBonus)), running.World.Get(generator, CynoDuration), 9);
    }
}
