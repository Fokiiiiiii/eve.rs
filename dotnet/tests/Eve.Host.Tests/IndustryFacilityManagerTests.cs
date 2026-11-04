using Eve.Host;
using Eve.Marshal;
using Xunit;

namespace Eve.Host.Tests;

public sealed class IndustryFacilityManagerTests
{
    [Fact]
    public void Max_activity_modifiers_include_each_client_activity_with_its_cost_index()
    {
        var indexes = new Dictionary<int, decimal>
        {
            [1] = 0.11m,
            [3] = 0.12m,
            [4] = 0.13m,
            [5] = 0.14m,
            [8] = 0.15m,
            [9] = 0.16m,
        };

        var response = Assert.IsType<PyDict>(CharacterServicesComposition.ActivityModifiersPayload(indexes));
        Assert.Equal(indexes.Count, response.Entries.Count);
        foreach (var (activityId, expected) in indexes)
        {
            var row = Assert.Single(response.Entries, entry => entry.Key is PyInt key && key.Value == activityId);
            Assert.Equal((double)expected, Assert.IsType<PyReal>(row.Value).Value);
        }
    }
}
