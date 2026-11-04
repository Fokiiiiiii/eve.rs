using Eve.Sde;
using Npgsql;

namespace Eve.Universe.Tests;

/// <summary>The committed J-space data file: its counts, its sources, and its statics and effects checked against the pinned SDE.</summary>
public sealed class JSpaceSystemsTests
{
    [Fact]
    public void The_file_holds_all_2604_j_space_systems_with_their_classes()
    {
        var systems = JSpaceSystems.Load();

        Assert.Equal(2604, systems.Count);
        Assert.Equal(new[] { 358, 537, 506, 523, 531, 118, 25 }, new[] { 1, 2, 3, 4, 5, 6, 13 }.Select(c => systems.Count(s => s.WormholeClass == c)));
        Assert.Equal(12, systems.Single(s => s.Id == 31000005).WormholeClass);
        Assert.Equal("Thera", systems.Single(s => s.Id == 31000005).Name);
        Assert.Empty(systems.Single(s => s.Id == 31000005).Statics);
    }

    [Fact]
    public void The_hundred_shattered_systems_take_their_statics_from_the_wiki_and_the_rest_from_evejs()
    {
        var systems = JSpaceSystems.Load();

        Assert.Equal(100, systems.Count(s => s.StaticsSource == "wiki-shattered"));
        var j015092 = systems.Single(s => s.Name == "J015092");
        Assert.Equal(1, j015092.WormholeClass);
        Assert.Equal("Black Hole", j015092.Effect);
        Assert.Equal(new[] { "N110", "Z060" }, j015092.Statics.Select(s => s.Code));
        Assert.Equal(new[] { "J244" }, systems.Single(s => s.Name == "J005926").Statics.Select(s => s.Code).Skip(1));
        Assert.Equal("Red Giant", systems.Single(s => s.Name == "J005926").Effect);
        Assert.Equal("Red Giant", systems.Single(s => s.Name == "J010569").Effect);
    }

    [PgFact]
    public async Task Every_static_is_a_wormhole_type_of_the_pinned_sde_and_every_effect_is_a_beacon()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var types = await WormholeTypeCatalog.LoadAsync(sde);
        var beacons = await WormholeEffects.LoadAsync(sde);
        var beaconIds = beacons.Values.Select(b => b.TypeId).ToHashSet();
        var systems = JSpaceSystems.Load();

        foreach (var system in systems)
        {
            foreach (var statics in system.Statics)
            {
                Assert.True(types.ContainsKey(statics.TypeId), $"{system.Name} static {statics.Code} type {statics.TypeId}");
            }
            if (system.EffectTypeId is int beacon)
            {
                Assert.Contains(beacon, beaconIds);
            }
        }
        Assert.Equal(1038, systems.Count(s => s.EffectTypeId is not null));
    }
}
