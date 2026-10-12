using System.Net;
using System.Net.Sockets;
using System.Text;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>The store behind <c>machoNet.SetGlobalConfigValue</c>: what the client is given, and what a restart reads back.</summary>
public class GlobalConfigStoreTests
{
    private static readonly PyDict Base = Py.Dict(
        ("imageserverurl", new PyStr("http://127.0.0.1:26001/")),
        ("defaultPortraitSaveSize", new PyInt(1024)),
        ("HyperNetKillSwitch", new PyInt(0)));

    private static Dictionary<string, PyValue> Map(PyDict dict) =>
        dict.Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value);

    private static List<string> Keys(PyDict dict) =>
        dict.Entries.Select(e => JsSemantics.StringValue(e.Key)!).ToList();

    private sealed class FailingPersistence : IGlobalConfigPersistence
    {
        public Task<IReadOnlyList<KeyValuePair<string, PyValue?>>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KeyValuePair<string, PyValue?>>>([]);

        public Task SaveAsync(string key, PyValue? value, CancellationToken cancellationToken) =>
            throw new IOException("the database is down");
    }

    [Fact]
    public async Task Without_writes_the_client_is_given_the_base_entries_in_their_order()
    {
        var store = await GlobalConfigStore.OpenAsync(Base, new InMemoryGlobalConfigPersistence());

        Assert.Equal(["imageserverurl", "defaultPortraitSaveSize", "HyperNetKillSwitch"], Keys(store.Current()));
    }

    [Fact]
    public async Task A_set_key_replaces_its_base_entry_in_place_and_a_new_key_follows_the_base_entries()
    {
        var store = await GlobalConfigStore.OpenAsync(Base, new InMemoryGlobalConfigPersistence());

        await store.ApplyAsync("imageserverurl", new PyStr("http://cdn/"), isDelete: false);
        await store.ApplyAsync("new_mission_giver_enabled", new PyStr("1"), isDelete: false);
        await store.ApplyAsync("another_flag", new PyInt(7), isDelete: false);

        var current = store.Current();
        Assert.Equal(
            ["imageserverurl", "defaultPortraitSaveSize", "HyperNetKillSwitch", "another_flag", "new_mission_giver_enabled"],
            Keys(current));
        Assert.Equal(new PyStr("http://cdn/"), Map(current)["imageserverurl"]);
        Assert.Equal(new PyStr("1"), Map(current)["new_mission_giver_enabled"]);
    }

    [Fact]
    public async Task A_delete_or_a_none_value_removes_the_key_and_the_removal_survives_a_restart()
    {
        var persistence = new InMemoryGlobalConfigPersistence();
        var store = await GlobalConfigStore.OpenAsync(Base, persistence);

        Assert.Null(await store.ApplyAsync("defaultPortraitSaveSize", new PyInt(0), isDelete: true));
        Assert.Null(await store.ApplyAsync("HyperNetKillSwitch", PyNone.Instance, isDelete: false));
        Assert.Equal(["imageserverurl"], Keys(store.Current()));

        // The reopened store does not bring the base entries back over the saved removals.
        var reopened = await GlobalConfigStore.OpenAsync(Base, persistence);
        Assert.Equal(["imageserverurl"], Keys(reopened.Current()));
    }

    [Fact]
    public async Task The_writes_are_saved_and_a_restarted_store_reads_them_back()
    {
        var persistence = new InMemoryGlobalConfigPersistence();
        var first = await GlobalConfigStore.OpenAsync(Base, persistence);
        await first.ApplyAsync("imageserverurl", new PyStr("http://cdn/"), isDelete: false);
        await first.ApplyAsync("flag", new PyInt(1), isDelete: false);

        var restarted = Map(( await GlobalConfigStore.OpenAsync(Base, persistence)).Current());

        Assert.Equal(new PyStr("http://cdn/"), restarted["imageserverurl"]);
        Assert.Equal(new PyInt(1), restarted["flag"]);
    }

    [Fact]
    public async Task A_write_that_cannot_be_saved_changes_nothing_and_is_reported()
    {
        var store = await GlobalConfigStore.OpenAsync(Base, new FailingPersistence());
        var before = Keys(store.Current());

        await Assert.ThrowsAsync<IOException>(() => store.ApplyAsync("imageserverurl", new PyStr("http://evil/"), isDelete: false).AsTask());

        Assert.Equal(before, Keys(store.Current()));
        Assert.Equal(new PyStr("http://127.0.0.1:26001/"), Map(store.Current())["imageserverurl"]);
    }
}

