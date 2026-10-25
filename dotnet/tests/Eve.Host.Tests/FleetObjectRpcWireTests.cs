using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Fleet_object_rpcs_are_dispatched_through_host_with_transport_kwargs()
    {
        await using var dataSource = new NpgsqlDataSourceBuilder(
            "Host=127.0.0.1;Port=1;Database=eve;Username=postgres").Build();
        var fleet = new FleetObjectService(new FleetStore(dataSource), () => FixedNowMs);

        Start(configure: registry => registry.Register(fleet));

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue> Call(long callId, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, "fleetObjectHandler", method, new PyTuple(args), kwargs))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(reply.Payload))!;
        }

        var nextCallId = 1L;
        foreach (var method in new[]
        {
            "GetFleetMaxSize", "GetInitState", "GetJoinRequests", "GetMotd",
            "GetWings", "GetWings", "GetWings", "GetWings", "GetWings", "GetWings",
            "Init", "KickMember", "LeaveFleet", "LoadFleetSetup", "MakeLeader",
            "MassMoveMembers", "MoveMember", "RejectJoinRequest", "SendBroadcast",
            "SetAcceptsConduitJumpsValue", "SetAcceptsRegroupValue", "SetAutoJoinSquadID",
            "SetFleetMaxSize", "SetMotdEx", "SetOptions"
        })
        {
            var args = method switch
            {
                "Init" => new PyValue[] { PyNone.Instance, PyNone.Instance },
                "KickMember" or "MakeLeader" or "RejectJoinRequest" => [new PyLong(90_000_002)],
                "LoadFleetSetup" or "SetMotdEx" => [new PyWString("setup")],
                "MassMoveMembers" => [new PyList([new PyLong(90_000_002)]), new PyInt(-1), new PyInt(-1), new PyInt(4)],
                "MoveMember" => [new PyLong(90_000_002), new PyInt(-1), new PyInt(-1), new PyInt(4)],
                "SendBroadcast" => [new PyStr("EnemySpotted"), new PyInt(3), PyNone.Instance, PyNone.Instance],
                "SetAcceptsConduitJumpsValue" or "SetAcceptsRegroupValue" => [PyBool.True],
                "SetAutoJoinSquadID" or "SetFleetMaxSize" => [new PyInt(1)],
                "SetOptions" => [Py.KeyVal([("isFreeMove", PyBool.True)])],
                _ => []
            };

            var result = await Call(nextCallId++, method, args);
            if (method == "GetWings")
                Assert.IsType<PyDict>(result);
            else
                Assert.Same(PyNone.Instance, result);
        }
    }

    [Fact]
    public async Task Fleet_finder_rpcs_are_dispatched_through_host_with_transport_kwargs()
    {
        await using var dataSource = new NpgsqlDataSourceBuilder(
            "Host=127.0.0.1;Port=1;Database=eve;Username=postgres").Build();
        var store = new FleetStore(dataSource);
        var fleet = new FleetObjectService(store, () => FixedNowMs);
        var proxy = new FleetProxyService(store);

        Start(configure: registry =>
        {
            registry.Register(fleet);
            registry.Register(proxy);
        });

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue> Call(long callId, string service, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, callId, service, method, new PyTuple(args), kwargs))));
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            return Result(MarshalCodec.Decode(reply.Payload))!;
        }

        Assert.Same(PyNone.Instance, await Call(1, "fleetObjectHandler", "SetTakesFleetWarp", PyBool.True));
        Assert.Same(PyNone.Instance, await Call(2, "fleetObjectHandler", "UpdateMemberInfo", new PyInt(587)));

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 3, "fleetProxy", "ApplyToJoinFleet",
                new PyTuple([new PyLong(90_000_001), PyBool.False]), kwargs))));
        var refusal = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.IsType<PyObject>(MarshalCodec.Decode(refusal.Payload));

        var adverts = Assert.IsType<PyDict>(await Call(4, "fleetProxy", "GetAvailableFleetAds"));
        Assert.Empty(adverts.Entries);
        Assert.Same(PyNone.Instance, await Call(5, "fleetProxy", "AddFleetFinderAdvert",
            Py.KeyVal([("fleetName", new PyWString("Fleet")), ("description", new PyWString("Open"))])));
        Assert.Same(PyNone.Instance, await Call(6, "fleetProxy", "UpdateFleetAdvertWithNewLeader", Py.KeyVal()));
        Assert.Same(PyNone.Instance, await Call(7, "fleetProxy", "RemoveFleetFinderAdvert"));
        Assert.Same(PyNone.Instance, await Call(8, "fleetProxy", "GetMyFleetFinderAdvert"));
        Assert.Same(PyNone.Instance, await Call(9, "fleetProxy", "UpdateAdvertInfo",
            new PyInt(1), Py.KeyVal()));
        Assert.Same(PyNone.Instance, await Call(10, "fleetProxy", "UpdateAdvertAllowedEntities", Py.KeyVal()));
    }
}
