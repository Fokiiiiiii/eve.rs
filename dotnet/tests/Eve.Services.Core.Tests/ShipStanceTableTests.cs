using System.Text.Json;
using Eve.Sde;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

/// <summary>
/// The stance table of the T3D hulls against the pinned SDE (SF-6): every hull is a Tactical Destroyer (group 1305) or the Anhinga (group 1201), each has three
/// distinct mode items of group 1306, the Defense, Propulsion and Sharpshooter modes are named for their stance, and the switch time (attribute 1985) that the
/// cooldown reads is in the static data for every hull.
/// </summary>
public class ShipStanceTableTests
{
    private const int ModeGroup = 1306, TacticalDestroyerGroup = 1305, AttackBattlecruiserGroup = 1201, SwitchTimeAttribute = 1985;
    private const int Defense = 1, Speed = 2, Sniper = 3;

    /// <summary>A test that reads the pinned SDE: it is skipped (and says so) without a PostgreSQL with the SDE loaded, and never passes silently.</summary>
    private sealed class PgFactAttribute : FactAttribute
    {
        public PgFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null)
            {
                Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
            }
        }
    }

    [PgFact]
    public async Task Every_stance_table_entry_is_a_mode_item_of_the_sde_for_its_hull()
    {
        var source = Npgsql.NpgsqlDataSource.Create(Eve.Data.PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var types = (await sde.ReadAsync("types")).ToDictionary(row => (int)row.Key, row => row.Doc);
        var dogma = (await sde.ReadAsync("typeDogma")).ToDictionary(row => (int)row.Key, row => row.Doc);

        Assert.Equal(6, ShipStanceMgrService.StanceModifiersByShipType.Count);
        foreach (var (hullId, stances) in ShipStanceMgrService.StanceModifiersByShipType)
        {
            var hull = types[hullId];
            var group = hull.GetProperty("groupID").GetInt32();
            Assert.True(group is TacticalDestroyerGroup or AttackBattlecruiserGroup, $"hull {hullId} is in group {group}");
            Assert.Equal(3, stances.Values.Distinct().Count());
            foreach (var (stance, modeId) in stances)
            {
                var mode = types[modeId];
                Assert.Equal(ModeGroup, mode.GetProperty("groupID").GetInt32());
                var name = mode.GetProperty("name").GetProperty("en").GetString() ?? "";
                if (group == TacticalDestroyerGroup)
                {
                    var keyword = stance switch { Defense => "Defense", Speed => "Propulsion", Sniper => "Sharpshooter", _ => "?" };
                    Assert.Contains(keyword, name);
                }
            }
            var switchTime = Attribute(dogma, hullId, SwitchTimeAttribute);
            Assert.True(switchTime > 0, $"hull {hullId} has no switch time");
        }
    }

    private static double Attribute(IReadOnlyDictionary<int, JsonElement> dogma, int typeId, int attribute) =>
        dogma.TryGetValue(typeId, out var doc) && doc.TryGetProperty("dogmaAttributes", out var attributes)
            ? attributes.EnumerateArray().Where(a => a.GetProperty("attributeID").GetInt32() == attribute).Select(a => a.GetProperty("value").GetDouble()).FirstOrDefault()
            : 0;
}
