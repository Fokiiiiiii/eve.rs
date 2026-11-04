using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Security_modified_systems_returns_the_empty_client_rowset_through_host_wire()
    {
        Start(configure: registry => registry.Register(new SecurityMgrService()), strictRpc: true);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var request = Packets.CallWithKwargs(
            open, 1, "securityMgr", "get_modified_systems", new PyTuple([]),
            Py.Dict(("machoVersion", new PyInt(1))));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));
        var value = Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));

        var rowset = Assert.IsType<PyObjectEx>(value);
        Assert.Equal(ObjectExKind.Ex2, rowset.Kind);
        Assert.Empty(rowset.List);
        Assert.Empty(rowset.Dict);
        var header = Assert.IsType<PyTuple>(rowset.Header);
        var rowsetType = Assert.IsType<PyTuple>(header.Items[0]);
        Assert.Equal(new PyToken("carbon.common.script.sys.crowset.CIndexedRowset"), rowsetType.Items[0]);
        var metadata = Assert.IsType<PyDict>(header.Items[1]);
        Assert.Equal(
            new PyStr("solarSystemID"),
            metadata.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == "columnName").Value);
    }
}
