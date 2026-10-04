using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.CharacterInfo;

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

    private sealed class EmptyStructureDirectoryReadCallbacks : Eve.Services.Core.IStructureDirectoryReadCallbacks
    {
        public ValueTask<Eve.Marshal.PyValue> GetJumpBridgesWithMyAccessAsync(Eve.Services.ServiceCall call) =>
            ValueTask.FromResult<Eve.Marshal.PyValue>(new Eve.Marshal.PyTuple([
                new Eve.Marshal.PyList([]), new Eve.Marshal.PyList([]), new Eve.Marshal.PyList([])]));

        public ValueTask<Eve.Marshal.PyValue> GetNearbyJumpBridgesAsync(Eve.Services.ServiceCall call) =>
            ValueTask.FromResult<Eve.Marshal.PyValue>(new Eve.Marshal.PyList([]));

        public ValueTask<Eve.Marshal.PyValue> GetStructureDescriptionAsync(Eve.Services.ServiceCall call) =>
            ValueTask.FromResult<Eve.Marshal.PyValue>(new Eve.Marshal.PyStr(""));
    }

    private sealed class EmptyInsuranceReadModel : Eve.Services.Core.IInsuranceReadModel
    {
        public ValueTask<IReadOnlyDictionary<long, long>> GetInsurancePricesCentsAsync(
            IReadOnlyCollection<long> typeIds, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyDictionary<long, long>>(new Dictionary<long, long>());

        public ValueTask<IReadOnlyList<Eve.Services.Core.InsuranceContractSnapshot>> GetContractsAsync(
            long ownerId, Eve.Services.Core.InsuranceOwnerKind ownerKind, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<Eve.Services.Core.InsuranceContractSnapshot>>([]);

        public ValueTask<Eve.Services.Core.InsuranceContractSnapshot?> GetContractForShipAsync(
            long shipId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Eve.Services.Core.InsuranceContractSnapshot?>(null);
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
    public async Task Character_personal_read_model_loads_contacts_and_notes_from_owner_scoped_tables()
    {
        var tables = new Dictionary<string, System.Text.Json.Nodes.JsonObject>
        {
            ["characters"] = System.Text.Json.Nodes.JsonNode.Parse("""
                {
                  "1001": {
                    "personalContacts": { "2001": { "contactID": 2001, "inWatchlist": true, "relationshipID": -10, "labelMask": 5 } },
                    "blockedOwners": { "3001": { "senderID": 3001 } },
                    "personalContactLabels": { "4": { "name": "Fleet", "color": 7 } }
                  },
                  "1002": {
                    "personalContacts": { "2002": { "contactID": 2002, "inWatchlist": false, "relationshipID": -5, "labelMask": 0 } },
                    "blockedOwners": {},
                    "personalContactLabels": { "8": { "name": "Friends", "color": 3 } }
                  }
                }
                """)!.AsObject(),
            ["characterNotes"] = System.Text.Json.Nodes.JsonNode.Parse("""
                {
                  "_meta": { "nextNoteID": 5 },
                  "characters": {
                    "1001": {
                      "notes": {
                        "1": { "noteID": 1, "label": "S:Folders", "note": "1::F::0::Main|" },
                        "2": { "noteID": 2, "label": "Briefing", "note": "Dock at Perimeter" }
                      },
                      "itemNotes": { "4001": { "itemID": 4001, "note": "Saved ship note" } }
                    },
                    "1002": {
                      "notes": { "1": { "noteID": 1, "label": "S:Folders", "note": "1::F::0::Main|" } },
                      "itemNotes": {}
                    }
                  }
                }
                """)!.AsObject(),
        };
        Task<System.Text.Json.Nodes.JsonObject> ReadTable(string name, CancellationToken _) =>
            Task.FromResult((System.Text.Json.Nodes.JsonObject)tables[name].DeepClone());
        var model = new PersistenceCharMgrReadModel(ReadTable);

        var first = await model.ReadAsync(1001, CancellationToken.None);
        var second = await model.ReadAsync(1002, CancellationToken.None);

        Assert.Equal(new CharMgrContact(2001, true, -10, 5), Assert.Single(first!.Contacts));
        Assert.Equal(new long[] { 3001 }, first.BlockedSenders);
        Assert.Equal(new CharMgrContactLabel("Fleet", 7), first.Labels[4]);
        Assert.Equal(new CharMgrOwnerNote(2, "Briefing", "Dock at Perimeter"), first.OwnerNotes.Single(note => note.NoteId == 2));
        Assert.Equal("Saved ship note", first.ItemNotes[4001]);
        Assert.Equal(5, first.NextOwnerNoteId);
        Assert.Equal(new CharMgrContact(2002, false, -5, 0), Assert.Single(second!.Contacts));
        Assert.Empty(second.BlockedSenders);
        Assert.Equal(new CharMgrContactLabel("Friends", 3), second.Labels[8]);
        Assert.Empty(second.ItemNotes);
        Assert.Null(await model.ReadAsync(0, CancellationToken.None));

        Task<System.Text.Json.Nodes.JsonObject> ReadWithoutNotes(string name, CancellationToken _) =>
            name == "characterNotes"
                ? Task.FromException<System.Text.Json.Nodes.JsonObject>(new Eve.GameStore.PersistenceException(
                    "table does not exist",
                    Eve.GameStore.PersistenceCodes.TableNotFound))
                : Task.FromResult((System.Text.Json.Nodes.JsonObject)tables[name].DeepClone());
        var missingNotes = await new PersistenceCharMgrReadModel(ReadWithoutNotes).ReadAsync(1001, CancellationToken.None);
        Assert.NotNull(missingNotes);
        Assert.Empty(missingNotes.OwnerNotes);
        Assert.Empty(missingNotes.ItemNotes);
        Assert.Null(missingNotes.NextOwnerNoteId);
    }

    [Fact]
    public async Task Optional_game_store_reads_default_only_for_missing_tables()
    {
        Task<System.Text.Json.Nodes.JsonObject> MissingTable(string tableName, CancellationToken cancellationToken) =>
            Task.FromException<System.Text.Json.Nodes.JsonObject>(new Eve.GameStore.PersistenceException(
                "table does not exist",
                Eve.GameStore.PersistenceCodes.TableNotFound));
        var offers = new StoreOffersService(async ct =>
            (System.Text.Json.Nodes.JsonObject?)await PersistenceReadHelpers.ReadOptionalTableAsync(
                MissingTable,
                "newEdenStore",
                ct));
        var result = await offers.CallAsync(new ServiceCall
        {
            Method = "GetOffers",
            Args = [],
            Session = new ClientSession { SessionId = 1 },
        });
        Assert.Equal(new PyList([]), result);

        Task<System.Text.Json.Nodes.JsonObject> DatabaseFailure(string tableName, CancellationToken cancellationToken) =>
            Task.FromException<System.Text.Json.Nodes.JsonObject>(new Eve.GameStore.PersistenceException(
                "database unavailable",
                "PERSISTENCE_CONNECTION_FAILED"));
        await Assert.ThrowsAsync<Eve.GameStore.PersistenceException>(async () =>
            await PersistenceReadHelpers.ReadOptionalTableAsync(
                DatabaseFailure,
                "newEdenStore",
                CancellationToken.None));
    }

    [Fact]
    public void Existing_read_services_are_registered_under_the_client_names()
    {
        var registry = new ServiceRegistry();
        var mail = new Eve.Services.Core.MailService(
            _ => Task.FromResult(new System.Text.Json.Nodes.JsonObject()),
            (_, _, _, _) => Task.CompletedTask);
        var notifications = new Eve.Services.Core.NotificationMgrService(
            (_, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(null),
            () => FixedNowMs);
        var calendar = new Eve.Services.Core.CalendarMgrService();
        var calendarProxy = new Eve.Services.Core.CalendarProxyService();
        var wars = new Eve.Services.Corporations.WarsInfoMgrService(null!, () => FixedNowMs);
        var online = new Eve.Services.CharacterInfo.OnlineStatusService(new Eve.Services.CharacterSessions(),
            (_, _) => ValueTask.FromResult<IReadOnlyCollection<long>>([]));
        var mailingLists = new Eve.Services.Core.MailingListService(
            _ => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(null));
        var rewards = new Eve.Services.Core.RewardsService();
        var storeOffers = new Eve.Services.Core.StoreOffersService(
            _ => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(null));
        var industryJobs = new Eve.Services.Core.IndustryJobsService(
            _ => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(null));
        var characterMining = new Eve.Services.Core.CharacterMiningLedgerService(
            _ => Task.FromResult(new System.Text.Json.Nodes.JsonObject()));
        var corporationMining = new Eve.Services.Core.CorpMiningLedgerService(
            _ => Task.FromResult(new System.Text.Json.Nodes.JsonObject()));
        var agents = new Eve.Services.Core.AgentMissionService(
            (_, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(null),
            (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, System.Text.Json.Nodes.JsonObject>>(new Dictionary<long, System.Text.Json.Nodes.JsonObject>()),
            (_, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonArray?>(null));
        var insurance = new Eve.Services.Core.InsuranceService(new EmptyInsuranceReadModel(), () => FixedNowMs);
        var structureDirectory = new Eve.Services.Core.StructureDirectoryService(new EmptyStructureDirectoryReadCallbacks());
        Func<CancellationToken, ValueTask<IReadOnlyList<System.Text.Json.Nodes.JsonObject>>> emptyIncursions =
            _ => ValueTask.FromResult<IReadOnlyList<System.Text.Json.Nodes.JsonObject>>([]);
        Func<long, CancellationToken, ValueTask<IReadOnlyList<System.Text.Json.Nodes.JsonObject>>> emptyAnomalies =
            (_, _) => ValueTask.FromResult<IReadOnlyList<System.Text.Json.Nodes.JsonObject>>([]);
        var map = new Eve.Services.Core.IncursionAnomalyService("map", emptyIncursions, emptyAnomalies);
        var tales = new Eve.Services.Core.IncursionAnomalyService("taleMgr", emptyIncursions, emptyAnomalies);
        var scanning = new Eve.Services.Core.IncursionAnomalyService("scanMgr", emptyIncursions, emptyAnomalies);
        var sovereignty = Eve.Services.Core.SovFwService.Create(
            _ => ValueTask.FromResult(Eve.Services.Core.SovFwReadSnapshot.Empty),
            _ => ValueTask.FromResult(false));

        foreach (var service in new Eve.Services.IService[]
        {
            mail, notifications, calendar, calendarProxy, wars, online, mailingLists, rewards,
            storeOffers, industryJobs, characterMining, corporationMining, agents, insurance,
            structureDirectory, map, tales, scanning,
        }.Concat(sovereignty))
            registry.Register(service);

        Assert.Same(mail, registry.Lookup("mailMgr"));
        Assert.Same(notifications, registry.Lookup("notificationMgr"));
        Assert.Same(calendar, registry.Lookup("calendarMgr"));
        Assert.Same(calendarProxy, registry.Lookup("calendarProxy"));
        Assert.Same(wars, registry.Lookup("warsInfoMgr"));
        Assert.Same(online, registry.Lookup("onlineStatus"));
        Assert.Same(mailingLists, registry.Lookup("mailingListsMgr"));
        Assert.Same(rewards, registry.Lookup("rewardMgr"));
        Assert.Same(storeOffers, registry.Lookup("storeManager"));
        Assert.Same(industryJobs, registry.Lookup("industryManager"));
        Assert.Same(characterMining, registry.Lookup("characterMiningLedger"));
        Assert.Same(corporationMining, registry.Lookup("corpMiningLedger"));
        Assert.Same(agents, registry.Lookup("agentMgr"));
        Assert.Same(insurance, registry.Lookup("insuranceSvc"));
        Assert.Same(structureDirectory, registry.Lookup("structureDirectory"));
        Assert.Same(map, registry.Lookup("map"));
        Assert.Same(tales, registry.Lookup("taleMgr"));
        Assert.Same(scanning, registry.Lookup("scanMgr"));
        Assert.Same(sovereignty[0], registry.Lookup("sovMgr"));
        Assert.Same(sovereignty[1], registry.Lookup("fwWarzoneSolarsystem"));
        Assert.Equal(20, registry.Count);
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

private sealed class InSpaceInventory : Eve.Services.Ships.ISpaceContainers
    {
        public const int SystemId = 30000142;
        public bool IsInSpace(ClientSession session) => true;
        public int? SolarSystemOf(ClientSession session) => SystemId;
        public Task<Eve.Inventory.Item?> OpenAsync(ClientSession session, long containerId, CancellationToken cancellationToken) => Task.FromResult<Eve.Inventory.Item?>(null);
        public Task<long?> MoveAsync(ClientSession session, long itemId, long sourceId, long destinationId, long? quantity, CancellationToken cancellationToken) => Task.FromResult<long?>(null);
        public Task<bool?> MoveAllAsync(ClientSession session, IReadOnlyList<long> itemIds, long sourceId, long destinationId, CancellationToken cancellationToken) => Task.FromResult<bool?>(null);
        public Task<IReadOnlyList<long>> JettisonAsync(ClientSession session, IReadOnlyList<long> itemIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<long>>([]);
    }

    private sealed class DroneBayPort : Eve.Services.Ships.ISpaceDroneBays
    {
        public int RoomCalls { get; private set; }
        public (long ItemId, long SourceId, long DestinationId, int Flag, long? Quantity)? AddCall { get; private set; }

        public Task<Eve.Ships.CharacterShips.Room?> RoomAsync(ClientSession session, long shipId, int flag, CancellationToken cancellationToken)
        {
            RoomCalls++;
            return Task.FromResult<Eve.Ships.CharacterShips.Room?>(new(25, 5));
        }

        public Task<long?> AddAsync(ClientSession session, long itemId, long sourceId, long destinationId, int destinationFlag, long? quantity, CancellationToken cancellationToken)
        {
            AddCall = (itemId, sourceId, destinationId, destinationFlag, quantity);
            return Task.FromResult<long?>(itemId);
        }
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

    [Fact]
    public async Task Inventory_registration_injects_the_drone_bay_port_for_capacity_and_return()
    {
        const long characterId = 90000001;
        const long shipId = 1000000001;
        const long droneId = 1000000002;
        await using var source = new Npgsql.NpgsqlDataSourceBuilder(
            "Host=127.0.0.1;Database=host_port_test;Username=unused;Pooling=false").Build();
        var items = new Eve.Inventory.PgItemStore(source);
        var statics = new Eve.Dogma.DogmaStatic([], [], []);
        var ships = new Eve.Ships.CharacterShips(
            source,
            statics,
            new Eve.Characters.PgCharacterStore(source),
            items,
            new Eve.Skills.PgSkillStore(source),
            new Eve.Fitting.PgModuleStateStore(source));
        var droneBays = new DroneBayPort();
        var space = new InSpaceInventory();
        var broker = HostInventoryServiceFactory.CreateInvBroker(
            items,
            new Eve.Inventory.ItemTypeCatalog(new Eve.Sde.SdeReader(source)),
            new Eve.Universe.StationCatalog([]),
            ships,
            statics,
            () => FixedNowMs,
            space,
            droneBays);

        var session = new Eve.Services.ClientSession();
        session.Set("charid", new Eve.Marshal.PyInt(characterId));
        session.Set("shipid", new Eve.Marshal.PyInt(shipId));
        session.Set("solarsystemid2", new Eve.Marshal.PyInt(InSpaceInventory.SystemId));
        var opened = Assert.IsType<Eve.Marshal.PySubStruct>(await broker.GetInventoryFromId(new Eve.Services.ServiceCall
        {
            Method = "GetInventoryFromId",
            Args = [new Eve.Marshal.PyInt(shipId)],
            Session = session,
        }));
        var oid = Assert.IsType<Eve.Marshal.PyStr>(
            Assert.IsType<Eve.Marshal.PyTuple>(Assert.IsType<Eve.Marshal.PySubStream>(opened.Value).Decoded).Items[0]).Value;

        var capacity = await broker.GetCapacity(new Eve.Services.ServiceCall
        {
            Method = "GetCapacity",
            Args = [new Eve.Marshal.PyInt(Eve.Ships.CharacterShips.DroneBayFlag)],
            Session = session,
            BoundObjectId = oid,
        });
        Assert.IsType<Eve.Marshal.PyObject>(capacity);
        Assert.Equal(1, droneBays.RoomCalls);

        var added = await broker.Add(new Eve.Services.ServiceCall
        {
            Method = "Add",
            Args = [new Eve.Marshal.PyInt(droneId), new Eve.Marshal.PyInt(InSpaceInventory.SystemId)],
            Kwargs = new Eve.Marshal.PyDict([
                new KeyValuePair<Eve.Marshal.PyValue, Eve.Marshal.PyValue>(new Eve.Marshal.PyStr("flag"), new Eve.Marshal.PyInt(Eve.Ships.CharacterShips.DroneBayFlag))]),
            Session = session,
            BoundObjectId = oid,
        });

        Assert.Equal(Eve.Services.InventoryWire.Id(droneId), added);
        Assert.Equal((droneId, (long)InSpaceInventory.SystemId, shipId, Eve.Ships.CharacterShips.DroneBayFlag, (long?)null), droneBays.AddCall);
    }

    [Fact]
    public async Task Structure_directory_host_read_model_uses_jump_bridge_profiles_and_access_groups()
    {
        const long corporationId = 98000001;
        const long allianceId = 99000001;
        const long allowedCharacter = 90000001;
        const long deniedCharacter = 90000002;
        const long profileId = 50001;
        const long groupId = 70001;
        var tables = new Dictionary<string, System.Text.Json.Nodes.JsonObject>
        {
            ["structures"] = new()
            {
                ["1001"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["structureID"] = 1001,
                    ["typeID"] = 35841,
                    ["solarSystemID"] = 30000142,
                    ["ownerCorpID"] = corporationId,
                    ["allianceID"] = allianceId,
                    ["profileID"] = profileId,
                    ["itemName"] = "Bridge A",
                    ["serviceStates"] = new System.Text.Json.Nodes.JsonObject { ["1"] = 1, ["15"] = 1 },
                    ["accessProfile"] = new System.Text.Json.Nodes.JsonObject { ["docking"] = "owner" },
                    ["devFlags"] = new System.Text.Json.Nodes.JsonObject { ["destinationSolarsystemID"] = 30000143 },
                },
                ["1002"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["structureID"] = 1002,
                    ["typeID"] = 35841,
                    ["solarSystemID"] = 30000143,
                    ["ownerCorpID"] = corporationId,
                    ["allianceID"] = allianceId,
                    ["profileID"] = profileId,
                    ["itemName"] = "Bridge B",
                    ["serviceStates"] = new System.Text.Json.Nodes.JsonObject { ["1"] = 1, ["15"] = 1 },
                    ["accessProfile"] = new System.Text.Json.Nodes.JsonObject { ["docking"] = "owner" },
                    ["devFlags"] = new System.Text.Json.Nodes.JsonObject { ["destinationSolarsystemID"] = 30000142 },
                },
            },
            ["structureProfiles"] = new()
            {
                ["profilesByID"] = new System.Text.Json.Nodes.JsonObject
                {
                    [profileId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["corporationID"] = corporationId,
                        ["isDefault"] = false,
                        ["settingsBySettingID"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["19"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["groupID"] = groupId, ["value"] = 1 }),
                            ["34"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["groupID"] = groupId, ["value"] = 1 }),
                        },
                    },
                },
            },
            ["accessGroups"] = new()
            {
                [groupId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                {
                    ["creatorID"] = 90000003,
                    ["members"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                    {
                        ["memberID"] = allowedCharacter,
                        ["membershipType"] = 0,
                    }),
                },
            },
        };
        Task<System.Text.Json.Nodes.JsonObject> ReadTable(string name, CancellationToken _) =>
            Task.FromResult((System.Text.Json.Nodes.JsonObject)tables.GetValueOrDefault(name, new()).DeepClone());
        Task<Eve.Relations.Affiliation> Affiliation(long character, CancellationToken token) =>
            Task.FromResult(new Eve.Relations.Affiliation(corporationId, allianceId, null));
        var model = new PersistenceStructureDirectoryReadModel(ReadTable, Affiliation);

        var allowedSession = new Eve.Services.ClientSession();
        allowedSession.Set("charid", new Eve.Marshal.PyInt(allowedCharacter));
        var accessCall = new Eve.Services.ServiceCall { Method = "GetJumpBridgesWithMyAccess", Args = [], Session = allowedSession };
        var access = Assert.IsType<Eve.Marshal.PyTuple>(await model.GetJumpBridgesWithMyAccessAsync(accessCall));
        Assert.Single(Assert.IsType<Eve.Marshal.PyList>(access.Items[0]).Items);
        Assert.Equal(2, Assert.IsType<Eve.Marshal.PyList>(access.Items[1]).Items.Count);
        Assert.Empty(Assert.IsType<Eve.Marshal.PyList>(access.Items[2]).Items);
        Assert.Equal(2, Assert.IsType<Eve.Marshal.PyList>(await model.GetNearbyJumpBridgesAsync(accessCall)).Items.Count);

        var deniedSession = new Eve.Services.ClientSession();
        deniedSession.Set("charid", new Eve.Marshal.PyInt(deniedCharacter));
        var deniedCall = new Eve.Services.ServiceCall { Method = "GetJumpBridgesWithMyAccess", Args = [], Session = deniedSession };
        var denied = Assert.IsType<Eve.Marshal.PyTuple>(await model.GetJumpBridgesWithMyAccessAsync(deniedCall));
        Assert.Empty(Assert.IsType<Eve.Marshal.PyList>(denied.Items[1]).Items);
        Assert.Equal(2, Assert.IsType<Eve.Marshal.PyList>(denied.Items[2]).Items.Count);
        Assert.Empty(Assert.IsType<Eve.Marshal.PyList>(await model.GetNearbyJumpBridgesAsync(deniedCall)).Items);

        var unknownTables = new Dictionary<string, System.Text.Json.Nodes.JsonObject>
        {
            ["structures"] = tables["structures"],
            ["structureProfiles"] = new() { ["profilesByID"] = new System.Text.Json.Nodes.JsonObject() },
            ["accessGroups"] = new(),
        };
        Task<System.Text.Json.Nodes.JsonObject> ReadUnknown(string name, CancellationToken _) =>
            Task.FromResult((System.Text.Json.Nodes.JsonObject)unknownTables.GetValueOrDefault(name, new()).DeepClone());
        var unknown = new PersistenceStructureDirectoryReadModel(ReadUnknown, Affiliation);
        Assert.Empty(Assert.IsType<Eve.Marshal.PyList>(await unknown.GetNearbyJumpBridgesAsync(accessCall)).Items);
    }

    [Fact]
    public async Task Incursion_and_scan_host_read_models_read_active_content_and_dungeon_state()
    {
        const long systemId = 30000142;
        const long now = 1_790_646_377_000;
        var active = new[]
        {
            new Eve.Content.ContentInstance(501, "incursion", "incursion-template", 0, "active",
                "{\"taleID\":501,\"templateClassID\":7,\"templateNameID\":5001,\"stagingSolarSystemID\":30000142,\"aggressorFactionID\":500001,\"state\":1,\"influence\":0.4,\"systems\":[{\"solarSystemID\":30000142,\"sceneType\":1}]}",
                now - 1000, null, null, null),
            new Eve.Content.ContentInstance(601, "anomaly", "anomaly-template", systemId, "active",
                "{\"siteID\":601,\"targetID\":\"anomaly-601\",\"solarSystemID\":30000142,\"position\":[1,2,3],\"difficulty\":2,\"dungeonID\":91}",
                now - 1000, null, null, null),
        };
        Task<IReadOnlyList<Eve.Content.ContentInstance>> ActiveOf(string kind, long owner, CancellationToken _) =>
            Task.FromResult<IReadOnlyList<Eve.Content.ContentInstance>>(active
                .Where(instance => instance.Kind == kind && (instance.Kind == "incursion" ? owner == 0 : instance.Owner == owner))
                .ToArray());
        Task<System.Text.Json.Nodes.JsonObject> ReadGameStore(string name, CancellationToken _) =>
            Task.FromResult(name == "dungeonRuntimeState"
                ? new System.Text.Json.Nodes.JsonObject
                {
                    ["instancesByID"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["602"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["instanceID"] = 602,
                            ["siteKind"] = "anomaly",
                            ["lifecycleState"] = "active",
                            ["solarSystemID"] = systemId,
                            ["siteID"] = 602,
                            ["targetID"] = "anomaly-602",
                            ["position"] = new System.Text.Json.Nodes.JsonArray(System.Text.Json.Nodes.JsonValue.Create(4), System.Text.Json.Nodes.JsonValue.Create(5), System.Text.Json.Nodes.JsonValue.Create(6)),
                            ["difficulty"] = 3,
                        },
                    },
                }
                : new System.Text.Json.Nodes.JsonObject());
        var readModel = new PersistenceIncursionAnomalyReadModel(ActiveOf, ReadGameStore, () => now);
        var session = new Eve.Services.ClientSession();
        session.Set("solarsystemid2", new Eve.Marshal.PyInt(systemId));
        var call = new Eve.Services.ServiceCall { Method = "GetIncursionGlobalReport", Args = [], Session = session };
        var map = new Eve.Services.Core.IncursionAnomalyService("map", readModel.ReadIncursionsAsync, readModel.ReadAnomaliesAsync);
        var report = Assert.IsType<Eve.Marshal.PyList>(await map.CallAsync(call));
        Assert.Single(report.Items);

        var scanner = new Eve.Services.Core.IncursionAnomalyService("scanMgr", readModel.ReadIncursionsAsync, readModel.ReadAnomaliesAsync);
        var fullState = Assert.IsType<Eve.Marshal.PyList>(await scanner.CallAsync(new Eve.Services.ServiceCall { Method = "GetFullState", Args = [], Session = session }));
        var anomalies = Assert.IsType<Eve.Marshal.PyDict>(fullState.Items[0]);
        Assert.Equal(2, anomalies.Entries.Count);
    }

    [Fact]
    public async Task Weapon_target_context_reads_affiliation_crimewatch_war_limited_engagement_and_friendly_fire()
    {
        const long now = 1_790_646_377_000;
        const long attacker = 90000001;
        const long target = 90000002;
        const long corporation = 98000001;
        const long alliance = 99000001;
        var tables = new Dictionary<string, System.Text.Json.Nodes.JsonObject>
        {
            ["crimewatchRuntime"] = new()
            {
                ["le:90000001:90000002"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["kind"] = "limitedEngagement",
                    ["firstCharacterID"] = attacker,
                    ["secondCharacterID"] = target,
                    ["engagement"] = new System.Text.Json.Nodes.JsonObject { ["expiresAtMs"] = now + 1_000 },
                },
            },
            ["corporationRuntime"] = new()
            {
                ["corporations"] = new System.Text.Json.Nodes.JsonObject
                {
                    [corporation.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["friendlyFire"] = 1,
                        ["aggressionSettings"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["enableAfter"] = "0",
                            ["disableAfter"] = null,
                        },
                    },
                },
            },
        };
        Task<System.Text.Json.Nodes.JsonObject> ReadTable(string name, CancellationToken _) =>
            Task.FromResult((System.Text.Json.Nodes.JsonObject)tables[name].DeepClone());
        var model = new PersistenceWeaponTargetContextReadModel(
            (_, _) => Task.FromResult(new Eve.Relations.Affiliation(corporation, alliance, null)),
            (_, _, _, _) => Task.FromResult(true),
            (_, _) => Task.FromResult(Eve.Crimewatch.CombatState.Idle with
            {
                Flag = Eve.Crimewatch.Flag.Criminal,
                FlagUntilMs = now + 1_000,
            }),
            (_, _) => Task.FromResult(-5.0),
            ReadTable,
            () => now);

        var context = await model.ReadAsync(attacker, target, CancellationToken.None);

        Assert.True(context.AttackerIsPlayer);
        Assert.True(context.TargetIsPlayerProperty);
        Assert.False(context.SamePilot);
        Assert.True(context.TargetIsCriminal);
        Assert.False(context.TargetIsSuspect);
        Assert.True(context.TargetIsOutlaw);
        Assert.True(context.ActiveWar);
        Assert.True(context.ActiveLimitedEngagement);
        Assert.True(context.LegalFriendlyFire);
    }

    [Fact]
    public async Task Sovereignty_host_read_model_uses_saved_claim_hub_structures_and_fuel_access_group()
    {
        const long systemId = 30000142;
        const long characterId = 90000001;
        const long corporationId = 98000001;
        const long allianceId = 99000001;
        const long fuelGroupId = 99000077;
        var tables = new Dictionary<string, System.Text.Json.Nodes.JsonObject>
        {
            ["sovereignty"] = new()
            {
                ["systems"] = new System.Text.Json.Nodes.JsonObject
                {
                    [systemId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["solarSystemID"] = systemId,
                        ["claimStructureID"] = 1020001,
                        ["infrastructureHubID"] = 1020002,
                        ["corporationID"] = corporationId,
                        ["allianceID"] = allianceId,
                        ["claimTime"] = 16600000,
                        ["fuelAccessGroupID"] = fuelGroupId,
                        ["warzoneID"] = 1,
                        ["factionID"] = 500003,
                        ["structures"] = new System.Text.Json.Nodes.JsonArray
                        {
                            new System.Text.Json.Nodes.JsonObject
                            {
                                ["itemID"] = 1020002,
                                ["typeID"] = 32226,
                                ["corporationID"] = corporationId,
                                ["allianceID"] = allianceId,
                                ["solarSystemID"] = systemId,
                                ["defenseMultiplier"] = 1.25,
                                ["isCapital"] = true,
                            },
                        },
                    },
                },
            },
            ["accessGroups"] = new()
            {
                [fuelGroupId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                {
                    ["creatorID"] = 90000003,
                    ["members"] = new System.Text.Json.Nodes.JsonArray
                    {
                        new System.Text.Json.Nodes.JsonObject { ["memberID"] = characterId, ["membershipType"] = 0 },
                    },
                },
            },
        };
        Task<System.Text.Json.Nodes.JsonObject> ReadTable(string name, CancellationToken _) =>
            Task.FromResult((System.Text.Json.Nodes.JsonObject)tables.GetValueOrDefault(name, new()).DeepClone());
        var model = new PersistenceSovFwReadModel(
            ReadTable,
            (_, _) => Task.FromResult(new Eve.Relations.Affiliation(corporationId, allianceId, null)));
        var session = new Eve.Services.ClientSession();
        session.Set("charid", new Eve.Marshal.PyInt(characterId));
        session.Set("solarsystemid2", new Eve.Marshal.PyInt(systemId));
        var call = new Eve.Services.ServiceCall { Method = "GetSystemSovereigntyInfo", Args = [], Session = session };

        var snapshot = await model.ReadSnapshotAsync(call);

        Assert.Equal(new Eve.Services.Core.SovClaimRecord(1020001, corporationId, allianceId), snapshot.ClaimsBySolarSystemId[systemId]);
        Assert.Equal(new Eve.Services.Core.SovHubRecord(1020002, corporationId, allianceId, 16600000), snapshot.HubsBySolarSystemId[systemId]);
        Assert.Equal(new Eve.Services.Core.SovStructureRecord(1020002, 32226, null, corporationId, allianceId, systemId,
            DefenseMultiplier: 1.25, IsCapital: true), Assert.Single(snapshot.StructuresBySolarSystemId[systemId]));
        Assert.Contains(snapshot.FwOccupationStates, state => state == new Eve.Services.Core.FwOccupationRecord(1, systemId, 500003));
        Assert.True(await model.IsOnLocalFuelAccessGroupAsync(call));
    }
}
