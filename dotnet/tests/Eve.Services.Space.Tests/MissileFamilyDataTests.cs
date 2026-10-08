using Eve.Data;
using Eve.Dogma;
using Eve.Services.Space.Effects;
using Eve.Sde;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The missile family derived from the pinned SDE (<c>DogmaStatic.MissileLauncherGroups</c>): exactly the launcher groups the simulation fires, and the F.O.F. missiles
/// of those launchers refused by name. A skipped test (no <c>EVE_PG_URL</c>) is not a pass.
/// </summary>
public class MissileFamilyDataTests
{
    /// <summary>The launcher groups the missile family must hold on the pinned SDE: cruise, rocket, torpedo, light, heavy, rapid light, heavy assault, rapid heavy, XL torpedo, rapid torpedo and XL cruise.</summary>
    private static readonly int[] MissileFamilyGroups = [506, 507, 508, 509, 510, 511, 524, 771, 1245, 1673, 1674];

    /// <summary>The launcher groups the family must not hold: defender, bomb, probe, sphere, festival, breacher, countermeasure and the structure launchers.</summary>
    private static readonly int[] NotMissileFamilyGroups = [862, 512, 589, 481, 1226, 501, 4807, 308, 1327, 1328, 1562, 1974];

    [PgFact]
    public async Task The_missile_family_derived_from_the_static_data_is_exactly_the_eleven_missile_launcher_groups()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));

        Assert.Equal(MissileFamilyGroups.Order(), statics.MissileLauncherGroups.Order());
        foreach (var group in NotMissileFamilyGroups)
        {
            Assert.DoesNotContain(group, statics.MissileLauncherGroups);
        }
        Assert.Equal(MissileFamilyGroups.Order(), MissileEffect.ServedLauncherGroups(statics).Order());
    }

    /// <summary>
    /// A launcher of the missile family, fitted and online, asked for the F.O.F. missile of its charge is refused by name (decision D2), before a target is asked for.
    /// The plain missile of the same launcher is not refused by that name.
    /// </summary>
    [PgFact]
    public async Task A_served_launcher_asked_for_its_fof_missile_is_refused_by_name_before_a_target_is_asked_for()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var statics = await DogmaStatic.LoadAsync(sde);
        var shipGroupIds = (await sde.ReadAsync("groups")).Where(row => row.Doc.GetProperty("categoryID").GetInt32() == 6).Select(row => row.Key).ToArray();
        var shipTypeId = checked((int)(await sde.ReadByFieldAsync("types", "groupID", shipGroupIds)).First(row => row.Doc.TryGetProperty("published", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.True).Key);
        await statics.EnsureTypesAsync([shipTypeId]);

        const int cruiseGroup = 506;
        var moduleTypeId = checked((int)(await sde.ReadByFieldAsync("types", "groupID", [cruiseGroup]))
            .Where(row => row.Doc.TryGetProperty("published", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.True)
            .Select(row => row.Key).OrderBy(id => id).First());
        await statics.EnsureTypesAsync([moduleTypeId]);
        Assert.Contains(cruiseGroup, statics.MissileLauncherGroups);

        const long characterId = 74101, shipId = 74102, moduleId = 74103;
        var ship = new Eve.Inventory.Item(shipId, shipTypeId, characterId, 60003760, 0, 1, true, null);
        var module = new Eve.Inventory.Item(moduleId, moduleTypeId, characterId, shipId, 11, 1, true, null);
        var world = new Eve.Dogma.DogmaWorld(statics, characterId, shipId);
        world.Add(new Eve.Dogma.DogmaItem(shipId, shipTypeId, characterId, ship.LocationId, ship.Flag, Eve.Dogma.ItemState.Online));
        world.Add(new Eve.Dogma.DogmaItem(moduleId, moduleTypeId, characterId, shipId, 11, Eve.Dogma.ItemState.Online));
        var snapshot = new Eve.Ships.ShipSnapshot { Character = null!, Ship = ship, Fitted = [module], Charges = [], Skills = [], World = world };

        var fof = ModulePackages.Of(snapshot, statics, moduleId, "fofMissileLaunching", 980050000105, 1);
        Assert.Null(fof.Effect);
        Assert.Equal("FOF_MISSILE_NOT_IMPLEMENTED", fof.Reason);
        Assert.NotEqual("FOF_MISSILE_NOT_IMPLEMENTED", ModulePackages.Of(snapshot, statics, moduleId, "missileLaunching", 980050000105, 1).Reason);
    }
}
