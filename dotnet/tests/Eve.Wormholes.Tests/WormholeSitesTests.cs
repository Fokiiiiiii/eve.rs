namespace Eve.Wormholes.Tests;

public sealed class WormholeSitesTests
{
    [Fact]
    public void A_system_has_four_to_nine_signatures_and_the_same_plan_for_the_same_seed()
    {
        foreach (var seed in Enumerable.Range(1, 300))
        {
            var planned = WormholeSites.Plan(2, drifter: false, (ulong)seed);
            Assert.InRange(planned.Count, 4, 9);
            Assert.Equal(planned, WormholeSites.Plan(2, drifter: false, (ulong)seed));
        }
    }

    [Fact]
    public void The_categories_follow_the_shares_and_each_combat_site_is_named_from_its_class_list()
    {
        var categories = new Dictionary<string, int>();
        var combatNames = WormholeSites.CombatSites[3].Select(site => site.Name).ToArray();
        for (ulong seed = 1; seed <= 2000; seed++)
        {
            foreach (var signature in WormholeSites.Plan(3, drifter: false, seed))
            {
                categories[signature.Category] = categories.GetValueOrDefault(signature.Category) + 1;
                if (signature.Category == "combat")
                {
                    Assert.Contains(signature.SiteName, combatNames);
                }
            }
        }
        var total = categories.Values.Sum();
        Assert.InRange(categories["combat"] / (double)total, 0.37, 0.43);
        Assert.InRange(categories["relic"] / (double)total, 0.12, 0.18);
        Assert.InRange(categories["data"] / (double)total, 0.12, 0.18);
        Assert.InRange(categories["gas"] / (double)total, 0.17, 0.23);
        Assert.InRange(categories["ore"] / (double)total, 0.08, 0.12);
    }

    [Fact]
    public void Gas_and_ore_follow_the_zones_of_the_class()
    {
        var c1Gas = new HashSet<string>();
        var c2Gas = new HashSet<string>();
        var c5Ore = new HashSet<string>();
        for (ulong seed = 1; seed <= 2000; seed++)
        {
            foreach (var signature in WormholeSites.Plan(1, drifter: false, seed).Where(s => s.Category == "gas"))
            {
                c1Gas.Add(signature.SiteName);
            }
            foreach (var signature in WormholeSites.Plan(2, drifter: false, seed).Where(s => s.Category == "gas"))
            {
                c2Gas.Add(signature.SiteName);
            }
            foreach (var signature in WormholeSites.Plan(5, drifter: false, seed).Where(s => s.Category == "ore"))
            {
                c5Ore.Add(signature.SiteName);
            }
        }
        // C1 and C2 have only the perimeter reservoirs; the frontier and core reservoirs need C3 and C5.
        Assert.All(c1Gas, name => Assert.EndsWith("Perimeter Reservoir", name));
        Assert.All(c2Gas, name => Assert.EndsWith("Perimeter Reservoir", name));
        Assert.Contains("Exceptional Core Deposit", c5Ore);
        Assert.DoesNotContain("Shattered Debris Field", c5Ore);
    }

    [Fact]
    public void Every_site_name_has_one_client_dungeon_key_and_all_the_wiki_sites_are_reachable()
    {
        var keys = new Dictionary<string, int>();
        foreach (var wormholeClass in new[] { 1, 2, 3, 4, 5, 6, 13 })
        {
            for (ulong seed = 1; seed <= 3000; seed++)
            {
                foreach (var signature in WormholeSites.Plan(wormholeClass, drifter: false, seed))
                {
                    Assert.True(keys.TryAdd(signature.SiteName, signature.DungeonKey) || keys[signature.SiteName] == signature.DungeonKey);
                }
            }
        }
        // 24 anomalies, 12 data, 12 relic, 9 gas reservoirs, 11 ore deposits (the Shattered Ice Field is an ice belt, not planned).
        Assert.Equal(68, keys.Count);
        Assert.Equal(3559, keys["Sizeable Perimeter Reservoir"]);
        Assert.Equal(5605, keys["Shattered Debris Field"]);
    }

    [Fact]
    public void A_drifter_system_has_exactly_five_combat_sites_and_c13_uses_the_class_six_lists()
    {
        foreach (var seed in Enumerable.Range(1, 50))
        {
            var drifter = WormholeSites.Plan(14, drifter: true, (ulong)seed);
            Assert.Equal(5, drifter.Count);
            Assert.All(drifter, signature => Assert.Equal("combat", signature.Category));
        }
        var classSix = WormholeSites.CombatSites[6].Select(site => site.Name).ToArray();
        var shattered = WormholeSites.Plan(13, drifter: false, 4);
        Assert.All(shattered.Where(signature => signature.Category == "combat"), signature => Assert.Contains(signature.SiteName, classSix));
    }
}
