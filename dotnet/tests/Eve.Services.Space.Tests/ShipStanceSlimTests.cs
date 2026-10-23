using Eve.Inventory;
using Eve.Space;
using Eve.Services;

namespace Eve.Services.Space.Tests;

/// <summary>
/// A ship's stance reaches the simulation with the ship (protocol 55): the stance and the time of its switch, as the slim item's <c>shipStance</c> carries them
/// </summary>
public class ShipStanceSlimTests
{
    private const int ConfessorDefenceMode = 34319, StanceFlag = ItemFlags.HiddenModifiers;
    private const long SwitchedAtMs = 1_700_000_000_000;

    [PgFact]
    public async Task A_stance_of_a_ship_in_space_is_sent_to_the_simulation_with_the_time_it_switched()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig);
        var ship = (await rig.Characters.GetAsync(flying.Character))!.ShipId!.Value;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.CreateAsync(connection, transaction, new NewItem(ConfessorDefenceMode, flying.Character, ship, StanceFlag, 1, true,
                CustomInfo: $"shipStance:1:switchedAtMs:{SwitchedAtMs}"));
            await transaction.CommitAsync();
        }
        var heard = rig.Sim.Heard.Count;

        await rig.Space.RefreshShipAsync(flying.Session, CancellationToken.None);

        var sent = Assert.Single(rig.Sim.Heard.Skip(heard).OfType<SimShipChanged>());
        Assert.Equal(new SimShipStance(1, Py.FileTimeFromUnixMs(SwitchedAtMs), 1), sent.Ship.Stance);
    }

    [PgFact]
    public async Task A_ship_that_was_never_switched_has_no_stance_in_the_simulation()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig);
        var heard = rig.Sim.Heard.Count;

        await rig.Space.RefreshShipAsync(flying.Session, CancellationToken.None);

        var sent = Assert.Single(rig.Sim.Heard.Skip(heard).OfType<SimShipChanged>());
        Assert.Null(sent.Ship.Stance);
    }
}
