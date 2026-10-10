using Eve.Inventory;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>A pilot starts a repairer or a booster of its own ship: the host works out what a cycle mends and what it costs, and the simulation does the rest.</summary>
public class RepairHostTests
{
    private const int SmallArmorRepairer = 523, SmallShieldBooster = 399, SmallHullRepairer = 524;
    private const int LowSlot = 11, MidSlot = 19;
    private const int CapacitorNeed = 6, Duration = 73, ShieldBonus = 68, StructureDamageAmount = 83, ArmorDamageAmount = 84;

    /// <summary>A pilot in space whose ship carries the module (online), fitted to the slot.</summary>
    internal static async Task<(ClientSession Session, Eve.Characters.Created Character, long Module)> InSpaceWithAsync(
        Rig rig, int typeId, int flag, int? shipTypeId = null, string characterName = "Space Tester", int sessionId = 1)
    {
        var character = await rig.NewCharacterAsync(characterName);
        long shipId = character.ShipId;
        if (shipTypeId is { } replacementTypeId)
        {
            var replacement = (await rig.Ships.GrantAsync(character.CharacterId, replacementTypeId, 1, singleton: true))!.Single().After;
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await rig.Characters.SetShipAsync(connection, transaction, character.CharacterId, replacement.Id);
            await transaction.CommitAsync();
            character = character with { ShipId = replacement.Id };
            shipId = replacement.Id;
        }
        else
        {
            Assert.IsType<Eve.Ships.ShipChanged>(
                await rig.Ships.MoveAsync(character.CharacterId, character.ShipId + 3, character.ShipId, Rig.Station, ItemFlags.Hangar, null));
        }
        var granted = (await rig.Ships.GrantAsync(character.CharacterId, typeId, 1, singleton: true))!.Single().After;
        await TrainAsync(rig, character.CharacterId);
        // move any modules already in the selected hull's slot back to the hangar
        foreach (var occupant in (await rig.Items.InLocationAsync(shipId, character.CharacterId)).Where(i => i.Flag == flag))
        {
            Changed(await rig.Ships.MoveAsync(character.CharacterId, occupant.Id, shipId, Rig.Station, ItemFlags.Hangar, null));
        }
        Changed(await rig.Ships.MoveAsync(character.CharacterId, granted.Id, Rig.Station, shipId, flag, 1), $"type {typeId} flag {flag}");
        Changed(await rig.Ships.SetOnlineAsync(character.CharacterId, shipId, granted.Id, true));
        var session = await rig.SessionAsync(character, sessionId);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        Events.Of(session);
        return (session, character, granted.Id);
    }

    /// <summary>The skills the modules need (Mechanics, Repair Systems, Shield Operation, Shield Management, Engineering and the emission and remote repair systems), all to level 5: what is tested is not the fitting rules.</summary>
    private static async Task TrainAsync(Rig rig, long character)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Skills.GrantAsync(connection, transaction, character, new[] { 3392, 3393, 3416, 3419, 3413, 3426, 3318, 3422, 3423, 16069, 27902, 3435, 3449, 3433, 19921, 3427, 3434, 3421, 21603, 62450 }.Select(type => new Eve.Skills.CharacterSkill(type, 5, 0)));
        await transaction.CommitAsync();
    }

    private static void Changed(Eve.Ships.ShipOutcome outcome, string what = "") =>
        Assert.True(outcome is Eve.Ships.ShipChanged, outcome is Eve.Ships.ShipRefused refused ? $"{what} {refused.Reason} {refused.Fit} {refused.Detail}" : outcome.ToString());

    private static async Task<double> ValueAsync(Rig rig, Eve.Characters.Created character, long module, int attribute) =>
        (await rig.Ships.LoadAsync(character.CharacterId, CancellationToken.None))!.World.Get(module, attribute);

    [PgFact]
    public async Task An_armor_repairer_is_started_with_the_points_a_cycle_mends_its_length_and_its_cost_from_dogma()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, module) = await InSpaceWithAsync(rig, SmallArmorRepairer, LowSlot);

        var answer = await rig.Space.ActivateAsync(session, module, "armorRepair", 0, 1000, CancellationToken.None);

        Assert.Equal(ModuleResult.Done, answer);
        var effect = Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect;
        Assert.Equal((module, "effects.ArmorRepair", 0L), (effect.ModuleId, effect.Fx, effect.TargetId));
        Assert.Null(effect.Turret);
        // what a cycle mends, how long it takes and what it costs are the module's own numbers after dogma, and the armor is mended as the cycle ends
        Assert.Equal(new SimAction.Repair(SimLayer.Armor, Math.Round(await ValueAsync(rig, character, module, ArmorDamageAmount), 6), true), effect.Action);
        Assert.Equal(await ValueAsync(rig, character, module, Duration), effect.DurationMs, 3);
        Assert.Equal(await ValueAsync(rig, character, module, CapacitorNeed), effect.CapacitorNeed, 3);
        Assert.True(effect.DurationMs > 0 && effect.CapacitorNeed > 0 && ((SimAction.Repair)effect.Action!).Amount > 0);
        // the client is told the cost and the length as for any generic module
        Assert.Equal([CapacitorNeed, Duration], effect.OnChanges.Select(c => c.AttributeId));
    }

    [PgFact]
    public async Task A_shield_booster_gives_its_points_as_the_cycle_begins_and_a_hull_repairer_mends_the_hull_as_it_ends()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, booster) = await InSpaceWithAsync(rig, SmallShieldBooster, MidSlot);
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, booster, "shieldBoosting", 0, 1000, CancellationToken.None));
        var effect = Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect;
        Assert.Equal(new SimAction.Repair(SimLayer.Shield, Math.Round(await ValueAsync(rig, character, booster, ShieldBonus), 6), false), effect.Action);
        Assert.Equal("effects.ShieldBoosting", effect.Fx);

        await using var other = await Rig.CreateAsync();
        var (session2, character2, hull) = await InSpaceWithAsync(other, SmallHullRepairer, MidSlot);
        Assert.Equal(ModuleResult.Done, await other.Space.ActivateAsync(session2, hull, "structureRepair", 0, 1000, CancellationToken.None));
        var hullEffect = Assert.IsType<SimActivate>(other.Sim.Heard.Last()).Effect;
        Assert.Equal(new SimAction.Repair(SimLayer.Structure, Math.Round(await ValueAsync(other, character2, hull, StructureDamageAmount), 6), true), hullEffect.Action);
    }
}

