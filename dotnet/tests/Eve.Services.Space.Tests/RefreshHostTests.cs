using Eve.Dogma;
using Eve.Skills;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// A running gun is refreshed in the simulation when its overheat changes (FP-C4b): the host rebuilds its effect from the ship as it flies and sends what changed (protocol 37,
/// <see cref="SimUpdateEffect"/>). A refresh that changes nothing is not sent.
/// </summary>
public class RefreshHostTests
{
    private const int RailgunI = 10678, Thermodynamics = 28164;

    [PgFact]
    public async Task An_overheat_of_a_running_gun_is_sent_to_the_simulation_as_a_refresh_of_its_effect_and_its_end_is_too()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, character, gun) = await AmmoHostTests.ArmedInSpaceAsync(rig, loaded: true);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Skills.GrantAsync(connection, transaction, character.CharacterId, [new CharacterSkill(Thermodynamics, 1, 0)]);
            await transaction.CommitAsync();
        }
        Assert.Equal(ModuleResult.Done, await rig.Space.ActivateAsync(session, gun, "projectileFired", 980050000105, 1000, CancellationToken.None));
        // nothing has changed yet: the activation sends no refresh of the effect it has just started
        Assert.Empty(rig.Sim.Updates);
        var sent = Assert.IsType<SimActivate>(rig.Sim.Heard.OfType<SimActivate>().Single()).Effect;
        var bare = sent.Turret!.RawDamage;

        var overload = rig.Statics.Type(RailgunI)!.EffectIds.Select(rig.Statics.Effect).Single(effect => effect is { Category: EffectDef.Overload })!.Id;
        Assert.Equal(ModuleResult.Done, await rig.Space.OverloadAsync(session, gun, overload, true, CancellationToken.None));
        var hot = Assert.Single(rig.Sim.Updates);
        Assert.Equal(gun, hot.ModuleId);
        // the overload changes what the gun runs (its cycle, its capacitor or its damage, whichever the gun's overload gives)
        Assert.True(
            hot.DurationMs != sent.DurationMs || hot.CapacitorNeed != sent.CapacitorNeed || !hot.Turret!.RawDamage.SequenceEqual(bare),
            "the overheat changes nothing the gun runs");

        // the end of the overheat is sent too: the numbers go back to the bare ones, and nothing else is sent
        Assert.Equal(ModuleResult.Done, await rig.Space.OverloadAsync(session, gun, overload, false, CancellationToken.None));
        Assert.Equal(2, rig.Sim.Updates.Count);
        Assert.Equal(sent.DurationMs, rig.Sim.Updates[1].DurationMs);
        Assert.Equal(sent.CapacitorNeed, rig.Sim.Updates[1].CapacitorNeed);
        Assert.Equal(bare, rig.Sim.Updates[1].Turret!.RawDamage);
    }
}
