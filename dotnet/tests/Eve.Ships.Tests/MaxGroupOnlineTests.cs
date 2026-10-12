using System.Text.Json;
using Eve.Data;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Sde;
using Npgsql;

namespace Eve.Ships.Tests;

/// <summary>The group limit on online modules (<c>maxGroupOnline</c>, attribute 978): the one-too-many refusal and what does not count against it.</summary>
public class MaxGroupOnlineTests
{
    private const int CapacitorBoosterGroup = 76;
    private const int AfterburnerGroup = 46;
    private const long Character = 75001;
    private const long ShipId = 75002;

    [PgFact]
    public async Task Onlining_the_eleventh_capacitor_booster_is_refused_and_the_tenth_is_allowed()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Rig.Url!));
        var sde = new SdeReader(source);
        var statics = await DogmaStatic.LoadAsync(sde);
        var booster = await TypeWithLimitAsync(sde, statics, CapacitorBoosterGroup);
        Assert.Equal(10, statics.Type(booster)!.Attributes[978]);

        // ten boosters online and an eleventh waiting in a slot: the reference refuses the eleventh (CannotOnlineReachedMaxGroupOnline)
        var (fitted, world) = FitOf(statics, Enumerable.Range(0, 11).Select(i => (Id: 1000L + i, TypeId: booster, Online: i < 10)).ToArray());
        var eleventh = fitted[10];
        var refusal = CharacterShips.MaxGroupOnlineRefusal(fitted, 11019, statics, world, eleventh);
        Assert.NotNull(refusal);
        Assert.Equal(ShipRefusal.MaxGroupOnline, refusal!.Reason);
        Assert.Equal((CapacitorBoosterGroup, 10, booster), (refusal.GroupId, refusal.MaxGroupOnline, refusal.ModuleTypeId));

        // nine online: the tenth may come online (the limit counts the modules that are already online, not the module itself)
        var (nine, nineWorld) = FitOf(statics, Enumerable.Range(0, 10).Select(i => (Id: 2000L + i, TypeId: booster, Online: i < 9)).ToArray());
        Assert.Null(CharacterShips.MaxGroupOnlineRefusal(nine, 11019, statics, nineWorld, nine[9]));
    }

    [PgFact]
    public async Task Modules_of_another_group_and_groups_without_a_limit_do_not_count_against_the_limit()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Rig.Url!));
        var sde = new SdeReader(source);
        var statics = await DogmaStatic.LoadAsync(sde);
        var booster = await TypeWithLimitAsync(sde, statics, CapacitorBoosterGroup);
        var afterburnerTypes = (await sde.ReadByFieldAsync("types", "groupID", [AfterburnerGroup]))
            .Select(row => checked((int)row.Key))
            .OrderBy(id => id)
            .ToArray();
        await statics.EnsureTypesAsync(afterburnerTypes);
        var afterburner = afterburnerTypes.First(typeId => !statics.Type(typeId)!.Attributes.ContainsKey(978));

        // nine boosters online and the rest of the fit is afterburners: the tenth booster is still allowed
        var others = Enumerable.Range(0, 8).Select(i => (Id: 3100L + i, TypeId: afterburner, Online: true));
        var fit = Enumerable.Range(0, 9).Select(i => (Id: 3000L + i, TypeId: booster, Online: true))
            .Concat(others)
            .Append((Id: 3200L, TypeId: booster, Online: false))
            .ToArray();
        var (fitted, world) = FitOf(statics, fit);
        Assert.Null(CharacterShips.MaxGroupOnlineRefusal(fitted, 11019, statics, world, fitted.Single(m => m.Id == 3200L)));
    }

    /// <summary>A published type of the group whose limit is the one the test reads (its <c>maxGroupOnline</c> is set).</summary>
    private static async Task<int> TypeWithLimitAsync(SdeReader sde, DogmaStatic statics, int group)
    {
        var candidates = (await sde.ReadByFieldAsync("types", "groupID", [group]))
            .Where(row => row.Doc.TryGetProperty("published", out var flag) && flag.ValueKind == JsonValueKind.True)
            .Select(row => checked((int)row.Key))
            .OrderBy(id => id)
            .ToArray();
        await statics.EnsureTypesAsync(candidates);
        return candidates.First(id => statics.Type(id)!.Attributes.ContainsKey(978));
    }

    /// <summary>The ship's fit as the host has it: each module fitted to the ship and online or offline, in the world the rule reads.</summary>
    private static (IReadOnlyList<Item> Fitted, DogmaWorld World) FitOf(DogmaStatic statics, (long Id, int TypeId, bool Online)[] modules)
    {
        var shipTypeId = 11019;
        var ship = new Item(ShipId, shipTypeId, Character, Rig.Station, 0, 1, true, null);
        var world = new DogmaWorld(statics, Character, ShipId);
        world.Add(new DogmaItem(ShipId, shipTypeId, Character, ship.LocationId, ship.Flag, ItemState.Online));
        var fitted = new List<Item>();
        foreach (var (id, typeId, online) in modules)
        {
            fitted.Add(new Item(id, typeId, Character, ShipId, 11, 1, true, null));
            world.Add(new DogmaItem(id, typeId, Character, ShipId, 11, online ? ItemState.Online : ItemState.Offline));
        }
        return (fitted, world);
    }
}