/// <summary>A pilot starts a module that is projected at another ship: the host works out what a cycle does to the target, how far it reaches and what it costs, and the simulation does the rest.</summary>
public class ProjectedHostTests
{
    private const int HighSlot = 27;
    private const int RemoteArmorRepairer = 11355, RemoteShieldBooster = 3586, EnergyNeutralizer = 533, EnergyNosferatu = 530, CapacitorTransmitter = 529;
    private const long Target = 1_000_000_000_042;

    private static async Task<(Rig Rig, ClientSession Session, Eve.Characters.Created Character, long Module)> ReadyAsync(
        int typeId, int slot = HighSlot, int? shipTypeId = null)
    {
        var rig = await Rig.CreateAsync();
        WireRemoteAssistance(rig);
        var (session, character, module) = await RepairHostTests.InSpaceWithAsync(rig, typeId, slot, shipTypeId);
        return (rig, session, character, module);
    }

    private static void WireRemoteAssistance(Rig rig)
    {
        rig.Space.RemoteAssistancePolicyOf = (request, safetyLevel, _) =>
        {
            var facts = new Eve.Crimewatch.PolicyFacts(
                AttackerIsPlayer: request.Attacker > 0,
                TargetIsPlayerProperty: request.Target > 0,
                SamePilot: request.Attacker == request.Target,
                TargetIsCapsule: request.TargetIsCapsule);
            return Task.FromResult(Eve.Crimewatch.PolicyEngine.Evaluate(new Eve.Crimewatch.PolicyRequest(
                Eve.Crimewatch.PolicyAction.RemoteAssistance, request.Where, facts, safetyLevel)));
        };
        rig.Space.RecordRemoteAssistance = (_, _) => Task.CompletedTask;
    }

    private static async Task<double> ValueAsync(Rig rig, Eve.Characters.Created character, long module, int attribute) =>
        (await rig.Ships.LoadAsync(character.CharacterId, CancellationToken.None))!.World.Get(module, attribute);

    [PgFact]
    public async Task A_remote_armor_repairer_is_started_at_its_target_with_the_points_the_reach_and_the_cost_from_dogma()
    {
        var (rig, session, character, module) = await ReadyAsync(RemoteArmorRepairer);
        await using var _ = rig;
        var targetCharacter = await rig.NewCharacterAsync("Remote armor target", 2);
        var targetSession = await WeaponHostTests.InSpaceAsync(rig, targetCharacter, 2);
        var targetId = targetSession.GetInt("shipid")!.Value;

        var answer = await rig.Space.ActivateAsync(session, module, "shipModuleRemoteArmorRepairer", targetId, 1000, CancellationToken.None);

        Assert.Equal(ModuleResult.Done, answer);
        var effect = Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect;
        Assert.Equal((module, targetId), (effect.ModuleId, effect.TargetId));
        Assert.Null(effect.Turret);
        var remote = Assert.IsType<SimAction.Remote>(effect.Action);
        // the armor comes as the cycle ends, as many points as the module says (49 for a small one), up to its range (7 km) and over its falloff (2.5 km)
        Assert.True(remote.AtCycleEnd);
        Assert.Equal("remoteArmorRepair", remote.Jam);
        Assert.Equal(new SimRemote.Repair(SimLayer.Armor, Math.Round(await ValueAsync(rig, character, module, 84), 6)), remote.What);
        Assert.Equal((7000d, 2500d), (remote.Optimal, remote.Falloff));
        Assert.Equal(await ValueAsync(rig, character, module, 73), effect.DurationMs, 3);
        Assert.True(effect.CapacitorNeed > 0 && effect.DurationMs > 0);
        // the client is told the cost, the length and the range
        Assert.Equal([6, 73, 54], effect.OnChanges.Select(c => c.AttributeId));
    }

