using System.Text.Json;
using Eve.Inventory;
using Eve.Sde;

namespace Eve.Ships.Tests;

/// <summary>
/// The hull data the ships are built from, checked against the pinned SDE for all 423 published hulls (SF-8, SF-9). A hull's cargo is the SDE's <c>types.capacity</c>
/// (<c>DogmaStatic</c> intrinsic attribute 38); its volume is the SDE's assembled volume; a packaged hull takes the client's packaged volume for its group
/// (<see cref="ItemVolumes"/>). The known data gaps of the SDE are pinned here, so a change in the static data is seen.
/// </summary>
public class HullDataTests
{
    private const int ShipCategory = 6, StrategicCruiserGroup = 963;
    private const int CapacityAttribute = 38, VolumeAttribute = 161, SubsystemHoldAttribute = 2675;

    /// <summary>Published hulls the SDE gives no cargo capacity: the two capsules and the Monitor (SF-8; cargo is 0 for them, as the SDE says).</summary>
    private static readonly int[] HullsWithoutCapacity = [670, 33328, 45534];

    /// <summary>Published hulls the SDE gives no skill requirement: the two capsules, Echelon and Primae (SF-9; no skill is required to fly them, as the SDE says).</summary>
    private static readonly int[] HullsWithoutSkill = [670, 33328, 3532, 2863];

    /// <summary>The one published hull the SDE gives no type bonus, and no dogma effect either (the Opux Luxury Yacht, SF-9).</summary>
    private const int HullWithoutTypeBonus = 635;

    private static async Task<List<(int TypeId, int GroupId, JsonElement Doc)>> PublishedHullsAsync(SdeReader sde)
    {
        var groups = (await sde.ReadAsync("groups")).ToDictionary(g => (int)g.Key, g => g.Doc.GetProperty("categoryID").GetInt32());
        return (await sde.ReadAsync("types"))
            .Where(row => groups.GetValueOrDefault(row.Doc.GetProperty("groupID").GetInt32()) == ShipCategory
                && row.Doc.TryGetProperty("published", out var published) && published.ValueKind == JsonValueKind.True)
            .Select(row => ((int)row.Key, row.Doc.GetProperty("groupID").GetInt32(), row.Doc))
            .ToList();
    }

    [PgFact]
    public async Task Every_published_hull_takes_its_cargo_and_volume_from_the_static_data()
    {
        await using var rig = await Rig.CreateAsync();
        var sde = new SdeReader(rig.Source);
        var hulls = await PublishedHullsAsync(sde);
        Assert.Equal(423, hulls.Count);
        await rig.Statics.EnsureTypesAsync(hulls.Select(h => h.TypeId));

        var withoutCapacity = new List<int>();
        foreach (var (typeId, groupId, doc) in hulls)
        {
            var type = rig.Statics.Type(typeId)!;
            if (doc.TryGetProperty("capacity", out var capacity) && capacity.ValueKind == JsonValueKind.Number)
            {
                Assert.Equal(capacity.GetDouble(), type.Attributes[CapacityAttribute]);
            }
            else
            {
                withoutCapacity.Add(typeId);
            }
            Assert.Equal(doc.GetProperty("volume").GetDouble(), type.Attributes[VolumeAttribute]);
            if (groupId == StrategicCruiserGroup)
            {
                Assert.True(type.Attributes.GetValueOrDefault(SubsystemHoldAttribute) > 0, $"hull {typeId} has no subsystem hold");
            }
        }
        Assert.Equal(HullsWithoutCapacity.Order(), withoutCapacity.Order());
        Assert.Equal(5000, ItemVolumes.Packaged(29984, StrategicCruiserGroup, 92_000));
        Assert.Equal(2500, ItemVolumes.Packaged(29999, 25, 10_000));
    }

    [PgFact]
    public async Task The_known_data_gaps_of_the_published_hulls_are_exactly_the_ones_pinned()
    {
        await using var rig = await Rig.CreateAsync();
        var sde = new SdeReader(rig.Source);
        var hulls = await PublishedHullsAsync(sde);
        await rig.Statics.EnsureTypesAsync(hulls.Select(h => h.TypeId));
        var typeBonuses = (await sde.ReadAsync("typeBonus")).Select(row => (int)row.Key).ToHashSet();

        var withoutSkill = hulls.Where(h => !rig.Statics.Type(h.TypeId)!.RequiredSkills.Any()).Select(h => h.TypeId).ToList();
        var withoutBonus = hulls.Where(h => !typeBonuses.Contains(h.TypeId)).Select(h => h.TypeId).ToList();

        Assert.Equal(HullsWithoutSkill.Order(), withoutSkill.Order());
        Assert.Equal([HullWithoutTypeBonus], withoutBonus);
        Assert.Empty(rig.Statics.Type(HullWithoutTypeBonus)!.EffectIds);
    }
}