/// <summary>
/// A front and a host over a real socket: an administrator's write reaches every connected client as a notification, the
/// fronts get the new cluster configuration (what the next handshake reads), and a player's write is refused.
/// </summary>
public class GlobalConfigWireTests : IAsyncLifetime
{
    private const long FixedNowMs = 1_790_646_377_000;
    private const ulong AdminRole = 1UL << 56;   // ROLE_ADMIN
    private const ulong PlayerRole = 1UL << 61;  // ROLE_PLAYER

    private readonly CancellationTokenSource _cancel = new();
    private readonly ConnectedSessions _connected = new();
    private readonly List<TestFront> _fronts = [];
    private GlobalConfigStore _config = null!;
    private HostServer _server = null!;
    private Socket _listener = null!;
    private Task _serving = Task.CompletedTask;

    public async Task InitializeAsync()
    {
        _config = await GlobalConfigStore.OpenAsync(GlobalConfig.Build(new ClusterOptions()), new InMemoryGlobalConfigPersistence());
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

    private sealed class Info(ServiceRegistry registry, Func<PyDict> config) : IMachoNetInfo
    {
        public IReadOnlyList<KeyValuePair<string, string?>> Services() =>
            [.. registry.ServiceNames.Select(n => new KeyValuePair<string, string?>(n, ServiceLocations.Of(n)))];

        public PyDict GlobalConfig() => config();

        public int UserCount() => 0;
    }

    private static SessionOpen Open(ulong sessionId, uint userId, ulong role) =>
        new(sessionId, (userId * 1_000_000UL) + 0xFFAA, userId, role, "127.0.0.1", "EN", "GB");

    /// <summary>Connects a front and reads its acknowledgement and cluster configuration.</summary>
    private async Task<(TestFront Front, Dictionary<string, PyValue> Config)> ConnectAsync()
    {
        var front = await TestFront.ConnectAsync((IPEndPoint)_listener.LocalEndPoint!);
        _fronts.Add(front);
        await front.SendAsync(new Hello(HostCodec.ProtocolVersion, 0xFFAA));
        Assert.IsType<HelloAck>(await front.ReceiveAsync());
        return (front, ConfigOf(await front.ReceiveAsync()));
    }

    private static Dictionary<string, PyValue> ConfigOf(HostMessage message) =>
        ((PyDict)MarshalCodec.Decode(Assert.IsType<ClusterConfig>(message).Payload)).Entries
            .ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value);

    private static string Ascii(ReadOnlyMemory<byte> payload) => Encoding.ASCII.GetString(payload.Span);

    /// <summary>The value a successful call answered (a CALL_RSP).</summary>
    private static PyValue? ResultOf(ReadOnlyMemory<byte> payload)
    {
        var state = ((PyTuple)((PyObject)MarshalCodec.Decode(payload)).Args).Items;
        Assert.Equal(7, ((PyInt)state[0]).Value); // CALL_RSP
        var body = (PyTuple)state[4];
        return body.Items[0] is PySubStream { Decoded: { } inner } ? inner : body.Items[0];
    }

