using Eve.Marshal;
using Eve.Services.Corporations;
using Eve.ReadSide;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Standing_history_and_composition_keep_client_wire_shapes_through_host_dispatch()
    {
        await using var source = NpgsqlDataSource.Create("Host=localhost;Timeout=1");
        var service = new StandingMgrService(new Eve.ReadSide.ReadSide(source), null!, () => FixedNowMs);
        Start(configure: registry => registry.Register(service));
        var (front, open) = await OpenFrontAsync();

        var transactions = Assert.IsType<PyList>(await HostRpcAsync(
            front, open, 1, "standingMgr", "GetStandingTransactions", new PyLong(500001), new PyLong(900001)));
        Assert.Empty(transactions.Items);

        var cached = Assert.IsType<PyObject>(await HostRpcAsync(
            front, open, 2, "standingMgr", "GetStandingCompositions", new PyLong(500001), new PyLong(980001)));
        Assert.Equal("carbon.common.script.net.objectCaching.CachedMethodCallResult", WireText(cached.Name));
        var payload = Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(cached.Args).Items[1]).Decoded!;
        var rowset = Assert.IsType<PyObject>(payload);
        Assert.Equal("carbon.common.script.sys.crowset.CRowset", WireText(rowset.Name));
        Assert.Empty(Assert.IsType<PyList>(WireField(rowset, "lines")).Items);
    }
}