    [PgFact]
    public async Task A_remote_shield_booster_and_a_capacitor_transmitter_give_as_the_cycle_begins()
    {
        var (rig, session, character, booster) = await ReadyAsync(RemoteShieldBooster);
        await using var _ = rig;
        var targetCharacter = await rig.NewCharacterAsync("Remote shield target", 2);
        var targetSession = await WeaponHostTests.InSpaceAsync(rig, targetCharacter, 2);
        var targetId = targetSession.GetInt("shipid")!.Value;
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, booster, "shipModuleRemoteShieldBooster", targetId, 1000, CancellationToken.None));
        var effect = Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect;
        var remote = Assert.IsType<SimAction.Remote>(effect.Action);
        Assert.False(remote.AtCycleEnd);
        Assert.Equal("shieldTransfer", remote.Jam);
        Assert.Equal(new SimRemote.Repair(SimLayer.Shield, Math.Round(await ValueAsync(rig, character, booster, 68), 6)), remote.What);

        var (other, session2, character2, transmitter) = await ReadyAsync(CapacitorTransmitter);
        await using var _other = other;
        var targetCharacter2 = await other.NewCharacterAsync("Capacitor target", 2);
        var targetSession2 = await WeaponHostTests.InSpaceAsync(other, targetCharacter2, 2);
        var targetId2 = targetSession2.GetInt("shipid")!.Value;
        Assert.Equal(ModuleResult.Done, await other.Space.ActivateAsync(session2, transmitter, "shipModuleRemoteCapacitorTransmitter", targetId2, 1000, CancellationToken.None));
        var transmit = Assert.IsType<SimAction.Remote>(Assert.IsType<SimActivate>(other.Sim.Heard.Last()).Effect.Action);
        Assert.False(transmit.AtCycleEnd);
        Assert.Equal(new SimRemote.Transfer(Math.Round(await ValueAsync(other, character2, transmitter, 90), 6)), transmit.What);
        // a transmitter has no falloff: it reaches its range and no more
        Assert.Equal((3500d, 0d), (transmit.Optimal, transmit.Falloff));
    }

    [PgFact]
    public async Task A_neutralizer_takes_as_the_cycle_begins_and_a_nosferatu_as_it_ends_both_are_attacks()
    {
        var (rig, session, character, neutralizer) = await ReadyAsync(EnergyNeutralizer);
        await using var _ = rig;
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, neutralizer, "energyNeutralizerFalloff", Target, 1000, CancellationToken.None));
        var neut = Assert.IsType<SimAction.Remote>(Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect.Action);
        Assert.False(neut.AtCycleEnd);
        Assert.Equal("ewEnergyNeut", neut.Jam);
        Assert.Equal(new SimRemote.Neutralize(Math.Round(await ValueAsync(rig, character, neutralizer, 97), 6), 0), neut.What);
        Assert.Equal((4000d, 2000d), (neut.Optimal, neut.Falloff));

        var (other, session2, character2, nosferatu) = await ReadyAsync(EnergyNosferatu);
        await using var _other = other;
        Assert.Equal(ModuleResult.Done, await other.Space.ActivateAsync(session2, nosferatu, "energyNosferatuFalloff", Target, 1000, CancellationToken.None));
        var vampire = Assert.IsType<SimAction.Remote>(Assert.IsType<SimActivate>(other.Sim.Heard.Last()).Effect.Action);
        Assert.True(vampire.AtCycleEnd);
        Assert.Equal("ewEnergyVampire", vampire.Jam);
        Assert.Equal(new SimRemote.Drain(Math.Round(await ValueAsync(other, character2, nosferatu, 90), 6), 0), vampire.What);
    }

    [PgFact]
    public async Task A_projected_module_asked_for_without_a_target_is_refused_and_one_the_simulation_finds_too_far_is_told_with_the_range()
    {
        var (rig, session, _, module) = await ReadyAsync(RemoteArmorRepairer);
        await using var _ = rig;
        var targetCharacter = await rig.NewCharacterAsync("Projected range target", 2);
        var targetSession = await WeaponHostTests.InSpaceAsync(rig, targetCharacter, 2);
        var targetId = targetSession.GetInt("shipid")!.Value;
        var heard = rig.Sim.Heard.Count;
        var none = await rig.Space.ActivateAsync(session, module, "shipModuleRemoteArmorRepairer", 0, 1000, CancellationToken.None);
        Assert.Equal("TARGET_REQUIRED", none!.Reason);
        Assert.Equal(heard, rig.Sim.Heard.Count);

        rig.Sim.ActivateAnswer = new CommandReply(false, 0, "TARGET_OUT_OF_RANGE") { TargetType = Rig.Cockroach, DesiredRange = 9500, ActualDistance = 12_345.6 };
        var far = await rig.Space.ActivateAsync(session, module, "shipModuleRemoteArmorRepairer", targetId, 1000, CancellationToken.None);
        Assert.Equal("TARGET_OUT_OF_RANGE", far!.Reason);
        Assert.Equal((Rig.Cockroach, 9500d, 12_345.6), (far.TargetType, far.DesiredRange, far.ActualDistance));
        Assert.True(far.ShipType > 0);
    }

    private const int MidSlot = 19;
    private const int SmallEmpSmartbomb = 1563;
    private const int StasisWebifier = 526, WarpScrambler = 447, WarpDisruptor = 3242, TargetPainter = 12709, SensorDampener = 1968, RadarJammer = 1956, MultispectralJammer = 1957, TrackingDisruptorModule = 2108, GuidanceDisruptorModule = 5321;

    private static async Task<(SimAction.Remote Action, SimEffect Effect, Rig Rig, Eve.Characters.Created Character, long Module)> StartedAsync(int typeId, string effectName)
    {
        var (rig, session, character, module) = await ReadyAsync(typeId, MidSlot);
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, module, effectName, Target, 1000, CancellationToken.None));
        var effect = Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect;
        return (Assert.IsType<SimAction.Remote>(effect.Action), effect, rig, character, module);
    }

    [PgFact]
    public async Task A_web_a_painter_and_a_dampener_hold_their_target_with_the_percentages_dogma_gives_and_the_names_of_the_HUD()
    {
        var (web, _, rig, character, module) = await StartedAsync(StasisWebifier, "remoteWebifierFalloff");
        await using (rig)
        {
            // from the start of each cycle, for as long as it runs; the percentage is signed as it is in dogma (a web slows)
            Assert.False(web.AtCycleEnd);
            Assert.Equal("webify", web.Jam);
            Assert.Equal(new SimRemote.Web(Math.Round(await ValueAsync(rig, character, module, 20), 6)), web.What);
            Assert.True(((SimRemote.Web)web.What).Percent < 0);
            Assert.Equal(10_000d, web.Optimal);
        }

        var (paint, _, rig2, character2, module2) = await StartedAsync(TargetPainter, "remoteTargetPaintFalloff");
        await using (rig2)
        {
            Assert.Equal("ewTargetPaint", paint.Jam);
            Assert.Equal(new SimRemote.Paint(Math.Round(await ValueAsync(rig2, character2, module2, 554), 6)), paint.What);
            Assert.True(((SimRemote.Paint)paint.What).Percent > 0);
        }

        var (damp, _, rig3, character3, module3) = await StartedAsync(SensorDampener, "remoteSensorDampFalloff");
        await using (rig3)
        {
            Assert.Equal("ewRemoteSensorDamp", damp.Jam);
            Assert.Equal(new SimRemote.Damp(Math.Round(await ValueAsync(rig3, character3, module3, 309), 6), Math.Round(await ValueAsync(rig3, character3, module3, 566), 6)), damp.What);
        }
    }

    [PgFact]
    public async Task An_ECM_jammer_is_started_with_its_strength_for_each_kind_of_sensor()
    {
        var (radar, effect, rig, character, module) = await StartedAsync(RadarJammer, "remoteECMFalloff");
        await using (rig)
        {
            // told to the HUD of the target when the jam lands (the simulation rolls for it), so the effect itself carries no name
            Assert.Equal("", radar.Jam);
            Assert.False(radar.AtCycleEnd);
            var jam = Assert.IsType<SimRemote.Jam>(radar.What);
            // the strengths are what dogma gives after skills (gravimetric, ladar, magnetometric, radar = 238, 239, 240, 241); a radar jammer is strong against radar sensors only
            Assert.Equal(
                (await ValueAsync(rig, character, module, 238), await ValueAsync(rig, character, module, 239), await ValueAsync(rig, character, module, 240), await ValueAsync(rig, character, module, 241)),
                (jam.Gravimetric, jam.Ladar, jam.Magnetometric, jam.Radar));
            Assert.True(jam.Radar > 2.5 * jam.Gravimetric && jam.Gravimetric == jam.Ladar && jam.Ladar == jam.Magnetometric, $"{jam}");
            Assert.Equal((28_800d, 27_000d), (radar.Optimal, radar.Falloff));
            Assert.Equal(20_000d, effect.DurationMs);
            Assert.True(effect.CapacitorNeed > 0);
        }

        var (multi, _, rig2, _, _) = await StartedAsync(MultispectralJammer, "remoteECMFalloff");
        await using (rig2)
        {
            // a multispectral jammer is as strong against every kind, and weaker than a specialised one against its own
            var jam = Assert.IsType<SimRemote.Jam>(multi.What);
            Assert.True(jam.Gravimetric == jam.Ladar && jam.Ladar == jam.Magnetometric && jam.Magnetometric == jam.Radar && jam.Radar > 0, $"{jam}");
            Assert.Equal((19_200d, 18_000d), (multi.Optimal, multi.Falloff));
        }
    }

    [PgFact]
    public async Task A_smartbomb_is_started_with_its_field_and_its_damage_from_dogma_and_does_not_hurt_pilots_where_the_safety_forbids_it()
    {
        var (rig, session, character, module) = await ReadyAsync(SmallEmpSmartbomb, HighSlot);
        await using var _ = rig;
        // it needs no target
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, module, "empWave", 0, 1000, CancellationToken.None));
        var effect = Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect;
        var bomb = Assert.IsType<SimAction.Smartbomb>(effect.Action);
        Assert.Equal(Math.Round(await ValueAsync(rig, character, module, 99), 6), bomb.Range);
        Assert.True(bomb.Range > 0);
        Assert.Equal(
            [Math.Round(await ValueAsync(rig, character, module, 114), 6), Math.Round(await ValueAsync(rig, character, module, 118), 6), Math.Round(await ValueAsync(rig, character, module, 117), 6), Math.Round(await ValueAsync(rig, character, module, 116), 6)],
            bomb.Damage);
        Assert.True(bomb.Damage.Sum() > 0);
        Assert.Equal(await ValueAsync(rig, character, module, 73), effect.DurationMs, 3);
        Assert.Equal(0, effect.TargetId);
        // the rig's system is not null security: the simulation is told not to hurt pilots with it
        Assert.False(bomb.HurtsPilots);
    }

