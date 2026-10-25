using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Structure_control_delivery_deployment_and_beacon_calls_use_registered_host_routes()
    {
        var observed = new List<string>();
        var callbacks = new StructureRpcCallbacks
        {
            DropOffItemsAsync = (_, ids, shipId, structureId) =>
            {
                observed.Add($"drop:{shipId}:{structureId}:{ids.Count}");
                return ValueTask.FromResult<IReadOnlyList<long>>([ids[0], ids[^1]]);
            },
            GetStructurePilotAsync = (_, structureId) =>
            {
                observed.Add($"pilot:{structureId}");
                return ValueTask.FromResult<long?>(structureId == 7001 ? 9001 : null);
            },
            TakeControlAsync = (_, structureId) =>
            {
                observed.Add($"take:{structureId}");
                return ValueTask.CompletedTask;
            },
            BoardStructureAsync = (_, structureId) =>
            {
                observed.Add($"board:{structureId}");
                return ValueTask.CompletedTask;
            },
            EjectFromStructureAsync = (_, shipId) =>
            {
                observed.Add($"eject:{shipId}");
                return ValueTask.CompletedTask;
            },
            ReleaseControlAsync = _ =>
            {
                observed.Add("release");
                return ValueTask.CompletedTask;
            },
            DeliverAsync = (_, structureId, recipientId, ids) =>
            {
                observed.Add($"deliver:{structureId}:{recipientId}:{string.Join(',', ids)}");
                return ValueTask.CompletedTask;
            },
            AnchorAsync = (_, request) =>
            {
                observed.Add($"anchor:{request.ItemId}:{request.StructureName}:{request.ReinforceWeekday}:{request.ReinforceHour}");
                return ValueTask.CompletedTask;
            },
            UnanchorAsync = (_, structureId) =>
            {
                observed.Add($"unanchor:{structureId}");
                return ValueTask.CompletedTask;
            },
            CancelUnanchorAsync = (_, structureId) =>
            {
                observed.Add($"cancel-unanchor:{structureId}");
                return ValueTask.CompletedTask;
            },
            RenameStructureAsync = (_, structureId, name) =>
            {
                observed.Add($"rename:{structureId}:{name}");
                return ValueTask.CompletedTask;
            },
        };
        var services = new IService[]
        {
            new StructureRpcService("structureControl", callbacks),
            new StructureRpcService("structureCargoDelivery", callbacks),
            new StructureRpcService("structureDeliveries", callbacks),
            new StructureRpcService("structureDeployment", callbacks),
            new StructureTravelRpcService("structureCynoBeaconMgr", _ =>
            {
                observed.Add("beacon");
                return ValueTask.FromResult<PyValue>(PyNone.Instance);
            }),
        };

        Start(
            configure: registry =>
            {
                foreach (var service in services) registry.Register(service);
            },
            sessionOpened: session => session.Set("structureid", new PyInt(7001)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue> Call(string service, string method, PyTuple args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, (uint)(observed.Count + 1), service, method, args, kwargs))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload)) ?? PyNone.Instance;
        }

        Assert.Equal(new PyInt(9001), await Call("structureControl", "GetStructurePilot", Py.Tuple(new PyInt(7001))));
        Assert.Equal(PyNone.Instance, await Call("structureControl", "TakeControl", Py.Tuple()));
        Assert.Equal(PyNone.Instance, await Call("structureControl", "BoardStructure", Py.Tuple(new PyInt(7002))));
        Assert.Equal(PyNone.Instance, await Call("structureControl", "EjectFromStructure", Py.Tuple(new PyInt(7003))));
        Assert.Equal(PyNone.Instance, await Call("structureControl", "ReleaseControl", Py.Tuple()));

        var moved = Assert.IsType<PyList>(await Call(
            "structureCargoDelivery",
            "DropOffItems",
            Py.Tuple(new PyList(Enumerable.Range(1, 500).Select(id => (PyValue)new PyInt(id)).ToArray()), new PyInt(7004), new PyInt(7005))));
        Assert.Equal(new PyList([new PyInt(1), new PyInt(500)]), moved);

        Assert.Equal(PyNone.Instance, await Call(
            "structureDeliveries",
            "Deliver",
            Py.Tuple(new PyInt(7001), new PyInt(9002), new PyList([new PyInt(21), new PyInt(22)]))));
        Assert.Equal(PyNone.Instance, await Call(
            "structureDeployment",
            "Anchor",
            Py.Tuple(
                new PyInt(8001), new PyReal(1), new PyReal(2), new PyReal(0.5), new PyInt(7001),
                new PyStr("Structure"), new PyStr("bio"), new PyInt(3), new PyInt(17), new PyDict([]))));
        Assert.Equal(PyNone.Instance, await Call(
            "structureDeployment",
            "Unanchor",
            Py.Tuple(new PyInt(8001))));
        Assert.Equal(PyNone.Instance, await Call(
            "structureDeployment",
            "CancelUnanchor",
            Py.Tuple(new PyInt(8001))));
        Assert.Equal(PyNone.Instance, await Call(
            "structureDeployment",
            "RenameStructure",
            Py.Tuple(new PyInt(8001), new PyStr("Renamed"))));
        Assert.Equal(PyNone.Instance, await Call(
            "structureCynoBeaconMgr",
            "CmdJumpToStructureBeacon",
            Py.Tuple(new PyInt(7001), new PyInt(30000142))));

        Assert.Equal(
            [
                "pilot:7001", "take:7001", "board:7002", "eject:7003", "release",
                "drop:7004:7005:500", "deliver:7001:9002:21,22",
                "anchor:8001:Structure:3:17",
                "unanchor:8001", "cancel-unanchor:8001", "rename:8001:Renamed", "beacon",
            ],
            observed);
    }

    [Fact]
    public async Task Structure_directory_and_guests_use_registered_host_routes()
    {
        var storedStructures = new System.Text.Json.Nodes.JsonObject
        {
            ["1001"] = new System.Text.Json.Nodes.JsonObject
            {
                ["structureID"] = 1001,
                ["typeID"] = 35841,
                ["solarSystemID"] = 30000142,
                ["ownerCorpID"] = 8000,
                ["description"] = "before",
            },
        };
        async Task<System.Text.Json.Nodes.JsonObject> ReadTable(string table, CancellationToken _)
        {
            await Task.Yield();
            return table == "structures"
                ? (System.Text.Json.Nodes.JsonObject)storedStructures.DeepClone()
                : new System.Text.Json.Nodes.JsonObject();
        }
        Task<Eve.Relations.Affiliation> Affiliation(long _, CancellationToken __) =>
            Task.FromResult(new Eve.Relations.Affiliation(8000, null, null));
        Task SetDescription(long structureId, string description, CancellationToken _)
        {
            storedStructures[structureId.ToString()]!["description"] = description;
            return Task.CompletedTask;
        }

        var directoryRead = new PersistenceStructureDirectoryReadModel(
            ReadTable, Affiliation, null!, SetDescription,
            stationManagerOfAsync: (_, _, _) => Task.FromResult(true));
        long? guestsStructure = null;
        IReadOnlySet<long>? observerGuests = null;
        var guests = new StructureGuestsService(
            (structureId, _) =>
            {
                guestsStructure = structureId;
                return ValueTask.FromResult<IReadOnlyList<StructureGuestSnapshot>>(
                    [new(9002, 8002, 7002, 6002), new(9001, 8001, 7001, 6001)]);
            },
            (_, structureId, characterIds) =>
            {
                guestsStructure = structureId;
                observerGuests = characterIds;
            });

        Start(
            configure: registry =>
            {
                registry.Register(new StructureDirectoryService(directoryRead));
                registry.Register(guests);
            },
            sessionOpened: session =>
            {
                session.Set("charid", new PyInt(9000));
                session.Set("corpid", new PyInt(8000));
                session.Set("structureid", new PyInt(7001));
                session.Set("solarsystemid2", new PyInt(30000142));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        uint callId = 1;
        async Task<PyValue> Call(string service, string method, PyTuple args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId++, service, method, args, kwargs))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload)) ?? PyNone.Instance;
        }

        var bridgeAccess = Assert.IsType<PyTuple>(
            await Call("structureDirectory", "GetJumpBridgesWithMyAccess", Py.Tuple()));
        Assert.Equal(3, bridgeAccess.Items.Count);
        Assert.All(bridgeAccess.Items, value => Assert.Empty(Assert.IsType<PyList>(value).Items));
        Assert.Empty(Assert.IsType<PyList>(
            await Call("structureDirectory", "GetNearbyJumpBridges", Py.Tuple())).Items);
        Assert.Equal("before", System.Text.Encoding.UTF8.GetString(Assert.IsType<PyBytes>(await Call(
            "structureDirectory", "GetStructureDescription", Py.Tuple(new PyInt(1001)))).Value.Span));
        Assert.Equal(PyNone.Instance, await Call(
            "structureDirectory", "SetStructureDescription", Py.Tuple(new PyInt(1001), new PyStr("after"))));
        Assert.Equal("after", System.Text.Encoding.UTF8.GetString(Assert.IsType<PyBytes>(await Call(
            "structureDirectory", "GetStructureDescription", Py.Tuple(new PyInt(1001)))).Value.Span));

        var guestSnapshot = Assert.IsType<PyDict>(
            await Call("structureGuests", "GetGuests", Py.Tuple(new PyInt(7001))));
        Assert.Equal(2, guestSnapshot.Entries.Count);
        Assert.Equal(
            new PyList([new PyInt(8001), new PyInt(7001), new PyInt(6001)]),
            guestSnapshot.Entries.Single(entry => entry.Key == new PyInt(9001)).Value);
        Assert.Equal(7001, guestsStructure);
        Assert.Equal(new HashSet<long> { 9001, 9002 }, observerGuests);
    }

}
