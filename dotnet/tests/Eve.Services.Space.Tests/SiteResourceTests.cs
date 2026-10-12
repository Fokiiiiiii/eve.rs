using Eve.Services.Space.Sites;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The resources of a gas or ore site (SpaceContainers.Resources): a deposit's rocks and a reservoir's cloud are made as items of the site and placed in the
/// scene of their system, at the offsets of their template, and a pilot's entry puts the live ones back.
/// </summary>
public sealed class SiteResourceTests
{
    private const int SolarSystem = 30_100_032;
    private const long Site = 5_300_000_000_001, Reservoir = 5_300_000_000_002;

    [PgFact]
    public async Task A_deposit_places_its_rocks_and_a_reservoir_its_cloud_and_a_later_entry_puts_the_live_ones_back()
    {
        await using var rig = await Rig.CreateAsync();
        var templates = SiteTemplates.Load();
        var deposit = templates.ById("wormhole-3811")!;
        var reservoir = templates.ById("wormhole-3555")!;
        double[] origin = [1.0e9, 2.0e8, -3.0e8];

        await rig.Containers.PlaceSiteResourcesAsync(SolarSystem, Site, deposit, origin);
        await rig.Containers.PlaceSiteResourcesAsync(SolarSystem, Reservoir, reservoir, origin);

        var placed = rig.Sim.Heard.OfType<SimPlaceAsteroid>().ToList();
        Assert.Equal(deposit.Rocks!.Count + 1, placed.Count);
        for (var index = 0; index < deposit.Rocks.Count; index++)
        {
            var rock = placed[index];
            Assert.Equal(deposit.Rocks[index].TypeId, rock.TypeId);
            Assert.Equal(origin[0] + deposit.Rocks[index].Offset[0], rock.Position[0], 6);
            Assert.Equal(origin[2] + deposit.Rocks[index].Offset[2], rock.Position[2], 6);
        }
        var cloud = placed[^1];
        Assert.Contains(cloud.TypeId, reservoir.Clouds!);
        Assert.Equal(origin[0], cloud.Position[0], 6);

        // a pilot comes back to the system: the rocks and the cloud of the live site are placed again (the scene was dropped meanwhile)
        var before = rig.Sim.Heard.Count;
        await rig.Containers.RestoreSiteResourcesAsync(SolarSystem, [Site]);
        var restored = rig.Sim.Heard.Skip(before).OfType<SimPlaceAsteroid>().ToList();
        Assert.Equal(deposit.Rocks.Count, restored.Count);
        Assert.Equal(deposit.Rocks.Select(rock => rock.TypeId), restored.Select(rock => rock.TypeId));
    }

    [PgFact]
    public async Task A_site_that_ends_takes_its_rocks_and_cloud_out_of_the_scene_and_the_store()
    {
        await using var rig = await Rig.CreateAsync();
        var templates = SiteTemplates.Load();
        var deposit = templates.ById("wormhole-3811")!;
        double[] origin = [1.0e9, 2.0e8, -3.0e8];
        await rig.Containers.PlaceSiteResourcesAsync(SolarSystem, Site, deposit, origin);

        var removedBefore = rig.Sim.Heard.OfType<SimRemoveWreck>().Count();
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Containers.RemoveSiteResourcesAsync(connection, transaction, SolarSystem, Site);
            await transaction.CommitAsync();
        }

        var removed = rig.Sim.Heard.OfType<SimRemoveWreck>().Skip(removedBefore).ToList();
        Assert.Equal(deposit.Rocks!.Count, removed.Count);
        // nothing is left to put back: the store has no row of the site
        var before = rig.Sim.Heard.Count;
        await rig.Containers.RestoreSiteResourcesAsync(SolarSystem, [Site]);
        Assert.Empty(rig.Sim.Heard.Skip(before).OfType<SimPlaceAsteroid>());
    }
}