// SDE type 580 uses a mid slot and is restricted to battleship-class hulls.

    [PgFact]

    public async Task An_ECM_burst_is_packaged_from_its_dogma_range_and_racial_strengths_without_a_target()
    {
        const int BurstJammerI = 580, Raven = 638;
        var (rig, session, character, module) = await ReadyAsync(BurstJammerI, MidSlot, shipTypeId: Raven);
        await using var _ = rig;
        Assert.Equal(MidSlot, (await rig.Items.GetAsync(module))!.Flag);
        // the rig's system is high security: an ECM burst starts there only with the safety at none (EcmBurstHostTests covers the refusal)
        rig.Space.SafetyLevelOf = (_, _) => Task.FromResult(Eve.Crimewatch.SafetyLevel.None);
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, module, "ecmBurstJammer", 0, 1000, CancellationToken.None));
        var effect = Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect;
        var burst = Assert.IsType<SimAction.EcmBurst>(effect.Action);
        Assert.Equal(Math.Round(await ValueAsync(rig, character, module, 142), 6), burst.Range);
        Assert.Equal(
            [Math.Round(await ValueAsync(rig, character, module, 238), 6), Math.Round(await ValueAsync(rig, character, module, 239), 6),
             Math.Round(await ValueAsync(rig, character, module, 240), 6), Math.Round(await ValueAsync(rig, character, module, 241), 6)],
            burst.Strength);
        Assert.True(burst.Range > 0 && burst.Strength.Any(strength => strength > 0));
        Assert.Equal(0, effect.TargetId);
    }


    [PgFact]
    public async Task A_tracking_disruptor_takes_the_percentages_dogma_gives_from_the_range_the_falloff_and_the_tracking_of_its_target()
    {
        var (disrupt, effect, rig, character, module) = await StartedAsync(TrackingDisruptorModule, "shipModuleTrackingDisruptor");
        await using (rig)
        {
            Assert.Equal("ewTrackingDisrupt", disrupt.Jam);
            Assert.False(disrupt.AtCycleEnd);
            var what = Assert.IsType<SimRemote.Disrupt>(disrupt.What);
            Assert.Equal(
                (Math.Round(await ValueAsync(rig, character, module, 351), 6), Math.Round(await ValueAsync(rig, character, module, 349), 6), Math.Round(await ValueAsync(rig, character, module, 767), 6)),
                (what.RangePercent, what.FalloffPercent, what.TrackingPercent));
            Assert.True(what.RangePercent < 0 && what.FalloffPercent < 0 && what.TrackingPercent < 0, $"{what}");
            Assert.True(disrupt.Optimal > 0 && effect.DurationMs > 0);
        }
    }

    [PgFact]
    public async Task A_guidance_disruptor_takes_the_percentages_dogma_gives_from_the_speed_the_flight_and_the_explosion_of_the_missiles_of_its_target()
    {
        var (disrupt, effect, rig, character, module) = await StartedAsync(GuidanceDisruptorModule, "shipModuleGuidanceDisruptor");
        await using (rig)
        {
            Assert.Equal("ewGuidanceDisrupt", disrupt.Jam);
            Assert.False(disrupt.AtCycleEnd);
            var what = Assert.IsType<SimRemote.GuidanceDisrupt>(disrupt.What);
            Assert.Equal(
                (Math.Round(await ValueAsync(rig, character, module, 547), 6), Math.Round(await ValueAsync(rig, character, module, 596), 6), Math.Round(await ValueAsync(rig, character, module, 847), 6), Math.Round(await ValueAsync(rig, character, module, 848), 6)),
                (what.VelocityPercent, what.DelayPercent, what.AoeVelocityPercent, what.AoeCloudPercent));
            // the missiles go slower and not as far, and burst wider and slower
            Assert.True(what.VelocityPercent < 0 && what.DelayPercent < 0 && what.AoeVelocityPercent < 0 && what.AoeCloudPercent > 0, $"{what}");
            Assert.True(disrupt.Optimal > 0 && effect.DurationMs > 0);
        }
    }

    [PgFact]
    public async Task A_ship_tells_the_simulation_the_strength_of_its_sensors_and_how_it_resists_ECM()
    {
        var (rig, _, _, _) = await ReadyAsync(RadarJammer, MidSlot);
        await using var _ = rig;
        var ship = rig.Sim.Heard.OfType<SimEnter>().Last().Ship;
        var sensors = Assert.IsType<SimSensors>(ship.Sensors);
        // an ordinary hull has one kind of sensor that counts, and takes a jam in full
        Assert.True(new[] { sensors.Gravimetric, sensors.Ladar, sensors.Magnetometric, sensors.Radar }.Max() > 0, $"{sensors}");
        Assert.Equal(1d, sensors.EcmResistance);
        // energyWarfareResistance (2045) is read from the hull's own attributes: an ordinary hull takes a neutralisation in full (protocol 27)
        Assert.Equal(1d, sensors.EnergyWarfareResistance);
    }

    [PgFact]
    public async Task A_scrambler_holds_a_microwarpdrive_too_and_a_disruptor_does_not()
    {
        var (scram, _, rig, character, module) = await StartedAsync(WarpScrambler, "warpScrambleBlockMWDWithNPCEffect");
        await using (rig)
        {
            Assert.Equal("warpScramblerMWD", scram.Jam);
            Assert.Equal(new SimRemote.Scramble(Math.Round(await ValueAsync(rig, character, module, 105), 6), true), scram.What);
            Assert.Equal(2d, ((SimRemote.Scramble)scram.What).Strength);
        }

        var (disrupt, _, rig2, _, _) = await StartedAsync(WarpDisruptor, "warpDisrupt");
        await using (rig2)
        {
            Assert.Equal("warpScrambler", disrupt.Jam);
            Assert.Equal(new SimRemote.Scramble(1d, false), disrupt.What);
            Assert.Equal(20_000d, disrupt.Optimal);
        }
    }


    [PgFact]
    public async Task A_remote_sensor_booster_packages_positive_range_scan_and_sensor_strength_without_hostility()
    {
        const int RemoteSensorBoosterI = 1963;
        var (rig, session, character, module) = await ReadyAsync(RemoteSensorBoosterI, MidSlot);
        await using var _ = rig;
        var other = await WeaponHostTests.InSpaceAsync(rig, await rig.NewCharacterAsync("Space Victim", 2), 2);
        var safetyChecks = 0;
        var aggression = new List<Eve.Crimewatch.AggressionRequest>();
        rig.Space.SafetyLevelOf = (_, _) =>
        {
            safetyChecks++;
            return Task.FromResult(2);
        };
        rig.Space.Aggression = (request, _) =>
        {
            aggression.Add(request);
            return Task.CompletedTask;
        };

        var target = other.GetInt("shipid")!.Value;
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, module, "remoteSensorBoostFalloff", target, 1000, CancellationToken.None));
        var effect = Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect;
        Assert.Equal((module, 6427, target), (effect.ModuleId, effect.EffectId, effect.TargetId));
        Assert.Equal(await ValueAsync(rig, character, module, 73), effect.DurationMs, 3);
        Assert.True(effect.DurationMs > 0);

        var remote = Assert.IsType<SimAction.Remote>(effect.Action);
        Assert.Equal("remoteSensorBoost", remote.Jam);
        Assert.False(remote.AtCycleEnd);
        Assert.True(remote.Optimal > 0 && remote.Falloff > 0);
        var boost = Assert.IsType<SimRemote.Damp>(remote.What);
        Assert.Equal(Math.Round(await ValueAsync(rig, character, module, 309), 6), boost.RangePercent);
        Assert.Equal(Math.Round(await ValueAsync(rig, character, module, 566), 6), boost.ResolutionPercent);
        Assert.Equal(Math.Round(await ValueAsync(rig, character, module, 1027), 6), boost.GravimetricStrengthPercent);
        Assert.Equal(Math.Round(await ValueAsync(rig, character, module, 1028), 6), boost.LadarStrengthPercent);
        Assert.Equal(Math.Round(await ValueAsync(rig, character, module, 1029), 6), boost.MagnetometricStrengthPercent);
        Assert.Equal(Math.Round(await ValueAsync(rig, character, module, 1030), 6), boost.RadarStrengthPercent);
        Assert.True(boost.RangePercent > 0 && boost.ResolutionPercent > 0);
        Assert.All(
            new[] { boost.GravimetricStrengthPercent, boost.LadarStrengthPercent, boost.MagnetometricStrengthPercent, boost.RadarStrengthPercent },
            percent => Assert.True(percent > 0));
        Assert.Equal(0, safetyChecks);
        Assert.Empty(aggression);
    }


    [PgFact]
    public async Task Remote_tracking_computer_assistance_avoids_crimewatch_while_tracking_disruption_is_an_attack()
    {
        var rig = await Rig.CreateAsync();
        WireRemoteAssistance(rig);
        await using var _ = rig;
        var sde = new Eve.Sde.SdeReader(rig.Source);
        var catalogBuild = await sde.BuildAsync()
            ?? throw new InvalidOperationException("The test SDE schema must expose its actual build number.");
        var moduleGroupIds = (await sde.ReadAsync("groups"))
            .Where(row => row.Doc.GetProperty("categoryID").GetInt32() == 7)
            .Select(row => row.Key)
            .ToArray();
        var publishedModuleTypeIds = (await sde.ReadByFieldAsync("types", "groupID", moduleGroupIds))
            .Where(row => row.Doc.TryGetProperty("published", out var published) && published.ValueKind == System.Text.Json.JsonValueKind.True)
            .Select(row => row.Key)
            .ToArray();
        var moduleDogma = await sde.ReadAsync("typeDogma", publishedModuleTypeIds);
        var remoteTrackingEffect = Assert.Single(rig.Statics.Effects, effect =>
            effect.Name.Equals("shipModuleRemoteTrackingComputer", StringComparison.OrdinalIgnoreCase));
        var trackingDisruptorEffect = Assert.Single(rig.Statics.Effects, effect =>
            effect.Name.Equals("shipModuleTrackingDisruptor", StringComparison.OrdinalIgnoreCase));
        int TypeIdFor(Eve.Dogma.EffectDef effect)
        {
            var matchingTypes = moduleDogma.Where(row =>
                row.Doc.TryGetProperty("dogmaEffects", out var effects)
                && effects.ValueKind == System.Text.Json.JsonValueKind.Array
                && effects.EnumerateArray().Any(item => item.GetProperty("effectID").GetInt32() == effect.Id))
                .ToArray();
            Assert.True(matchingTypes.Length > 0, "SDE build " + catalogBuild + " has no published module for " + effect.Name + ".");
            return checked((int)matchingTypes[0].Key);
        }
        var remoteTrackingTypeId = TypeIdFor(remoteTrackingEffect);
        var trackingDisruptorTypeId = TypeIdFor(trackingDisruptorEffect);

        var (supportSession, _, remoteModule) =
            await RepairHostTests.InSpaceWithAsync(rig, remoteTrackingTypeId, MidSlot);
        var victimCharacter = await rig.NewCharacterAsync("Remote tracking target", 2);
        var victimSession = await WeaponHostTests.InSpaceAsync(rig, victimCharacter, 2);
        var victimShipId = victimSession.GetInt("shipid")!.Value;
        var safetyChecks = 0;
        List<Eve.Crimewatch.AggressionRequest> aggression = [];
        rig.Space.SafetyLevelOf = (_, _) =>
        {
            safetyChecks++;
            return Task.FromResult(2);
        };
        rig.Space.Aggression = (request, _) =>
        {
            aggression.Add(request);
            return Task.CompletedTask;
        };

        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(
            supportSession, remoteModule, "shipModuleRemoteTrackingComputer", victimShipId, 1000, CancellationToken.None));
        var assist = Assert.IsType<SimAction.Remote>(Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect.Action);
        var assistBonus = Assert.IsType<SimRemote.Disrupt>(assist.What);
        Assert.True(assistBonus.RangePercent > 0 && assistBonus.FalloffPercent > 0 && assistBonus.TrackingPercent > 0);
        Assert.Equal(0, safetyChecks);
        Assert.Empty(aggression);

        var (attackerSession, _, disruptorModule) =
            await RepairHostTests.InSpaceWithAsync(rig, trackingDisruptorTypeId, MidSlot, characterName: "Tracking disruptor attacker", sessionId: 3);
        safetyChecks = 0;
        aggression.Clear();
        rig.Space.SafetyLevelOf = (_, _) =>
        {
            safetyChecks++;
            return Task.FromResult(0);
        };
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(
            attackerSession, disruptorModule, "shipModuleTrackingDisruptor", victimShipId, 1000, CancellationToken.None));
        var attack = Assert.IsType<SimAction.Remote>(Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect.Action);
        var disruption = Assert.IsType<SimRemote.Disrupt>(attack.What);
        Assert.True(disruption.RangePercent < 0 || disruption.FalloffPercent < 0 || disruption.TrackingPercent < 0);
        Assert.Equal(1, safetyChecks);
        Assert.Single(aggression);
    }


    [PgFact]
    public async Task Mutadaptive_cycle_requests_receive_the_spooled_amount_and_clear_after_the_final_response()
    {
        var rig = await Rig.CreateAsync();
        WireRemoteAssistance(rig);
        await using var _ = rig;
        var sde = new Eve.Sde.SdeReader(rig.Source);
        var catalogBuild = await sde.BuildAsync()
            ?? throw new InvalidOperationException("The test SDE schema must expose its actual build number.");
        var effect = Assert.Single(rig.Statics.Effects, candidate =>
            candidate.Name.Equals("ShipModuleRemoteArmorMutadaptiveRepairer", StringComparison.OrdinalIgnoreCase));
        var moduleGroupIds = (await sde.ReadAsync("groups"))
            .Where(row => row.Doc.GetProperty("categoryID").GetInt32() == 7)
            .Select(row => row.Key)
            .ToArray();
        var publishedModuleTypeIds = (await sde.ReadByFieldAsync("types", "groupID", moduleGroupIds))
            .Where(row => row.Doc.TryGetProperty("published", out var published) && published.ValueKind == System.Text.Json.JsonValueKind.True)
            .Select(row => row.Key)
            .ToArray();
        var moduleDogma = await sde.ReadAsync("typeDogma", publishedModuleTypeIds);
        var moduleTypeId = checked((int)moduleDogma.First(row =>
            row.Doc.TryGetProperty("dogmaEffects", out var effects)
            && effects.ValueKind == System.Text.Json.JsonValueKind.Array
            && effects.EnumerateArray().Any(typeEffect => typeEffect.GetProperty("effectID").GetInt32() == effect.Id)).Key);
        await rig.Statics.EnsureTypesAsync([moduleTypeId]);
        var moduleType = rig.Statics.Type(moduleTypeId)
            ?? throw new InvalidOperationException("SDE build " + catalogBuild + " has no mutadaptive module type.");
        var amountAttribute = rig.Statics.AttributeId("armorDamageAmount")
            ?? throw new InvalidOperationException("The SDE is missing armorDamageAmount.");
        var rateAttribute = rig.Statics.AttributeId("repairMultiplierBonusPerCycle")
            ?? throw new InvalidOperationException("The SDE is missing repairMultiplierBonusPerCycle.");
        var maximumAttribute = rig.Statics.AttributeId("repairMultiplierBonusMax")
            ?? throw new InvalidOperationException("The SDE is missing repairMultiplierBonusMax.");
        var durationAttribute = rig.Statics.AttributeId("duration")
            ?? throw new InvalidOperationException("The SDE is missing duration.");
        Assert.True(moduleType.Attributes.TryGetValue(amountAttribute, out var baseAmount));
        Assert.True(moduleType.Attributes.TryGetValue(rateAttribute, out var bonusPerCycle));
        Assert.True(moduleType.Attributes.TryGetValue(maximumAttribute, out var maximumBonus));
        Assert.True(moduleType.Attributes.TryGetValue(durationAttribute, out var durationMs) && durationMs > 0);

        const int Rodiva = 49712;
        var (session, character, moduleId) = await RepairHostTests.InSpaceWithAsync(rig, moduleTypeId, HighSlot, shipTypeId: Rodiva);
        baseAmount = await ValueAsync(rig, character, moduleId, amountAttribute);
        var targetCharacter = await rig.NewCharacterAsync("Mutadaptive target", 2);
        var targetSession = await WeaponHostTests.InSpaceAsync(rig, targetCharacter, 2);
        var targetId = targetSession.GetInt("shipid")!.Value;
        var safetyChecks = 0;
        List<Eve.Crimewatch.AggressionRequest> aggression = [];
        rig.Space.SafetyLevelOf = (_, _) =>
        {
            safetyChecks++;
            return Task.FromResult(2);
        };
        rig.Space.Aggression = (request, _) =>
        {
            aggression.Add(request);
            return Task.CompletedTask;
        };

        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(
            session, moduleId, "ShipModuleRemoteArmorMutadaptiveRepairer", targetId, 1000, CancellationToken.None));
        var activation = Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect;
        var remote = Assert.IsType<SimAction.Remote>(activation.Action);
        var repair = Assert.IsType<SimRemote.MutadaptiveRepair>(remote.What);
        Assert.Equal(baseAmount, repair.Amount);
        Assert.Equal(bonusPerCycle, repair.RepairMultiplierBonusPerCycle);
        Assert.Equal(maximumBonus, repair.RepairMultiplierBonusMax);
        Assert.True(remote.AtCycleEnd);

        var cycleDurationMs = checked((long)Math.Round(durationMs));
        var firstCycle = new SimRemoteRepairCycle(
            (ulong)session.SessionId, moduleId, targetId, 1, 1, cycleDurationMs, cycleDurationMs,
            false, baseAmount, bonusPerCycle, maximumBonus);
        rig.Sim.Say(firstCycle);
        var firstResponse = Assert.IsType<SimRemoteRepairCycleResult>(rig.Sim.Heard.Last());
        Assert.True(firstResponse.Accepted);
        // the first delivery is x1.0: the bonus is applied to the cycle that lands and only then advanced (assistanceModuleRuntime.js)
        Assert.Equal(Eve.Services.Space.Effects.ModuleAttributes.Six(baseAmount), firstResponse.Amount);

        Assert.Equal(ModuleResult.Done, await rig.Space.DeactivateAsync(
            session, moduleId, "ShipModuleRemoteArmorMutadaptiveRepairer", CancellationToken.None));
        var finalCycle = new SimRemoteRepairCycle(
            (ulong)session.SessionId, moduleId, targetId, 2, 2, cycleDurationMs * 2, cycleDurationMs,
            true, baseAmount, bonusPerCycle, maximumBonus);
        rig.Sim.Say(finalCycle);
        var finalResponse = Assert.IsType<SimRemoteRepairCycleResult>(rig.Sim.Heard.Last());
        Assert.True(finalResponse.Accepted);
        Assert.Equal(Eve.Services.Space.Effects.ModuleAttributes.Six(baseAmount * (1 + Math.Min(bonusPerCycle, maximumBonus))), finalResponse.Amount);

        rig.Sim.Say(finalCycle);
        var staleResponse = Assert.IsType<SimRemoteRepairCycleResult>(rig.Sim.Heard.Last());
        Assert.False(staleResponse.Accepted);
        Assert.Equal("STALE_CYCLE", staleResponse.Reason);
        Assert.Equal(0, safetyChecks);
        Assert.Empty(aggression);
    }
}
