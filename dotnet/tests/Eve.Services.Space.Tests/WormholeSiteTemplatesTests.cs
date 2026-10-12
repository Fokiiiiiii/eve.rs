using Eve.Npc;
using Eve.Services.Space.Sites;
using Eve.Wormholes;

namespace Eve.Services.Space.Tests;

public sealed class WormholeSiteTemplatesTests
{
    [Fact]
    public void Every_planned_combat_data_and_relic_site_has_its_template_by_dungeon_key_name_and_kind()
    {
        var templates = SiteTemplates.Load();
        var planned = WormholeSites.CombatSites.Values.SelectMany(sites => sites).Select(site => (site, SiteTemplate.Anomaly, "combat"))
            .Concat(WormholeSites.DataSites.Values.SelectMany(sites => sites).Select(site => (site, SiteTemplate.Signature, "data")))
            .Concat(WormholeSites.RelicSites.Values.SelectMany(sites => sites).Select(site => (site, SiteTemplate.Signature, "relic")))
            .ToList();

        Assert.Equal(48, planned.Count);
        Assert.Equal(48, templates.All.Count(template => template.IsWormhole));
        foreach (var (site, kind, family) in planned)
        {
            var template = templates.ById($"wormhole-{site.DungeonKey}");
            Assert.NotNull(template);
            Assert.Equal(site.Name, template.Name);
            Assert.Equal(kind, template.Kind);
            Assert.Equal(family, template.Family);
            Assert.NotEmpty(template.Waves);
            Assert.Equal(1908, template.StrengthAttribute);
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
