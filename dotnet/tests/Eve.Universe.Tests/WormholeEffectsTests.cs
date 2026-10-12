using Eve.Sde;
using Npgsql;

namespace Eve.Universe.Tests;

/// <summary>The system effects from the pinned SDE (build 3396210): the per-class beacons and their dogma modifiers.</summary>
public sealed class WormholeEffectsTests
{
    [PgFact]
    public async Task There_is_one_beacon_for_each_of_the_six_effects_at_each_class_1_to_6()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var effects = await WormholeEffects.LoadAsync(new SdeReader(source));

        Assert.Equal(36, effects.Count);
        foreach (var name in new[] { "Black Hole", "Cataclysmic Variable", "Magnetar", "Pulsar", "Red Giant", "Wolf Rayet" })
        {
            for (var systemClass = 1; systemClass <= 6; systemClass++)
            {
                Assert.True(effects.ContainsKey((name, systemClass)), $"{name} at class {systemClass}");
            }
        }
    }

    [PgFact]
    public async Task The_modifiers_of_a_beacon_are_the_sde_values_for_its_class()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var effects = await WormholeEffects.LoadAsync(new SdeReader(source));

        Assert.Equal(30854, effects[("Black Hole", 6)].TypeId);
        Assert.Equal(2.0, effects[("Black Hole", 6)].Modifiers["maxTargetRangeMultiplier"]);
        Assert.Equal(0.5, effects[("Black Hole", 6)].Modifiers["stasisWebStrengthMultiplier"]);
        Assert.Equal(1.15, effects[("Red Giant", 1)].Modifiers["heatDamageMultiplier"]);
        Assert.Equal(1.72, effects[("Pulsar", 4)].Modifiers["shieldCapacityMultiplier"]);
        Assert.Equal(50.0, effects[("Pulsar", 6)].Modifiers["armorEmDamageResistanceBonus"]);
    }
}
