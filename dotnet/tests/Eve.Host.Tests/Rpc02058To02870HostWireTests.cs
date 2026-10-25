using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Eve.GameStore;
using Eve.Host;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Station;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Map_beacon_and_industry_cost_maps_keep_the_dict_shape_consumed_by_the_client()
    {
        const long systemId = 30_000_142;
        await using var source = NpgsqlDataSource.Create("Host=localhost;Timeout=1");
        var map = new MapService(new Eve.ReadSide.ReadSide(source), () => FixedNowMs)
        {
            ReadDataHook = call => ValueTask.FromResult<PyValue?>(call.Method switch
            {
                "GetBeaconCount" => new PyDict([
                    new(new PyInt(systemId), new PyInt(2)),
                ]),
                "GetIndustryCostModifier" when call.Arg(0) is PyInt { Value: 1 } => new PyDict([
                    new(new PyInt(systemId), new PyReal(0.125)),
                ]),
                _ => null,
            }),
        };
        var route = new IncursionAnomalyService(
            "map",
            _ => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            (_, _) => ValueTask.FromResult<IReadOnlyList<JsonObject>>([]),
            fallback: map,
            nowUnixMs: () => FixedNowMs);
        Start(configure: registry => registry.Register(route), strictRpc: true);
        var (front, open) = await OpenFrontAsync();

        var beacons = Assert.IsType<PyDict>(
            await HostRpcAsync(front, open, 1, "map", "GetBeaconCount"));
        Assert.Equal(new PyInt(2),
            Assert.Single(beacons.Entries, entry => entry.Key == new PyInt(systemId)).Value);

        var costs = Assert.IsType<PyDict>(
            await HostRpcAsync(front, open, 2, "map", "GetIndustryCostModifier", new PyInt(1)));
        Assert.Equal(new PyReal(0.125),
            Assert.Single(costs.Entries, entry => entry.Key == new PyInt(systemId)).Value);
    }

    [Fact]
    public async Task Overview_preset_links_round_trip_through_the_registered_host_service_with_macho_version()
    {
        JsonObject? persisted = null;
        var service = new Eve.Services.Core.OverviewPresetMgrService(
            () => persisted?.DeepClone() as JsonObject,
            state =>
            {
                persisted = state.DeepClone() as JsonObject;
                return true;
            });
        StartWithCoreServicesOverridden(registry => registry.Register(service), strictRpc: true);
        var (front, open) = await OpenFrontAsync();
        var kwargs = new PyDict([new(new PyStr("machoVersion"), new PyInt(1))]);
        uint callId = 0;

        async Task<PyValue?> Call(string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open, ++callId, "overviewPresetMgr", method, new PyTuple(args), kwargs))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(reply.Payload));
        }

        var payload = Py.KeyVal(
            ("presetName", new PyStr("Default")),
            ("tabOrder", new PyList([new PyInt(2), new PyInt(1)])));
        var key = Assert.IsType<PyObject>(await Call("StoreLinkAndGetID", payload));
        Assert.Equal(new PyInt(1), WireField(key, "sqID"));
        var stored = Assert.IsType<PyStr>(await Call("GetStoredPreset", key));
        Assert.Equal("[[\"presetName\",\"Default\"],[\"tabOrder\",[2,1]]]", stored.Value);

        var repeated = Assert.IsType<PyObject>(await Call("StoreLinkAndGetID", payload));
        Assert.Equal(WireField(key, "hashvalue"), WireField(repeated, "hashvalue"));
        Assert.Equal(WireField(key, "sqID"), WireField(repeated, "sqID"));
        Assert.Same(PyNone.Instance, await Call("GetStoredPreset", Py.KeyVal(
            ("hashvalue", new PyStr("missing")),
            ("sqID", new PyInt(99)))));
    }

    [Fact]
    public async Task Structure_hangar_view_is_returned_from_persisted_structure_state_through_host_dispatch()
    {
        const long structureId = 30_000_142;
        Task<JsonObject> ReadTable(string table, CancellationToken _)
        {
            Assert.Equal("structures", table);
            return Task.FromResult(new JsonObject
            {
                [structureId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                {
                    ["state"] = 1,
                    ["upkeepState"] = 2,
                    ["shield"] = 1.0,
                    ["armor"] = 0.75,
                    ["hull"] = 0.5,
                    ["stateStartedAt"] = 1_700_000_000_000L,
                    ["stateEndsAt"] = 1_700_900_000_000L,
                },
            });
        }

        var backend = new PersistenceStructureProfilesRpcBackend(ReadTable, null!, null!);
        var service = new StructureRpcService("structureHangarViewMgr", new StructureRpcCallbacks
        {
            GetMyHangarViewStateAsync = (call, id) => backend.GetMyHangarViewStateAsync(id, call.Cancellation),
        });
        Start(
            configure: registry => registry.Register(service),
            strictRpc: true,
            sessionOpened: session => session.Set("structureid", new PyInt(structureId)));
        var (front, open) = await OpenFrontAsync();

        var state = Assert.IsType<PyObjectEx>(
            await HostRpcAsync(front, open, 1, "structureHangarViewMgr", "GetMyHangarViewState"));
        Assert.Equal(ObjectExKind.Ex2, state.Kind);
        var header = Assert.IsType<PyList>(state.Header).Items;
        var attributes = Assert.IsType<PyDict>(header[1]);
        Assert.Contains(attributes.Entries,
            entry => entry.Key == new PyStr("_operatingState") && entry.Value == new PyInt(1));
        Assert.Contains(attributes.Entries,
            entry => entry.Key == new PyStr("_upkeepState") && entry.Value == new PyInt(2));
        Assert.Contains(attributes.Entries,
            entry => entry.Key == new PyStr("_damageState") && entry.Value is PyList);
    }

    private void StartWithCoreServicesOverridden(Action<ServiceRegistry> configure, bool strictRpc)
    {
        var registry = new ServiceRegistry();
        Func<long> now = () => FixedNowMs;
        foreach (var registered in CoreServices.Create(new NoAccounts(), now, new TestCalendarStore(), new TestCalendarAudienceReader()))
            registry.Register(registered);
        configure(registry);

        var config = GlobalConfig.Build(Options);
        registry.Register(new MachoNetService(new Info(registry, config), now));
        var dispatcher = new PacketDispatcher(registry, now) { StrictRpc = strictRpc };
        var encoded = MarshalCodec.Encode(config);
        _server = new HostServer(
            new HostOptions { MaxFrontConnections = 1024 },
            dispatcher,
            new InMemoryAccountDirectory(new AccountOptions { AutoCreate = true }),
            () => encoded);
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(16);
        _serving = _server.ServeAsync(_listener, _cancel.Token);
    }
}
