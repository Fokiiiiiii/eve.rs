using Eve.Crimewatch;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Crimewatch;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Crimewatch_bound_get_character_security_status_uses_the_registered_host_route()
    {
        const long characterId = 90000001;
        const long targetId = 90000002;
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Database=eve;Username=postgres;Timeout=1;Command Timeout=1");
        var service = new CrimewatchService(
            new PgCrimewatchStore(dataSource), null!, () => 1_800_000_000_000,
            (id, _) => Task.FromResult(id == targetId ? 2.5 : 0d));
        Start(strictRpc: true,
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("charid", new PyLong(characterId));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "crimewatch", "MachoBindObject",
                new PyTuple([new PyTuple([new PyInt(30000142), new PyInt(1)]), PyNone.Instance]), kwargs))));
        var bindResponse = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        var binding = Assert.IsType<PyTuple>(Result(bindResponse));
        var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0]))!;

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.BoundCall(open, 2, oid, "GetCharacterSecurityStatus", new PyTuple([new PyLong(targetId)]), kwargs))));
        var reply = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(reply)).Type);
        Assert.Equal(new PyReal(2.5), Result(reply));
    }

    [Fact]
    public async Task Crimewatch_bound_rpcs_return_the_client_shapes_through_the_registered_host_route()
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Database=eve;Username=postgres;Timeout=1;Command Timeout=1");
        var service = new CrimewatchService(
            new PgCrimewatchStore(dataSource), null!, () => 1_800_000_000_000,
            (id, _) => Task.FromResult(id == 90000002 ? 2.5 : 0d));
        Start(strictRpc: true, configure: registry => registry.Register(service));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "crimewatch", "MachoBindObject",
                new PyTuple([new PyTuple([new PyInt(30000142), new PyInt(1)]), PyNone.Instance]), kwargs))));
        var bind = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        var binding = Assert.IsType<PyTuple>(Result(bind));
        var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0]))!;

        long callId = 1;
        async Task<PyValue> Call(string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.BoundCall(open, ++callId, oid, method, new PyTuple(args), kwargs))));
            var decoded = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
            Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded)).Type);
            return Result(decoded)!;
        }

        var states = Assert.IsType<PyTuple>(await Call("GetClientStates"));
        Assert.Equal(4, states.Items.Count);
        var timers = Assert.IsType<PyTuple>(states.Items[0]);
        Assert.Equal(5, timers.Items.Count);
        Assert.All(timers.Items, timer => Assert.Equal(2, Assert.IsType<PyTuple>(timer).Items.Count));
        Assert.Empty(Assert.IsType<PyDict>(states.Items[1]).Entries);
        var flagged = Assert.IsType<PyTuple>(states.Items[2]);
        Assert.Equal(2, flagged.Items.Count);
        Assert.Equal(new PyInt(2), states.Items[3]);
        Assert.Equal(new PyInt(0), await Call("GetMySecurityStatus"));
        Assert.Equal(new PyReal(2.5), await Call("GetCharacterSecurityStatus", new PyLong(90000002)));
        Assert.IsType<PyObjectEx>(await Call("GetSecurityStatusTransactions"));
        Assert.Equal(new PyInt(2), await Call("GetSafetyLevel"));
        Assert.Equal(new PyInt(2), await Call("SetSafetyLevel", new PyInt(0)));
        Assert.Equal(PyNone.Instance, await Call("StartDuelChallenge", new PyLong(90000002)));
        Assert.Equal(PyNone.Instance, await Call("RespondToDuelChallenge", new PyLong(90000002), new PyLong(0), PyBool.True));
    }

    [Fact]
    public async Task Crimewatch_apply_tags_is_bound_and_preserves_the_client_argument_order()
    {
        const long characterId = 90000001;
        (double Target, double Current)? applied = null;
        Eve.Services.ClientSession? connectedSession = null;
        var service = new CrimewatchService(null!, null!, () => 1_800_000_000_000)
        {
            ApplySecurityTagsAsync = (_, target, current, _) =>
            {
                applied = (target, current);
                return ValueTask.CompletedTask;
            },
        };
        Start(strictRpc: true,
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                connectedSession = session;
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 1, "crimewatch", "MachoResolveObject",
                new PyTuple([new PyTuple([new PyInt(30000142), new PyInt(1)])]), kwargs))));
        var resolve = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        Assert.IsType<PyInt>(Result(resolve));

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, 2, "crimewatch", "MachoBindObject",
                new PyTuple([new PyTuple([new PyInt(30000142), new PyInt(1)]), PyNone.Instance]), kwargs))));
        var bind = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        var binding = Assert.IsType<PyTuple>(Result(bind));
        var oid = BoundObjects.ExtractName(Assert.IsType<PySubStruct>(binding.Items[0]))!;

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.BoundCall(open, 3, oid, "ApplyTagsForSecStatus",
                new PyTuple([new PyReal(-4.5), new PyReal(-5.0)]), kwargs))));
        var reply = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(reply)).Type);
        Assert.Equal(PyNone.Instance, Result(reply));
        Assert.Equal((-4.5, -5.0), applied);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.BoundCall(open, 4, oid, "ApplyTagsForSecStatus",
                new PyTuple([new PyInt(-4), new PyLong(-5)]), kwargs))));
        var numericReply = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        Assert.Equal(PyNone.Instance, Result(numericReply));
        Assert.Equal((-4.0, -5.0), applied);
        var invalidStatus = await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyTagsForSecStatus(new Eve.Services.ServiceCall
        {
            Method = "ApplyTagsForSecStatus",
            Args = [PyNone.Instance, new PyReal(-5)],
            Session = connectedSession!,
        }).AsTask());
        Assert.Equal("Security tag exchange requires finite target and current security statuses.", invalidStatus.Message);

        Assert.Equal(Eve.Crimewatch.SecurityStatusEventTypeIds.HandInTags,
            Eve.Crimewatch.SecurityStatusEventTypeIds.FromHistoryReason("hand_in_tags"));

        var crossing = SecurityTagRequirements.Calculate(-8.25, -7.0);
        Assert.Equal(1, crossing.Single(requirement => requirement.TypeId == 33138).Quantity);
        Assert.Equal(2, crossing.Single(requirement => requirement.TypeId == 33139).Quantity);
        Assert.Equal(1, SecurityTagRequirements.Calculate(-0.1, 0).Single(requirement => requirement.TypeId == 33141).Quantity);
        Assert.Throws<ArgumentOutOfRangeException>(() => SecurityTagRequirements.Calculate(0, 1));
    }
}
