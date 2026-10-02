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
    internal static async Task<(ClientSession Session, Eve.Characters.Created Character, long Module)> InSpaceWithAsync(Rig rig, int typeId, int flag)
    {
        var character = await rig.NewCharacterAsync();
        Assert.IsType<Eve.Ships.ShipChanged>(await rig.Ships.MoveAsync(character.CharacterId, character.ShipId + 3, character.ShipId, Rig.Station, ItemFlags.Hangar, null));
        var granted = (await rig.Ships.GrantAsync(character.CharacterId, typeId, 1, singleton: true))!.Single().After;
        await TrainAsync(rig, character.CharacterId);
        // the starting ship comes with a few modules: what is in the slot goes back to the hangar
        foreach (var occupant in (await rig.Items.InLocationAsync(character.ShipId, character.CharacterId)).Where(i => i.Flag == flag))
        {
            Changed(await rig.Ships.MoveAsync(character.CharacterId, occupant.Id, character.ShipId, Rig.Station, ItemFlags.Hangar, null));
        }
        Changed(await rig.Ships.MoveAsync(character.CharacterId, granted.Id, Rig.Station, character.ShipId, flag, 1), $"type {typeId} flag {flag}");
        Changed(await rig.Ships.SetOnlineAsync(character.CharacterId, character.ShipId, granted.Id, true));
        var session = await rig.SessionAsync(character, 1);
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
        await rig.Skills.GrantAsync(connection, transaction, character, new[] { 3392, 3393, 3416, 3419, 3413, 3426, 3318, 3422, 3423, 16069, 27902, 3435, 3449, 3433, 19921 }.Select(type => new Eve.Skills.CharacterSkill(type, 5, 0)));
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

    private static async Task<(Rig Rig, ClientSession Session, Eve.Characters.Created Character, long Module)> ReadyAsync(int typeId, int slot = HighSlot)
    {
        var rig = await Rig.CreateAsync();
        var (session, character, module) = await RepairHostTests.InSpaceWithAsync(rig, typeId, slot);
        return (rig, session, character, module);
    }

    private static async Task<double> ValueAsync(Rig rig, Eve.Characters.Created character, long module, int attribute) =>
        (await rig.Ships.LoadAsync(character.CharacterId, CancellationToken.None))!.World.Get(module, attribute);

    [PgFact]
    public async Task A_remote_armor_repairer_is_started_at_its_target_with_the_points_the_reach_and_the_cost_from_dogma()
    {
        var (rig, session, character, module) = await ReadyAsync(RemoteArmorRepairer);
        await using var _ = rig;

        var answer = await rig.Space.ActivateAsync(session, module, "shipModuleRemoteArmorRepairer", Target, 1000, CancellationToken.None);

        Assert.Equal(ModuleResult.Done, answer);
        var effect = Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect;
        Assert.Equal((module, Target), (effect.ModuleId, effect.TargetId));
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
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, booster, "shipModuleRemoteShieldBooster", Target, 1000, CancellationToken.None));
        var effect = Assert.IsType<SimActivate>(rig.Sim.Heard.Last()).Effect;
        var remote = Assert.IsType<SimAction.Remote>(effect.Action);
        Assert.False(remote.AtCycleEnd);
        Assert.Equal("shieldTransfer", remote.Jam);
        Assert.Equal(new SimRemote.Repair(SimLayer.Shield, Math.Round(await ValueAsync(rig, character, booster, 68), 6)), remote.What);

        var (other, session2, character2, transmitter) = await ReadyAsync(CapacitorTransmitter);
        await using var _other = other;
        Assert.Equal(ModuleResult.Done, await other.Space.ActivateAsync(session2, transmitter, "shipModuleRemoteCapacitorTransmitter", Target, 1000, CancellationToken.None));
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
        var heard = rig.Sim.Heard.Count;
        var none = await rig.Space.ActivateAsync(session, module, "shipModuleRemoteArmorRepairer", 0, 1000, CancellationToken.None);
        Assert.Equal("TARGET_REQUIRED", none!.Reason);
        Assert.Equal(heard, rig.Sim.Heard.Count);

        rig.Sim.ActivateAnswer = new CommandReply(false, 0, "TARGET_OUT_OF_RANGE") { TargetType = 601, DesiredRange = 9500, ActualDistance = 12_345.6 };
        var far = await rig.Space.ActivateAsync(session, module, "shipModuleRemoteArmorRepairer", Target, 1000, CancellationToken.None);
        Assert.Equal("TARGET_OUT_OF_RANGE", far!.Reason);
        Assert.Equal((601, 9500d, 12_345.6), (far.TargetType, far.DesiredRange, far.ActualDistance));
        Assert.True(far.ShipType > 0);
    }

    private const int MidSlot = 19;
    private const int StasisWebifier = 526, WarpScrambler = 447, WarpDisruptor = 3242, TargetPainter = 12709, SensorDampener = 1968;

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
}
