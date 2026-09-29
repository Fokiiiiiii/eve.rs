using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

/// <summary>The host over a real socket, driven by a scripted front.</summary>
public class HostServerTests : IAsyncLifetime
{
    private const long FixedNowMs = 1_790_646_377_000;

    private readonly CancellationTokenSource _cancel = new();
    private Socket _listener = null!;
    private HostServer _server = null!;
    private Task _serving = Task.CompletedTask;
    private readonly List<string> _log = [];
    private readonly List<TestFront> _fronts = [];

    private static readonly ClusterOptions Options = new();

    public Task InitializeAsync() => Task.CompletedTask;

    private void Start(AccountOptions? identity = null, int maxPending = 10_000, Action<ServiceRegistry>? configure = null, IAccountDirectory? accounts = null)
    {
        var registry = new ServiceRegistry();
        configure?.Invoke(registry);
        Func<long> now = () => FixedNowMs;
        foreach (var service in CoreServices.Create(new SubscriptionSettings(), new NoAccounts(), now))
        {
            registry.Register(service);
        }
        var config = GlobalConfig.Build(Options);
        registry.Register(new MachoNetService(new Info(registry, config), now));
        var dispatcher = new PacketDispatcher(registry, now);
        var encoded = MarshalCodec.Encode(config);
        _server = new HostServer(
            new HostOptions { MaxPendingPacketsPerSession = maxPending, Log = message => { lock (_log) { _log.Add(message); } } },
            dispatcher,
            accounts ?? new InMemoryAccountDirectory(identity ?? new AccountOptions()),
            () => encoded);
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(16);
        _serving = _server.ServeAsync(_listener, _cancel.Token);
    }

    private async Task<TestFront> ConnectAsync()
    {
        var front = await TestFront.ConnectAsync((IPEndPoint)_listener.LocalEndPoint!);
        _fronts.Add(front);
        return front;
    }

    public async Task DisposeAsync()
    {
        foreach (var front in _fronts)
        {
            front.Dispose();
        }
        await _cancel.CancelAsync();
        _listener?.Dispose();
        await _serving;
    }

    /// <summary>A service whose first call is slow: it shows whether a session's packets are kept in order.</summary>
    private sealed class SlowService : IService
    {
        public string Name => "slow";

        public bool HasMethod(string method) => method is "First" or "Second";

