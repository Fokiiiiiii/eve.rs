using Eve.Npc;
using Eve.Services.Space.Sites;
using Eve.Wormholes;

namespace Eve.Services.Space.Tests;

public sealed class WormholeSiteTemplatesTests
{
    /// <summary>The sites of a category in every class (C1 to C6 and C13).</summary>
    private static IEnumerable<Site> AllOf(string category) =>
        Enumerable.Range(1, 6).Append(WormholeSites.ShatteredClass).SelectMany(wormholeClass => WormholeSites.PoolOf(category, wormholeClass)).Distinct();

    [Fact]
    public void Every_planned_site_has_its_template_by_dungeon_key_name_kind_and_family()
    {
        var templates = SiteTemplates.Load();
        var planned = WormholeSites.CombatSites.Values.SelectMany(sites => sites).Select(site => (site, SiteTemplate.Anomaly, "combat"))
            .Concat(WormholeSites.DataSites.Values.SelectMany(sites => sites).Select(site => (site, SiteTemplate.Signature, "data")))
            .Concat(WormholeSites.RelicSites.Values.SelectMany(sites => sites).Select(site => (site, SiteTemplate.Signature, "relic")))
            .Concat(AllOf("gas").Select(site => (site, SiteTemplate.Signature, "gas")))
            .Concat(AllOf("ore").Select(site => (site, SiteTemplate.Anomaly, "ore")))
            .ToList();

        Assert.Equal(68, planned.Count);
        Assert.Equal(68, templates.All.Count(template => template.IsWormhole));
        foreach (var (site, kind, family) in planned)
        {
            var template = templates.ById($"wormhole-{site.DungeonKey}");
            Assert.NotNull(template);
            Assert.Equal(site.Name, template.Name);
            Assert.Equal(kind, template.Kind);
            Assert.Equal(family, template.Family);
            Assert.Equal(1908, template.StrengthAttribute);
            if (family is "combat" or "data" or "relic")
            {
                Assert.NotEmpty(template.Waves);
            }
            if (family == "gas")
            {
                Assert.NotEmpty(template.Clouds!);
                Assert.Empty(template.Rocks ?? []);
            }
            if (family == "ore")
            {
                Assert.NotEmpty(template.Rocks!);
                Assert.Null(template.Clouds);
            }
        }
    }

    [Fact]
    public void The_sleeper_waves_of_the_wormhole_sites_resolve_to_npc_pools_and_no_k_space_band_draws_them()
    {
        var selector = new NpcSelector(NpcAuthority.Load());
        var templates = SiteTemplates.Load();
        foreach (var query in templates.All.Where(template => template.IsWormhole).SelectMany(template => template.Waves).Select(wave => wave.Query).Distinct())
        {
            var (selection, error, _) = selector.Select(query);
            Assert.True(selection is not null, $"the wave {query} names no NPC: {error}");
        }
        Assert.DoesNotContain(templates.For(SiteTemplate.Anomaly, "combat", "highsec"), template => template.IsWormhole);
        Assert.DoesNotContain(templates.For(SiteTemplate.Signature, "data", "nullsec"), template => template.IsWormhole);
    }
}
