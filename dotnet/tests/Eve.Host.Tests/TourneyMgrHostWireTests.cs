using Eve.Marshal;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Tournament_ship_list_uses_the_dictionary_field_consumed_by_the_client_through_host_dispatch()
    {
        Start();
        var (front, open) = await OpenFrontAsync();

        var result = Assert.IsType<PyDict>(
            await HostRpcAsync(front, open, 1, "tourneyMgr", "QueryShipList", new PyInt(42_001)));
        var entry = Assert.Single(result.Entries);
        Assert.Equal("allowedShipList", WireText(entry.Key));
        Assert.Empty(Assert.IsType<PyList>(entry.Value).Items);
    }

    [Fact]
    public async Task Tournament_setup_and_matches_use_the_mappings_consumed_by_the_client_through_host_dispatch()
    {
        Start();
        var (front, open) = await OpenFrontAsync();

        var setup = Assert.IsType<PyTuple>(
            await HostRpcAsync(front, open, 1, "tourneyMgr", "GetTourneySetup"));
        Assert.Equal(3, setup.Items.Count);
        Assert.Empty(Assert.IsType<PyDict>(setup.Items[0]).Entries);
        Assert.Empty(Assert.IsType<PyDict>(setup.Items[1]).Entries);
        Assert.Empty(Assert.IsType<PyList>(setup.Items[2]).Items);

        var matches = Assert.IsType<PyDict>(
            await HostRpcAsync(front, open, 2, "tourneyMgr", "GetPotentialMatches", new PyInt(42_001)));
        Assert.Empty(matches.Entries);
    }
}
