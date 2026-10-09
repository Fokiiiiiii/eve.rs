using Eve.Fitting;
using Eve.Inventory;
using Eve.Macho;
using Eve.Ships;

namespace Eve.Services.Space.Tests;

/// <summary>
/// Nanite paste repair in space, through the host: a start is held back while the hold has no paste, a module that runs is not repaired, and a stop ends
/// the record. A siege module (SDE 906, "prevent ejection") that runs holds the pilot in its ship: the eject is refused.
/// </summary>
public class ModuleRepairHostTests
{
    private const int NaniteRepairPaste = CharacterShips.NaniteRepairPasteTypeId;
    private const int SiegeModuleI = 20280;

    [PgFact]
    public async Task A_damaged_module_repairs_in_space_once_the_hold_has_the_paste_and_a_stop_ends_its_record()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: SiegeModuleI);
        await rig.Modules.SetModuleConditionsAsync(new Dictionary<long, ModuleCondition> { [flying.Module] = new(0.5, 0, 1, false) });

        // no paste in the hold: the repair does not start, and the client is told the paste is short
        var lacking = await rig.Space.StartRepairAsync(flying.Session, new[] { flying.Module }, CancellationToken.None);
        Assert.Empty(lacking!.Started);
        Assert.Equal(new[] { flying.Module }, lacking.MissingPaste);
        Assert.Empty(lacking.Failed);

        // the siege module has 999999 hit points: its whole repair is about 250000 units of paste
        await PutPasteAsync(rig, flying, 1_000_000);
        var started = await rig.Space.StartRepairAsync(flying.Session, new[] { flying.Module }, CancellationToken.None);
        Assert.Equal(new[] { flying.Module }, started!.Started);
        Assert.Empty(started.MissingPaste);

        // a second start of the same module is not a second repair
        var again = await rig.Space.StartRepairAsync(flying.Session, new[] { flying.Module }, CancellationToken.None);
        Assert.Equal(new[] { flying.Module }, again!.Started);

        var record = await rig.Space.EndRepairAsync(flying.Session, flying.Module, CancellationToken.None);
        Assert.Equal(flying.Module, record!.ModuleId);
        Assert.True(record.PasteUnits > 0);
        Assert.Null(await rig.Space.EndRepairAsync(flying.Session, flying.Module, CancellationToken.None));
    }

    [PgFact]
    public async Task A_module_that_runs_is_not_repaired_and_a_repair_is_not_started_for_a_module_that_is_not_damaged()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: SiegeModuleI);
        await PutPasteAsync(rig, flying, 1_000_000);

        // not damaged: nothing to mend, so the start is refused
        var undamaged = await rig.Space.StartRepairAsync(flying.Session, new[] { flying.Module }, CancellationToken.None);
        Assert.Equal(new[] { flying.Module }, undamaged!.Failed);

        await rig.Modules.SetModuleConditionsAsync(new Dictionary<long, ModuleCondition> { [flying.Module] = new(0.5, 0, 1, false) });
        ModuleFuelHostTests.StartModule(rig, flying);
        var running = await rig.Space.StartRepairAsync(flying.Session, new[] { flying.Module }, CancellationToken.None);
        Assert.Equal(new[] { flying.Module }, running!.Failed);
        Assert.Null(await rig.Space.EndRepairAsync(flying.Session, flying.Module, CancellationToken.None));
    }

    [PgFact]
    public async Task A_siege_module_that_runs_prevents_ejection_and_the_pilot_stays_in_its_ship()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, minerTypeId: SiegeModuleI);
        rig.Space.LifecycleBlockers = (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        var ship = flying.Session.GetInt("shipid");
        ModuleFuelHostTests.StartModule(rig, flying);

        await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.EjectAsync(flying.Session, CancellationToken.None).AsTask());

        Assert.Equal(ship, flying.Session.GetInt("shipid"));
    }

    private static async Task PutPasteAsync(Rig rig, DroneHostTests.Flying flying, long quantity)
    {
        var ship = flying.Session.GetInt("shipid")!.Value;
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Items.CreateAsync(connection, transaction, new NewItem(NaniteRepairPaste, flying.Character, ship, CharacterShips.CargoFlag, quantity));
        await transaction.CommitAsync();
    }
}
