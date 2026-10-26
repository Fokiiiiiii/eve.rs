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

    [Fact]
    public async Task Character_manager_client_mutations_keep_their_registered_host_reply_shapes()
    {
        const long characterId = 90_000_001;
        var character = new Eve.Characters.Character(
            characterId, 1, "Wire Pilot", 1, 1, 1, 1, 35, 1373, 1_000_001,
            null, null, 60_000_001, 60_000_001, 30_000_142, 20_000_020, 10_000_002, null,
            DateTimeOffset.UnixEpoch, null, null);
        var personal = Eve.Services.CharacterInfo.CharMgrPersonalData.Empty with { BlockedSenders = [9_000_002] };
        string? description = null;
        (long Status, string? ExtraInfo)? activity = null;
        var charMgr = new CharMgrService(
            characters: null!,
            homeStations: null!,
            readRecentShipKillmailPage: (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<PyValue>>([]),
            readCharacterDescription: (_, _) => ValueTask.FromResult(description),
            writeCharacterDescription: (_, value, _) =>
            {
                description = value;
                return ValueTask.CompletedTask;
            },
            readCharacterSecurityStatus: (_, _) => ValueTask.FromResult(0d),
            readMedal1GraphicId: (_, _) => ValueTask.FromResult<long?>(null),
            readPersonalData: (_, _) => ValueTask.FromResult<Eve.Services.CharacterInfo.CharMgrPersonalData?>(personal),
            updatePersonalData: (_, update, _) =>
            {
                personal = update(personal);
                return ValueTask.FromResult<Eve.Services.CharacterInfo.CharMgrPersonalData?>(personal);
            },
            writeActivityStatus: (_, status, extraInfo, _) =>
            {
                activity = (status, extraInfo);
                return ValueTask.CompletedTask;
            },
            readCharacterById: (id, _) => ValueTask.FromResult<Eve.Characters.Character?>(id == characterId ? character : null));
        Start(configure: registry => registry.Register(charMgr),
            sessionOpened: session => session.CharacterId = characterId);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        Assert.Equal(PyNone.Instance, await CallOverHost(front, open, 1, "charMgr", "SetCharacterDescription",
            Py.Tuple(new PyStr("client biography"))));
        Assert.Equal("client biography", T(await CallOverHost(front, open, 2, "charMgr", "GetCharacterDescription",
            Py.Tuple(new PyInt(characterId)))));

        var activityKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("status"), new PyInt(1)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("extraInfo"), new PyLong(134_273_341_290_000_000)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 3, "charMgr", "SetActivityStatus", new PyTuple([]), activityKwargs))));
        var activityReply = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Same(PyNone.Instance, Result(MarshalCodec.Decode(activityReply.Payload)));
        Assert.Equal((1L, "134273341290000000"), activity);

        Assert.Equal(PyNone.Instance, await CallOverHost(front, open, 4, "charMgr", "SetNote",
            Py.Tuple(new PyInt(9_000_003), new PyStr("client note"))));
        Assert.Equal("client note", T(await CallOverHost(front, open, 5, "charMgr", "GetNote",
            Py.Tuple(new PyInt(9_000_003)))));

        Assert.Equal(PyNone.Instance, await CallOverHost(front, open, 6, "charMgr", "UnblockOwners",
            Py.Tuple(new PyList([new PyInt(9_000_002)]))));
        Assert.Empty(personal.BlockedSenders);

        Assert.Same(PyNone.Instance, await CallOverHost(front, open, 7, "charMgr", "GetCharacterDescription",
            Py.Tuple(new PyInt(characterId + 1))));
    }
}
