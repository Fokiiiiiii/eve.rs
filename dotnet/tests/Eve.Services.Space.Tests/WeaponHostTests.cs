using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>A pilot starts the gun of its ship at what it has locked: the host works out what the gun shoots and the safety of the pilot decides where it may.</summary>
public class WeaponHostTests
{
    private static async Task<ClientSession> InSpaceAsync(Rig rig, Eve.Characters.Created character, long id)
    {
        var session = await rig.SessionAsync(character, id);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        Events.Of(session);
        return session;
    }

    private static async Task<long> GunOfAsync(Rig rig, Eve.Characters.Created character) =>
        (await rig.Ships.LoadAsync(character.CharacterId, CancellationToken.None))!.Fitted.Single(m => m.TypeId == 3638).Id;

    [PgFact]
    public async Task A_gun_is_started_at_a_ship_nobody_flies_and_the_simulation_is_given_what_it_shoots()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = await InSpaceAsync(rig, character, 1);
        var gun = await GunOfAsync(rig, character);

        var answer = await rig.Space.ActivateAsync(session, gun, "targetAttack", 980050000105, 1000, CancellationToken.None);

        Assert.Equal(ModuleResult.Done, answer);
        var heard = Assert.IsType<SimActivate>(rig.Sim.Heard.Last());
        Assert.Equal((gun, 980050000105), (heard.Effect.ModuleId, heard.Effect.TargetId));
        var turret = Assert.IsType<SimTurret>(heard.Effect.Turret);
        Assert.Equal((3638, "effects.Laser"), (turret.WeaponTypeId, turret.Fx));
        // the firing ends the protection of the undocked ship, as a movement does
        Assert.Equal(["OnInvulnCancelled"], Events.Of(session).Select(e => e.Name));
    }

    [PgFact]
    public async Task A_gun_is_not_started_at_the_ship_of_another_pilot_in_empire_space_while_the_safety_is_full()
    {
        await using var rig = await Rig.CreateAsync();
        var attacker = await rig.NewCharacterAsync("Space Tester", 1);
        var victim = await rig.NewCharacterAsync("Space Victim", 2);
        var session = await InSpaceAsync(rig, attacker, 1);
        var other = await InSpaceAsync(rig, victim, 2);
        var gun = await GunOfAsync(rig, attacker);
        var heard = rig.Sim.Heard.Count;

        var answer = await rig.Space.ActivateAsync(session, gun, "targetAttack", other.GetInt("shipid")!.Value, 1000, CancellationToken.None);

        Assert.Equal(ModuleResult.Refusal("SafetyActivated", 3638, "Civilian Gatling Railgun"), answer);
        // the simulation was never asked, and the pilot keeps its protection
        Assert.Equal(heard, rig.Sim.Heard.Count);
        Assert.Empty(Events.Of(session));
    }
}
