using System.Text.Json;
using Eve.Sde;
using Eve.Services.Space.Sites;

namespace Eve.Services.Space.Tests;

public sealed class CosmicSiteAnchorTests
{
    [Fact]
    public void A_map_moon_row_becomes_a_site_anchor_in_its_static_position()
    {
        using var document = JsonDocument.Parse("""{"position":{"x":1,"y":2,"z":3}}""");
        var anchor = SiteAnchor.FromSdeRow(new SdeRow(9_000_001, document.RootElement));

        Assert.NotNull(anchor);
        Assert.Equal(9_000_001, anchor.Id);
        Assert.Equal(new[] { 1d, 2d, 3d }, anchor.Position);
    }

    [Fact]
    public void An_SDE_row_without_a_position_is_not_a_site_anchor()
    {
        using var document = JsonDocument.Parse("{}");

        Assert.Null(SiteAnchor.FromSdeRow(new SdeRow(9_000_001, document.RootElement)));
    }
}
