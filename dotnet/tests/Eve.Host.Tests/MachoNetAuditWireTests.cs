using System.Net;
using System.Net.Sockets;
using System.Text;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Tests;

/// <summary>End-to-end wire checks for the client-facing machoNet calls.</summary>
public sealed class MachoNetAuditWireTests : IAsyncLifetime
{
    private const long FixedNowMs = 1_790_646_377_000;
    private const ulong AdminRole = 1UL << 56;
    private const ulong PlayerRole = 1UL << 61;

    private readonly CancellationTokenSource _cancel = new();
    private readonly ConnectedSessions _connected = new();
    private readonly List<TestFront> _fronts = [];
    private readonly TestGlobalConfigPersistence _persistence = new();
    private GlobalConfigStore _config = null!;
    private HostServer _server = null!;
    private Socket _listener = null!;
    private Task _serving = Task.CompletedTask;

    public async Task InitializeAsync()
    {
        _config = await GlobalConfigStore.OpenAsync(
            GlobalConfig.Build(new ClusterOptions()),
            _persistence);
        Func<long> now = () => FixedNowMs;
        var registry = new ServiceRegistry();
        registry.Register(new MachoNetService(
            new Info(registry, _config.Current),
            now,
            GlobalConfigWrites.Create(_config, _connected, () => _server.PublishClusterConfig())));
        _server = new HostServer(
            new HostOptions
            {
                SessionOpened = session =>
                {
                    _connected.Add(session);
                    session.Closed += () => _connected.Remove(session);
                },
            },
            new PacketDispatcher(registry, now),
            new InMemoryAccountDirectory(new AccountOptions { AutoCreate = true }),
            () => MarshalCodec.Encode(_config.Current()));
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(16);
        _serving = _server.ServeAsync(_listener, _cancel.Token);
    }

    public async Task DisposeAsync()
    {
        foreach (var front in _fronts)
        {
            front.Dispose();
        }
        await _cancel.CancelAsync();
        _listener.Dispose();
        await _serving;
    }

    private sealed class TestGlobalConfigPersistence : IGlobalConfigPersistence
    {
        private readonly Dictionary<string, PyValue?> _rows = new(StringComparer.Ordinal);

        public bool FailWrites { get; set; }

        public Task<IReadOnlyList<KeyValuePair<string, PyValue?>>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KeyValuePair<string, PyValue?>>>([.. _rows]);

        public Task SaveAsync(string key, PyValue? value, CancellationToken cancellationToken)
        {
            if (FailWrites)
                return Task.FromException(new IOException("the test store is unavailable"));
            _rows[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class Info(ServiceRegistry registry, Func<PyDict> config) : IMachoNetInfo
    {
        public IReadOnlyList<KeyValuePair<string, string?>> Services() =>
            [.. registry.ServiceNames.Select(name => new KeyValuePair<string, string?>(name, ServiceLocations.Of(name)))];

        public PyDict GlobalConfig() => config();

        public int UserCount() => 0;

        public PyValue ClusterGameStatisticsForClient() =>
            Py.Tuple(new PyDict([]), new PyDict([]), new PyInt(1));
    }

    private static SessionOpen Open(ulong sessionId, uint userId, ulong role) =>
        new(sessionId, (userId * 1_000_000UL) + 0xFFAA, userId, role, "127.0.0.1", "EN", "GB");

    private async Task<(TestFront Front, Dictionary<string, PyValue> Config)> ConnectAsync()
    {
        var front = await TestFront.ConnectAsync((IPEndPoint)_listener.LocalEndPoint!);
        _fronts.Add(front);
        await front.SendAsync(new Hello(HostCodec.ProtocolVersion, 0xFFAA));
        Assert.IsType<HelloAck>(await front.ReceiveAsync());
        return (front, ConfigOf(await front.ReceiveAsync()));
    }

    private static int Integer(PyValue value) => (int)Assert.IsType<PyInt>(value).Value;

    private static string? Text(PyValue? value) => JsSemantics.StringValue(value);

    private static Dictionary<string, PyValue> ConfigOf(HostMessage message) =>
        ((PyDict)MarshalCodec.Decode(Assert.IsType<ClusterConfig>(message).Payload)).Entries
            .ToDictionary(entry => Text(entry.Key)!, entry => entry.Value);

    private static PyValue? Result(ReadOnlyMemory<byte> payload)
    {
        var packet = Assert.IsType<PyObject>(MarshalCodec.Decode(payload));
        var state = Assert.IsType<PyTuple>(packet.Args).Items;
        Assert.Equal((int)MachoMessage.CallRsp, Integer(state[0]));
        var body = Assert.IsType<PyTuple>(state[4]).Items;
        return body[0] is PySubStream { Decoded: { } inner } ? inner : body[0];
    }

    private static (string ClassName, PyTuple Args) Error(ReadOnlyMemory<byte> payload)
    {
        var packet = Assert.IsType<PyObject>(MarshalCodec.Decode(payload));
        var state = Assert.IsType<PyTuple>(packet.Args).Items;
        Assert.Equal((int)MachoMessage.ErrorResponse, Integer(state[0]));
        var body = Assert.IsType<PyTuple>(state[4]).Items;
        Assert.Equal((int)MachoMessage.CallReq, Integer(body[0]));
        Assert.Equal(2, Integer(body[1])); // WrappedException
        var wrapped = Assert.IsType<PyTuple>(body[2]).Items;
        var objectEx = Assert.IsType<PyObjectEx>(Assert.IsType<PySubStream>(wrapped[0]).Decoded);
        var header = Assert.IsType<PyTuple>(objectEx.Header).Items;
        return (Assert.IsType<PyToken>(header[0]).Value, Assert.IsType<PyTuple>(header[1]));
    }

    private static bool IsGlobalConfigNotification(Send send) =>
        Encoding.ASCII.GetString(send.Payload.Span).Contains("OnGlobalConfigUpdated", StringComparison.Ordinal);

    private static PyTuple NotificationArgs(Send send)
    {
        var packet = Assert.IsType<PyObject>(MarshalCodec.Decode(send.Payload));
        var state = Assert.IsType<PyTuple>(packet.Args).Items;
        Assert.Equal((int)MachoMessage.Notification, Integer(state[0]));
        var row = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(state[4]).Items[0]).Items;
        var encoded = Assert.IsType<PyBuffer>(row[1]);
        var notification = Assert.IsType<PyTuple>(MarshalCodec.Decode(encoded.Value.ToArray())).Items;
        var body = Assert.IsType<PyTuple>(notification[1]).Items;
        Assert.Equal(1, Integer(body[0]));
        return Assert.IsType<PyTuple>(body[1]);
    }

    private static bool IsCallResponse(Send send)
    {
        var packet = Assert.IsType<PyObject>(MarshalCodec.Decode(send.Payload));
        var state = Assert.IsType<PyTuple>(packet.Args).Items;
        return Integer(state[0]) == (int)MachoMessage.CallRsp;
    }

    [Fact]
    public async Task GetTime_returns_a_long_filetime_with_the_transport_version_keyword()
    {
        var (front, _) = await ConnectAsync();
        var caller = Open(1, 42, PlayerRole);
        await front.SendAsync(caller);
        await front.ReceiveAsync();
        await front.SendAsync(new Packet(caller.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            caller,
            1,
            "machoNet",
            "GetTime",
            new PyTuple([]),
            Py.Dict(("machoVersion", new PyInt(1)))))));

        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(new PyLong((FixedNowMs * 10_000) + 116_444_736_000_000_000), Result(reply.Payload));
    }

