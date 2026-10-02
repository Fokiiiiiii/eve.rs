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

    [PgFact]
    public async Task A_gun_is_started_at_the_ship_of_another_pilot_where_the_pilots_safety_allows_the_crime()
    {
        await using var rig = await Rig.CreateAsync();
        var attacker = await rig.NewCharacterAsync("Space Tester", 1);
        var victim = await rig.NewCharacterAsync("Space Victim", 2);
        var session = await InSpaceAsync(rig, attacker, 1);
        var other = await InSpaceAsync(rig, victim, 2);
        var gun = await GunOfAsync(rig, attacker);
        var level = 2;
        rig.Space.SafetyLevelOf = (_, _) => Task.FromResult(level);
        // in high security the aggression would make the pilot a criminal: only the safety none lets it, partial does not
        level = 1;
        Assert.Equal(ModuleResult.Refusal("SafetyActivated", 3638, "Civilian Gatling Railgun"), await rig.Space.ActivateAsync(session, gun, "targetAttack", other.GetInt("shipid")!.Value, 1000, CancellationToken.None));
        level = 0;
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, gun, "targetAttack", other.GetInt("shipid")!.Value, 1000, CancellationToken.None));
        Assert.IsType<SimActivate>(rig.Sim.Heard.Last());
    }

    [PgFact]
    public async Task A_gun_the_simulation_accepts_at_the_ship_of_another_pilot_is_told_to_crimewatch_once_and_a_refused_one_or_one_at_a_ship_nobody_flies_is_not()
    {
        await using var rig = await Rig.CreateAsync();
        var attacker = await rig.NewCharacterAsync("Space Tester", 1);
        var victim = await rig.NewCharacterAsync("Space Victim", 2);
        var session = await InSpaceAsync(rig, attacker, 1);
        var other = await InSpaceAsync(rig, victim, 2);
        var gun = await GunOfAsync(rig, attacker);
        List<(long Attacker, long Target, long System, Eve.Universe.SecurityClass Where, bool Capsule)> told = [];
        rig.Space.Aggression = (by, at, system, where, capsule, _) =>
        {
            told.Add((by, at, system, where, capsule));
            return Task.CompletedTask;
        };
        var level = 2;
        rig.Space.SafetyLevelOf = (_, _) => Task.FromResult(level);
        var ship = other.GetInt("shipid")!.Value;

        // the safety stops it: nothing was started, nothing is told
        Assert.Equal("SafetyActivated", (await rig.Space.ActivateAsync(session, gun, "targetAttack", ship, 1000, CancellationToken.None))!.Reason);
        // a ship nobody flies may be shot anywhere: no pilot, no crime
        level = 0;
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, gun, "targetAttack", 980050000105, 1000, CancellationToken.None));
        Assert.Empty(told);
        // the simulation refuses the gun: it did not start
        rig.Sim.ActivateAnswer = new CommandReply(false, 0, "TARGET_NOT_LOCKED");
        Assert.False((await rig.Space.ActivateAsync(session, gun, "targetAttack", ship, 1000, CancellationToken.None))!.Ok);
        Assert.Empty(told);

        rig.Sim.ActivateAnswer = new CommandReply(true, 0, null);
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, gun, "targetAttack", ship, 1000, CancellationToken.None));

        var one = Assert.Single(told);
        Assert.Equal((attacker.CharacterId, victim.CharacterId, session.GetInt("solarsystemid")!.Value, false), (one.Attacker, one.Target, one.System, one.Capsule));
    }

    [PgFact]
    public async Task A_failure_of_crimewatch_does_not_stop_a_gun_the_simulation_started()
    {
        await using var rig = await Rig.CreateAsync();
        var attacker = await rig.NewCharacterAsync("Space Tester", 1);
        var victim = await rig.NewCharacterAsync("Space Victim", 2);
        var session = await InSpaceAsync(rig, attacker, 1);
        var other = await InSpaceAsync(rig, victim, 2);
        var gun = await GunOfAsync(rig, attacker);
        rig.Space.SafetyLevelOf = (_, _) => Task.FromResult(0);
        rig.Space.Aggression = (_, _, _, _, _, _) => throw new InvalidOperationException("the database is gone");

        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, gun, "targetAttack", other.GetInt("shipid")!.Value, 1000, CancellationToken.None));
    }
}
