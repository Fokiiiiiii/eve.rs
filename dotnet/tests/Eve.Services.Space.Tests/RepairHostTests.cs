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
    private static async Task<(ClientSession Session, Eve.Characters.Created Character, long Module)> InSpaceWithAsync(Rig rig, int typeId, int flag)
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

    /// <summary>The skills the modules need (Mechanics, Repair Systems, Shield Operation, Shield Management, Engineering), all to level 5: what is tested is not the fitting rules.</summary>
    private static async Task TrainAsync(Rig rig, long character)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Skills.GrantAsync(connection, transaction, character, new[] { 3392, 3393, 3416, 3419, 3413, 3426, 3318 }.Select(type => new Eve.Skills.CharacterSkill(type, 5, 0)));
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
