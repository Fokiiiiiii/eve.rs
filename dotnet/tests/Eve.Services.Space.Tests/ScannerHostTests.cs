using Eve.Inventory;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The scanners' settle (protocol 40, ADR 0013): a Cargo Scanner's cycle reveals the locked ship's hold, a Ship Scanner's its fitted hardware, to the pilot who ran it,
/// once, and the target is told nothing. The simulation's answer is the lock and the reach; these tests drive the host's side through the work-cycle dispatcher.
/// </summary>
public class ScannerHostTests
{
    private const byte Cargo = SimModuleWorkCommands.ScanCargo;
    private const byte Ship = SimModuleWorkCommands.ScanShip;
    private const int Tritanium = 34;

    [PgFact]
    public async Task A_granted_cargo_scan_reveals_the_hold_of_the_locked_ship_once_and_its_fittings_are_not_in_it()
    {
        await using var rig = await Rig.CreateAsync();
        var (scanner, target, scannerType) = await ScannerAndTargetAsync(rig, "Cargo Scanner I");
        await HoldAsync(rig, target, Tritanium, 100, ItemFlags.Cargo);
        await HoldAsync(rig, target, scannerType, 1, ItemFlags.HiSlot0);
        rig.Sim.MeasureAnswer = 1_000;
        WorkCycleHostTests.StartModule(rig, scanner);

        var result = await WorkCycleHostTests.SettleAsync(rig, ScanCycle(scanner, target, 1, Cargo));
        await rig.Events.PumpAsync();

        Assert.True(result.Accepted, result.Reason);
        Assert.False(result.Complete, "the target stays: the cycle is done, not the target gone");
        var (shipId, rows) = Assert.Single(Revealed(scanner, "OnCargoScanComplete"));
        Assert.Equal(ShipOf(target), shipId);
        Assert.Equal(new[] { (Tritanium, 100L) }, rows);

        // a repeat of the cycle is answered from the record and reveals nothing again
        var again = await WorkCycleHostTests.SettleAsync(rig, ScanCycle(scanner, target, 1, Cargo));
        await rig.Events.PumpAsync();
        Assert.True(again.Accepted);
        Assert.Single(Revealed(scanner, "OnCargoScanComplete"));
    }

    [PgFact]
    public async Task A_granted_ship_scan_reveals_the_fitted_hardware_and_the_capacitor_of_the_locked_ship()
    {
        await using var rig = await Rig.CreateAsync();
        var (scanner, target, scannerType) = await ScannerAndTargetAsync(rig, "Ship Scanner I");
        await HoldAsync(rig, target, scannerType, 1, ItemFlags.HiSlot0);
        await HoldAsync(rig, target, Tritanium, 100, ItemFlags.Cargo);
        rig.Sim.MeasureAnswer = 1_000;
        WorkCycleHostTests.StartModule(rig, scanner);

        var result = await WorkCycleHostTests.SettleAsync(rig, ScanCycle(scanner, target, 2, Ship));
        await rig.Events.PumpAsync();

        Assert.True(result.Accepted, result.Reason);
        var (shipId, charge, capacity, rows) = Assert.Single(ShipScansOf(scanner));
        Assert.Equal(ShipOf(target), shipId);
        Assert.Equal(new[] { (scannerType, 1L) }, rows);
        Assert.True(capacity > 0 && charge == capacity, $"a full capacitor of {capacity} GJ, told as the charge: {charge}");
    }

    [PgFact]
    public async Task A_scan_out_of_reach_or_of_the_pilots_own_ship_is_refused_and_reveals_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        var (scanner, target, _) = await ScannerAndTargetAsync(rig, "Cargo Scanner I");
        await HoldAsync(rig, target, Tritanium, 100, ItemFlags.Cargo);
        WorkCycleHostTests.StartModule(rig, scanner);

        rig.Sim.MeasureAnswer = 1e9;
        var far = await WorkCycleHostTests.SettleAsync(rig, ScanCycle(scanner, target, 3, Cargo));
        Assert.Equal("TARGET_OUT_OF_RANGE", far.Reason);

        rig.Sim.MeasureAnswer = 1_000;
        var own = await WorkCycleHostTests.SettleAsync(rig, ScanCycle(scanner, scanner, 4, Cargo));
        Assert.Equal("TARGET_SELF", own.Reason);

        await rig.Events.PumpAsync();
        Assert.Empty(Revealed(scanner, "OnCargoScanComplete"));
    }

    /// <summary>A pilot with a scanner of <paramref name="scannerName"/> fitted, and a second pilot in the same system whose ship is the target.</summary>
    private static async Task<(DroneHostTests.Flying Scanner, DroneHostTests.Flying Target, int ScannerType)> ScannerAndTargetAsync(Rig rig, string scannerName)
    {
        var types = await new Eve.Sde.SdeReader(rig.Source).ReadTypeSummariesAsync();
        var scannerType = DroneHostTests.SdeTypeId(types, scannerName);
        var scanner = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: scannerType);
        // a second pilot needs its own session: the rig's sessions are keyed by their id
        var target = await DroneHostTests.FlyingWithADroneAsync(rig, characterName: "Scanned Pilot", sessionId: 2);
        Assert.Equal(scanner.System, target.System);
        await rig.Statics.EnsureTypesAsync([scannerType]);
        return (scanner, target, scannerType);
    }

    private static async Task HoldAsync(Rig rig, DroneHostTests.Flying ship, int typeId, long quantity, int flag)
    {
        var shipId = ship.Session.GetInt("shipid")!.Value;
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Items.CreateAsync(connection, transaction, new NewItem(typeId, ship.Character, shipId, flag, quantity, false));
        await transaction.CommitAsync();
    }

    private static long ShipOf(DroneHostTests.Flying pilot) => pilot.Session.GetInt("shipid")!.Value;

    private static SimModuleWorkCycle ScanCycle(DroneHostTests.Flying scanner, DroneHostTests.Flying target, ulong cycleId, byte command) => new(
        (ulong)scanner.Session.SessionId, scanner.Character, ShipOf(scanner), scanner.System, scanner.Module,
        ShipOf(target), cycleId, command, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>What the pilot was sent under <paramref name="name"/> with a cargo reveal: the scanned ship and its rows.</summary>
    private static IEnumerable<(long ShipId, IReadOnlyList<(int TypeId, long Quantity)> Rows)> Revealed(DroneHostTests.Flying pilot, string name) =>
        Events.Of(pilot.Session, clear: false).Where(e => e.Name == name).Select(e => (Events.Number(e.Args[0]), Rows(e.Args[1])));

    /// <summary>What the pilot was sent with a ship reveal: the scanned ship, the capacitor (its charge and capacity in GJ) and the fitted rows.</summary>
    private static IEnumerable<(long ShipId, double Charge, double Capacity, IReadOnlyList<(int TypeId, long Quantity)> Rows)> ShipScansOf(DroneHostTests.Flying pilot) =>
        Events.Of(pilot.Session, clear: false).Where(e => e.Name == "OnShipScanCompleted").Select(e =>
            (Events.Number(e.Args[0]), ((PyReal)e.Args[1]).Value, ((PyReal)e.Args[2]).Value, Rows(e.Args[3])));

    private static IReadOnlyList<(int TypeId, long Quantity)> Rows(PyValue list) =>
        [.. ((PyList)list).Items.Select(row => ((PyTuple)row).Items).Select(pair => ((int)Events.Number(pair[0]), Events.Number(pair[1])))];
}
