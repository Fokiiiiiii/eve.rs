namespace Eve.Wormholes.Tests;

/// <summary>The project's site rules: the count, the shares, the combat names of the class, and the five combat sites of a Drifter system.</summary>
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
    public void The_categories_follow_the_shares_and_only_combat_has_a_name_from_the_class_list()
    {
        var categories = new Dictionary<string, int>();
        for (ulong seed = 1; seed <= 2000; seed++)
        {
            foreach (var signature in WormholeSites.Plan(3, drifter: false, seed))
            {
                categories[signature.Category] = categories.GetValueOrDefault(signature.Category) + 1;
                if (signature.Category == "combat")
                {
                    Assert.Contains(signature.TemplateName, WormholeSites.CombatNames[3]);
                }
                else
                {
                    Assert.Null(signature.TemplateName);
                }
            }
        }
        var total = categories.Values.Sum();
        Assert.InRange(categories["combat"] / (double)total, 0.37, 0.43);
        Assert.InRange(categories["gas"] / (double)total, 0.17, 0.23);
        Assert.InRange(categories["ore"] / (double)total, 0.08, 0.12);
    }

    [Fact]
    public void A_drifter_system_has_exactly_five_combat_sites_and_c13_uses_the_class_six_list()
    {
        foreach (var seed in Enumerable.Range(1, 50))
        {
            var drifter = WormholeSites.Plan(14, drifter: true, (ulong)seed);
            Assert.Equal(5, drifter.Count);
            Assert.All(drifter, signature => Assert.Equal("combat", signature.Category));
        }
        var shattered = WormholeSites.Plan(13, drifter: false, 4);
        Assert.All(shattered.Where(signature => signature.Category == "combat"), signature => Assert.Contains(signature.TemplateName, WormholeSites.CombatNames[6]));
    }
}
