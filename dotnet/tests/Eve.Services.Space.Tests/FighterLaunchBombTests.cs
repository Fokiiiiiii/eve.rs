using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// A fighter's LaunchBomb through the host, against the database and the fake simulation: the squadron names a point (the client's point-targeted
/// activation), the bomb type of the squadron (SDE 2324) gives the blast its radius and four damages, the simulation is asked for the blast as a window's
/// end, and the security class of the system refuses it in high and low security (the ability's flags) but not in zero security.
/// </summary>
public sealed class FighterLaunchBombTests
{
    private const int SolarSystem = 30_000_142;
    private const long Target = 980_050_000_077;
    private const int Hornet = 40_362; // slot 2 is LaunchBomb (ability 7); the bomb is Micro Electron Bomb 41549 (EM 640, 15 km)
    private const int LaunchBombSlot = 2;

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) =>
        new() { Method = method, Args = args, Session = session };

    private static string UserMessage(PyValue payload) => new MachoError(MachoErrorType.WrappedException, payload).UserErrorMessage;

    /// <summary>The host with the system's security (a level), as a pilot in that system sees it.</summary>
    private static SpaceFighters Host(FakeSim sim, Rig rig, double security) =>
        new(sim, rig.Ships, rig.Statics, _ => SolarSystem, null, _ => security);

    /// <summary>A squadron item of the pilot, out in the solar system (as a launch leaves it).</summary>
    private static async Task<long> SquadronInSpaceAsync(Rig rig, long owner, int type)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await rig.Items.CreateAsync(connection, transaction, new NewItem(type, owner, SolarSystem, CharacterShips.InSpaceFlag, 1, false));
        await transaction.CommitAsync();
        return id;
    }

    private static string StateOf(long ship) => System.Text.Json.JsonSerializer.Serialize(new { Ship = ship, Tube = 159, Slots = new { } });

    [PgFact]
    public async Task A_launch_bomb_names_a_point_and_is_sent_to_the_simulation_as_a_blast_of_the_bombs_radius_and_damage()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Bomber Pilot");
        var session = await rig.SessionAsync(pilot);
        var squadron = await SquadronInSpaceAsync(rig, pilot.CharacterId, Hornet);
        await rig.Ships.SetSquadronStateAsync(pilot.CharacterId, squadron, StateOf(pilot.ShipId));
        var sim = new FakeSim();
        var fighters = Host(sim, rig, security: 0.0);
        await fighters.RebuildAsync(session, pilot.CharacterId);
        var point = new PyTuple([new PyReal(12_000d), new PyReal(-3_000d), new PyReal(45_000d)]);

        var reply = await fighters.CmdActivateAbilitySlotsAsync(Call("CmdActivateAbilitySlots", session, new PyList([new PyInt(squadron)]), new PyInt(LaunchBombSlot), point));

        Assert.IsType<PyNone>(Assert.Single(Assert.IsType<PyDict>(reply).Entries).Value);
        var activated = Assert.Single(sim.Heard.OfType<SimActivate>());
        Assert.Equal(0, activated.Effect.TargetId);
        Assert.Equal(60_000d, activated.Effect.DurationMs);
        var blast = Assert.IsType<SimAction.Blast>(activated.Effect.Action);
        Assert.Equal([12_000d, -3_000d, 45_000d], blast.Centre);
        Assert.Equal(15_000d, blast.RadiusMeters);
        Assert.Equal([640d, 0d, 0d, 0d], blast.Damage);
        Assert.True(blast.IsValid);
    }

    [PgFact]
    public async Task A_launch_bomb_with_no_point_is_refused_with_its_reason_and_nothing_is_sent()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Bomber Pilot");
        var session = await rig.SessionAsync(pilot);
        var squadron = await SquadronInSpaceAsync(rig, pilot.CharacterId, Hornet);
        await rig.Ships.SetSquadronStateAsync(pilot.CharacterId, squadron, StateOf(pilot.ShipId));
        var sim = new FakeSim();
        var fighters = Host(sim, rig, security: 0.0);
        await fighters.RebuildAsync(session, pilot.CharacterId);

        var reply = await fighters.CmdActivateAbilitySlotsAsync(Call("CmdActivateAbilitySlots", session, new PyList([new PyInt(squadron)]), new PyInt(LaunchBombSlot), new PyInt(Target)));

        Assert.Equal("That fighter ability requires a target point.", UserMessage(Assert.Single(Assert.IsType<PyDict>(reply).Entries).Value));
        Assert.Empty(sim.Heard.OfType<SimActivate>());
    }

    [PgFact]
    public async Task A_launch_bomb_is_refused_in_high_and_low_security_and_runs_in_zero_security()
    {
        var point = new PyTuple([new PyReal(0d), new PyReal(0d), new PyReal(20_000d)]);
        foreach (var (security, refusal) in new[] { (0.7, "That ability cannot be used in high security space."), (0.3, "That ability cannot be used in low security space.") })
        {
            await using var rig = await Rig.CreateAsync();
            var pilot = await rig.NewCharacterAsync("Bomber Pilot");
            var session = await rig.SessionAsync(pilot);
            var squadron = await SquadronInSpaceAsync(rig, pilot.CharacterId, Hornet);
            await rig.Ships.SetSquadronStateAsync(pilot.CharacterId, squadron, StateOf(pilot.ShipId));
            var sim = new FakeSim();
            var fighters = Host(sim, rig, security);
            await fighters.RebuildAsync(session, pilot.CharacterId);

            var reply = await fighters.CmdActivateAbilitySlotsAsync(Call("CmdActivateAbilitySlots", session, new PyList([new PyInt(squadron)]), new PyInt(LaunchBombSlot), point));

            Assert.Equal(refusal, UserMessage(Assert.Single(Assert.IsType<PyDict>(reply).Entries).Value));
            Assert.Empty(sim.Heard.OfType<SimActivate>());
        }
    }
}