        public async ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            if (call.Method == "First")
            {
                await Task.Delay(300);
            }
            return new PyStr(call.Method);
        }
    }

    private sealed class NoAccounts : IOmegaAccountStore
    {
        public string? GetOmegaExpiry(long accountId) => null;
    }

    private sealed class Info(ServiceRegistry registry, PyDict config) : IMachoNetInfo
    {
        public IReadOnlyList<KeyValuePair<string, string?>> Services() =>
            [.. registry.ServiceNames.Select(n => new KeyValuePair<string, string?>(n, ServiceLocations.Of(n)))];

        public PyDict GlobalConfig() => config;

        public int UserCount() => 0;
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static SessionOpen Open(ulong sessionId = 1_790_646_377_000UL * 15, uint userId = 42) =>
        new(sessionId, (userId * 1_000_000UL) + 0xFFAA, userId, 0, "127.0.0.1", "EN", "GB");

    private static async Task<TestFront> Handshaken(TestFront front)
    {
        await front.SendAsync(new Hello(HostCodec.ProtocolVersion, 0xFFAA));
        Assert.IsType<HelloAck>(await front.ReceiveAsync());
        Assert.IsType<ClusterConfig>(await front.ReceiveAsync());
        return front;
    }

    /// <summary>The text of a decoded string: the decoder returns every string opcode as bytes.</summary>
    private static string? T(PyValue? value) => JsSemantics.StringValue(value);

    private static PyValue? Result(PyValue response)
    {
        var state = ((PyTuple)((PyObject)response).Args).Items;
        Assert.Equal(7, ((PyInt)state[0]).Value); // CALL_RSP
        var payload = (PyTuple)state[4];
        return payload.Items[0] is PySubStream { Decoded: { } inner } ? inner : payload.Items[0];
    }

    // ── tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_front_that_says_hello_gets_an_ack_and_the_cluster_config()
    {
        Start();
        var front = await ConnectAsync();
        await front.SendAsync(new Hello(HostCodec.ProtocolVersion, 0xFFAA));
        Assert.Equal(new HelloAck(HostCodec.ProtocolVersion), await front.ReceiveAsync());
        var config = Assert.IsType<ClusterConfig>(await front.ReceiveAsync());
        var dict = Assert.IsType<PyDict>(MarshalCodec.Decode(config.Payload));
        Assert.Contains(dict.Entries, e => T(e.Key) == "imageserverurl" && T(e.Value) == "http://127.0.0.1:26001/");
        Assert.Contains(dict.Entries, e => T(e.Key) == "defaultPortraitSaveSize" && e.Value is PyInt { Value: 1024 });
    }

    [Fact]
    public async Task A_front_that_speaks_another_version_is_dropped()
    {
        Start();
        var front = await ConnectAsync();
        await front.SendAsync(new Hello((ushort)(HostCodec.ProtocolVersion + 1), 1));
        Assert.Null(await front.ReceiveOrEndAsync());
        Assert.Contains(_log, line => line.Contains("speaks version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Logins_create_accounts_on_first_use_and_then_need_the_same_password_hash()
    {
        Start();
        var front = await Handshaken(await ConnectAsync());
        byte[] hash = [1, 2, 3];

        var first = await front.AuthenticateAsync("pilot", hash, 1);
        var created = Assert.IsType<Accepted>(first.Outcome);
        Assert.Equal(1u, created.UserId);
        Assert.Equal("GB", created.CountryCode);

        Assert.Equal(created, Assert.IsType<Accepted>((await front.AuthenticateAsync("pilot", hash, 2)).Outcome));
        Assert.Equal("LoginAuthFailed", Assert.IsType<Rejected>((await front.AuthenticateAsync("pilot", [9, 9, 9], 3)).Outcome).Reason);
        Assert.Equal(2u, Assert.IsType<Accepted>((await front.AuthenticateAsync("other", [4], 4)).Outcome).UserId);
    }

    private sealed class FixedAccounts(Func<LoginAttempt, LoginResult> answer) : IAccountDirectory
    {
        public ValueTask<LoginResult> LoginAsync(LoginAttempt attempt, CancellationToken cancellationToken) => ValueTask.FromResult(answer(attempt));
    }

    private sealed class BrokenAccounts : IAccountDirectory
    {
        public ValueTask<LoginResult> LoginAsync(LoginAttempt attempt, CancellationToken cancellationToken) => throw new InvalidOperationException("the database is down");
    }

    [Fact]
    public async Task The_accounts_decision_reaches_the_front_as_it_was_made()
    {
        Start(accounts: new FixedAccounts(a => a.UserName switch
        {
            "banned" => new LoginRefused(LoginRefused.Banned),
            "gm" => new LoggedIn(7, 0xF00D, "JP"),
            _ => new LoginRefused(LoginRefused.BadCredentials),
        }));
        var front = await Handshaken(await ConnectAsync());
        Assert.Equal(new Accepted(7, 0xF00D, "JP"), (await front.AuthenticateAsync("gm", [1], 1)).Outcome);
        Assert.Equal(new Rejected("ACCOUNTBANNED"), (await front.AuthenticateAsync("banned", [1], 2)).Outcome);
        Assert.Equal(new Rejected("LoginAuthFailed"), (await front.AuthenticateAsync("anyone", [1], 3)).Outcome);
    }

    [Fact]
    public async Task A_failing_accounts_store_refuses_the_login_and_the_link_survives()
    {
        Start(accounts: new BrokenAccounts());
        var front = await Handshaken(await ConnectAsync());
        Assert.Equal(new Rejected("Unavailable"), (await front.AuthenticateAsync("pilot", [1], 1)).Outcome);
        Assert.Equal(new Rejected("Unavailable"), (await front.AuthenticateAsync("pilot", [1], 2)).Outcome); // still answering
        Assert.Contains(_log, line => line.Contains("the database is down", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_replies_carry_the_correlation_of_their_request()
    {
        Start();
        var front = await Handshaken(await ConnectAsync());
        var reply = await front.AuthenticateAsync("pilot", [1], corr: 4_000_000_001);
        Assert.Equal(4_000_000_001u, reply.Corr);
    }

    [Fact]
    public async Task Development_switches_can_skip_the_password_or_refuse_unknown_users()
    {
        Start(new AccountOptions { SkipPasswordCheck = true });
        var open = await Handshaken(await ConnectAsync());
        Assert.IsType<Accepted>((await open.AuthenticateAsync("pilot", [1], 1)).Outcome);
        Assert.IsType<Accepted>((await open.AuthenticateAsync("pilot", [2], 2)).Outcome);
    }

    [Fact]
    public async Task Unknown_users_are_refused_when_auto_creation_is_off()
    {
        Start(new AccountOptions { AutoCreate = false });
        var front = await Handshaken(await ConnectAsync());
        Assert.Equal("LoginAuthFailed", Assert.IsType<Rejected>((await front.AuthenticateAsync("nobody", [1], 1)).Outcome).Reason);
    }

    [Fact]
    public async Task Opening_a_session_sends_the_initial_state_before_anything_else()
    {
        Start();
        var front = await Handshaken(await ConnectAsync());
        var open = Open() with { Role = 0x0100_0000_0000_0001 };
        await front.SendAsync(open);

        var packet = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(open.SessionId, packet.SessionId);
        var value = Assert.IsType<PyObject>(MarshalCodec.Decode(packet.Payload));
        Assert.Equal("carbon.common.script.net.machoNetPacket.SessionInitialStateNotification", T(value.Name));
        var state = ((PyTuple)value.Args).Items;
        Assert.Equal(14, state.Count);
        Assert.Equal(18, ((PyInt)state[0]).Value);
        Assert.Equal(42, ((PyInt)state[3]).Value);
        var payload = ((PyTuple)state[4]).Items;
        Assert.Equal((long)open.SessionId, ((PyLong)payload[0]).Value);
        Assert.Equal(5, ((PyInt)payload[1]).Value);
        var initial = ((PyDict)payload[2]).Entries.ToDictionary(e => T(e.Key)!, e => e.Value);
        Assert.Equal(new PyInt(42), initial["userid"]);
        Assert.Equal(new PyInt(30), initial["userType"]);
        Assert.Equal(new PyLong(0x0100_0000_0000_0001), initial["role"]);
        Assert.Equal("EN", T(initial["languageID"]));
        Assert.Equal("GB", T(initial["countryCode"]));
        Assert.Equal("127.0.0.1", T(initial["address"]));
        // the reference names these two as well, unset
        Assert.Equal(PyNone.Instance, initial["maxSessionTime"]);
        Assert.Equal(PyNone.Instance, initial["inDetention"]);
        Assert.Equal(
            ["languageID", "countryCode", "userid", "maxSessionTime", "userType", "role", "address", "inDetention"],
            ((PyDict)payload[2]).Entries.Select(e => T(e.Key)!));
        // the client's address: type 2 (client), its client id
        var destination = (PyTuple)((PyObject)state[2]).Args;
        Assert.Equal(new PyInt(2), destination.Items[0]);
        Assert.Equal(new PyLong((long)open.ClientId), destination.Items[1]);
    }

    [Fact]
    public async Task A_call_is_answered_through_the_dispatcher()
    {
        Start();
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync(); // the initial state

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.Call(open, 1, "machoNet", "GetTime"))));
        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        var time = Assert.IsType<PyLong>(Result(MarshalCodec.Decode(reply.Payload)));
        Assert.Equal(Py.FileTimeFromUnixMs(FixedNowMs), time.Value);
    }

    [Fact]
    public async Task GetInitVals_lists_the_services_and_the_global_configuration()
    {
        Start();
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.Call(open, 1, "machoNet", "GetInitVals"))));
        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        var pair = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(reply.Payload)));
        var services = Assert.IsType<PyDict>(pair.Items[0]).Entries.ToDictionary(e => T(e.Key)!, e => e.Value);
        Assert.Contains("machoNet", services.Keys);
        Assert.Contains("pingService", services.Keys);
        Assert.Contains("subscriptionMgr", services.Keys);
        var config = Assert.IsType<PyDict>(pair.Items[1]);
        Assert.Contains(config.Entries, e => T(e.Key) == "imageserverurl");
    }

    [Fact]
    public async Task Service_locations_come_from_the_client_routing_table()
    {
        Assert.Equal("station", ServiceLocations.Of("invbroker"));
        Assert.Equal("solarsystem2", ServiceLocations.Of("beyonce"));
        Assert.Equal("character", ServiceLocations.Of("dogmaIM"));
        Assert.Null(ServiceLocations.Of("machoNet"));
        Assert.Null(ServiceLocations.Of("noSuchService"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_ping_is_answered_and_an_unknown_service_gets_none_as_the_reference_does()
    {
        Start();
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.Ping(open, 1))));
        var pong = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(21, ((PyInt)((PyTuple)((PyObject)MarshalCodec.Decode(pong.Payload)).Args).Items[0]).Value);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.Call(open, 2, "noSuchService", "Nothing"))));
        var none = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.IsType<PyNone>(Result(MarshalCodec.Decode(none.Payload)));
    }

    [Fact]
    public async Task One_sessions_packets_are_answered_in_order_and_sessions_do_not_mix()
    {
        Start();
        var front = await Handshaken(await ConnectAsync());
        var sessions = new[] { Open(1_000, 1), Open(2_000, 2), Open(3_000, 3) };
        foreach (var open in sessions)
        {
            await front.SendAsync(open);
        }
        for (var i = 0; i < sessions.Length; i++)
        {
            await front.ReceiveAsync(); // three initial states
        }

        // 60 calls per session, interleaved, each carrying its call id
        for (var call = 1; call <= 60; call++)
        {
            foreach (var open in sessions)
            {
                await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.Call(open, call, "pingService", "Ping"))));
            }
        }
        var seen = sessions.ToDictionary(s => s.SessionId, _ => new List<long>());
        for (var n = 0; n < 60 * sessions.Length; n++)
        {
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            var state = ((PyTuple)((PyObject)MarshalCodec.Decode(reply.Payload)).Args).Items;
            var destination = (PyTuple)((PyObject)state[2]).Args;
            var clientId = ((PyLong)destination.Items[1]).Value;
            Assert.Equal((long)sessions.Single(s => s.SessionId == reply.SessionId).ClientId, clientId);
            seen[reply.SessionId].Add(((PyInt)destination.Items[2]).Value);
        }
        foreach (var (_, calls) in seen)
        {
            Assert.Equal(Enumerable.Range(1, 60).Select(i => (long)i), calls);
        }
    }

    [Fact]
    public async Task A_slow_call_holds_back_its_own_session_but_not_the_others()
    {
        Start(configure: registry => registry.Register(new SlowService()));
        var front = await Handshaken(await ConnectAsync());
        var slow = Open(1_000, 1);
        var other = Open(2_000, 2);
        await front.SendAsync(slow);
        await front.SendAsync(other);
        await front.ReceiveAsync();
        await front.ReceiveAsync();

        await front.SendAsync(new Packet(slow.SessionId, MarshalCodec.Encode(Packets.Call(slow, 1, "slow", "First"))));
        await front.SendAsync(new Packet(slow.SessionId, MarshalCodec.Encode(Packets.Call(slow, 2, "slow", "Second"))));
        await front.SendAsync(new Packet(other.SessionId, MarshalCodec.Encode(Packets.Call(other, 1, "slow", "Second"))));

        // the other session is answered while the slow one is still busy; then the slow session's two
        // calls arrive in the order they were sent, although the second is quick
        var order = new List<(ulong Session, string Method)>();
        for (var i = 0; i < 3; i++)
        {
            var reply = Assert.IsType<Send>(await front.ReceiveAsync());
            order.Add((reply.SessionId, T(Result(MarshalCodec.Decode(reply.Payload)))!));
        }
        Assert.Equal([(other.SessionId, "Second"), (slow.SessionId, "First"), (slow.SessionId, "Second")], order);
    }

    [Fact]
    public async Task An_undecodable_packet_is_dropped_and_the_session_carries_on()
    {
        Start();
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        await front.SendAsync(new Packet(open.SessionId, new byte[] { 0x7e, 0, 0, 0, 0, 0xff, 0xff, 0xff }));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.Call(open, 1, "pingService", "Ping"))));
        Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Contains(_log, line => line.Contains("undecodable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Packets_for_a_closed_or_unknown_session_are_ignored()
    {
        Start();
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        await front.SendAsync(new SessionClose(open.SessionId));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.Call(open, 1, "pingService", "Ping"))));
        await front.SendAsync(new Packet(99, new byte[] { 1, 2, 3 }));
        await Task.Delay(200); // long enough for a wrongly served packet to have been answered

        // the link still works, and nothing was answered for the closed or the unknown session
        var second = Open(5_000, 7);
        await front.SendAsync(second);
        await front.SendAsync(new Packet(second.SessionId, MarshalCodec.Encode(Packets.Call(second, 1, "pingService", "Ping"))));
        var sessions = new List<ulong>();
        for (var i = 0; i < 2; i++)
        {
            sessions.Add(Assert.IsType<Send>(await front.ReceiveAsync()).SessionId);
        }
        Assert.Equal([second.SessionId, second.SessionId], sessions);
    }

    [Fact]
    public async Task A_session_opened_twice_is_a_protocol_error()
    {
        Start();
        var front = await Handshaken(await ConnectAsync());
        await front.SendAsync(Open());
        await front.ReceiveAsync();
        await front.SendAsync(Open());
        Assert.Null(await front.ReceiveOrEndAsync());
    }

    [Fact]
    public async Task A_front_may_not_send_what_only_a_host_sends()
    {
        Start();
        var front = await Handshaken(await ConnectAsync());
        await front.SendAsync(new Send(1, new byte[] { 1 }));
        Assert.Null(await front.ReceiveOrEndAsync());
    }

    [Fact]
    public async Task Garbage_or_an_oversized_frame_closes_that_front_and_the_host_keeps_serving()
    {
        Start();
        var bad = await ConnectAsync();
        await bad.SendRawAsync(BitConverter.GetBytes(HostCodec.MaxFrame + 1));
        Assert.Null(await bad.ReceiveOrEndAsync());

        var garbage = await ConnectAsync();
        await garbage.SendRawAsync([3, 0, 0, 0, 0xff, 0xff, 0xff]);
        Assert.Null(await garbage.ReceiveOrEndAsync());

        var good = await Handshaken(await ConnectAsync());
        Assert.IsType<Accepted>((await good.AuthenticateAsync("pilot", [1], 1)).Outcome);
    }

    [Fact]
    public async Task A_session_with_too_many_packets_waiting_is_cut_off()
    {
        Start(maxPending: 5);
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();

        // Flood the session faster than it is served; the host disconnects it rather than queue without limit.
        var packet = MarshalCodec.Encode(Packets.Call(open, 1, "pingService", "Ping"));
        for (var i = 0; i < 2_000; i++)
        {
            await front.SendAsync(new Packet(open.SessionId, packet));
        }
        Disconnect? disconnect = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (disconnect is null)
        {
            var message = await front.ReceiveAsync(timeout.Token);
            disconnect = message as Disconnect;
        }
        Assert.Equal(open.SessionId, disconnect.SessionId);
        Assert.Equal("Overloaded", disconnect.Reason);
    }

    [Fact]
    public async Task Two_fronts_are_served_at_once()
    {
        Start();
        var a = await Handshaken(await ConnectAsync());
        var b = await Handshaken(await ConnectAsync());
        var (ra, rb) = (await a.AuthenticateAsync("a", [1], 1), await b.AuthenticateAsync("b", [2], 1));
        Assert.NotEqual(Assert.IsType<Accepted>(ra.Outcome).UserId, Assert.IsType<Accepted>(rb.Outcome).UserId);
        Assert.Equal(2, _server.ConnectedFronts);
    }
}