    [Fact]
    public async Task GetClusterGameStatisticsForClient_returns_the_host_tuple_not_the_client_fallback()
    {
        var (front, _) = await ConnectAsync();
        var caller = Open(1, 42, PlayerRole);
        await front.SendAsync(caller);
        await front.ReceiveAsync();

        var fallback = Py.Tuple(new PyDict([]), new PyDict([]), new PyInt(0));
        await front.SendAsync(new Packet(caller.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            caller,
            1,
            "machoNet",
            "GetClusterGameStatisticsForClient",
            Py.Tuple(new PyStr("EVE"), fallback),
            Py.Dict(("machoVersion", new PyInt(1)))))));

        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(Py.Tuple(new PyDict([]), new PyDict([]), new PyInt(1)), Result(reply.Payload));
    }

    [Fact]
    public async Task SetGlobalConfigValue_handles_client_kwargs_errors_positional_ints_longs_none_and_notifications()
    {
        var (front, initial) = await ConnectAsync();
        Assert.False(initial.ContainsKey("new_mission_giver_enabled"));
        var admin = Open(1, 42, AdminRole);
        var player = Open(2, 43, PlayerRole);
        await front.SendAsync(admin);
        await front.ReceiveAsync();
        await front.SendAsync(player);
        await front.ReceiveAsync();

        await front.SendAsync(new Packet(player.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            player,
            1,
            "machoNet",
            "SetGlobalConfigValue",
            new PyTuple([]),
            Py.Dict(
                ("key", new PyStr("imageserverurl")),
                ("value", new PyStr("http://evil/")),
                ("machoVersion", new PyInt(1)))))));
        var refused = Assert.IsType<Send>(await front.ReceiveAsync());
        var roleError = Error(refused.Payload);
        Assert.Equal("exceptions.RuntimeError", roleError.ClassName);
        Assert.Equal(2, roleError.Args.Items.Count);
        Assert.Equal("RoleNotAssigned", Text(roleError.Args.Items[0]));
        Assert.Equal("machoNet.SetGlobalConfigValue requires ROLE_ADMIN or ROLE_SERVICE", Text(roleError.Args.Items[1]));

        await front.SendAsync(new Packet(admin.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            admin,
            2,
            "machoNet",
            "SetGlobalConfigValue",
            new PyTuple([]),
            Py.Dict(
                ("key", new PyStr("new_mission_giver_enabled")),
                ("value", new PyStr("1")),
                ("machoVersion", new PyInt(1)))))));
        var messages = new List<HostMessage>();
        for (var index = 0; index < 4; index++)
        {
            messages.Add(await front.ReceiveAsync());
        }
        var notifications = messages.OfType<Send>().Where(IsGlobalConfigNotification).ToArray();
        Assert.Equal(new ulong[] { admin.SessionId, player.SessionId }, notifications.Select(send => send.SessionId).OrderBy(id => id));
        Assert.All(notifications, send =>
        {
            var args = NotificationArgs(send);
            Assert.Equal(2, args.Items.Count);
            Assert.Equal("new_mission_giver_enabled", Text(args.Items[0]));
            Assert.Equal("1", Text(args.Items[1]));
        });
        var configIndex = messages.FindIndex(message => message is ClusterConfig);
        var responseIndex = messages.FindIndex(message => message is Send send && IsCallResponse(send));
        Assert.Single(messages.OfType<ClusterConfig>());
        Assert.True(notifications.All(send => messages.IndexOf(send) < configIndex));
        Assert.True(notifications.All(send => messages.IndexOf(send) < responseIndex));
        var response = Assert.IsType<Send>(messages[responseIndex]);
        Assert.Equal(PyNone.Instance, Result(response.Payload));
        var published = ConfigOf(Assert.IsType<ClusterConfig>(messages[configIndex]));
        Assert.Equal("1", Text(published["new_mission_giver_enabled"]));

        await front.SendAsync(new Packet(admin.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            admin,
            3,
            "machoNet",
            "SetGlobalConfigValue",
            Py.Tuple(
                new PyStr("wide_value"),
                new PyLong(7),
                new PyLong(0),
                new PyInt(1),
                new PyLong(0)),
            Py.Dict(("machoVersion", new PyInt(1)))))));
        var wideConfig = Assert.IsType<ClusterConfig>(await front.ReceiveAsync());
        Assert.Equal(new PyLong(7), ConfigOf(wideConfig)["wide_value"]);
        Assert.Equal(PyNone.Instance, Result(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));

        await front.SendAsync(new Packet(admin.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            admin,
            4,
            "machoNet",
            "SetGlobalConfigValue",
            Py.Tuple(
                new PyStr("wide_value"),
                PyNone.Instance,
                new PyLong(1),
                new PyLong(0),
                new PyLong(1)),
            Py.Dict(("machoVersion", new PyInt(1)))))));
        var removalMessages = new List<HostMessage>();
        for (var index = 0; index < 4; index++)
        {
            removalMessages.Add(await front.ReceiveAsync());
        }
        var removalNotifications = removalMessages.OfType<Send>().Where(IsGlobalConfigNotification).ToArray();
        Assert.Equal(2, removalNotifications.Length);
        Assert.All(removalNotifications, send =>
        {
            var args = NotificationArgs(send);
            Assert.Equal("wide_value", Text(args.Items[0]));
            Assert.IsType<PyNone>(args.Items[1]);
        });
        var removalConfig = ConfigOf(Assert.IsType<ClusterConfig>(removalMessages.Single(message => message is ClusterConfig)));
        Assert.False(removalConfig.ContainsKey("wide_value"));
        Assert.Equal(PyNone.Instance, Result(Assert.IsType<Send>(removalMessages.Single(message =>
            message is Send send && IsCallResponse(send))).Payload));

        await front.SendAsync(new Packet(admin.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            admin,
            5,
            "machoNet",
            "SetGlobalConfigValue",
            Py.Tuple(new PyStr("bugReporting_BugReportServer"), new PyStr("http://elsewhere/")),
            Py.Dict(("machoVersion", new PyInt(1)))))));
        var protectedRefusal = Assert.IsType<Send>(await front.ReceiveAsync());
        var protectedError = Error(protectedRefusal.Payload);
        Assert.Equal("exceptions.RuntimeError", protectedError.ClassName);
        Assert.Equal(2, protectedError.Args.Items.Count);
        Assert.Equal("ProtectedGlobalConfigKey", Text(protectedError.Args.Items[0]));
        Assert.Equal(
            "bugReporting_BugReportServer is not set through machoNet.SetGlobalConfigValue",
            Text(protectedError.Args.Items[1]));

        _persistence.FailWrites = true;
        await front.SendAsync(new Packet(admin.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
            admin,
            6,
            "machoNet",
            "SetGlobalConfigValue",
            Py.Tuple(new PyStr("write_failure"), new PyStr("1")),
            Py.Dict(("machoVersion", new PyInt(1)))))));
        var failedWrite = Error(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        Assert.Equal("exceptions.RuntimeError", failedWrite.ClassName);
        Assert.Single(failedWrite.Args.Items);
        Assert.StartsWith("Unhandled error ", Text(failedWrite.Args.Items[0]));
        Assert.Contains(" in machoNet.SetGlobalConfigValue", Text(failedWrite.Args.Items[0]));
        Assert.DoesNotContain(_config.Current().Entries, entry => Text(entry.Key) == "write_failure");
        var (_, afterFailure) = await ConnectAsync();
        Assert.False(afterFailure.ContainsKey("write_failure"));
    }
}
