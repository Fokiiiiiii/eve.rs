using Eve.Data;
using Eve.Dogma;
using Eve.Sde;
using Npgsql;

namespace Eve.Services.Space.Tests;

public class ModuleEffectsTests
{
    [PgFact]
    public async Task Every_effect_the_space_host_serves_exists_in_the_static_data_and_is_not_listed_as_uncovered()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));
        var registry = Eve.Services.Space.Effects.ModuleEffects.Default;
        var known = statics.Effects.Select(e => e.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // a typo in a handler's name would make it serve nothing, silently
        Assert.All(registry.Names, name => Assert.Contains(name, known));
        var uncovered = registry.Uncovered(statics).Select(e => e.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(registry.Names, name => Assert.DoesNotContain(name, uncovered));
        // the effects of this release, by name: the list only grows
        foreach (var name in new[]
        {
            "moduleBonusAfterburner", "moduleBonusMicrowarpdrive", "targetAttack", "projectileFired", "armorRepair", "shieldBoosting", "structureRepair", "empWave", "remoteWebifierFalloff",
            "remoteTargetPaintFalloff", "remoteSensorDampFalloff", "warpScrambleBlockMWDWithNPCEffect", "warpDisrupt", "shipModuleTrackingDisruptor", "shipModuleGuidanceDisruptor", "remoteECMFalloff",
            "ecmBurstJammer", "cloaking", "cloakingWarpSafe", "cloakingPrototype", "shipModuleRemoteShieldBooster", "shipModuleRemoteArmorRepairer", "shipModuleRemoteHullRepairer", "shipModuleRemoteCapacitorTransmitter", "energyNeutralizerFalloff", "energyNosferatuFalloff",
        })
        {
            Assert.Contains(name, registry.Names, StringComparer.OrdinalIgnoreCase);
        }
    }
}
