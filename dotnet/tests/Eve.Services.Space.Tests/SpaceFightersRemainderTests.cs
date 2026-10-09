using System.Text.Json;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The fighter paths of the remainder (ADR 0015) through the host, against the database and the fake simulation: a kamikaze's detonation, a fighter's
/// missiles, the charges and cooldowns of an ability that survive a restart, a squadron let go whose size is written back to its item, and the squadrons
/// out in space taken up again. The squadrons are items in space: the restart paths need no launch tubes.
/// </summary>
public sealed class SpaceFightersRemainderTests
{
    private const int SolarSystem = 30_000_142;
    private const long Target = 980_050_000_077;
    private const int Templar = 23_055; // slot 0 AttackM, slot 1 microwarpdrive, slot 2 missiles
    private const int Shadow = 2_948; // slot 2 kamikaze

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) =>
        new() { Method = method, Args = args, Session = session };

    private static string UserMessage(PyValue payload) => new MachoError(MachoErrorType.WrappedException, payload).UserErrorMessage;

    private static SpaceFighters Host(FakeSim sim, Rig rig) => new(sim, rig.Ships, rig.Statics, _ => SolarSystem);

    /// <summary>A squadron item of the pilot, out in the solar system (as a launch leaves it).</summary>
    private static async Task<long> SquadronInSpaceAsync(Rig rig, long owner, int type, long quantity)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await rig.Items.CreateAsync(connection, transaction, new NewItem(type, owner, SolarSystem, CharacterShips.InSpaceFlag, quantity, false));
        await transaction.CommitAsync();
        return id;
    }

    /// <summary>The state a squadron keeps on its item: the ship it came from, and its ability slots (charges and cooldown end in Unix milliseconds).</summary>
    private static string StateOf(long ship, params (int Slot, int? Charges, long CooldownUntilMs)[] slots) => JsonSerializer.Serialize(new
    {
        Ship = ship,
        Tube = 159,
        Slots = slots.ToDictionary(slot => slot.Slot.ToString(), slot => new { Charges = slot.Charges, CooldownUntilMs = slot.CooldownUntilMs }),
    });

    /// <summary>Waits (up to five seconds) for a condition the host reaches in the background.</summary>
    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100 && !await condition(); attempt++)
        {
            await Task.Delay(50);
        }
    }

    [PgFact]
    public async Task A_kamikaze_squadron_closes_in_on_its_target_and_its_detonation_destroys_the_squadron_item()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Fighter Pilot");
        var session = await rig.SessionAsync(pilot);
        var squadron = await SquadronInSpaceAsync(rig, pilot.CharacterId, Shadow, 2);
        await rig.Ships.SetSquadronStateAsync(pilot.CharacterId, squadron, StateOf(pilot.ShipId));
        var sim = new FakeSim();
        var fighters = Host(sim, rig);
        await fighters.RebuildAsync(session, pilot.CharacterId);

        var reply = await fighters.CmdActivateAbilitySlotsAsync(Call("CmdActivateAbilitySlots", session, new PyList([new PyInt(squadron)]), new PyInt(2), new PyInt(Target)));
        Assert.IsType<PyNone>(Assert.Single(Assert.IsType<PyDict>(reply).Entries).Value);

        var activated = Assert.Single(sim.Heard.OfType<SimActivate>());
        var kamikaze = Assert.IsType<SimAction.Kamikaze>(activated.Effect.Action);
        Assert.Equal(4, kamikaze.Damage.Count);
        Assert.True(kamikaze.Damage.Sum() > 0 && kamikaze.RangeMeters > 0, "the detonation carries the squadron's damage and its range");

        // the squadron is destroyed by its own detonation: its item leaves space
        sim.Say(new SimDestroyed(1UL << 62, squadron, 0, [0, 0, 0], [1, 0, 0], 6, 0));
        await WaitAsync(async () => (await rig.Ships.FightersInSpaceAsync(pilot.CharacterId, SolarSystem)).All(item => item.Id != squadron));
        Assert.DoesNotContain(await rig.Ships.FightersInSpaceAsync(pilot.CharacterId, SolarSystem), item => item.Id == squadron);
    }

    [PgFact]
    public async Task A_squadron_fires_its_missiles_as_missiles_with_a_reach_and_an_explosion_and_not_as_a_doomsday()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Fighter Pilot");
        var session = await rig.SessionAsync(pilot);
        var squadron = await SquadronInSpaceAsync(rig, pilot.CharacterId, Templar, 3);
        await rig.Ships.SetSquadronStateAsync(pilot.CharacterId, squadron, StateOf(pilot.ShipId));
        var sim = new FakeSim();
        var fighters = Host(sim, rig);
        await fighters.RebuildAsync(session, pilot.CharacterId);

        // slot 2 is the missiles, slot 0 the attack missile (AttackM)
        foreach (var slot in new[] { 0, 2 })
        {
            var reply = await fighters.CmdActivateAbilitySlotsAsync(Call("CmdActivateAbilitySlots", session, new PyList([new PyInt(squadron)]), new PyInt(slot), new PyInt(Target)));
            Assert.IsType<PyNone>(Assert.Single(Assert.IsType<PyDict>(reply).Entries).Value);
        }

        var missiles = sim.Heard.OfType<SimActivate>().Select(activate => activate.Effect.Action).ToList();
        Assert.Equal(2, missiles.Count);
        foreach (var action in missiles)
        {
            var missile = Assert.IsType<SimAction.Missile>(action);
            Assert.Equal(4, missile.Damage.Count);
            Assert.True(missile.ReachMeters > 0 && missile.ExplosionRadius > 0, "a missile has a reach and an explosion");
        }
    }

    [PgFact]
    public async Task An_ability_keeps_its_charges_and_its_cooldown_across_a_restart()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Fighter Pilot");
        var session = await rig.SessionAsync(pilot);
        var squadron = await SquadronInSpaceAsync(rig, pilot.CharacterId, Templar, 3);
        // one charge left in the missiles
        await rig.Ships.SetSquadronStateAsync(pilot.CharacterId, squadron, StateOf(pilot.ShipId, (2, 1, 0)));
        var sim = new FakeSim();
        var first = Host(sim, rig);
        await first.RebuildAsync(session, pilot.CharacterId);
        var fired = await first.CmdActivateAbilitySlotsAsync(Call("CmdActivateAbilitySlots", session, new PyList([new PyInt(squadron)]), new PyInt(2), new PyInt(Target)));
        Assert.IsType<PyNone>(Assert.Single(Assert.IsType<PyDict>(fired).Entries).Value);
        // the host keeps the spent charge on the item
        await WaitAsync(async () => (await rig.Items.GetAsync(squadron))?.CustomInfo?.Contains("\"Charges\":0") == true);

        // a host restart: a new host takes the squadron up again, and the missiles are out of charges
        var second = Host(sim, rig);
        await second.RebuildAsync(session, pilot.CharacterId);
        var refused = await second.CmdActivateAbilitySlotsAsync(Call("CmdActivateAbilitySlots", session, new PyList([new PyInt(squadron)]), new PyInt(2), new PyInt(Target)));
        Assert.Equal("That ability has no charges left.", UserMessage(Assert.Single(Assert.IsType<PyDict>(refused).Entries).Value));

        // a cooldown that was running before the restart still runs after it
        var cooling = await SquadronInSpaceAsync(rig, pilot.CharacterId, Templar, 1);
        var hourFromNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3_600_000;
        await rig.Ships.SetSquadronStateAsync(pilot.CharacterId, cooling, StateOf(pilot.ShipId, (1, null, hourFromNow)));
        var third = Host(sim, rig);
        await third.RebuildAsync(session, pilot.CharacterId);
        var cooled = await third.CmdActivateAbilitySlotsAsync(Call("CmdActivateAbilitySlots", session, new PyList([new PyInt(cooling)]), new PyInt(1), new PyInt(Target)));
        Assert.Equal("That ability is still cooling down.", UserMessage(Assert.Single(Assert.IsType<PyDict>(cooled).Entries).Value));
    }

    [PgFact]
    public async Task A_squadron_let_go_that_loses_members_keeps_its_new_size_on_its_item()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Fighter Pilot");
        var session = await rig.SessionAsync(pilot);
        // the squadron came from another ship, so the pilot's ship does not control it: it is let go on the restart
        var squadron = await SquadronInSpaceAsync(rig, pilot.CharacterId, Shadow, 4);
        await rig.Ships.SetSquadronStateAsync(pilot.CharacterId, squadron, StateOf(pilot.ShipId + 1));
        var sim = new FakeSim();
        var fighters = Host(sim, rig);
        await fighters.RebuildAsync(session, pilot.CharacterId);
        var order = Assert.Single(sim.Heard.OfType<SimFighterOrder>());
        Assert.Equal(SimFighterOrders.Abandon, order.Command);

        sim.Say(new SimFighterReleased(order.SessionId, squadron, [0, 0, 0]));
        sim.Say(new SimFighterSize(0, squadron, 2));

        await WaitAsync(async () => (await rig.Items.GetAsync(squadron))?.Quantity == 2);
        Assert.Equal(2, (await rig.Items.GetAsync(squadron))!.Quantity);
    }

    [PgFact]
    public async Task A_restarted_host_takes_up_the_controlled_squadrons_and_lets_go_the_others()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Fighter Pilot");
        var session = await rig.SessionAsync(pilot);
        var mine = await SquadronInSpaceAsync(rig, pilot.CharacterId, Templar, 3);
        var others = await SquadronInSpaceAsync(rig, pilot.CharacterId, Shadow, 2);
        await rig.Ships.SetSquadronStateAsync(pilot.CharacterId, mine, StateOf(pilot.ShipId));
        await rig.Ships.SetSquadronStateAsync(pilot.CharacterId, others, StateOf(pilot.ShipId + 1));
        var sim = new FakeSim();
        var fighters = Host(sim, rig);

        await fighters.RebuildAsync(session, pilot.CharacterId);

        var launches = sim.Heard.OfType<SimLaunchFighter>().Select(launch => launch.SquadronSize).OrderBy(size => size).ToList();
        Assert.Equal(new[] { 2, 3 }, launches);
        Assert.Single(sim.Heard.OfType<SimFighterOrder>(), order => order.Command == SimFighterOrders.Abandon);
        // the controlled squadron answers its abilities again
        var reply = await fighters.CmdActivateAbilitySlotsAsync(Call("CmdActivateAbilitySlots", session, new PyList([new PyInt(mine)]), new PyInt(2), new PyInt(Target)));
        Assert.IsType<PyNone>(Assert.Single(Assert.IsType<PyDict>(reply).Entries).Value);
    }
}
