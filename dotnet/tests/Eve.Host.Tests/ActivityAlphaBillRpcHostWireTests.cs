using System.Net;
using System.Net.Sockets;
using Eve.Host;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Skills;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public partial class HostServerTests
{
    [PgFact]
    public async Task Activity_alpha_injector_and_bill_rpcs_keep_client_wire_contracts_on_the_composed_host()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var character = await rig.CreateCharacterAsync();
        var characterData = Assert.IsType<Eve.Characters.Character>(await rig.Characters.GetAsync(character));
        var activityMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("activityMgr"));
        var alphaInjector = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("alphaInjectorMgr"));
        var billMgr = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("billMgr"));

        StartComposed(registry =>
        {
            registry.Register(activityMgr);
            registry.Register(alphaInjector);
            registry.Register(billMgr);
        }, session =>
        {
            session.CharacterId = character;
            session.UserId = 1;
            session.Set("charid", new PyLong(character));
            session.Set("corpid", new PyLong(characterData.CorporationId));
            session.Set("stationid", new PyLong(rig.Station));
        });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        var notifications = new List<string>();
        long callId = 0;
        async Task<PyValue> Exchange(string service, string method, PyValue[] args, PyDict kwargs)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, ++callId, service, method, new PyTuple(args), kwargs))));
            while (true)
            {
                var response = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
                var packet = MachoPacket.Decode(response);
                if (packet?.Type == MachoMessage.Notification)
                {
                    notifications.Add(packet.Dest.BroadcastId ?? string.Empty);
                    continue;
                }
                return response;
            }
        }

        PyDict TransportKwargs() => new([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue> Call(string service, string method, params PyValue[] args) =>
            Result(await Exchange(service, method, args, TransportKwargs()))!;

        var initialBadges = Assert.IsType<PyList>(await Call("activityMgr", "GetActivityBadgeStatuses"));
        Assert.Empty(initialBadges.Items);
        Assert.Equal(["OnSystemActivityUpdated"], notifications);
        notifications.Clear();

        Assert.Equal(PyBool.True, await Call("activityMgr", "ActivitySeen", new PyInt(42)));
        Assert.Equal(PyBool.True, await Call("activityMgr", "ActivitySeen", new PyLong(4_294_967_297)));
        Assert.Equal(PyBool.False, await Call("activityMgr", "ActivitySeen", new PyInt(0)));
        var badges = Assert.IsType<PyList>(await Call("activityMgr", "GetActivityBadgeStatuses"));
        Assert.Equal(new PyList([new PyInt(42), new PyLong(4_294_967_297)]), badges);
        Assert.Equal(["OnSystemActivityUpdated"], notifications);
        notifications.Clear();

        Assert.Equal(new PyInt(1), await Call("activityMgr", "GetActivitiesPopState"));
        Assert.Equal(PyBool.True, await Call("activityMgr", "SetActivitiesPopState", new PyInt(0)));
        Assert.Equal(new PyInt(0), await Call("activityMgr", "GetActivitiesPopState"));
        Assert.Equal(PyBool.True, await Call("activityMgr", "SetActivitiesPopState", new PyInt(1)));
        Assert.Equal(new PyInt(1), await Call("activityMgr", "GetActivitiesPopState"));

        // Rows 897 and 898 are duplicate observations of this same, zero-argument client RPC.
        Assert.Equal(new PyLong(0), await Call("alphaInjectorMgr", "GetNextAvailableInjection"));
        Assert.Equal(new PyLong(0), await Call("alphaInjectorMgr", "GetNextAvailableInjection"));
        var malformedAlphaCall = await Exchange("alphaInjectorMgr", "GetNextAvailableInjection", [],
            new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("unexpected"), new PyInt(1))]));
        Assert.Equal(MachoMessage.ErrorResponse,
            Assert.IsType<MachoPacket>(MachoPacket.Decode(malformedAlphaCall)).Type);

        Assert.Empty(Assert.IsType<PyList>(await Call("billMgr", "GetCorporationBills")).Items);
        Assert.Empty(Assert.IsType<PyList>(await Call("billMgr", "GetCorporationBillsReceivable")).Items);

        foreach (var (method, args) in new[]
        {
            ("GetAutomaticPaySettings", Array.Empty<PyValue>()),
            ("SendAutomaticPaySettings", [new PyDict([])]),
            ("CharPayBill", [new PyLong(1)]),
            ("PayCorporationBill", [new PyLong(1)]),
        })
        {
            var response = await Exchange("billMgr", method, args, TransportKwargs());
            Assert.Equal(MachoMessage.ErrorResponse,
                Assert.IsType<MachoPacket>(MachoPacket.Decode(response)).Type);
        }
    }

    private void StartComposed(Action<ServiceRegistry> configure, Action<ClientSession>? sessionOpened = null)
    {
        var registry = new ServiceRegistry();
        configure(registry);
        Func<long> now = () => FixedNowMs;
        var config = GlobalConfig.Build(Options);
        registry.Register(new MachoNetService(new Info(registry, config), now));
        var dispatcher = new PacketDispatcher(registry, now) { StrictRpc = true };
        var encoded = MarshalCodec.Encode(config);
        _server = new HostServer(
            new HostOptions
            {
                MaxFrontConnections = 1_024,
                MaxPendingPacketsPerSession = 10_000,
                MaxPendingBytesPerSession = 16 * 1024 * 1024,
                MaxQueuedOutboundBytesPerSession = 8 * 1024 * 1024,
                Log = message => { lock (_log) _log.Add(message); },
                SessionOpened = sessionOpened,
            },
            dispatcher,
            new InMemoryAccountDirectory(new AccountOptions { AutoCreate = true }),
            () => encoded);
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(16);
        _serving = _server.ServeAsync(_listener, _cancel.Token);
    }
}
