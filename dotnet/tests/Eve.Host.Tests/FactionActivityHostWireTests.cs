using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    private sealed class LpPoolNotificationProbe(SolarSystemLpPoolNotifications notifications) : IService
    {
        public string Name => "fwNotificationProbe";
        public bool HasMethod(string method) => method == "Publish";
        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            notifications.OnPoolChanged(30_000_142, 90_000_005, 21, 34);
            return ValueTask.FromResult<PyValue>(PyNone.Instance);
        }
    }

    [Fact]
    public async Task Solar_system_lp_change_notifies_the_donor_after_the_registered_host_reply()
    {
        const long characterId = 90_000_005;
        const long solarSystemId = 30_000_142;
        var sessions = new CharacterSessions();
        ClientSession? session = null;
        var notifications = new SolarSystemLpPoolNotifications(sessions);
        Start(strictRpc: true,
            configure: registry => registry.Register(new LpPoolNotificationProbe(notifications)),
            sessionOpened: opened =>
            {
                opened.Set("charid", new PyLong(characterId));
                opened.Set("solarsystemid2", new PyInt(solarSystemId));
                sessions.Enter(characterId, opened);
                session = opened;
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.Call(open, 1, "fwNotificationProbe", "Publish"))));

        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(reply.Payload))).Type);
        var notification = Assert.IsType<Send>(await front.ReceiveAsync());
        var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(notification.Payload)));
        Assert.Equal(MachoMessage.Notification, packet.Type);
        Assert.Equal("OnSolarSystemLPChange", packet.Dest.BroadcastId);
        Assert.Equal("charid", packet.Dest.IdType);
        var envelope = Assert.IsType<PyTuple>(packet.Payload);
        var inner = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(Assert.Single(envelope.Items)).Items[1]);
        Assert.Equal(MarshalCodec.Encode(new PyTuple([new PyInt(0),
            new PyTuple([new PyInt(1), new PyTuple([new PyLong(21), new PyLong(34)])])])),
            inner.Value.ToArray());
        Assert.Equal(characterId, session!.CharacterId);
    }

    [Fact]
    public async Task Direct_enlistment_persists_and_sends_the_client_enlistment_notification_over_host()
    {
        const long characterId = 90_000_004;
        const long corporationId = 99;
        const long factionId = 500_001;
        var state = new FwCharacterWarfareState(null, 0);
        ClientSession? session = null;
        var service = new FwCharacterEnlistmentMgrService(new FwCharacterEnlistmentRpcCallbacks(
            ReadCharacterStateAsync: (_, _) => ValueTask.FromResult<FwCharacterWarfareState?>(state),
            ReadCorporationFactionIdAsync: (_, _) => ValueTask.FromResult<long?>(500_002),
            ReadAllowedFactionsAsync: (_, _) => ValueTask.FromResult<IReadOnlyList<long>>([factionId]),
            SaveCharacterStateAsync: (_, enlistedFaction, cooldown, _) =>
            {
                state = new FwCharacterWarfareState(enlistedFaction, cooldown);
                return ValueTask.FromResult(true);
            },
            CurrentFileTime: () => 133_000_000_000_000_000,
            ReadStandingAsync: (_, _, _) => ValueTask.FromResult(0d)));
        Start(strictRpc: true,
            configure: registry => registry.Register(service),
            sessionOpened: opened =>
            {
                opened.Set("charid", new PyLong(characterId));
                opened.Set("corpid", new PyLong(corporationId));
                session = opened;
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.Call(open, 1, "fwCharacterEnlistmentMgr", "CreateMyDirectEnlistment", new PyInt(factionId)))));

        var sessionChange = Assert.IsType<Send>(await front.ReceiveAsync());
        var changedPacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(sessionChange.Payload)));
        Assert.Equal(MachoMessage.SessionChangeNotification, changedPacket.Type);

        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        var response = MarshalCodec.Decode(reply.Payload);
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<MachoPacket>(MachoPacket.Decode(response)).Type);
        Assert.Same(PyNone.Instance, Result(response));
        Assert.Equal(new FwCharacterWarfareState(factionId, 0), state);
        Assert.Equal(factionId, session!.GetInt("warfactionid"));

        var notification = Assert.IsType<Send>(await front.ReceiveAsync());
        var notificationPacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(notification.Payload)));
        Assert.Equal(MachoMessage.Notification, notificationPacket.Type);
        Assert.Equal("OnEnlistmentStateUpdated", notificationPacket.Dest.BroadcastId);
        Assert.Equal("charid", notificationPacket.Dest.IdType);
        var envelope = Assert.IsType<PyTuple>(notificationPacket.Payload);
        var inner = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(Assert.Single(envelope.Items)).Items[1]);
        Assert.Equal(MarshalCodec.Encode(new PyTuple([new PyInt(0),
            new PyTuple([new PyInt(1), new PyTuple([new PyInt(factionId), new PyInt(factionId), new PyInt(500_002)])])])),
            inner.Value.ToArray());
    }
}
