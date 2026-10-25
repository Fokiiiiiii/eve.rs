using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.CharacterInfo;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task CharMgr_observed_reads_and_personal_actions_accept_macho_kwargs_over_registered_host_routes()
    {
        var forwarded = new List<(string Method, IReadOnlyList<PyValue> Args)>();
        var charMgr = new CharMgrService(
            characters: null!,
            homeStations: null!,
            readRecentShipKillmailPage: (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<PyValue>>([]),
            readCharacterDescription: (_, _) => ValueTask.FromResult<string?>(null),
            writeCharacterDescription: (_, _, _) => ValueTask.CompletedTask,
            readCharacterSecurityStatus: (_, _) => ValueTask.FromResult(0d),
            readMedal1GraphicId: (_, _) => ValueTask.FromResult<long?>(null),
            globalAssetsInventoryCall: call =>
            {
                forwarded.Add((call.Method, call.Args.ToArray()));
                return ValueTask.FromResult<PyValue>(call.Method switch
                {
                    "ListStations" => Py.Rowset(
                        ["stationID", "solarSystemID", "typeID", "itemCount", "upkeepState"],
                        [Py.Tuple(new PyInt(60000001), new PyInt(30000142), new PyInt(1529), new PyInt(2), PyNone.Instance)]),
                    "ListStationItems" or "ListIncludingContainers" => new PyList([Py.Tuple(new PyInt(9001))]),
                    _ => throw new InvalidOperationException($"Unexpected global assets route: {call.Method}"),
                });
            });
        Start(
            configure: registry => registry.Register(charMgr),
            sessionOpened: session => session.Set("corporationID", new PyInt(1001)));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        var settings = Assert.IsType<PyTuple>(await CallOverHost(front, open, 1, "charMgr", "GetSettingsInfo", Py.Tuple([])));
        Assert.IsType<PyBuffer>(settings.Items[0]);
        Assert.Equal(new PyInt(0), settings.Items[1]);

        var privateInfo = Assert.IsType<PyObject>(await CallOverHost(
            front, open, 2, "charMgr", "GetPrivateInfoOnCorpChange", Py.Tuple(new PyInt(1001))));
        Assert.Equal(new PyInt(1001), RpcCoverageWireField(privateInfo, "corporationID"));
        Assert.Equal(PyNone.Instance, RpcCoverageWireField(privateInfo, "corporationDateTime"));
        Assert.Equal(PyNone.Instance, await CallOverHost(
            front, open, 3, "charMgr", "GetRecentShipKillsAndLosses", Py.Tuple(new PyInt(25), new PyInt(0))));

        var stations = await CallOverHost(front, open, 4, "charMgr", "ListStations", Py.Tuple([]));
        Assert.Equal(
            MarshalCodec.Decode(MarshalCodec.Encode(Py.Rowset(
                ["stationID", "solarSystemID", "typeID", "itemCount", "upkeepState"],
                [Py.Tuple(new PyInt(60000001), new PyInt(30000142), new PyInt(1529), new PyInt(2), PyNone.Instance)]))),
            stations);
        var stationItems = Assert.IsType<PyList>(await CallOverHost(front, open, 5, "charMgr", "ListStationItems", Py.Tuple(new PyInt(60000001))));
        var nestedItems = Assert.IsType<PyList>(await CallOverHost(front, open, 6, "charMgr", "ListIncludingContainers", Py.Tuple([])));
        Assert.Single(stationItems.Items);
        Assert.Single(nestedItems.Items);
        Assert.Equal(
            ["ListStations", "ListStationItems", "ListIncludingContainers"],
            forwarded.Select(call => call.Method));
        Assert.Equal(new PyInt(60000001), Assert.Single(forwarded[1].Args));

        Assert.Equal(PyNone.Instance, await CallOverHost(front, open, 7, "charMgr", "LogSettings", Py.Tuple(Py.Dict(("seen", PyValue.Of(true))))));
        Assert.Equal(PyNone.Instance, await CallOverHost(front, open, 8, "charMgr", "RemoveOwnerNote", Py.Tuple(new PyInt(1))));
    }
}
