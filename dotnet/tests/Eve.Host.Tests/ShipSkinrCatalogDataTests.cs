using System.Text.Json;
using Eve.Sde;

namespace Eve.Host.Tests;

public sealed class ShipSkinrCatalogDataTests
{
    [Fact]
    public void Keyed_maps_read_official_sde_jsonl_value_arrays()
    {
        using var pointValues = JsonDocument.Parse(
            "[{\"_key\":1,\"_value\":25},{\"_key\":2,\"_value\":50}]");

        var actual = ShipSkinrCatalog.ReadKeyedIntMap(pointValues.RootElement);

        Assert.Equal(new[] { (1, 25), (2, 50) },
            actual.OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value)));
    }

    [Fact]
    public void Slot_configurations_apply_ship_overrides_before_the_default()
    {
        var configurations = new[]
        {
            Row(5, "{\"allowAllShips\":true,\"config\":[1,2,3,4,5,6,7,8],\"priority\":3}"),
            Row(6, "{\"priority\":0,\"ships\":[100,200]}"),
            Row(7, "{\"priority\":1,\"config\":[1,3,4],\"ships\":[200,300]}"),
        };
        var groupByType = new Dictionary<int, int>
        {
            [100] = 10,
            [200] = 10,
            [300] = 10,
            [400] = 10,
            [500] = 20,
        };
        var categoryByGroup = new Dictionary<int, int>
        {
            [10] = 6,
            [20] = 1,
        };

        var actual = ShipSkinrCatalog.BuildSlotsByShipType(
            configurations, groupByType, categoryByGroup, shipCategoryId: 6);

        Assert.False(actual.ContainsKey(100));
        Assert.False(actual.ContainsKey(200));
        Assert.Equal(new[] { 1, 3, 4 }, actual[300].Order().ToArray());
        Assert.Equal(Enumerable.Range(1, 8), actual[400].Order());
        Assert.False(actual.ContainsKey(500));
    }

    private static SdeRow Row(long key, string json)
    {
        using var document = JsonDocument.Parse(json);
        return new SdeRow(key, document.RootElement.Clone());
    }
}
