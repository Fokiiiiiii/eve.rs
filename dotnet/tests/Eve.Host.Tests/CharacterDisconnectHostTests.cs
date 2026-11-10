using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class CharacterDisconnectHostTests
{
    [PgFact]
    public async Task Abrupt_disconnect_updates_corporation_last_online_and_clean_logoff_is_not_recorded_twice()
    {
        const long now = 1_800_000_000_000;
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync(nowUnixMs: now);
        Assert.Equal(1, await rig.CreateAccountAsync());
        var observerAccount = await rig.CreateAccountAsync("mct-observer");
        var target = await rig.CreateCharacterAsync();
        var observer = await rig.CreateCharacterAsync(accountId: observerAccount);
        await rig.AddCorporationMemberAsync(target);
        await rig.AddCorporationMemberAsync(observer);
        await rig.Characters.SetLastLogoffAsync(target,
            DateTimeOffset.FromUnixTimeMilliseconds(now - (long)TimeSpan.FromDays(3).TotalMilliseconds));

        using var stop = new CancellationTokenSource();
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(8);
        var serving = rig.Server.ServeAsync(listener, stop.Token);
        TestFront? targetFront = null;
        TestFront? observerFront = null;
        try
        {
            targetFront = await OpenFrontAsync();
            observerFront = await OpenFrontAsync();
            var targetOpen = new SessionOpen(81_001, 81_002, 1, 0, "127.0.0.1", "EN", "GB");
            var observerOpen = new SessionOpen(82_001, 82_002, (uint)observerAccount, 0, "127.0.0.1", "EN", "GB");
            await OpenSessionAsync(targetFront, targetOpen);
            await OpenSessionAsync(observerFront, observerOpen);
            var callId = 1L;

            await CallAsync(observerFront, observerOpen, callId++, "charUnboundMgr", "SelectCharacterID", new PyLong(observer));
            await CallAsync(observerFront, observerOpen, callId++, "charMgr", "AddContact",
                new PyLong(target), new PyInt(-10), PyBool.True, PyBool.False, new PyStr(""));
            await CallAsync(targetFront, targetOpen, callId++, "charUnboundMgr", "SelectCharacterID", new PyLong(target));
            Assert.Equal(target, rig.Sessions.Of(target)?.CharacterId);
            await CallAsync(targetFront, targetOpen, callId++, "charMgr", "AddContact",
                new PyLong(observer), new PyInt(-10), PyBool.True, PyBool.False, new PyStr(""));
            Assert.Equal(-1, LastOnlineHours(await CallAsync(observerFront, observerOpen, callId++,
                "corpRegistry", "GetMemberTrackingInfoSimple"), target));

            targetFront.Dispose();
            var offlineNotice = await ReadNotificationAsync(observerFront, "OnContactLoggedOff");
            Assert.Equal("clientID", offlineNotice.IdType);
            Assert.Equal(new PyLong(target), Assert.Single(offlineNotice.Args));
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(now), (await rig.Characters.GetAsync(target))!.LastLogoffAt);
            await WaitUntilAsync(() => rig.Sessions.Of(target) is null);
            var afterDisconnect = await CallAsync(observerFront, observerOpen, callId++,
                "corpRegistry", "GetMemberTrackingInfoSimple");
            Assert.Equal(0, LastOnlineHours(afterDisconnect, target));
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(now), (await rig.Characters.GetAsync(target))!.LastLogoffAt);

            rig.AdvanceTo(now + 5_000);
            Assert.Equal(PyBool.True, await CallAsync(observerFront, observerOpen, callId++,
                "userSvc", "UserLogOffCharacter"));
            var explicitLogoffAt = DateTimeOffset.FromUnixTimeMilliseconds(now + 5_000);
            rig.AdvanceTo(now + 10_000);
            observerFront.Dispose();
            await WaitUntilAsync(() => rig.Sessions.Of(observer) is null);
            Assert.Equal(explicitLogoffAt, (await rig.Characters.GetAsync(observer))!.LastLogoffAt);
        }
        finally
        {
            targetFront?.Dispose();
            observerFront?.Dispose();
            await stop.CancelAsync();
            listener.Dispose();
            await serving;
        }

        async Task<TestFront> OpenFrontAsync()
        {
            var front = await TestFront.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);
            await front.SendAsync(new Hello(HostCodec.ProtocolVersion, 0xFFAA));
            Assert.IsType<HelloAck>(await front.ReceiveAsync());
            Assert.IsType<ClusterConfig>(await front.ReceiveAsync());
            return front;
        }
    }

    private static async Task OpenSessionAsync(TestFront front, SessionOpen open)
    {
        await front.SendAsync(open);
        Assert.Equal(open.SessionId, Assert.IsType<Send>(await front.ReceiveAsync()).SessionId);
    }

    private static async Task<PyValue> CallAsync(TestFront front, SessionOpen open, long callId,
        string service, string method, params PyValue[] args)
    {
        await front.SendAsync(new Packet(open.SessionId,
            MarshalCodec.Encode(Packets.Call(open, callId, service, method, args))));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var message = await front.ReceiveAsync(timeout.Token);
            if (message is Disconnect disconnect)
                throw new Xunit.Sdk.XunitException($"Host disconnected session: {disconnect.Reason}");
            var response = Assert.IsType<Send>(message);
            var decoded = MarshalCodec.Decode(response.Payload);
            var packet = MachoPacket.Decode(decoded);
            if (packet?.Type != MachoMessage.CallRsp)
                continue;
            var state = ((PyTuple)((PyObject)decoded!).Args).Items;
            var payload = Assert.IsType<PyTuple>(state[4]);
            return payload.Items[0] is PySubStream { Decoded: { } inner } ? inner : payload.Items[0];
        }
    }

    private static async Task<(string Name, string IdType, PyValue[] Args)> ReadNotificationAsync(TestFront front, string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var message = await front.ReceiveAsync(timeout.Token);
            if (message is Disconnect disconnect)
                throw new Xunit.Sdk.XunitException($"Host disconnected session: {disconnect.Reason}");
            if (message is not Send send)
                continue;
            var state = ((PyTuple)((PyObject)MarshalCodec.Decode(send.Payload)).Args).Items;
            if (Number(state[0]) != MachoMessage.Notification)
                continue;
            var address = MachoAddress.Decode(state[2]);
            if (address.BroadcastId != name)
                continue;
            var body = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(state[4]).Items[0]).Items[1]);
            var notification = Assert.IsType<PyTuple>(MarshalCodec.Decode(body.Value.ToArray()));
            var args = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(notification.Items[1]).Items[1]);
            return (address.BroadcastId!, address.IdType!, [.. args.Items]);
        }
    }

    private static long LastOnlineHours(PyValue value, long characterId)
    {
        var rows = Assert.IsType<PyList>(value);
        var row = rows.Items.Select(Assert.IsType<PyList>)
            .Single(item => Number(item.Items[0]) == characterId);
        return Number(row.Items[8]);
    }

    private static long Number(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => throw new Xunit.Sdk.XunitException($"Expected an integer, received {value.GetType().Name}.")
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
