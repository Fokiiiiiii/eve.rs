using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.CharacterInfo;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Char_mgr_item_list_routes_return_item_records_with_client_accessed_fields()
    {
        var item = Py.KeyVal(
            ("itemID", new PyLong(9001)),
            ("typeID", new PyInt(34)),
            ("ownerID", new PyLong(42)),
            ("locationID", new PyLong(60000001)),
            ("flagID", new PyInt(4)),
            ("quantity", new PyInt(3)));
        var charMgr = new CharMgrService(
            characters: null!,
            homeStations: null!,
            readRecentShipKillmailPage: (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<PyValue>>([]),
            readCharacterDescription: (_, _) => ValueTask.FromResult<string?>(null),
            writeCharacterDescription: (_, _, _) => ValueTask.CompletedTask,
            readCharacterSecurityStatus: (_, _) => ValueTask.FromResult(0d),
            readMedal1GraphicId: (_, _) => ValueTask.FromResult<long?>(null),
            globalAssetsInventoryCall: _ => ValueTask.FromResult<PyValue>(new PyList([item])));

        Start(configure: registry => registry.Register(charMgr));
        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        foreach (var (callId, method, args) in new (long, string, PyValue)[]
        {
            (1, "List", Py.Tuple([])),
            (2, "ListStationItems", Py.Tuple(new PyLong(60000001))),
            (3, "ListIncludingContainers", Py.Tuple([])),
        })
        {
            var rows = Assert.IsType<PyList>(
                await CallOverHost(front, open, callId, "charMgr", method, args));
            var row = Assert.IsType<PyObject>(Assert.Single(rows.Items));
            Assert.Equal(new PyLong(9001), RpcCoverageWireField(row, "itemID"));
            Assert.Equal(new PyInt(34), RpcCoverageWireField(row, "typeID"));
            Assert.Equal(new PyInt(3), RpcCoverageWireField(row, "quantity"));
        }
    }
}
