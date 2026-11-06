using Eve.Inventory;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The host's side of a module's fuel (module_rules WP18, ADR 0013): the simulation asks for the fuel of each cycle that starts, the host takes the module's
/// consumption (713 of the type, 714 of the type after dogma) from the ship's fuel bay or cargo in the transaction that records the answer, and a refusal names
/// <c>NO_FUEL</c> and takes nothing.
/// </summary>
public class ModuleFuelHostTests
{
    /// <summary>The fuel of the siege modules (713 of Siege Module I), an ice product: its group is stored in the fuel bay first.</summary>
    private const int StrontiumClathrates = 16275;
    private const int SiegeModuleI = 20280;
    /// <summary>The Bosonic Field Generator (DDD class): 713 16275 and 714 2000 in the static data.</summary>
    private const int BosonicFieldGenerator = 40633;
    private const int SiegeCycleQuantity = 250;

    [PgFact]
    public async Task A_fuel_cycle_takes_the_cycles_fuel_from_the_ship_and_records_it()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithModuleAsync(rig, SiegeModuleI);
        StartModule(rig, flying);
        await PutFuelAsync(rig, flying, 1000, CharacterShips.CargoFlag);

        var result = await SettleAsync(rig, FuelCycle(flying, flying.Module, 1));

        Assert.True(result.Accepted, result.Reason);
        Assert.Equal(SiegeCycleQuantity, result.DeliveredUnits);
        Assert.Equal(1000 - SiegeCycleQuantity, await QuantityAsync(rig, flying, CharacterShips.CargoFlag));
        var recorded = await new PgWorkCycleLedger(rig.Source, rig.SpaceSchema).LastAsync(flying.Module);
        Assert.Equal((1L, true), (recorded?.CycleId, recorded?.Accepted));
    }

    [PgFact]
    public async Task A_repeat_of_a_paid_fuel_cycle_is_answered_from_the_record_and_takes_nothing_again()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithModuleAsync(rig, SiegeModuleI);
        StartModule(rig, flying);
        await PutFuelAsync(rig, flying, 1000, CharacterShips.CargoFlag);
        Assert.True((await SettleAsync(rig, FuelCycle(flying, flying.Module, 2))).Accepted);

        var repeat = await SettleAsync(rig, FuelCycle(flying, flying.Module, 2));

        Assert.True(repeat.Accepted);
        Assert.Equal(1000 - SiegeCycleQuantity, await QuantityAsync(rig, flying, CharacterShips.CargoFlag));
    }

    [PgFact]
    public async Task A_fuel_cycle_the_ship_cannot_pay_is_refused_by_name_and_takes_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithModuleAsync(rig, SiegeModuleI);
        StartModule(rig, flying);
        await PutFuelAsync(rig, flying, SiegeCycleQuantity - 1, CharacterShips.CargoFlag);

        var result = await SettleAsync(rig, FuelCycle(flying, flying.Module, 3));

        Assert.False(result.Accepted);
        Assert.Equal("NO_FUEL", result.Reason);
        Assert.Equal(SiegeCycleQuantity - 1, await QuantityAsync(rig, flying, CharacterShips.CargoFlag));
        var recorded = await new PgWorkCycleLedger(rig.Source, rig.SpaceSchema).LastAsync(flying.Module);
        Assert.Equal((3L, false, "NO_FUEL"), (recorded?.CycleId, recorded?.Accepted, recorded?.Reason));
    }

    [PgFact]
    public async Task Fuel_is_taken_from_the_fuel_bay_first_and_then_from_the_cargo_hold()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithModuleAsync(rig, SiegeModuleI);
        StartModule(rig, flying);
        // the strontium is an ice product: the fuel bay holds 100 and the cargo 1000
        await PutFuelAsync(rig, flying, 100, CharacterShips.FuelBayFlag);
        await PutFuelAsync(rig, flying, 1000, CharacterShips.CargoFlag);

        var result = await SettleAsync(rig, FuelCycle(flying, flying.Module, 4));

        Assert.True(result.Accepted, result.Reason);
        Assert.Equal(0, await QuantityAsync(rig, flying, CharacterShips.FuelBayFlag));
        Assert.Equal(1000 - (SiegeCycleQuantity - 100), await QuantityAsync(rig, flying, CharacterShips.CargoFlag));
    }

    [PgFact]
    public async Task A_restarted_host_answers_a_repeat_of_a_paid_fuel_cycle_without_paying_again()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithModuleAsync(rig, SiegeModuleI);
        StartModule(rig, flying);
        await PutFuelAsync(rig, flying, 1000, CharacterShips.CargoFlag);
        Assert.True((await SettleAsync(rig, FuelCycle(flying, flying.Module, 5))).Accepted);

        // the host restarts: the old dispatcher goes, a new one reads the same ledger
        rig.Containers.Dispose();
        using var restarted = new SpaceContainers(
            rig.Sim, rig.Ships, rig.Items, rig.SpaceItems, new PgWorkCycleLedger(rig.Source, rig.SpaceSchema), new EventBusFacts(rig.Events), rig.Statics, rig.Space, () => rig.Now);
        var repeat = await SettleAsync(rig, FuelCycle(flying, flying.Module, 5));

        Assert.True(repeat.Accepted);
        Assert.Equal(1000 - SiegeCycleQuantity, await QuantityAsync(rig, flying, CharacterShips.CargoFlag));
    }

    [PgFact]
    public async Task A_ddd_class_module_reaches_the_fuel_check_with_its_own_quantity_of_2000()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithModuleAsync(rig, BosonicFieldGenerator);
        StartModule(rig, flying);
        await PutFuelAsync(rig, flying, 1999, CharacterShips.CargoFlag);

        var unpaid = await SettleAsync(rig, FuelCycle(flying, flying.Module, 6));
        Assert.False(unpaid.Accepted);
        Assert.Equal("NO_FUEL", unpaid.Reason);
        Assert.Equal(1999, await QuantityAsync(rig, flying, CharacterShips.CargoFlag));

        await PutFuelAsync(rig, flying, 1, CharacterShips.CargoFlag);
        var paid = await SettleAsync(rig, FuelCycle(flying, flying.Module, 7));
        Assert.True(paid.Accepted, paid.Reason);
        Assert.Equal(2000, paid.DeliveredUnits);
        Assert.Equal(0, await QuantityAsync(rig, flying, CharacterShips.CargoFlag));
    }

    [PgFact]
    public async Task A_fuel_cycle_for_a_module_that_is_not_running_is_refused_as_not_controlled_and_takes_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithModuleAsync(rig, SiegeModuleI);
        await PutFuelAsync(rig, flying, 1000, CharacterShips.CargoFlag);

        var result = await SettleAsync(rig, FuelCycle(flying, flying.Module, 8));

        Assert.False(result.Accepted);
        Assert.Equal("NOT_CONTROLLED", result.Reason);
        Assert.Equal(1000, await QuantityAsync(rig, flying, CharacterShips.CargoFlag));
    }

    /// <summary>A pilot in space with the module (of this type) fitted and online, and the ship's cargo and fuel bay empty.</summary>
    private static Task<DroneHostTests.Flying> FlyingWithModuleAsync(Rig rig, int moduleTypeId) =>
        DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: moduleTypeId);

    /// <summary>The pilot's module runs, as an activation the simulation accepted leaves it.</summary>
    internal static void StartModule(Rig rig, DroneHostTests.Flying flying) =>
        rig.Space.StartModuleForTest((ulong)flying.Session.SessionId, flying.Module);

    private static long ShipIdOf(DroneHostTests.Flying flying) => flying.Session.GetInt("shipid")!.Value;

    internal static async Task PutFuelAsync(Rig rig, DroneHostTests.Flying flying, long quantity, int flag)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Items.CreateAsync(connection, transaction,
            new NewItem(StrontiumClathrates, flying.Character, ShipIdOf(flying), flag, quantity));
        await transaction.CommitAsync();
    }

    /// <summary>The units of the strontium the ship holds in one of its flags.</summary>
    private static async Task<long> QuantityAsync(Rig rig, DroneHostTests.Flying flying, int flag) =>
        (await rig.Items.InLocationAsync(ShipIdOf(flying), flying.Character))
            .Where(item => item.TypeId == StrontiumClathrates && item.Flag == flag)
            .Sum(item => item.Quantity);

    internal static SimModuleWorkCycle FuelCycle(DroneHostTests.Flying flying, long moduleId, ulong cycleId) => new(
        (ulong)flying.Session.SessionId, flying.Character, ShipIdOf(flying), flying.System, moduleId, 0, cycleId, SimModuleWorkCommands.ConsumeFuel,
        0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Sends a cycle and waits for the answer to it (a repeat waits for a second answer).</summary>
    internal static async Task<SimModuleWorkResult> SettleAsync(Rig rig, SimModuleWorkCycle cycle)
    {
        var answered = rig.Sim.Heard.OfType<SimModuleWorkResult>().Count(result => result.CycleId == cycle.CycleId);
        rig.Sim.Say(cycle);
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimModuleWorkResult>().Count(result => result.CycleId == cycle.CycleId) > answered);
        return rig.Sim.Heard.OfType<SimModuleWorkResult>().Last(result => result.CycleId == cycle.CycleId);
    }
}