    [Fact]
    public async Task An_administrator_s_write_reaches_every_client_and_the_next_handshake_reads_it()
    {
        var (front, initial) = await ConnectAsync();
        Assert.False(initial.ContainsKey("new_mission_giver_enabled"));
        var admin = Open(1, 42, AdminRole);
        var player = Open(2, 43, PlayerRole);
        await front.SendAsync(admin);
        Assert.IsType<Send>(await front.ReceiveAsync()); // the initial state
        await front.SendAsync(player);
        Assert.IsType<Send>(await front.ReceiveAsync());

        // A player may not write the configuration: refused, and nobody is told.
        await front.SendAsync(new Packet(player.SessionId, MarshalCodec.Encode(
            Packets.Call(player, 1, "machoNet", "SetGlobalConfigValue", new PyStr("imageserverurl"), new PyStr("http://evil/")))));
        var refused = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Contains("exceptions.RuntimeError", Ascii(refused.Payload));
        Assert.Contains("RoleNotAssigned", Ascii(refused.Payload));

        // An administrator's write: both sessions hear it, the front gets the new configuration, and the call answers None.
        await front.SendAsync(new Packet(admin.SessionId, MarshalCodec.Encode(
            Packets.Call(admin, 2, "machoNet", "SetGlobalConfigValue", new PyStr("new_mission_giver_enabled"), new PyStr("1")))));
        var heard = new List<Send>();
        ClusterConfig? published = null;
        var answer = (Send?)null;
        for (var i = 0; i < 4; i++)
        {
            switch (await front.ReceiveAsync())
            {
                case ClusterConfig config:
                    published = config;
                    break;
                case Send send when Ascii(send.Payload).Contains("OnGlobalConfigUpdated", StringComparison.Ordinal):
                    heard.Add(send);
                    break;
                case Send send:
                    answer = send;
                    break;
            }
        }
        Assert.Equal(new ulong[] { 1, 2 }, heard.Select(s => s.SessionId).OrderBy(id => id));
        Assert.Equal(PyNone.Instance, ResultOf(answer!.Payload));
        Assert.Equal("1", JsSemantics.StringValue(ConfigOf(published!)["new_mission_giver_enabled"]));

        // The client reads it back through the call, and a front that connects afterwards gets it in its handshake.
        await front.SendAsync(new Packet(admin.SessionId, MarshalCodec.Encode(
            Packets.Call(admin, 3, "machoNet", "GetGlobalConfigValue", new PyStr("new_mission_giver_enabled")))));
        var pair = Assert.IsType<PyTuple>(ResultOf(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
        Assert.Equal("1", JsSemantics.StringValue(pair.Items[1])); // the wire returns strings as bytes
        var (_, later) = await ConnectAsync();
        Assert.Equal("1", JsSemantics.StringValue(later["new_mission_giver_enabled"]));

        // The bug-report server is protected: an administrator's write is refused, and the configuration does not change.
        await front.SendAsync(new Packet(admin.SessionId, MarshalCodec.Encode(
            Packets.Call(admin, 5, "machoNet", "SetGlobalConfigValue", new PyStr("bugReporting_BugReportServer"), new PyStr("http://elsewhere/")))));
        var protectedRefusal = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Contains("ProtectedGlobalConfigKey", Ascii(protectedRefusal.Payload));
        await front.SendAsync(new Packet(admin.SessionId, MarshalCodec.Encode(
            Packets.Call(admin, 6, "machoNet", "GetGlobalConfigValue", new PyStr("bugReporting_BugReportServer")))));
        var bugReport = Assert.IsType<PyTuple>(ResultOf(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
        Assert.Equal("http://127.0.0.1:26004/api", JsSemantics.StringValue(bugReport.Items[1]));

        // A removal is announced as None, and no front or call sees the key any more.
        await front.SendAsync(new Packet(admin.SessionId, MarshalCodec.Encode(
            Packets.Call(admin, 4, "machoNet", "SetGlobalConfigValue", new PyStr("new_mission_giver_enabled"), PyNone.Instance, new PyInt(1)))));
        var removal = new List<Send>();
        ClusterConfig? afterRemoval = null;
        for (var i = 0; i < 4; i++)
        {
            switch (await front.ReceiveAsync())
            {
                case ClusterConfig config:
                    afterRemoval = config;
                    break;
                case Send send when Ascii(send.Payload).Contains("OnGlobalConfigUpdated", StringComparison.Ordinal):
                    removal.Add(send);
                    break;
            }
        }
        Assert.Equal(2, removal.Count);
        Assert.False(ConfigOf(afterRemoval!).ContainsKey("new_mission_giver_enabled"));
    }
}

/// <summary>The overrides in PostgreSQL: their own schema, migrated once, and read back by a new store after a restart.</summary>
public class GlobalConfigPersistenceTests
{
    private static readonly PyDict Base = Py.Dict(
        ("imageserverurl", new PyStr("http://127.0.0.1:26001/")),
        ("HyperNetKillSwitch", new PyInt(0)));

    [PgFact]
    public async Task The_overrides_are_kept_in_their_own_schema_and_a_restarted_store_reads_them_back()
    {
        var schema = "gc_" + Guid.NewGuid().ToString("N")[..12];
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        try
        {
            Assert.Contains("0001_global_config", await PgGlobalConfigPersistence.MigrateAsync(source, schema));
            Assert.Empty(await PgGlobalConfigPersistence.MigrateAsync(source, schema)); // a second start applies nothing

            var store = await GlobalConfigStore.OpenAsync(Base, new PgGlobalConfigPersistence(source, schema));
            await store.ApplyAsync("imageserverurl", new PyStr("http://cdn/"), isDelete: false);
            await store.ApplyAsync("flag", new PyInt(1), isDelete: false);
            await store.ApplyAsync("HyperNetKillSwitch", PyNone.Instance, isDelete: false);

            var restarted = Map((await GlobalConfigStore.OpenAsync(Base, new PgGlobalConfigPersistence(source, schema))).Current());
            Assert.Equal("http://cdn/", JsSemantics.StringValue(restarted["imageserverurl"]));
            Assert.Equal(new PyInt(1), restarted["flag"]);
            Assert.False(restarted.ContainsKey("HyperNetKillSwitch"));
        }
        finally
        {
            await using var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static Dictionary<string, PyValue> Map(PyDict dict) =>
        dict.Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value);
}
