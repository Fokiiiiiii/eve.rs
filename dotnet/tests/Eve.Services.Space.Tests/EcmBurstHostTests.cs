using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>The host's side of an ECM burst (FP-C7): the simulation says which ships the burst reached, and the attack is recorded on each pilot among them.</summary>
public class EcmBurstHostTests
{
    [PgFact]
    public async Task An_ECM_burst_records_the_attack_on_each_pilot_it_reached_and_skips_a_ship_that_is_no_pilots()
    {
        await using var rig = await Rig.CreateAsync();
        var burster = await rig.NewCharacterAsync("Burst Pilot", 1);
        var session = await WeaponHostTests.InSpaceAsync(rig, burster, 1);
        var victim = await rig.NewCharacterAsync("Burst Victim", 2);
        var other = await WeaponHostTests.InSpaceAsync(rig, victim, 2);
        var aggression = new List<Eve.Crimewatch.AggressionRequest>();
        rig.Space.Aggression = (request, _) =>
        {
            aggression.Add(request);
            return Task.CompletedTask;
        };
        var victimShip = other.GetInt("shipid")!.Value;

        rig.Sim.Say(new SimEcmBurst(
            (ulong)session.SessionId,
            session.GetInt("shipid")!.Value,
            [new SimBurstReach(987654321, true), new SimBurstReach(victimShip, false)]));

        await Rig.UntilAsync(() => aggression.Count > 0);
        var one = Assert.Single(aggression);
        Assert.Equal((burster.CharacterId, victim.CharacterId, victimShip), (one.Attacker, one.Target, one.TargetShipId));
    }

    [PgFact]
    public async Task An_ECM_burst_is_refused_in_high_security_space_unless_the_pilots_safety_is_none()
    {
        const int BurstJammerI = 580, MidSlot = 19, Raven = 638;
        await using var rig = await Rig.CreateAsync();
        var (session, _, module) = await RepairHostTests.InSpaceWithAsync(rig, BurstJammerI, MidSlot, Raven);
        var level = Eve.Crimewatch.SafetyLevel.Full;
        rig.Space.SafetyLevelOf = (_, _) => Task.FromResult(level);
        var activations = rig.Sim.Heard.OfType<SimActivate>().Count();

        var refused = await rig.Space.ActivateAsync(session, module, "ecmBurstJammer", 0, 1000, CancellationToken.None);
        Assert.Equal("SafetyActivated", refused!.Reason);
        Assert.Equal(activations, rig.Sim.Heard.OfType<SimActivate>().Count());

        level = Eve.Crimewatch.SafetyLevel.None;
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, module, "ecmBurstJammer", 0, 1000, CancellationToken.None));
        Assert.IsType<SimAction.EcmBurst>(Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect.Action);
    }

    [PgFact]
    public async Task An_ECM_burst_from_a_ship_that_is_not_the_pilots_in_space_is_not_recorded()
    {
        await using var rig = await Rig.CreateAsync();
        var burster = await rig.NewCharacterAsync("Burst Pilot", 1);
        var session = await WeaponHostTests.InSpaceAsync(rig, burster, 1);
        var victim = await rig.NewCharacterAsync("Burst Victim", 2);
        var other = await WeaponHostTests.InSpaceAsync(rig, victim, 2);
        var aggression = new List<Eve.Crimewatch.AggressionRequest>();
        rig.Space.Aggression = (request, _) =>
        {
            aggression.Add(request);
            return Task.CompletedTask;
        };

        rig.Sim.Say(new SimEcmBurst((ulong)session.SessionId, session.GetInt("shipid")!.Value + 1, [new SimBurstReach(other.GetInt("shipid")!.Value, true)]));
        rig.Sim.Say(new SimEcmBurst((ulong)session.SessionId, session.GetInt("shipid")!.Value, [new SimBurstReach(other.GetInt("shipid")!.Value, true)]));

        await Rig.UntilAsync(() => aggression.Count > 0);
        await Task.Delay(50);
        Assert.Single(aggression);
    }
}
