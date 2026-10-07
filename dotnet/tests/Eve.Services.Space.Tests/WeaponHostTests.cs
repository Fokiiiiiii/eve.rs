using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>A pilot starts the gun of its ship at what it has locked: the host works out what the gun shoots and the safety of the pilot decides where it may.</summary>
public class WeaponHostTests
{
    internal static async Task<ClientSession> InSpaceAsync(Rig rig, Eve.Characters.Created character, long id)
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
        List<Eve.Crimewatch.AggressionRequest> told = [];
        rig.Space.Aggression = (request, _) =>
        {
            told.Add(request);
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
        Assert.Equal((attacker.CharacterId, victim.CharacterId, ship, session.GetInt("solarsystemid")!.Value, false), (one.Attacker, one.Target, one.TargetShipId, one.SolarSystemId, one.TargetIsCapsule));
        Assert.True(one.Security > 0.9, "the system the pilots are in is safe: the security is what the penalty reads");
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
        rig.Space.Aggression = (_, _) => throw new InvalidOperationException("the database is gone");

        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, gun, "targetAttack", other.GetInt("shipid")!.Value, 1000, CancellationToken.None));
    }


    [PgTheory]
    [InlineData(true, false, false, false, false, false)]
    [InlineData(false, true, false, false, false, false)]
    [InlineData(false, false, true, false, false, false)]
    [InlineData(false, false, false, true, false, false)]
    [InlineData(false, false, false, false, true, false)]
    [InlineData(false, false, false, false, false, true)]
    public async Task Legal_war_engagement_or_target_status_allows_highsec_ship_attack(
        bool activeWar,
        bool activeLimitedEngagement,
        bool targetIsCriminal,
        bool targetIsSuspect,
        bool targetIsOutlaw,
        bool legalFriendlyFire)
    {
        await using var rig = await Rig.CreateAsync();
        var attacker = await rig.NewCharacterAsync("Space Tester", 1);
        var victim = await rig.NewCharacterAsync("Space Victim", 2);
        var session = await InSpaceAsync(rig, attacker, 1);
        var other = await InSpaceAsync(rig, victim, 2);
        var gun = await GunOfAsync(rig, attacker);
        rig.Space.SafetyLevelOf = (_, _) => Task.FromResult(Eve.Crimewatch.SafetyLevel.Full);
        var contextReads = 0;
        rig.Space.WeaponTargetContextOf = (attackerId, targetId, _) =>
        {
            Assert.Equal(attacker.CharacterId, attackerId);
            Assert.Equal(victim.CharacterId, targetId);
            contextReads++;
            return Task.FromResult(new WeaponTargetContext(
                AttackerIsPlayer: true,
                TargetIsPlayerProperty: true,
                ActiveWar: activeWar,
                ActiveLimitedEngagement: activeLimitedEngagement,
                TargetIsCriminal: targetIsCriminal,
                TargetIsSuspect: targetIsSuspect,
                TargetIsOutlaw: targetIsOutlaw,
                LegalFriendlyFire: legalFriendlyFire));
        };

        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(
            session, gun, "targetAttack", other.GetInt("shipid")!.Value, 1000, CancellationToken.None));
        Assert.Equal(1, contextReads);
    }


    [Fact]
    public void Highsec_player_ship_safety_allows_only_illegal_aggression_with_safety_disabled()
    {
        var target = new WeaponTargetContext(AttackerIsPlayer: true, TargetIsPlayerProperty: true);
        Assert.True(WeaponSafety.StopsFire(Eve.Universe.SecurityClass.HighSec, Eve.Crimewatch.SafetyLevel.Full, target));
        Assert.True(WeaponSafety.StopsFire(Eve.Universe.SecurityClass.HighSec, Eve.Crimewatch.SafetyLevel.Partial, target));
        Assert.False(WeaponSafety.StopsFire(Eve.Universe.SecurityClass.HighSec, Eve.Crimewatch.SafetyLevel.None, target));
        Assert.True(WeaponSafety.StopsFire(Eve.Universe.SecurityClass.HighSec, 99, target));
        Assert.False(WeaponSafety.StopsFire(Eve.Universe.SecurityClass.HighSec, -1, target));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void War_and_limited_engagement_allow_highsec_ship_attacks(bool activeWar, bool activeLimitedEngagement)
    {
        var target = new WeaponTargetContext(
            AttackerIsPlayer: true,
            TargetIsPlayerProperty: true,
            ActiveWar: activeWar,
            ActiveLimitedEngagement: activeLimitedEngagement);

        Assert.False(WeaponSafety.StopsFire(Eve.Universe.SecurityClass.HighSec, Eve.Crimewatch.SafetyLevel.Full, target));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Safety_does_not_block_a_ship_attack_when_either_side_is_an_npc(bool attackerIsPlayer, bool targetIsPlayer)
    {
        var target = new WeaponTargetContext(attackerIsPlayer, targetIsPlayer);

        Assert.False(WeaponSafety.StopsFire(Eve.Universe.SecurityClass.HighSec, Eve.Crimewatch.SafetyLevel.Full, target));
    }

    [Fact]
    public void Crimewatch_flags_outlaw_status_and_legal_friendly_fire_are_legal_targets()
    {
        var targets = new[]
        {
            new WeaponTargetContext(true, true, TargetIsCriminal: true),
            new WeaponTargetContext(true, true, TargetIsSuspect: true),
            new WeaponTargetContext(true, true, TargetIsOutlaw: true),
            new WeaponTargetContext(true, true, LegalFriendlyFire: true),
            new WeaponTargetContext(true, true, SamePilot: true),
        };

        Assert.All(targets, target =>
            Assert.False(WeaponSafety.StopsFire(Eve.Universe.SecurityClass.HighSec, Eve.Crimewatch.SafetyLevel.Full, target)));
    }

    [Fact]
    public void Lowsec_ship_and_capsule_use_suspect_and_criminal_safety_thresholds()
    {
        var ship = new WeaponTargetContext(true, true);
        Assert.True(WeaponSafety.StopsFire(Eve.Universe.SecurityClass.LowSec, Eve.Crimewatch.SafetyLevel.Full, ship));
        Assert.False(WeaponSafety.StopsFire(Eve.Universe.SecurityClass.LowSec, Eve.Crimewatch.SafetyLevel.Partial, ship));

        var capsule = new WeaponTargetContext(true, true, TargetIsCapsule: true);
        Assert.True(WeaponSafety.StopsFire(Eve.Universe.SecurityClass.LowSec, Eve.Crimewatch.SafetyLevel.Full, capsule));
        Assert.True(WeaponSafety.StopsFire(Eve.Universe.SecurityClass.LowSec, Eve.Crimewatch.SafetyLevel.Partial, capsule));
        Assert.False(WeaponSafety.StopsFire(Eve.Universe.SecurityClass.LowSec, Eve.Crimewatch.SafetyLevel.None, capsule));
        Assert.False(WeaponSafety.StopsFire(Eve.Universe.SecurityClass.ZeroSec, Eve.Crimewatch.SafetyLevel.Full, capsule));
    }


    [PgFact]
    public async Task A_negative_sensor_dampener_still_uses_safety_and_aggression()
    {
        const int SensorDampener = 1968, MidSlot = 19;
        await using var rig = await Rig.CreateAsync();
        var (session, attacker, module) = await RepairHostTests.InSpaceWithAsync(rig, SensorDampener, MidSlot);
        var victim = await rig.NewCharacterAsync("Space Victim", 2);
        var other = await InSpaceAsync(rig, victim, 2);
        var level = 2;
        var safetyChecks = 0;
        var aggression = new List<Eve.Crimewatch.AggressionRequest>();
        rig.Space.SafetyLevelOf = (_, _) =>
        {
            safetyChecks++;
            return Task.FromResult(level);
        };
        rig.Space.Aggression = (request, _) =>
        {
            aggression.Add(request);
            return Task.CompletedTask;
        };
        var target = other.GetInt("shipid")!.Value;
        var activations = rig.Sim.Heard.OfType<SimActivate>().Count();

        var refused = await rig.Space.ActivateAsync(session, module, "remoteSensorDampFalloff", target, 1000, CancellationToken.None);
        Assert.Equal("SafetyActivated", refused!.Reason);
        Assert.Equal(activations, rig.Sim.Heard.OfType<SimActivate>().Count());
        Assert.Equal(1, safetyChecks);
        Assert.Empty(aggression);

        level = 0;
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, module, "remoteSensorDampFalloff", target, 1000, CancellationToken.None));
        Assert.Equal(2, safetyChecks);
        var effect = Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect;
        var remote = Assert.IsType<SimAction.Remote>(effect.Action);
        Assert.Equal("ewRemoteSensorDamp", remote.Jam);
        var damp = Assert.IsType<SimRemote.Damp>(remote.What);
        Assert.True(damp.RangePercent < 0 && damp.ResolutionPercent < 0);

        var one = Assert.Single(aggression);
        Assert.Equal((attacker.CharacterId, victim.CharacterId, target), (one.Attacker, one.Target, one.TargetShipId));
    }


    [Fact]
    public void Weapon_safety_uses_the_shared_attack_policy_facts()
    {
        var target = new Eve.Services.Space.WeaponTargetContext(
            AttackerIsPlayer: true,
            TargetIsPlayerProperty: true,
            ActiveWar: true);
        var facts = Eve.Services.Space.WeaponSafety.PolicyFactsOf(target);
        var decision = Eve.Crimewatch.PolicyEngine.Evaluate(new Eve.Crimewatch.PolicyRequest(
            Eve.Crimewatch.PolicyAction.Attack,
            Eve.Universe.SecurityClass.HighSec,
            facts));

        Assert.True(facts.ActiveWar);
        Assert.True(decision.Allowed);
        Assert.True(decision.Lawful);
        Assert.Equal(Eve.Crimewatch.Flag.None, decision.Consequence);
    }
}
