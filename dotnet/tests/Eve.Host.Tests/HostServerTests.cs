using Eve.Host;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.CharacterInfo;

namespace Eve.Host.Tests;

/// <summary>The host over a real socket, driven by a scripted front.</summary>
public sealed class PgFactAttribute : Xunit.FactAttribute
{
    public PgFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null)
            Skip = "EVE_PG_URL is not set";
    }
}

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

    private void Start(AccountOptions? identity = null, int maxPending = 10_000, Action<ServiceRegistry>? configure = null, IAccountDirectory? accounts = null, bool strictRpc = false)
    {
        var registry = new ServiceRegistry();
        configure?.Invoke(registry);
        Func<long> now = () => FixedNowMs;
        var calendarStore = new TestCalendarStore();
        var calendarAudience = new TestCalendarAudienceReader();
        foreach (var service in CoreServices.Create(new SubscriptionSettings(), new NoAccounts(), now, calendarStore, calendarAudience))
        {
            registry.Register(service);
        }
        var config = GlobalConfig.Build(Options);
        registry.Register(new MachoNetService(new Info(registry, config), now));
        var dispatcher = new PacketDispatcher(registry, now) { StrictRpc = strictRpc };
        var encoded = MarshalCodec.Encode(config);
        _server = new HostServer(
            new HostOptions { MaxPendingPacketsPerSession = maxPending, Log = message => { lock (_log) { _log.Add(message); } } },
            dispatcher,
            accounts ?? new InMemoryAccountDirectory(identity ?? new AccountOptions { AutoCreate = true }),
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

    private sealed class EmptyStructureDirectoryReadCallbacks :
        Eve.Services.Core.IStructureDirectoryReadCallbacks,
        Eve.Services.Core.IStructureSettingsReadCallbacks
    {
        public ValueTask<Eve.Marshal.PyValue> GetJumpBridgesWithMyAccessAsync(Eve.Services.ServiceCall call) =>
            ValueTask.FromResult<Eve.Marshal.PyValue>(new Eve.Marshal.PyTuple([
                new Eve.Marshal.PyList([]), new Eve.Marshal.PyList([]), new Eve.Marshal.PyList([])]));

        public ValueTask<Eve.Marshal.PyValue> GetNearbyJumpBridgesAsync(Eve.Services.ServiceCall call) =>
            ValueTask.FromResult<Eve.Marshal.PyValue>(new Eve.Marshal.PyList([]));

        public ValueTask<Eve.Marshal.PyValue> GetStructureDescriptionAsync(Eve.Services.ServiceCall call) =>
            ValueTask.FromResult<Eve.Marshal.PyValue>(new Eve.Marshal.PyStr(""));

        public ValueTask<Eve.Services.Core.PersistedStructureInfo?> GetStructureInfoAsync(long structureId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Eve.Services.Core.PersistedStructureInfo?>(null);

        public ValueTask<IReadOnlyList<Eve.Services.Core.StructureDockingAccessSnapshot>> GetDockingAccessSnapshotsAsync(
            long characterId, long corporationId, long allianceId, IReadOnlyList<long> structureIds, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<Eve.Services.Core.StructureDockingAccessSnapshot>>([]);

        public ValueTask<Eve.Marshal.PyDict> GetMyCharacterStructuresAsync(long characterId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new Eve.Marshal.PyDict([]));

        public ValueTask<Eve.Marshal.PyDict> GetMyCorporationStructuresAsync(long characterId, long corporationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new Eve.Marshal.PyDict([]));

        public ValueTask<Eve.Marshal.PyDict> CharacterGetServicesAsync(long characterId, long structureId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new Eve.Marshal.PyDict([]));

        public ValueTask<bool> CharacterHasServiceAsync(long characterId, long structureId, long serviceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask<bool> CharacterHasSettingAsync(long characterId, long structureId, long settingId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);


        public ValueTask<PyValue> GetStructureMapDataAsync(long solarSystemId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<PyValue>(new PyList([]));
        public ValueTask<PyValue> GetMyAccessibleOnlineCynoBeaconStructuresAsync(ServiceCall call) =>
            ValueTask.FromResult<PyValue>(new PyList([]));
        public ValueTask<PyValue> GetSolarSystemsWithBeaconsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<PyValue>(new PyList([]));
        public ValueTask<PyValue> GetValidWarHQsAsync(ServiceCall call, long ownerId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<PyValue>(new PyList([]));
        public ValueTask<bool> SetStructureDescriptionAsync(ServiceCall call) => ValueTask.FromResult(false);
        public ValueTask<IReadOnlyList<long>> GetStructureIdsInSolarSystemAsync(long solarSystemId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<long>>([]);
        public ValueTask<PyValue> CharacterGetServiceAsync(long characterId, long structureId, long serviceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<PyValue>(PyNone.Instance);
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

    private sealed class EmptyIndustryJobOperations : Eve.Services.Core.IIndustryJobOperations
    {
        public ValueTask<long> InstallJobAsync(
            Eve.Services.Core.IndustryJobInstallRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(0L);

        public ValueTask CompleteJobAsync(
            Eve.Services.Core.IndustryJobAction request,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask CompleteManyJobsAsync(
            Eve.Services.Core.IndustryJobBatch request,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask CancelJobAsync(
            Eve.Services.Core.IndustryJobAction request,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
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
        Start(new AccountOptions { AutoCreate = true, SkipPasswordCheck = true });
        var open = await Handshaken(await ConnectAsync());
        Assert.IsType<Accepted>((await open.AuthenticateAsync("pilot", [1], 1)).Outcome);
        Assert.IsType<Accepted>((await open.AuthenticateAsync("pilot", [2], 2)).Outcome);
    }

    [Fact]
    public async Task Unknown_users_are_refused_when_auto_creation_is_off()
    {
        Start(new AccountOptions());
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
    public async Task Agent_location_read_model_uses_npc_characters_and_station_location_ids()
    {
        using var stationAgent = System.Text.Json.JsonDocument.Parse("""{"locationID":60000004}""");
        using var missingLocation = System.Text.Json.JsonDocument.Parse("""{"name":{"en":"No location"}}""");
        string? requestedTable = null;
        var model = new PersistenceAgentLocationReadModel((table, _) =>
        {
            requestedTable = table;
            return Task.FromResult<IReadOnlyList<Eve.Sde.SdeRow>>([
                new Eve.Sde.SdeRow(300001, stationAgent.RootElement),
                new Eve.Sde.SdeRow(300002, missingLocation.RootElement),
            ]);
        });

        var locations = await model.ReadAsync(CancellationToken.None);

        Assert.Equal("npcCharacters", requestedTable);
        Assert.Equal(60000004, locations[300001]);
        Assert.DoesNotContain(300002, locations.Keys);
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
        var model = new PersistenceCharMgrReadModel(ReadTable,
            (_, _, _, _) => ValueTask.FromResult<System.Collections.Generic.IReadOnlyList<Eve.Marshal.PyValue>>([]),
            (_, _) => ValueTask.FromResult<string?>(null),
            (_, _, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.FromResult<long?>(null));

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
        var missingNotes = await new PersistenceCharMgrReadModel(ReadWithoutNotes,
            (_, _, _, _) => ValueTask.FromResult<System.Collections.Generic.IReadOnlyList<Eve.Marshal.PyValue>>([]),
            (_, _) => ValueTask.FromResult<string?>(null),
            (_, _, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.FromResult<long?>(null)).ReadAsync(1001, CancellationToken.None);
        Assert.NotNull(missingNotes);
        Assert.Empty(missingNotes.OwnerNotes);
        Assert.Empty(missingNotes.ItemNotes);
        Assert.Null(missingNotes.NextOwnerNoteId);
    }


    [Fact]
    public async Task Character_personal_rpc_writes_use_the_existing_owner_scoped_tables()
    {
        var tables = new Dictionary<string, System.Text.Json.Nodes.JsonObject>
        {
            ["characters"] = System.Text.Json.Nodes.JsonNode.Parse("""
                { "1001": { "personalContacts": {}, "blockedOwners": {} } }
                """)!.AsObject(),
            ["characterNotes"] = System.Text.Json.Nodes.JsonNode.Parse("""
                {
                  "_meta": { "nextNoteID": 5 },
                  "characters": {
                    "1001": {
                      "notes": { "2": { "noteID": 2, "label": "Briefing", "note": "old", "created": "old-created", "updated": "old-updated" } },
                      "itemNotes": { "4000": { "itemID": 4000, "note": "old", "updated": "old-updated" } }
                    }
                  }
                }
                """)!.AsObject(),
        };
        Task<System.Text.Json.Nodes.JsonObject> ReadTable(string name, CancellationToken _) =>
            Task.FromResult((System.Text.Json.Nodes.JsonObject)tables[name].DeepClone());
        var writes = new List<(string Table, string Path, System.Text.Json.Nodes.JsonNode? Data)>();
        var model = new PersistenceCharMgrReadModel(
            ReadTable,
            (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<Eve.Marshal.PyValue>>([]),
            (_, _) => ValueTask.FromResult<string?>(null),
            (_, _, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.FromResult<long?>(null),
            (table, path, data) =>
            {
                writes.Add((table, path, data?.DeepClone()));
                return new Eve.GameStore.WriteResult(true, null);
            },
            (_, _) => new Eve.GameStore.WriteResult(true, null));

        await model.UpdateAsync(1001, data => data with
        {
            Contacts = [new CharMgrContact(2001, true, -10, 3)],
            BlockedSenders = [3001],
            OwnerNotes = [new CharMgrOwnerNote(2, "Updated", "changed"), new CharMgrOwnerNote(5, "Briefing", "ready")],
            ItemNotes = new Dictionary<long, string> { [4000] = "changed", [4001] = "ship note" },
            NextOwnerNoteId = 6,
        }, CancellationToken.None);

        var contact = Assert.Single(writes, write => write.Path == "/1001/personalContacts/2001");
        Assert.Equal("characters", contact.Table);
        Assert.Equal(2001, contact.Data!["contactID"]!.GetValue<long>());
        Assert.True(contact.Data["inWatchlist"]!.GetValue<bool>());
        Assert.Equal(-10, contact.Data["relationshipID"]!.GetValue<long>());
        Assert.Equal(3L, contact.Data["labelMask"]!.GetValue<long>());
        Assert.Contains(writes, write => write.Table == "characters" &&
            write.Path == "/1001/blockedOwners/3001" && write.Data!["senderID"]!.GetValue<long>() == 3001);
        var newOwnerNote = Assert.Single(writes, write =>
            write.Table == "characterNotes" && write.Path == "/characters/1001/notes/5");
        Assert.Equal("ready", newOwnerNote.Data!["note"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(newOwnerNote.Data["created"]!.GetValue<string>()));
        Assert.False(string.IsNullOrEmpty(newOwnerNote.Data["updated"]!.GetValue<string>()));
        Assert.Contains(writes, write => write.Path == "/characters/1001/notes/2/label" &&
            write.Data!.GetValue<string>() == "Updated");
        Assert.Contains(writes, write => write.Path == "/characters/1001/notes/2/note" &&
            write.Data!.GetValue<string>() == "changed");
        Assert.Contains(writes, write => write.Path == "/characters/1001/notes/2/updated");
        Assert.DoesNotContain(writes, write => write.Path == "/characters/1001/notes/2");
        var newItemNote = Assert.Single(writes, write =>
            write.Table == "characterNotes" && write.Path == "/characters/1001/itemNotes/4001");
        Assert.Equal("ship note", newItemNote.Data!["note"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(newItemNote.Data["updated"]!.GetValue<string>()));
        Assert.Contains(writes, write => write.Path == "/characters/1001/itemNotes/4000/note" &&
            write.Data!.GetValue<string>() == "changed");
        Assert.Contains(writes, write => write.Path == "/characters/1001/itemNotes/4000/updated");
        Assert.Contains(writes, write => write.Path == "/_meta/nextNoteID" &&
            write.Data!.GetValue<long>() == 6);

        await model.WriteActivityStatusAsync(1001, 2, "12345", CancellationToken.None);
        await model.WriteLogSettingsAsync(1001, new Eve.Marshal.PyDict([
            new KeyValuePair<Eve.Marshal.PyValue, Eve.Marshal.PyValue>(
                new Eve.Marshal.PyStr("window"), new Eve.Marshal.PyStr("chat")),
        ]), CancellationToken.None);
        Assert.Contains(writes, write => write.Path == "/1001/activityStatus" &&
            write.Data!.GetValue<long>() == 2);
        Assert.Contains(writes, write => write.Path == "/1001/activityStatusExtraInfo" &&
            write.Data!.GetValue<string>() == "12345");
        Assert.Contains(writes, write => write.Path == "/1001/lastSettingsTelemetry" &&
            write.Data!["window"]!.GetValue<string>() == "chat");

        var removalTables = new Dictionary<string, System.Text.Json.Nodes.JsonObject>
        {
            ["characters"] = System.Text.Json.Nodes.JsonNode.Parse("""
                {
                  "1001": {
                    "personalContacts": { "2001": { "contactID": 2001 } },
                    "blockedOwners": { "3001": { "senderID": 3001 } }
                  }
                }
                """)!.AsObject(),
            ["characterNotes"] = System.Text.Json.Nodes.JsonNode.Parse("""
                {
                  "_meta": { "nextNoteID": 6 },
                  "characters": {
                    "1001": {
                      "notes": { "5": { "noteID": 5, "label": "Briefing", "note": "ready" } },
                      "itemNotes": { "4001": { "itemID": 4001, "note": "ship note" } }
                    }
                  }
                }
                """)!.AsObject(),
        };
        Task<System.Text.Json.Nodes.JsonObject> ReadRemovalTable(string name, CancellationToken _) =>
            Task.FromResult((System.Text.Json.Nodes.JsonObject)removalTables[name].DeepClone());
        var removals = new List<(string Table, string Path)>();
        var removeModel = new PersistenceCharMgrReadModel(
            ReadRemovalTable,
            (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<Eve.Marshal.PyValue>>([]),
            (_, _) => ValueTask.FromResult<string?>(null),
            (_, _, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.FromResult<long?>(null),
            (_, _, _) => new Eve.GameStore.WriteResult(true, null),
            (table, path) =>
            {
                removals.Add((table, path));
                return new Eve.GameStore.WriteResult(true, null);
            });
        await removeModel.UpdateAsync(1001, data => data with
        {
            Contacts = [],
            BlockedSenders = [],
            OwnerNotes = [],
            ItemNotes = new Dictionary<long, string>(),
            NextOwnerNoteId = null,
        }, CancellationToken.None);
        Assert.Contains(("characters", "/1001/personalContacts/2001"), removals);
        Assert.Contains(("characters", "/1001/blockedOwners/3001"), removals);
        Assert.Contains(("characterNotes", "/characters/1001/notes/5"), removals);
        Assert.Contains(("characterNotes", "/characters/1001/itemNotes/4001"), removals);
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
    public void CharMgr_killmail_rows_expose_character_ids_as_packed_row_attributes()
    {
        var rows = new[]
        {
            System.Text.Json.Nodes.JsonNode.Parse(
                """{"finalCharacterID":101,"victimCharacterID":202,"killmailID":303}""")!.AsObject(),
        };
        static PyValue ToPy(System.Text.Json.Nodes.JsonNode? node) =>
            node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<int>(out var integer)
                ? new PyInt(integer)
                : PyNone.Instance;

        var packed = global::Eve.Host.PersistenceCharMgrReadModel.PackKillmailRows(rows, ToPy);
        var row = Assert.IsType<PyPackedRow>(Assert.Single(packed));
        var attributes = row.Fields().ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);

        Assert.Equal(new PyInt(101), attributes["finalCharacterID"]);
        Assert.Equal(new PyInt(202), attributes["victimCharacterID"]);
        Assert.Contains("finalCharacterID", row.Columns.Select(column => column.Name));
        Assert.Contains("victimCharacterID", row.Columns.Select(column => column.Name));
    }

    [Fact]
    public async Task Sov_write_operations_flush_and_read_back_authorized_hub_and_skyhook_mutations()
    {
        const long systemId = 30_000_142;
        const long otherSystemId = 30_000_143;
        const long hubId = 81_001;
        const long corporationId = 90_000_001;
        const long allianceId = 70_000_001;
        const long accessListId = 77_001;
        var claimTimeFileTime = DateTime.UtcNow.ToFileTimeUtc() - TimeSpan.FromHours(23).Ticks;
        var tables = new Dictionary<string, System.Text.Json.Nodes.JsonObject>
        {
            ["sovereignty"] = new()
            {
                ["systems"] = new System.Text.Json.Nodes.JsonObject
                {
                    [systemId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["solarSystemID"] = systemId,
                        ["infrastructureHubID"] = hubId,
                        ["corporationID"] = corporationId,
                        ["allianceID"] = allianceId,
                        ["claimTime"] = claimTimeFileTime,
                        ["hubUpgradeConfiguration"] = new System.Text.Json.Nodes.JsonArray(),
                    },
                    [otherSystemId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["solarSystemID"] = otherSystemId,
                        ["infrastructureHubID"] = 81_002,
                        ["corporationID"] = 90_000_002,
                        ["allianceID"] = allianceId,
                    },
                    ["30000144"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["solarSystemID"] = 30_000_144,
                        ["infrastructureHubID"] = 81_003,
                        ["corporationID"] = 90_000_003,
                        ["allianceID"] = 70_000_002,
                    },
                },
                ["skyhooks"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["50001"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["skyhookID"] = 50_001,
                        ["typeID"] = 81_080,
                        ["solarSystemID"] = systemId,
                        ["planetID"] = 40_000_001,
                        ["corporationID"] = 90_000_002,
                        ["allianceID"] = 70_000_002,
                    },
                    ["50002"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["skyhookID"] = 50_002,
                        ["typeID"] = 81_080,
                        ["solarSystemID"] = systemId,
                        ["planetID"] = 40_000_002,
                        ["corporationID"] = 90_000_002,
                        ["allianceID"] = 70_000_002,
                        ["acquisitionPriceISK"] = 1_250_000m,
                        ["acquisitionWalletRefTypeID"] = 17,
                    },
                    ["50003"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["skyhookID"] = 50_003,
                        ["typeID"] = 81_080,
                        ["solarSystemID"] = systemId,
                        ["planetID"] = 40_000_003,
                        ["corporationID"] = 90_000_002,
                        ["allianceID"] = 70_000_002,
                    },
                },
            },
            ["accessGroups"] = new()
            {
                [accessListId.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                    new System.Text.Json.Nodes.JsonObject { ["creatorID"] = corporationId },
            },
        };
        var durable = tables.ToDictionary(
            pair => pair.Key,
            pair => (System.Text.Json.Nodes.JsonObject)pair.Value.DeepClone());
        var flushes = 0;
        var walletOperationKeys = new HashSet<string>(StringComparer.Ordinal);
        var chargeCommitted = false;
        var injectedPostChargeWriteFailure = false;

        System.Text.Json.Nodes.JsonObject ReadCache(string table) =>
            (System.Text.Json.Nodes.JsonObject)tables[table].DeepClone();

        void SetPath(System.Text.Json.Nodes.JsonObject root, string path, System.Text.Json.Nodes.JsonNode? value)
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                if (current[segments[index]] is not System.Text.Json.Nodes.JsonObject child)
                {
                    child = new System.Text.Json.Nodes.JsonObject();
                    current[segments[index]] = child;
                }
                current = child;
            }
            current[segments[^1]] = value?.DeepClone();
        }

        Eve.GameStore.WriteResult Write(string table, string? path, System.Text.Json.Nodes.JsonNode? value)
        {
            if (!tables.ContainsKey(table) || path is null || value is not System.Text.Json.Nodes.JsonObject)
                return new Eve.GameStore.WriteResult(false, "unexpected write");
            if (chargeCommitted && path == "/skyhooks/50002" && !injectedPostChargeWriteFailure)
            {
                injectedPostChargeWriteFailure = true;
                return new Eve.GameStore.WriteResult(false, "injected post-charge write failure");
            }
            SetPath(tables[table], path, value);
            return new Eve.GameStore.WriteResult(true, null);
        }

        Task<int> Flush(string table, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            durable[table] = (System.Text.Json.Nodes.JsonObject)tables[table].DeepClone();
            flushes++;
            return Task.FromResult(1);
        }

        Task<System.Text.Json.Nodes.JsonObject> ReadDurable(string table, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult((System.Text.Json.Nodes.JsonObject)durable[table].DeepClone());
        }

        ValueTask<bool> Authorize(
            Eve.Services.ServiceCall call,
            long actualSystemId,
            Eve.Services.Core.SovHubRecord hub) =>
            ValueTask.FromResult(actualSystemId == systemId &&
                call.Session.CharacterId == 90_000_010 &&
                hub.HubId == hubId &&
                hub.CorporationId == corporationId &&
                hub.AllianceId == allianceId);

        var charges = new List<global::Eve.Host.SovSkyhookAcquisitionCharge>();
        ValueTask<bool> Charge(
            IReadOnlyList<global::Eve.Host.SovSkyhookAcquisitionCharge> requested,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var charge in requested)
                if (walletOperationKeys.Add(charge.OperationKey))
                    charges.Add(charge);
            chargeCommitted = true;
            return ValueTask.FromResult(true);
        }

        ValueTask<bool> HasWalletOperation(
            global::Eve.Host.SovSkyhookAcquisitionCharge charge,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(walletOperationKeys.Contains(charge.OperationKey));
        }

        var writer = new global::Eve.Host.PersistenceSovFwWriteOperations(
            ReadCache,
            Write,
            Flush,
            ReadDurable,
            Authorize,
            new SemaphoreSlim(1, 1),
            Charge,
            HasWalletOperation);
        var call = new Eve.Services.ServiceCall
        {
            Method = "sovMgr",
            Args = [],
            Session = new Eve.Services.ClientSession { CharacterId = 90_000_010 },
        };
        var hub = new Eve.Services.Core.SovHubRecord(hubId, corporationId, allianceId, claimTimeFileTime);

        Assert.True(await writer.CanManageHubAsync(call, systemId, hub));
        Assert.Same(Eve.Marshal.PyNone.Instance,
            await writer.SetFuelAccessGroupAsync(call, systemId, accessListId));
        Assert.Same(Eve.Marshal.PyBool.True,
            await writer.SaveUpgradeConfigurationAsync(
                call, hubId, [60_001, 60_002],
                [new(81_081, true), new(81_082, false)]));
        Assert.Same(Eve.Marshal.PyBool.True,
            await writer.SelfDestructUpgradeAsync(call, hubId, 81_081));

        var workforce = new Eve.Marshal.PyDict(
        [
            new KeyValuePair<Eve.Marshal.PyValue, Eve.Marshal.PyValue>(
                new Eve.Marshal.PyStr("destinationSolarSystemID"),
                new Eve.Marshal.PyLong(otherSystemId)),
            new KeyValuePair<Eve.Marshal.PyValue, Eve.Marshal.PyValue>(
                new Eve.Marshal.PyStr("amount"),
                new Eve.Marshal.PyLong(100)),
        ]);
        Assert.Same(Eve.Marshal.PyBool.True,
            await writer.SaveWorkforceConfigurationAsync(call, hubId, workforce));

        var crossAllianceWorkforce = new Eve.Marshal.PyDict(
        [
            new KeyValuePair<Eve.Marshal.PyValue, Eve.Marshal.PyValue>(
                new Eve.Marshal.PyStr("destinationSolarSystemID"),
                new Eve.Marshal.PyLong(30_000_144)),
        ]);
        var flushesBeforeRejectedTransfer = flushes;
        Assert.Same(Eve.Marshal.PyBool.False,
            await writer.SaveWorkforceConfigurationAsync(call, hubId, crossAllianceWorkforce));
        Assert.Equal(flushesBeforeRejectedTransfer, flushes);
        Assert.Equal([50_001L],
            await writer.DestroySkyhooksAsync(call, systemId, hub, [50_001]));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await writer.AcquireSkyhooksAsync(call, systemId, hub, [50_002], accessListId);
        });
        Assert.True(injectedPostChargeWriteFailure);
        Assert.Single(charges);
        var pendingReceipt = durable["sovereignty"]["skyhookAcquisitionSettlements"]!
            .AsObject().Select(entry => entry.Value).OfType<System.Text.Json.Nodes.JsonObject>().Single();
        Assert.Equal("pending", pendingReceipt["state"]!.GetValue<string>());
        await writer.ReplayPendingSkyhookAcquisitionsAsync(CancellationToken.None);
        Assert.Single(charges);
        Assert.Equal("completed",
            durable["sovereignty"]["skyhookAcquisitionSettlements"]!.AsObject().Select(entry => entry.Value)
                .OfType<System.Text.Json.Nodes.JsonObject>().Single(receipt =>
                    receipt["requestFingerprint"]?.ToString() is not null &&
                    receipt["state"]?.ToString() == "completed")["state"]!.GetValue<string>());
        Assert.Equal([50_002L],
            await writer.AcquireSkyhooksAsync(call, systemId, hub, [50_002], accessListId));
        Assert.Single(charges);
        Assert.Equal([50_003L],
            await writer.AcquireSkyhooksAsync(call, systemId, hub, [50_003], accessListId));
        Assert.Collection(charges, _ => { }, _ => { });
        Assert.Equal(corporationId, charges[0].CorporationId);
        Assert.Equal(1_250_000m, charges[0].AmountIsk);
        Assert.Equal(17, charges[0].ReferenceTypeId);
        Assert.Equal(corporationId, charges[1].CorporationId);
        Assert.Equal(5_500_000_000m, charges[1].AmountIsk);
        Assert.Equal(184, charges[1].ReferenceTypeId);
        Assert.Equal([50_002L],
            await writer.AcquireSkyhooksAsync(call, systemId, hub, [50_002], accessListId));
        Assert.Collection(charges, _ => { }, _ => { });
        Assert.Equal([50_003L],
            await writer.AcquireSkyhooksAsync(call, systemId, hub, [50_003], accessListId));
        Assert.Collection(charges, _ => { }, _ => { });

        var savedSystem = durable["sovereignty"]["systems"]![systemId.ToString(
            System.Globalization.CultureInfo.InvariantCulture)]!;
        Assert.Equal(accessListId, savedSystem["fuelAccessGroupID"]!.GetValue<long>());
        Assert.Single(savedSystem["hubUpgradeConfiguration"]!.AsArray());
        Assert.Equal(otherSystemId, savedSystem["workforceConfiguration"]!["destinationSolarSystemID"]!.GetValue<long>());
        Assert.True(durable["sovereignty"]["skyhooks"]!["50001"]!["destroyed"]!.GetValue<bool>());
        Assert.False(durable["sovereignty"]["skyhooks"]!["50001"]!["productionActive"]!.GetValue<bool>());
        Assert.Equal(corporationId, durable["sovereignty"]["skyhooks"]!["50002"]!["corporationID"]!.GetValue<long>());
        Assert.Equal(allianceId, durable["sovereignty"]["skyhooks"]!["50002"]!["allianceID"]!.GetValue<long>());
        Assert.Equal(accessListId, durable["sovereignty"]["skyhooks"]!["50002"]!["accessListID"]!.GetValue<long>());
        Assert.Equal(corporationId, durable["sovereignty"]["skyhooks"]!["50003"]!["corporationID"]!.GetValue<long>());
        Assert.Equal(allianceId, durable["sovereignty"]["skyhooks"]!["50003"]!["allianceID"]!.GetValue<long>());
        Assert.True(flushes >= 6);

        var denied = new Eve.Services.ServiceCall
        {
            Method = "sovMgr",
            Args = [],
            Session = new Eve.Services.ClientSession { CharacterId = 90_000_011 },
        };
        Assert.False(await writer.CanManageHubAsync(denied, systemId, hub));
        Assert.Empty(await writer.DestroySkyhooksAsync(denied, systemId, hub, [50_002]));
    }

    [Fact]
    public async Task Skyhook_activation_writer_persists_versioned_state_and_reads_it_back()
    {
        var snapshot = new Eve.Services.Core.SkyhookResourceSnapshot(
            81_001, 81_080, 30_000_142, 40_000_001, 90_000_001, 70_000_001,
            false, 42, "resource-v1", Array.Empty<Eve.Services.Core.SkyhookReagentResource>(), null);
        var root = new System.Text.Json.Nodes.JsonObject
        {
            ["skyhooks"] = new System.Text.Json.Nodes.JsonObject
            {
                ["81001"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["skyhookID"] = snapshot.SkyhookId,
                    ["typeID"] = snapshot.TypeId,
                    ["solarSystemID"] = snapshot.SolarSystemId,
                    ["planetID"] = snapshot.PlanetId,
                    ["corporationID"] = snapshot.CorporationId,
                    ["allianceID"] = snapshot.AllianceId,
                    ["productionActive"] = false,
                    ["workforce"] = snapshot.Workforce,
                    ["resourceVersion"] = snapshot.ResourceVersion,
                    ["reagents"] = new System.Text.Json.Nodes.JsonArray(),
                },
            },
        };
        var flushCount = 0;
        var writeCount = 0;
        var permissions = new List<Eve.Services.Core.SkyhookResourcePermission>();

        System.Text.Json.Nodes.JsonObject ReadCacheTable(string table) =>
            table == "sovereignty"
                ? (System.Text.Json.Nodes.JsonObject)root.DeepClone()
                : new System.Text.Json.Nodes.JsonObject();

        Eve.GameStore.WriteResult Write(string table, string? path, System.Text.Json.Nodes.JsonNode? data)
        {
            if (table != "sovereignty" || path != "/skyhooks/81001" || data is not System.Text.Json.Nodes.JsonObject row)
                return new Eve.GameStore.WriteResult(false, "unexpected write");
            ((System.Text.Json.Nodes.JsonObject)root["skyhooks"]!)["81001"] = row.DeepClone();
            writeCount++;
            return new Eve.GameStore.WriteResult(true, null);
        }

        Task<int> Flush(string table, CancellationToken cancellationToken)
        {
            if (table != "sovereignty")
                throw new InvalidOperationException("unexpected table flush");
            flushCount++;
            return Task.FromResult(1);
        }

        ValueTask<Eve.Services.Core.SkyhookResourceSnapshot?> ReadSkyhook(
            long skyhookId,
            CancellationToken cancellationToken)
        {
            if (skyhookId != snapshot.SkyhookId ||
                ((System.Text.Json.Nodes.JsonObject)root["skyhooks"]!)["81001"] is not System.Text.Json.Nodes.JsonObject row)
                return ValueTask.FromResult<Eve.Services.Core.SkyhookResourceSnapshot?>(null);
            var active = bool.TryParse(row["productionActive"]?.ToString(), out var isActive) && isActive;
            var version = row["resourceVersion"]?.ToString() ?? string.Empty;
            return ValueTask.FromResult<Eve.Services.Core.SkyhookResourceSnapshot?>(
                snapshot with { ProductionActive = active, ResourceVersion = version });
        }

        ValueTask<bool> Authorize(
            Eve.Services.ServiceCall call,
            Eve.Services.Core.SkyhookResourceSnapshot site,
            Eve.Services.Core.SkyhookResourcePermission permission,
            CancellationToken cancellationToken)
        {
            permissions.Add(permission);
            return ValueTask.FromResult(true);
        }

        var writer = new global::Eve.Host.PersistenceSkyhookActivationWriter(
            ReadCacheTable,
            Write,
            Flush,
            ReadSkyhook,
            Authorize);
        var call = new Eve.Services.ServiceCall
        {
            Method = "ActivateSkyhook",
            Args = [],
            Session = new Eve.Services.ClientSession { CharacterId = 900_000_001 },
        };

        var activated = await writer.PersistAsync(call, snapshot, true, CancellationToken.None);
        Assert.NotNull(activated);
        Assert.True(activated.ProductionActive);
        Assert.NotEqual(snapshot.ResourceVersion, activated.ResourceVersion);
        Assert.Equal(1, writeCount);
        Assert.Equal(1, flushCount);
        Assert.Equal(Eve.Services.Core.SkyhookResourcePermission.Activate, Assert.Single(permissions));

        Assert.Null(await writer.PersistAsync(call, snapshot, false, CancellationToken.None));
        Assert.Equal(1, writeCount);
        Assert.Equal(1, flushCount);

        var deactivated = await writer.PersistAsync(call, activated, false, CancellationToken.None);
        Assert.NotNull(deactivated);
        Assert.False(deactivated.ProductionActive);
        Assert.Equal(2, writeCount);
        Assert.Equal(2, flushCount);
        Assert.Equal(
            [Eve.Services.Core.SkyhookResourcePermission.Activate, Eve.Services.Core.SkyhookResourcePermission.Deactivate, Eve.Services.Core.SkyhookResourcePermission.Deactivate],
            permissions);
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
        var calendarStore = new TestCalendarStore();
        var calendarAudience = new TestCalendarAudienceReader();
        var calendar = new Eve.Services.Core.CalendarMgrService(calendarStore, calendarAudience);
        var calendarProxy = new Eve.Services.Core.CalendarProxyService(calendarStore);
        var wars = new Eve.Services.Corporations.WarsInfoMgrService(null!, () => FixedNowMs);
        var voteManager = new Eve.Services.Corporations.VoteManagerService(null!);
          var fleetStore = new Eve.Services.Space.FleetStore(null!);
          var fleetObject = new Eve.Services.Space.FleetObjectService(fleetStore, () => FixedNowMs);
          var fleetManager = new Eve.Services.Space.FleetManagerService(fleetStore);
          var fleetProxy = new Eve.Services.Space.FleetProxyService(fleetStore);
        var online = new Eve.Services.CharacterInfo.OnlineStatusService(new Eve.Services.CharacterSessions(),
            (_, _) => ValueTask.FromResult<IReadOnlyCollection<long>>([]));
        var multiLoginBlocker = new Eve.Services.CharacterSelect.MultiLoginBlockerService();
        var populationCap = new Eve.Services.CharacterSelect.PopulationCapService(null!);
        var config = new Eve.Services.CharacterInfo.ConfigService(null!, null!, null!, null!);
        var securityMgr = new Eve.Services.Core.SecurityMgrService();
        var alert = new Eve.Services.Core.AlertService();
        var fighterMgr = new Eve.Services.Core.FighterMgrService();
        var shipKillCounter = new Eve.Services.Core.ShipKillCounterService();
        var officeManager = new Eve.Services.Corporations.OfficeManagerService(
            null!, null!, null!, null!, null!, static _ => null, () => FixedNowMs);
        var mailingLists = new Eve.Services.Core.MailingListService(
            _ => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(new System.Text.Json.Nodes.JsonObject()),
            (_, _) => ValueTask.FromResult<Eve.Marshal.PyValue>(Eve.Marshal.PyNone.Instance));
        var rewards = new Eve.Services.Core.RewardsService();
        var storeOffers = new Eve.Services.Core.StoreOffersService(
            _ => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(null));
        var industryJobs = new Eve.Services.Core.IndustryJobsService(
            _ => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(null),
            new EmptyIndustryJobOperations());
        // the ledger queries are only registered here (the registry test does not run them): the source is never opened
        var ledgerReads = Eve.Services.Core.MiningLedgerQueries.Register(new Eve.ReadSide.ReadSide(
            Npgsql.NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused"),
            new Dictionary<string, string> { [Eve.Services.Core.MiningLedgerQueries.SpaceSchema] = "unused" }));
        var characterMining = new Eve.Services.Core.CharacterMiningLedgerService(ledgerReads);
        var corporationMining = new Eve.Services.Core.CorpMiningLedgerService(ledgerReads);
        var agents = new Eve.Services.Core.AgentMissionService(
            (_, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(null),
            (_, _, _) => ValueTask.FromResult<IReadOnlyDictionary<long, System.Text.Json.Nodes.JsonObject>>(new Dictionary<long, System.Text.Json.Nodes.JsonObject>()),
            (_, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonArray?>(null));
        var emptyLoginRewards = new Eve.Services.Core.EmptyLoginRewardCallbacks();
        var loginCampaigns = new Eve.Services.Core.LoginCampaignMgrService(emptyLoginRewards);
        var seasonalCampaigns = new Eve.Services.Core.SeasonalLoginCampaignMgrService(emptyLoginRewards);
        var insurance = new Eve.Services.Core.InsuranceService(new EmptyInsuranceReadModel(), () => FixedNowMs);
        var structureReadCallbacks = new EmptyStructureDirectoryReadCallbacks();
        var structureDirectory = new Eve.Services.Core.StructureDirectoryService(structureReadCallbacks);
        var structureSettings = new Eve.Services.Core.StructureSettingsService(structureReadCallbacks);
        var skyhookResources = new Eve.Services.Core.SkyhookResourceService(
            (_, _) => ValueTask.FromResult<Eve.Services.Core.SkyhookResourceSnapshot?>(null),
            (_, _) => ValueTask.FromResult<IReadOnlyList<Eve.Services.Core.SkyhookResourceSnapshot>>([]),
            (_, _, _, _) => ValueTask.FromResult(false),
            (_, _, _, _) => ValueTask.FromResult<Eve.Services.Core.SkyhookResourceSnapshot?>(null));
        var skyhookMgr = new Eve.Services.Core.SkyhookResourceMgrService(skyhookResources);
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
            mail, notifications, calendar, calendarProxy, wars, voteManager, online, mailingLists, rewards,
            storeOffers, industryJobs, characterMining, corporationMining, agents, insurance, multiLoginBlocker, populationCap, config,
            loginCampaigns, seasonalCampaigns,
            securityMgr, alert, fighterMgr, shipKillCounter, officeManager,
            structureDirectory, structureSettings, skyhookMgr, map, tales, scanning, fleetObject, fleetManager, fleetProxy,
        }.Concat(sovereignty))
            registry.Register(service);

        Assert.Same(mail, registry.Lookup("mailMgr"));
        Assert.Same(notifications, registry.Lookup("notificationMgr"));
        Assert.Same(calendar, registry.Lookup("calendarMgr"));
        Assert.Same(calendarProxy, registry.Lookup("calendarProxy"));
        Assert.Same(wars, registry.Lookup("warsInfoMgr"));
        Assert.Same(voteManager, registry.Lookup("voteManager"));
          Assert.Same(fleetObject, registry.Lookup("fleetObjectHandler"));
          Assert.Same(fleetManager, registry.Lookup("fleetMgr"));
          Assert.Same(fleetProxy, registry.Lookup("fleetProxy"));
        Assert.Same(online, registry.Lookup("onlineStatus"));
        Assert.Same(multiLoginBlocker, registry.Lookup("multiLoginBlocker"));
        Assert.Same(PyBool.True, multiLoginBlocker.Login(null!));
        Assert.Same(populationCap, registry.Lookup("populationCap"));
        Assert.Same(config, registry.Lookup("config"));
        Assert.Same(loginCampaigns, registry.Lookup("loginCampaignManager"));
        Assert.Same(seasonalCampaigns, registry.Lookup("seasonalLoginCampaignManager"));
        Assert.Same(securityMgr, registry.Lookup("securityMgr"));
        Assert.Same(alert, registry.Lookup("alert"));
        Assert.Same(fighterMgr, registry.Lookup("fighterMgr"));
        Assert.Same(shipKillCounter, registry.Lookup("shipKillCounter"));
        Assert.Same(officeManager, registry.Lookup("officeManager"));
        Assert.Equal(Eve.Macho.MachoMessage.ProxyNodeId, ((PyInt)populationCap.MachoResolveObject(null!)).Value);
        Assert.Same(mailingLists, registry.Lookup("mailingListsMgr"));
        Assert.Same(rewards, registry.Lookup("rewardMgr"));
        Assert.Same(storeOffers, registry.Lookup("storeManager"));
        Assert.Same(industryJobs, registry.Lookup("industryManager"));
        Assert.Same(characterMining, registry.Lookup("characterMiningLedger"));
        Assert.Same(corporationMining, registry.Lookup("corpMiningLedger"));
        Assert.Same(agents, registry.Lookup("agentMgr"));
        Assert.Same(insurance, registry.Lookup("insuranceSvc"));
        Assert.Same(structureDirectory, registry.Lookup("structureDirectory"));
        Assert.Same(structureSettings, registry.Lookup("structureSettings"));
        Assert.Same(skyhookMgr, registry.Lookup("colonyResourcesMgr"));
        Assert.Same(map, registry.Lookup("map"));
        Assert.Same(tales, registry.Lookup("taleMgr"));
        Assert.Same(scanning, registry.Lookup("scanMgr"));
        Assert.Same(sovereignty[0], registry.Lookup("sovMgr"));
        Assert.Same(sovereignty[1], registry.Lookup("fwWarzoneSolarsystem"));
        Assert.Equal(36, registry.Count);
    }


    private sealed class TestCalendarAudienceReader : Eve.Calendar.ICalendarAudienceReader
    {
        public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<long>> GetMemberCharacterIdsAsync(
            short eventTag, long ownerId, System.Threading.CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IReadOnlyList<long>>([]);
    }

    private sealed class TestCalendarStore : Eve.Calendar.ICalendarStore
    {
        public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Eve.Calendar.CalendarEvent>> ListMonthAsync(
            long characterId, long corporationId, long allianceId, int month, int year,
            System.Threading.CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IReadOnlyList<Eve.Calendar.CalendarEvent>>([]);

        public System.Threading.Tasks.Task<Eve.Calendar.CalendarEvent?> GetEventAsync(
            long eventId, long ownerId, System.Threading.CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult<Eve.Calendar.CalendarEvent?>(null);

        public System.Threading.Tasks.Task<Eve.Calendar.CalendarEvent?> GetEventByIdAsync(
            long eventId, System.Threading.CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult<Eve.Calendar.CalendarEvent?>(null);

        public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Eve.Calendar.CalendarResponse>> GetResponsesForCharacterAsync(
            long characterId, System.Threading.CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IReadOnlyList<Eve.Calendar.CalendarResponse>>([]);

        public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Eve.Calendar.CalendarResponse>> GetResponsesToEventAsync(
            long eventId, long ownerId, System.Threading.CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IReadOnlyList<Eve.Calendar.CalendarResponse>>([]);

        public System.Threading.Tasks.Task<long> CreateEventAsync(
            Eve.Calendar.CalendarEventInput input, System.Collections.Generic.IReadOnlyCollection<long> invitees,
            System.Threading.CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromException<long>(new System.NotSupportedException());

        public System.Threading.Tasks.Task<bool> EditEventAsync(
            long eventId, long ownerId, long oldEventDateTime, Eve.Calendar.CalendarEventInput input,
            System.Threading.CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult(false);

        public System.Threading.Tasks.Task<bool> DeleteEventAsync(
            long eventId, long ownerId, long dateModified, System.Threading.CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult(false);

        public System.Threading.Tasks.Task<bool> UpdateParticipantsAsync(
            long eventId, long ownerId, System.Collections.Generic.IReadOnlyCollection<long> add,
            System.Collections.Generic.IReadOnlyCollection<long> remove,
            System.Threading.CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult(false);

        public System.Threading.Tasks.Task<bool> SendResponseAsync(
            long eventId, long ownerId, long characterId, short response,
            System.Threading.CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult(false);
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
    public async Task With_strict_rpc_a_throwing_handler_surfaces_as_an_error_packet()
    {
        Start(strictRpc: true, configure: registry => registry.Register(new BoomService()));
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync(); // the initial state

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.Call(open, 1, "boomSvc", "Boom"))));
        var reply = Assert.IsType<Send>(await front.ReceiveAsync());
        var state = ((PyTuple)((PyObject)MarshalCodec.Decode(reply.Payload)).Args).Items;
        Assert.Equal(15, ((PyInt)state[0]).Value); // ERRORRESPONSE, not a CALL_RSP carrying None
        var text = Encoding.ASCII.GetString(reply.Payload.Span);
        Assert.Contains("eveexceptions.UserError", text);
        Assert.Contains("InvalidOperationException: boom went the handler", text);
        Assert.Contains("Server error ", text);
    }

    /// <summary>A service whose only method throws.</summary>
    private sealed class BoomService : IService
    {
        public string Name => "boomSvc";

        public bool HasMethod(string method) => method is "Boom";

        public ValueTask<PyValue> CallAsync(ServiceCall call) => throw new InvalidOperationException("boom went the handler");
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
            droneBays,
            new Eve.Corporations.CorporationStore(
                source,
                new Eve.Characters.PgCharacterStore(source),
                new Eve.Wallet.PgWalletStore(source),
                new Eve.Wallet.WalletTransactions(source),
                new Eve.Skills.PgSkillStore(source),
                () => FixedNowMs));

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
    public async Task Asset_safety_manual_personal_delivery_enforces_five_days_half_percent_and_call_replay()
    {
        await using var rig = await AssetSafetyPgRig.CreateAsync();
        const long characterId = 900001;
        await rig.OpenWalletAsync(characterId, 1_000_000_000m);
        var structureId = rig.AddStructure(rig.SameSystemStation.SolarSystemId, "Safety source");
        var itemId = await rig.AddAssetAsync(characterId, structureId, 1_000);

        var trigger = rig.Call("MovePersonalAssetsToSafety", characterId, 0, 1);
        var triggerState = await rig.Store.MovePersonalAssetsToSafetyAsync(trigger, rig.SameSystemStation.SolarSystemId, structureId, CancellationToken.None);
        Assert.True(
            triggerState == Eve.Services.Core.AssetSafetyActionState.PersonalWrapCreated,
            $"state={triggerState}; {string.Join(System.Environment.NewLine, rig.LogMessages)}");

        var wrapId = await rig.WrapIdAsync(characterId, 1, structureId);
        var createdAt = await rig.WrapCreatedAtAsync(wrapId);
        var wraps = await rig.Store.GetItemsInSafetyForCharacterAsync(characterId, CancellationToken.None);
        Assert.Single(wraps.Items);
        var names = await rig.Store.GetWrapNamesAsync(characterId, 0, [wrapId], CancellationToken.None);
        Assert.Single(names.Entries);
        Assert.Contains(rig.SameSystemStation.SolarSystemName,
            Assert.IsType<Eve.Marshal.PyStr>(Assert.Single(names.Entries).Value).Value);

        var targets = await rig.Store.GetStructuresICanDeliverToAsync(
            characterId, 0, rig.SameSystemStation.SolarSystemId, CancellationToken.None);
        Assert.Equal(2, targets.Items.Count);
        Assert.NotEmpty(Assert.IsType<Eve.Marshal.PyList>(targets.Items[0]).Items);

        rig.Now = createdAt + 5 * AssetSafetyPgRig.DayMs - 1;
        Assert.Equal(Eve.Services.Core.AssetSafetyActionState.DeliveryNotReady,
            await rig.Store.MoveSafetyWrapToStructureAsync(
                rig.Call("MoveSafetyWrapToStructure", characterId, 0, 2), wrapId,
                rig.SameSystemStation.SolarSystemId, rig.SameSystemStation.Id, CancellationToken.None));

        var before = (await rig.WalletStore.GetAsync(characterId))!.Isk;
        rig.Now = createdAt + 5 * AssetSafetyPgRig.DayMs;
        var release = rig.Call("MoveSafetyWrapToStructure", characterId, 0, 3);
        Assert.Equal(Eve.Services.Core.AssetSafetyActionState.WrapDelivered,
            await rig.Store.MoveSafetyWrapToStructureAsync(
                release, wrapId, rig.SameSystemStation.SolarSystemId, rig.SameSystemStation.Id, CancellationToken.None));

        var fee = await rig.ExpectedFeeAsync(rig.AssetTypeId, 1_000, sameSystem: true);
        Assert.Equal(before - fee, (await rig.WalletStore.GetAsync(characterId))!.Isk);
        Assert.Equal(-fee, await rig.JournalAmountAsync(characterId, $"asset-safety-wrap:{wrapId}"));
        Assert.Equal(Eve.Services.Core.AssetSafetyActionState.WrapDelivered,
            await rig.Store.MoveSafetyWrapToStructureAsync(
                release, wrapId, rig.SameSystemStation.SolarSystemId, rig.SameSystemStation.Id, CancellationToken.None));
        Assert.Equal(-fee, await rig.JournalAmountAsync(characterId, $"asset-safety-wrap:{wrapId}"));
        Assert.Equal(1, await rig.ItemLocationCountAsync(itemId, rig.SameSystemStation.Id, characterId));
    }

    [Fact]
    public async Task Asset_safety_auto_delivery_waits_twenty_days_and_charges_fifteen_percent_cross_system()
    {
        await using var rig = await AssetSafetyPgRig.CreateAsync();
        const long characterId = 900002;
        await rig.OpenWalletAsync(characterId, 1_000_000_000m);
        var sourceSystem = await rig.HighSecuritySystemWithoutNpcStationAsync();
        var structureId = rig.AddStructure(sourceSystem, "Remote safety source");
        var itemId = await rig.AddAssetAsync(characterId, structureId, 1_000);

        Assert.Equal(Eve.Services.Core.AssetSafetyActionState.PersonalWrapCreated,
            await rig.Store.MovePersonalAssetsToSafetyAsync(
                rig.Call("MovePersonalAssetsToSafety", characterId, 0, 10),
                sourceSystem, structureId, CancellationToken.None));
        var wrapId = await rig.WrapIdAsync(characterId, 1, structureId);
        var createdAt = await rig.WrapCreatedAtAsync(wrapId);
        var before = (await rig.WalletStore.GetAsync(characterId))!.Isk;

        rig.Now = createdAt + 20 * AssetSafetyPgRig.DayMs - 1;
        await rig.Store.ProcessDueAutomaticDeliveriesAsync(CancellationToken.None);
        Assert.Equal(0, await rig.WrapStateAsync(wrapId));

        rig.Now++;
        await rig.Store.ProcessDueAutomaticDeliveriesAsync(CancellationToken.None);
        Assert.Equal(1, await rig.WrapStateAsync(wrapId));
        var destinationId = await rig.WrapDestinationAsync(wrapId);
        var destination = rig.Stations.Get(destinationId);
        Assert.NotNull(destination);
        Assert.NotEqual(sourceSystem, destination!.SolarSystemId);

        var fee = await rig.ExpectedFeeAsync(rig.AssetTypeId, 1_000, sameSystem: false);
        Assert.Equal(before - fee, (await rig.WalletStore.GetAsync(characterId))!.Isk);
        Assert.Equal(-fee, await rig.JournalAmountAsync(characterId, $"asset-safety-wrap:{wrapId}"));
        await rig.Store.ProcessDueAutomaticDeliveriesAsync(CancellationToken.None);
        Assert.Equal(-fee, await rig.JournalAmountAsync(characterId, $"asset-safety-wrap:{wrapId}"));
        Assert.Equal(1, await rig.ItemLocationCountAsync(itemId, destinationId, characterId));
    }

    [Fact]
    public async Task Asset_safety_corporation_access_is_limited_to_ceo_and_directors()
    {
        await using var rig = await AssetSafetyPgRig.CreateAsync();
        const long corporationId = 980001;
        const long ceoId = 910001;
        const long directorId = 910002;
        const long memberId = 910003;
        await rig.CreateCorporationAsync(corporationId, ceoId, directorId, memberId);
        var structureId = rig.AddStructure(rig.SameSystemStation.SolarSystemId, "Corporation safety source");
        var itemId = await rig.AddAssetAsync(corporationId, structureId, 5);

        Assert.Equal(Eve.Services.Core.AssetSafetyActionState.NotAuthorized,
            await rig.Store.MoveCorpAssetsToSafetyAsync(
                rig.Call("MoveCorpAssetsToSafety", memberId, corporationId, 1),
                rig.SameSystemStation.SolarSystemId, structureId, CancellationToken.None));
        Assert.Equal(1, await rig.ItemLocationCountAsync(itemId, structureId, corporationId));

        Assert.Equal(Eve.Services.Core.AssetSafetyActionState.CorporationWrapCreated,
            await rig.Store.MoveCorpAssetsToSafetyAsync(
                rig.Call("MoveCorpAssetsToSafety", directorId, corporationId, 2),
                rig.SameSystemStation.SolarSystemId, structureId, CancellationToken.None));
        var wrapId = await rig.WrapIdAsync(corporationId, 2, structureId);

        Assert.Empty((await rig.Store.GetItemsInSafetyForCorpAsync(memberId, corporationId, CancellationToken.None)).Items);
        Assert.Single((await rig.Store.GetItemsInSafetyForCorpAsync(directorId, corporationId, CancellationToken.None)).Items);
        Assert.Single((await rig.Store.GetItemsInSafetyForCorpAsync(ceoId, corporationId, CancellationToken.None)).Items);
        Assert.Empty((await rig.Store.GetWrapNamesAsync(memberId, corporationId, [wrapId], CancellationToken.None)).Entries);
        Assert.Single((await rig.Store.GetWrapNamesAsync(ceoId, corporationId, [wrapId], CancellationToken.None)).Entries);
    }

    [Fact]
    public async Task Asset_safety_release_failure_rolls_back_wallet_items_wrap_and_receipt()
    {
        await using var rig = await AssetSafetyPgRig.CreateAsync();
        const long characterId = 900004;
        await rig.OpenWalletAsync(characterId, 1_000_000_000m);
        var structureId = rig.AddStructure(rig.SameSystemStation.SolarSystemId, "Rollback safety source");
        var itemId = await rig.AddAssetAsync(characterId, structureId, 1_000);

        Assert.Equal(Eve.Services.Core.AssetSafetyActionState.PersonalWrapCreated,
            await rig.Store.MovePersonalAssetsToSafetyAsync(
                rig.Call("MovePersonalAssetsToSafety", characterId, 0, 20),
                rig.SameSystemStation.SolarSystemId, structureId, CancellationToken.None));
        var wrapId = await rig.WrapIdAsync(characterId, 1, structureId);
        var createdAt = await rig.WrapCreatedAtAsync(wrapId);
        var before = (await rig.WalletStore.GetAsync(characterId))!.Isk;
        rig.Now = createdAt + 5 * AssetSafetyPgRig.DayMs;
        var release = rig.Call("MoveSafetyWrapToStructure", characterId, 0, 21);
        var (triggerName, functionName) = await rig.AddMoveFailureTriggerAsync(wrapId, rig.SameSystemStation.Id);

        Assert.Equal(Eve.Services.Core.AssetSafetyActionState.PersistenceFailure,
            await rig.Store.MoveSafetyWrapToStructureAsync(
                release, wrapId, rig.SameSystemStation.SolarSystemId, rig.SameSystemStation.Id, CancellationToken.None));
        Assert.Equal(before, (await rig.WalletStore.GetAsync(characterId))!.Isk);
        Assert.Equal(wrapId, await rig.ItemLocationAsync(itemId));
        Assert.Equal(0, await rig.WrapStateAsync(wrapId));
        Assert.Equal(0, await rig.ReceiptCountAsync("release"));

        await rig.RemoveMoveFailureTriggerAsync(triggerName, functionName);
        Assert.Equal(Eve.Services.Core.AssetSafetyActionState.WrapDelivered,
            await rig.Store.MoveSafetyWrapToStructureAsync(
                release, wrapId, rig.SameSystemStation.SolarSystemId, rig.SameSystemStation.Id, CancellationToken.None));
        Assert.Equal(1, await rig.ReceiptCountAsync("release"));
    }

    [Fact]
    public async Task Asset_safety_in_wormhole_and_pochven_ejects_items_into_space()
    {
        await using var rig = await AssetSafetyPgRig.CreateAsync();
        const long characterId = 900005;
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.WalletStore.OpenAsync(connection, transaction, characterId, 100_000m, 0);
        await transaction.CommitAsync();

        foreach (var systemId in new[] { rig.WormholeSystemId, rig.PochvenSystemId })
        {
            var structureId = rig.AddStructure(systemId, $"Ejection source {systemId}");
            var itemId = await rig.AddAssetAsync(characterId, structureId, 3);
            Assert.Equal(Eve.Services.Core.AssetSafetyActionState.ItemsEjected,
                await rig.Store.MovePersonalAssetsToSafetyAsync(
                    rig.Call("MovePersonalAssetsToSafety", characterId, 0, checked((int)(30 + systemId % 1000))),
                    systemId, structureId, CancellationToken.None));
            var wrapId = await rig.WrapIdForStateAsync(characterId, 1, structureId, 2);
            Assert.True(await rig.SpaceItemExistsAsync(wrapId));
            Assert.Equal(0, await rig.ItemOwnerAsync(itemId));
            Assert.Equal(wrapId, await rig.ItemLocationAsync(itemId));
        }

        Assert.Empty((await rig.Store.GetItemsInSafetyForCharacterAsync(characterId, CancellationToken.None)).Items);
    }

    private sealed class AssetSafetyPgRig : IAsyncDisposable
    {
        public const long DayMs = 86_400_000L;
        private readonly System.Collections.Generic.List<string> _schemas;
        private readonly System.Text.Json.Nodes.JsonObject _structures = new();
        private long _nextStructureId = 7_000_000_000_000;

        private AssetSafetyPgRig(
            Npgsql.NpgsqlDataSource source,
            string inventorySchema,
            string walletSchema,
            string spaceSchema,
            string corporationSchema,
            string assetSafetySchema,
            Eve.Sde.SdeReader sde,
            int structureTypeId,
            int assetTypeId,
            Eve.Universe.StationCatalog stations,
            int sameSystem)
        {
            Source = source;
            InventorySchema = inventorySchema;
            WalletSchema = walletSchema;
            SpaceSchema = spaceSchema;
            CorporationSchema = corporationSchema;
            AssetSafetySchema = assetSafetySchema;
            Sde = sde;
            StructureTypeId = structureTypeId;
            AssetTypeId = assetTypeId;
            Stations = stations;
            SameSystemStation = stations.All.First(s => s.SolarSystemId == sameSystem);
            Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _schemas = [assetSafetySchema, corporationSchema, spaceSchema, walletSchema, inventorySchema];

            Items = new Eve.Inventory.PgItemStore(source, inventorySchema);
            WalletStore = new Eve.Wallet.PgWalletStore(source, walletSchema);
            Wallets = new Eve.Wallet.WalletTransactions(source, walletSchema);
            SpaceItems = new Eve.Ships.PgSpaceItemStore(source, spaceSchema);
            System.Threading.Tasks.Task<System.Text.Json.Nodes.JsonObject> ReadTableAsync(string table, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return System.Threading.Tasks.Task.FromResult(
                    table.Equals("structures", System.StringComparison.Ordinal) ? _structures : new System.Text.Json.Nodes.JsonObject());
            }
            System.Threading.Tasks.Task<Eve.Relations.Affiliation> AffiliationOfAsync(long characterId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var corp = _affiliations.GetValueOrDefault(characterId);
                return System.Threading.Tasks.Task.FromResult(new Eve.Relations.Affiliation(corp > 0 ? corp : null, null, null));
            }
            _readTable = ReadTableAsync;
            _affiliationOf = AffiliationOfAsync;
            StructureDirectory = new global::Eve.Host.PersistenceStructureDirectoryReadModel(ReadTableAsync, AffiliationOfAsync);
            Store = new global::Eve.Host.PersistenceStructureAssetSafetyReadModel(
                source, Items, SpaceItems, Wallets, Sde, ReadTableAsync, AffiliationOfAsync, () => Now,
                inventorySchema, corporationSchema, assetSafetySchema, StructureDirectory,
                message => LogMessages.Add(message));
        }

        private readonly System.Collections.Generic.Dictionary<long, long> _affiliations = new();
        private readonly System.Func<string, CancellationToken, System.Threading.Tasks.Task<System.Text.Json.Nodes.JsonObject>> _readTable;
        private readonly System.Func<long, CancellationToken, System.Threading.Tasks.Task<Eve.Relations.Affiliation>> _affiliationOf;

        public Npgsql.NpgsqlDataSource Source { get; }
        public string InventorySchema { get; }
        public string WalletSchema { get; }
        public string SpaceSchema { get; }
        public string CorporationSchema { get; }
        public string AssetSafetySchema { get; }
        public Eve.Sde.SdeReader Sde { get; }
        public int StructureTypeId { get; }
        public int AssetTypeId { get; }
        public Eve.Universe.StationCatalog Stations { get; }
        public Eve.Universe.Station SameSystemStation { get; }
        public Eve.Inventory.PgItemStore Items { get; }
        public Eve.Wallet.PgWalletStore WalletStore { get; }
        public Eve.Wallet.WalletTransactions Wallets { get; }
        public Eve.Ships.PgSpaceItemStore SpaceItems { get; }
        public global::Eve.Host.PersistenceStructureDirectoryReadModel StructureDirectory { get; }
        public global::Eve.Host.PersistenceStructureAssetSafetyReadModel Store { get; }
        public long Now { get; set; }
        public System.Collections.Generic.List<string> LogMessages { get; } = [];
        public long WormholeSystemId { get; private set; }
        public long PochvenSystemId { get; private set; }
        public long HighSecuritySystemId { get; private set; }

        public static async Task<AssetSafetyPgRig> CreateAsync()
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var connectionBuilder = new Npgsql.NpgsqlConnectionStringBuilder(
                Eve.Data.PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!))
            { Pooling = false };
            var source = new Npgsql.NpgsqlDataSourceBuilder(connectionBuilder.ConnectionString).Build();
            var inventorySchema = $"as_i_{suffix}";
            var walletSchema = $"as_w_{suffix}";
            var spaceSchema = $"as_s_{suffix}";
            var corporationSchema = $"as_c_{suffix}";
            var assetSafetySchema = $"as_a_{suffix}";
            try
            {
                var sde = new Eve.Sde.SdeReader(source);
                var types = await sde.ReadTypeSummariesAsync();
                var structureType = types.First(t => t.Published && t.Name.Equals("Astrahus", StringComparison.OrdinalIgnoreCase));
                var assetType = types.First(t => t.Published && t.Name.Equals("Tritanium", StringComparison.OrdinalIgnoreCase));
                var stations = await Eve.Universe.StationCatalog.LoadAsync(sde);
                var regionRows = await sde.ReadAsync("mapRegions");
                var regions = regionRows.ToDictionary(
                    row => checked((int)row.Key),
                    row => (
                        Name: Eve.Sde.SdeReader.English(row.Doc.GetProperty("name")),
                        Wormhole: row.Doc.TryGetProperty("wormholeClassID", out var classId)
                            && classId.ValueKind == System.Text.Json.JsonValueKind.Number
                            && classId.GetInt32() > 0));
                var systemRows = await sde.ReadAsync("mapSolarSystems");
                var systems = systemRows.ToDictionary(
                    row => checked((int)row.Key),
                    row => (
                        RegionId: row.Doc.GetProperty("regionID").GetInt32(),
                        Security: row.Doc.TryGetProperty("securityStatus", out var sec) && sec.ValueKind == System.Text.Json.JsonValueKind.Number
                            ? sec.GetDouble() : 0d));
                var stationSystems = stations.All.Select(s => s.SolarSystemId).ToHashSet();
                var sameStation = stations.All.OrderBy(s => s.Id).First(station =>
                {
                    if (!systems.TryGetValue(station.SolarSystemId, out var system)
                        || !regions.TryGetValue(system.RegionId, out var region))
                        return false;
                    return !region.Wormhole && !region.Name.Equals("Pochven", StringComparison.OrdinalIgnoreCase);
                });
                var rig = new AssetSafetyPgRig(
                    source, inventorySchema, walletSchema, spaceSchema, corporationSchema, assetSafetySchema,
                    sde, structureType.TypeId, assetType.TypeId, stations, sameStation.SolarSystemId);

                await Eve.Inventory.PgItemStore.MigrateAsync(source, inventorySchema);
                await Eve.Wallet.PgWalletStore.MigrateAsync(source, walletSchema);
                await Eve.Ships.PgSpaceItemStore.MigrateAsync(source, spaceSchema);
                await global::Eve.Host.PersistenceStructureAssetSafetyReadModel.MigrateAsync(source, assetSafetySchema);
                await rig.CreateCorporationSchemaAsync();

                foreach (var (systemId, system) in systems)
                {
                    var region = regions.GetValueOrDefault(system.RegionId);
                    if (region.Wormhole && rig.WormholeSystemId == 0)
                        rig.WormholeSystemId = systemId;
                    if (region.Name.Equals("Pochven", StringComparison.OrdinalIgnoreCase) && rig.PochvenSystemId == 0)
                        rig.PochvenSystemId = systemId;
                    if (system.Security >= 0.45 && !stationSystems.Contains(systemId) && !region.Wormhole
                        && !region.Name.Equals("Pochven", StringComparison.OrdinalIgnoreCase) && rig.HighSecuritySystemId == 0)
                        rig.HighSecuritySystemId = systemId;
                }
                Assert.True(rig.WormholeSystemId > 0, "The current SDE must contain a wormhole system.");
                Assert.True(rig.PochvenSystemId > 0, "The current SDE must contain Pochven.");
                Assert.True(rig.HighSecuritySystemId > 0, "The current SDE must contain a high-security system without an NPC station.");
                return rig;
            }
            catch
            {
                foreach (var schema in new[] { assetSafetySchema, corporationSchema, spaceSchema, walletSchema, inventorySchema })
                {
                    try
                    {
                        await using var connection = await source.OpenConnectionAsync();
                        await using var command = Command(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE");
                        await command.ExecuteNonQueryAsync();
                    }
                    catch
                    {
                    }
                }
                await source.DisposeAsync();
                throw;
            }
        }

        public async Task CreateCorporationAsync(long corporationId, long ceoId, long directorId, long memberId)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var corporation = new Npgsql.NpgsqlCommand(
                $"INSERT INTO {CorporationSchema}.corporations (id, ceo_id) VALUES ($1, $2)", connection, transaction))
            {
                corporation.Parameters.Add(new Npgsql.NpgsqlParameter { Value = corporationId });
                corporation.Parameters.Add(new Npgsql.NpgsqlParameter { Value = ceoId });
                await corporation.ExecuteNonQueryAsync();
            }
            await using (var members = new Npgsql.NpgsqlCommand(
                $"INSERT INTO {CorporationSchema}.members (corporation_id, character_id, roles) VALUES ($1, $2, $3), ($1, $4, $5), ($1, $6, $7)",
                connection, transaction))
            {
                members.Parameters.Add(new Npgsql.NpgsqlParameter { Value = corporationId });
                members.Parameters.Add(new Npgsql.NpgsqlParameter { Value = ceoId });
                members.Parameters.Add(new Npgsql.NpgsqlParameter { Value = 0L });
                members.Parameters.Add(new Npgsql.NpgsqlParameter { Value = directorId });
                members.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Eve.Corporations.CorporationRoles.Director });
                members.Parameters.Add(new Npgsql.NpgsqlParameter { Value = memberId });
                members.Parameters.Add(new Npgsql.NpgsqlParameter { Value = 0L });
                await members.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
            _affiliations[ceoId] = corporationId;
            _affiliations[directorId] = corporationId;
            _affiliations[memberId] = corporationId;
        }

        public long AddStructure(long systemId, string name)
        {
            var id = System.Threading.Interlocked.Increment(ref _nextStructureId);
            _structures[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
            {
                ["structureID"] = id,
                ["itemID"] = id,
                ["typeID"] = StructureTypeId,
                ["solarSystemID"] = systemId,
                ["ownerCorpID"] = 980001L,
                ["ownerID"] = 980001L,
                ["allianceID"] = 0L,
                ["destroyedAt"] = false,
                ["name"] = name,
                ["position"] = new System.Text.Json.Nodes.JsonArray(
                    System.Text.Json.Nodes.JsonValue.Create(1_000_000_000_000d),
                    System.Text.Json.Nodes.JsonValue.Create(2_000_000_000_000d),
                    System.Text.Json.Nodes.JsonValue.Create(3_000_000_000_000d)),
            };
            return id;
        }

        public async Task<long> AddAssetAsync(long ownerId, long structureId, long quantity)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var id = await Items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(AssetTypeId, ownerId, structureId, 0, quantity));
            await transaction.CommitAsync();
            return id;
        }

        public async Task OpenWalletAsync(long ownerId, decimal isk)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await WalletStore.OpenAsync(connection, transaction, ownerId, isk, 0);
            await transaction.CommitAsync();
        }

        public Eve.Services.ServiceCall Call(string method, long characterId, long corporationId, int callId)
        {
            var session = new Eve.Services.ClientSession { CharacterId = characterId };
            if (corporationId > 0)
                session.Set("corpid", new Eve.Marshal.PyInt(corporationId));
            return new Eve.Services.ServiceCall
            {
                Method = method,
                Args = [],
                Session = session,
                CallId = new Eve.Marshal.PyInt(callId),
            };
        }

        public async Task<long> WrapIdAsync(long ownerId, short scope, long sourceStructureId)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var command = Command(connection,
                $"SELECT wrap_id FROM {AssetSafetySchema}.asset_safety_wraps WHERE owner_id=$1 AND scope=$2 AND source_structure_id=$3 ORDER BY wrap_id DESC LIMIT 1");
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = ownerId });
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = scope });
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = sourceStructureId });
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public async Task<long> WrapIdForStateAsync(long ownerId, short scope, long sourceStructureId, short state)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var command = Command(connection,
                $"SELECT wrap_id FROM {AssetSafetySchema}.asset_safety_wraps WHERE owner_id=$1 AND scope=$2 AND source_structure_id=$3 AND state=$4 ORDER BY wrap_id DESC LIMIT 1");
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = ownerId });
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = scope });
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = sourceStructureId });
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = state });
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public async Task<long> WrapCreatedAtAsync(long wrapId)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var command = Command(connection,
                $"SELECT created_at_ms FROM {AssetSafetySchema}.asset_safety_wraps WHERE wrap_id=$1");
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = wrapId });
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<short> WrapStateAsync(long wrapId)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var command = Command(connection,
                $"SELECT state FROM {AssetSafetySchema}.asset_safety_wraps WHERE wrap_id=$1");
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = wrapId });
            return Convert.ToInt16(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<long> WrapDestinationAsync(long wrapId)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var command = Command(connection,
                $"SELECT destination_id FROM {AssetSafetySchema}.asset_safety_wraps WHERE wrap_id=$1");
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = wrapId });
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<long> ItemLocationAsync(long itemId)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var command = Command(connection, $"SELECT location_id FROM {InventorySchema}.items WHERE id=$1");
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = itemId });
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<long> ItemOwnerAsync(long itemId)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var command = Command(connection, $"SELECT owner_id FROM {InventorySchema}.items WHERE id=$1");
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = itemId });
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<long> ItemLocationCountAsync(long itemId, long locationId, long ownerId)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var command = Command(connection,
                $"SELECT count(*) FROM {InventorySchema}.items WHERE id=$1 AND location_id=$2 AND owner_id=$3");
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = itemId });
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = locationId });
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = ownerId });
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<bool> SpaceItemExistsAsync(long itemId)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var command = Command(connection, $"SELECT count(*) FROM {SpaceSchema}.space_items WHERE item_id=$1");
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = itemId });
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) == 1;
        }

        public async Task<decimal> JournalAmountAsync(long ownerId, string operationKey)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var command = Command(connection,
                $"SELECT amount FROM {WalletSchema}.journal WHERE owner_id=$1 AND operation_key=$2");
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = ownerId });
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = operationKey });
            return Convert.ToDecimal(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<long> ReceiptCountAsync(string action)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var command = Command(connection,
                $"SELECT count(*) FROM {AssetSafetySchema}.asset_safety_operation_receipts WHERE action=$1");
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = action });
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<decimal> ExpectedFeeAsync(int typeId, long quantity, bool sameSystem)
        {
            decimal price = 0;
            try
            {
                var market = await Sde.ReadAsync("marketPrices", [typeId]);
                if (market.Count > 0)
                {
                    var row = market[0].Doc;
                    price = ReadDecimal(row, "averagePrice");
                    if (price <= 0)
                        price = ReadDecimal(row, "adjustedPrice");
                }
            }
            catch (Npgsql.PostgresException error) when (error.SqlState == "42P01")
            {
            }
            if (price <= 0)
            {
                var types = await Sde.ReadAsync("types", [typeId]);
                if (types.Count > 0)
                    price = ReadDecimal(types[0].Doc, "basePrice");
            }
            if (price <= 0)
                price = 1m;
            return decimal.Round(price * quantity * (sameSystem ? 0.005m : 0.15m), 2, MidpointRounding.AwayFromZero);
        }

        public async Task<long> HighSecuritySystemWithoutNpcStationAsync()
        {
            var regions = (await Sde.ReadAsync("mapRegions")).ToDictionary(
                r => checked((int)r.Key),
                r => (
                    Name: Eve.Sde.SdeReader.English(r.Doc.GetProperty("name")),
                    Wormhole: r.Doc.TryGetProperty("wormholeClassID", out var wormhole)
                        && wormhole.ValueKind == System.Text.Json.JsonValueKind.Number
                        && wormhole.GetInt32() > 0));
            var stationSystems = Stations.All.Select(s => s.SolarSystemId).ToHashSet();
            foreach (var row in await Sde.ReadAsync("mapSolarSystems"))
            {
                var region = regions[checked((int)row.Doc.GetProperty("regionID").GetInt32())];
                var security = row.Doc.TryGetProperty("securityStatus", out var sec) && sec.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? sec.GetDouble() : 0;
                var id = checked((int)row.Key);
                if (security >= 0.45 && !stationSystems.Contains(id) && !region.Wormhole
                    && !region.Name.Equals("Pochven", StringComparison.OrdinalIgnoreCase))
                    return id;
            }
            throw new InvalidOperationException("The current SDE has no high-security system without an NPC station.");
        }

        public async Task<(string TriggerName, string FunctionName)> AddMoveFailureTriggerAsync(long wrapId, long destinationId)
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var functionName = $"as_fail_{suffix}";
            var triggerName = $"as_fail_trigger_{suffix}";
            var ddl = $"""
                CREATE FUNCTION {AssetSafetySchema}.{functionName}() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    IF OLD.location_id = {wrapId} AND NEW.location_id = {destinationId} THEN
                        RAISE EXCEPTION 'forced Asset Safety rollback';
                    END IF;
                    RETURN NEW;
                END
                $body$;
                """;
            await using var connection = await Source.OpenConnectionAsync();
            await using (var function = Command(connection, ddl))
                await function.ExecuteNonQueryAsync();
            await using (var trigger = Command(connection,
                $"CREATE TRIGGER {triggerName} BEFORE UPDATE OF location_id ON {InventorySchema}.items FOR EACH ROW EXECUTE FUNCTION {AssetSafetySchema}.{functionName}()"))
                await trigger.ExecuteNonQueryAsync();
            return (triggerName, functionName);
        }

        public async Task RemoveMoveFailureTriggerAsync(string triggerName, string functionName)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using (var trigger = Command(connection,
                $"DROP TRIGGER IF EXISTS {triggerName} ON {InventorySchema}.items"))
                await trigger.ExecuteNonQueryAsync();
            await using (var function = Command(connection,
                $"DROP FUNCTION IF EXISTS {AssetSafetySchema}.{functionName}()"))
                await function.ExecuteNonQueryAsync();
        }

        private async Task CreateCorporationSchemaAsync()
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using (var schema = Command(connection, $"CREATE SCHEMA {CorporationSchema}"))
                await schema.ExecuteNonQueryAsync();
            await using (var corporations = Command(connection,
                $"CREATE TABLE {CorporationSchema}.corporations (id bigint PRIMARY KEY, ceo_id bigint NOT NULL, ceo_roles_suspended boolean NOT NULL DEFAULT false, is_npc boolean NOT NULL DEFAULT false, deleted boolean NOT NULL DEFAULT false)"))
                await corporations.ExecuteNonQueryAsync();
            await using (var members = Command(connection,
                $"CREATE TABLE {CorporationSchema}.members (corporation_id bigint NOT NULL, character_id bigint NOT NULL, roles bigint NOT NULL DEFAULT 0, PRIMARY KEY(corporation_id, character_id))"))
                await members.ExecuteNonQueryAsync();
        }

        private System.Threading.Tasks.Task<System.Text.Json.Nodes.JsonObject> ReadTableAsync(
            string table,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return System.Threading.Tasks.Task.FromResult(
                table.Equals("structures", StringComparison.Ordinal) ? _structures : new System.Text.Json.Nodes.JsonObject());
        }

        private System.Threading.Tasks.Task<Eve.Relations.Affiliation> AffiliationOfAsync(
            long characterId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var corporation = _affiliations.GetValueOrDefault(characterId);
            return System.Threading.Tasks.Task.FromResult(
                new Eve.Relations.Affiliation(corporation > 0 ? corporation : null, null, null));
        }



        private static decimal ReadDecimal(System.Text.Json.JsonElement doc, string property) =>
            doc.TryGetProperty(property, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number
                && value.TryGetDecimal(out var number) ? number : 0m;

        private static Npgsql.NpgsqlCommand Command(Npgsql.NpgsqlConnection connection, string sql) =>
            new(sql, connection);

        public async ValueTask DisposeAsync()
        {
            foreach (var schema in _schemas)
            {
                await using var connection = await Source.OpenConnectionAsync();
                await using var command = Command(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
            await Source.DisposeAsync();
        }
    }

    [PgFact]
    public async Task Foundations_owns_and_disposes_its_shared_local_timers()
    {
        await using var source = Npgsql.NpgsqlDataSource.Create(
            Eve.Data.PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var schemas = new System.Collections.Generic.List<string>();
        string Schema(string _, string defaultName)
        {
            var schema = $"lt_{suffix}_{defaultName}";
            schemas.Add(schema);
            return schema;
        }

        var foundationsType = typeof(global::Foundations);
        object? foundations = null;
        Eve.Scheduler.LocalTimers? timers = null;
        try
        {
            var create = foundationsType.GetMethod("CreateAsync",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
            var creation = (Task)create.Invoke(null, new object?[]
            {
                source,
                (Func<long>)(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                (Func<string, string, string>)Schema,
                (Action<string>)(_ => { })
            })!;
            await creation;
            foundations = creation.GetType().GetProperty("Result")!.GetValue(creation)!;
            timers = (Eve.Scheduler.LocalTimers)foundationsType.GetProperty("LocalTimers")!.GetValue(foundations)!;
            timers.Set("foundations.test", Timeout.InfiniteTimeSpan, () => Task.CompletedTask);
            Assert.Equal(1, timers.Count);

            var start = (Task)foundationsType.GetMethod("StartAsync")!
                .Invoke(foundations, new object?[] { CancellationToken.None })!;
            await start;
        }
        finally
        {
            try
            {
                if (foundations is IAsyncDisposable disposable) await disposable.DisposeAsync();
            }
            finally
            {
                foreach (var schema in schemas)
                {
                    await using var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                    await drop.ExecuteNonQueryAsync();
                }
            }
        }

        Assert.NotNull(timers);
        Assert.Equal(0, timers!.Count);
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
        var model = new PersistenceStructureDirectoryReadModel(ReadTable, Affiliation, null!, null!);

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
        var unknown = new PersistenceStructureDirectoryReadModel(ReadUnknown, Affiliation, null!, null!);
        Assert.Empty(Assert.IsType<Eve.Marshal.PyList>(await unknown.GetNearbyJumpBridgesAsync(accessCall)).Items);
    }

[Fact]
    public async Task Structure_directory_host_reads_deployed_rows_and_docking_profiles()
    {
        const long structureId = 88000011;
        const long deniedStructureId = 88000012;
        const long characterId = 90000011;
        const long corporationId = 98000011;
        const long allianceId = 99000011;
        const long allowedGroupId = 700001;
        const long blockedGroupId = 700002;
        const long allowedProfileId = 500011;
        const long blockedProfileId = 500012;
        var tables = new Dictionary<string, System.Text.Json.Nodes.JsonObject>
        {
            ["structures"] = new()
            {
                [structureId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                {
                    ["structureID"] = structureId,
                    ["typeID"] = 35841,
                    ["solarSystemID"] = 30000142,
                    ["ownerCorpID"] = corporationId,
                    ["allianceID"] = allianceId,
                    ["profileID"] = allowedProfileId,
                    ["serviceStates"] = new System.Text.Json.Nodes.JsonObject { ["1"] = 1, ["15"] = 1 },
                },
                [deniedStructureId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                {
                    ["structureID"] = deniedStructureId,
                    ["typeID"] = 35841,
                    ["solarSystemID"] = 30000144,
                    ["ownerCorpID"] = corporationId,
                    ["allianceID"] = allianceId,
                    ["profileID"] = blockedProfileId,
                    ["serviceStates"] = new System.Text.Json.Nodes.JsonObject { ["1"] = 1 },
                },
            },
            ["structureProfiles"] = new()
            {
                ["profilesByID"] = new System.Text.Json.Nodes.JsonObject
                {
                    [allowedProfileId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["corporationID"] = corporationId,
                        ["isDefault"] = false,
                        ["settingsBySettingID"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["19"] = new System.Text.Json.Nodes.JsonArray(
                                new System.Text.Json.Nodes.JsonObject { ["groupID"] = allowedGroupId, ["value"] = 1 }),
                        },
                    },
                    [blockedProfileId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["corporationID"] = corporationId,
                        ["isDefault"] = false,
                        ["settingsBySettingID"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["19"] = new System.Text.Json.Nodes.JsonArray(
                                new System.Text.Json.Nodes.JsonObject { ["groupID"] = blockedGroupId, ["value"] = 1 }),
                        },
                    },
                },
            },
            ["accessGroups"] = new()
            {
                [allowedGroupId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                {
                    ["creatorID"] = 90000012,
                    ["members"] = new System.Text.Json.Nodes.JsonArray
                    {
                        new System.Text.Json.Nodes.JsonObject { ["memberID"] = characterId, ["membershipType"] = 0 },
                    },
                },
                [blockedGroupId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                {
                    ["creatorID"] = 90000013,
                    ["members"] = new System.Text.Json.Nodes.JsonArray
                    {
                        new System.Text.Json.Nodes.JsonObject { ["memberID"] = characterId, ["membershipType"] = 3 },
                    },
                },
            },
        };
        Task<System.Text.Json.Nodes.JsonObject> ReadTable(string name, CancellationToken _) =>
            Task.FromResult((System.Text.Json.Nodes.JsonObject)tables.GetValueOrDefault(name, new()).DeepClone());
        Task<Eve.Relations.Affiliation> Affiliation(long id, CancellationToken _) =>
            Task.FromResult(id == characterId
                ? new Eve.Relations.Affiliation(corporationId, allianceId, null)
                : new Eve.Relations.Affiliation(98000012, null, null));
        var readModel = new PersistenceStructureDirectoryReadModel(ReadTable, Affiliation, null!, null!);
        var persisted = await readModel.GetStructureInfoAsync(structureId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.True(persisted.PersistentlyDeployed);
        Assert.NotSame(Eve.Marshal.PyNone.Instance, persisted.Info);

        var myStructures = await readModel.GetMyCharacterStructuresAsync(characterId, CancellationToken.None);
        Assert.Equal(new long[] { structureId, deniedStructureId },
            myStructures.Entries.Select(entry => entry.Key switch
            {
                Eve.Marshal.PyInt number => (long)number.Value,
                Eve.Marshal.PyLong number => number.Value,
                _ => throw new InvalidOperationException("Expected an integer structure ID."),
            }).OrderBy(id => id));
        var corporationStructures = await readModel.GetMyCorporationStructuresAsync(
            characterId, corporationId, CancellationToken.None);
        Assert.Equal(myStructures.Entries.Count, corporationStructures.Entries.Count);
        var otherCorporationStructures = await readModel.GetMyCorporationStructuresAsync(
            characterId, corporationId + 1, CancellationToken.None);
        Assert.Empty(otherCorporationStructures.Entries);

        var session = new Eve.Services.ClientSession { CharacterId = characterId };
        session.Set("corpid", new Eve.Marshal.PyInt(corporationId));
        session.Set("allianceid", new Eve.Marshal.PyInt(allianceId));
        var service = new Eve.Services.Core.StructureDirectoryService(readModel);
        var docking = await service.CallAsync(new Eve.Services.ServiceCall
        {
            Method = "CheckMyDockingAccessToStructures",
            Args = [new Eve.Marshal.PyList([new Eve.Marshal.PyLong(structureId), new Eve.Marshal.PyLong(deniedStructureId)])],
            Session = session,
        });
        var allowed = Assert.IsType<Eve.Marshal.PyList>(docking);
        Assert.Equal(new long[] { structureId },
            allowed.Items.Select(item => item switch
            {
                Eve.Marshal.PyInt number => (long)number.Value,
                Eve.Marshal.PyLong number => number.Value,
                _ => throw new InvalidOperationException("Expected an integer structure ID."),
            }));
    }


    [Fact]
    public async Task Structure_settings_host_read_model_uses_persisted_owner_profile_and_access_group_rows()
    {
        const long structureId = 88000001;
        const long ownerCorporationId = 98000001;
        const long ownerAllianceId = 99000001;
        const long allowedCharacterId = 90000001;
        const long deniedCharacterId = 90000002;
        const long profileId = 50001;
        const long groupId = 70001;
        const long overrideGroupId = 70002;
        var tables = new Dictionary<string, System.Text.Json.Nodes.JsonObject>
        {
            ["structures"] = new()
            {
                [structureId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                {
                    ["structureID"] = structureId,
                    ["typeID"] = 35841,
                    ["solarSystemID"] = 30000142,
                    ["ownerCorpID"] = ownerCorporationId,
                    ["allianceID"] = ownerAllianceId,
                    ["profileID"] = profileId,
                    ["serviceStates"] = new System.Text.Json.Nodes.JsonObject { ["1"] = 1, ["15"] = 1, ["999"] = 1 },
                },
            },
            ["structureProfiles"] = new()
            {
                ["profilesByID"] = new System.Text.Json.Nodes.JsonObject
                {
                    [profileId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["corporationID"] = ownerCorporationId,
                        ["isDefault"] = false,
                        ["settingsBySettingID"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["19"] = new System.Text.Json.Nodes.JsonArray(
                                new System.Text.Json.Nodes.JsonObject { ["groupID"] = groupId, ["value"] = 1 },
                                new System.Text.Json.Nodes.JsonObject { ["groupID"] = overrideGroupId, ["value"] = 1 }),
                            ["34"] = new System.Text.Json.Nodes.JsonArray(
                                new System.Text.Json.Nodes.JsonObject { ["groupID"] = 70003, ["value"] = 1 }),
                        },
                    },
                },
            },
            ["accessGroups"] = new()
            {
                [groupId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                {
                    ["creatorID"] = 90000003,
                    ["members"] = new System.Text.Json.Nodes.JsonArray
                    {
                        new System.Text.Json.Nodes.JsonObject { ["memberID"] = allowedCharacterId, ["membershipType"] = 0 },
                        new System.Text.Json.Nodes.JsonObject { ["memberID"] = deniedCharacterId, ["membershipType"] = 3 },
                    },
                },
                [overrideGroupId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                {
                    ["creatorID"] = 90000004,
                    ["members"] = new System.Text.Json.Nodes.JsonArray
                    {
                        new System.Text.Json.Nodes.JsonObject { ["memberID"] = allowedCharacterId, ["membershipType"] = 0 },
                    },
                },
            },
            ["invtypes"] = new()
            {
                [structureId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                {
                    ["typeID"] = structureId,
                },
            },
        };
        var tableReads = new List<string>();
        Task<System.Text.Json.Nodes.JsonObject> ReadTable(string name, CancellationToken _)
        {
            tableReads.Add(name);
            return Task.FromResult((System.Text.Json.Nodes.JsonObject)tables.GetValueOrDefault(name, new()).DeepClone());
        }
        Task<Eve.Relations.Affiliation> Affiliation(long character, CancellationToken _) =>
            Task.FromResult(character == deniedCharacterId
                ? new Eve.Relations.Affiliation(ownerCorporationId, ownerAllianceId, null)
                : new Eve.Relations.Affiliation(98000002, 99000002, null));
        var model = new PersistenceStructureDirectoryReadModel(ReadTable, Affiliation, null!, null!);

        var allowedServices = Assert.IsType<Eve.Marshal.PyDict>(
            await model.CharacterGetServicesAsync(allowedCharacterId, structureId, CancellationToken.None));
        var visibleService = Assert.Single(allowedServices.Entries);
        Assert.Equal(new Eve.Marshal.PyInt(1), visibleService.Key);
        Assert.True(await model.CharacterHasServiceAsync(allowedCharacterId, structureId, 1, CancellationToken.None));
        Assert.False(await model.CharacterHasServiceAsync(allowedCharacterId, structureId, 15, CancellationToken.None));
        Assert.False(await model.CharacterHasServiceAsync(allowedCharacterId, structureId, 999, CancellationToken.None));
        Assert.True(await model.CharacterHasSettingAsync(allowedCharacterId, structureId, 19, CancellationToken.None));
        Assert.False(await model.CharacterHasSettingAsync(allowedCharacterId, structureId, 34, CancellationToken.None));

        var deniedServices = Assert.IsType<Eve.Marshal.PyDict>(
            await model.CharacterGetServicesAsync(deniedCharacterId, structureId, CancellationToken.None));
        Assert.Empty(deniedServices.Entries);
        Assert.False(await model.CharacterHasServiceAsync(deniedCharacterId, structureId, 1, CancellationToken.None));
        Assert.False(await model.CharacterHasServiceAsync(deniedCharacterId, structureId, 15, CancellationToken.None));
        Assert.False(await model.CharacterHasSettingAsync(deniedCharacterId, structureId, 19, CancellationToken.None));
        Assert.Equal(new[] { "structures", "structureProfiles", "accessGroups" }, tableReads.Distinct());

        Task<System.Text.Json.Nodes.JsonObject> MissingProfileTable(string name, CancellationToken _) =>
            Task.FromResult(name == "structures"
                ? (System.Text.Json.Nodes.JsonObject)tables["structures"].DeepClone()
                : new System.Text.Json.Nodes.JsonObject());
        var missingProfile = new PersistenceStructureDirectoryReadModel(MissingProfileTable, Affiliation, null!, null!);
        Assert.False(await missingProfile.CharacterHasServiceAsync(
            allowedCharacterId, structureId, 1, CancellationToken.None));
    }

[Fact]
    public async Task Missing_default_profile_falls_back_only_for_the_owner_corporation()
    {
        const long structureId = 88000002;
        const long ownerCorporationId = 98000003;
        const long ownerCharacterId = 90000003;
        const long otherCharacterId = 90000004;
        const long explicitMissingProfileId = 50003;
        var tables = new Dictionary<string, System.Text.Json.Nodes.JsonObject>
        {
            ["structures"] = new()
            {
                [structureId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                {
                    ["structureID"] = structureId,
                    ["typeID"] = 35841,
                    ["solarSystemID"] = 30000142,
                    ["ownerCorpID"] = ownerCorporationId,
                    ["profileID"] = 0,
                    ["serviceStates"] = new System.Text.Json.Nodes.JsonObject { ["1"] = 1 },
                },
            },
        };
        Task<System.Text.Json.Nodes.JsonObject> ReadTable(string name, CancellationToken _) =>
            Task.FromResult((System.Text.Json.Nodes.JsonObject)tables.GetValueOrDefault(name, new()).DeepClone());
        Task<Eve.Relations.Affiliation> Affiliation(long characterId, CancellationToken _) =>
            Task.FromResult(characterId == ownerCharacterId
                ? new Eve.Relations.Affiliation(ownerCorporationId, null, null)
                : new Eve.Relations.Affiliation(98000004, null, null));

        var model = new PersistenceStructureDirectoryReadModel(ReadTable, Affiliation, null!, null!);
        Assert.True(await model.CharacterHasServiceAsync(ownerCharacterId, structureId, 1, CancellationToken.None));
        Assert.False(await model.CharacterHasServiceAsync(otherCharacterId, structureId, 1, CancellationToken.None));

        var explicitStructure = (System.Text.Json.Nodes.JsonObject)tables["structures"][structureId.ToString(
            System.Globalization.CultureInfo.InvariantCulture)]!.DeepClone();
        explicitStructure["profileID"] = explicitMissingProfileId;
        tables["structures"][structureId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = explicitStructure;
        var missingExplicitProfile = new PersistenceStructureDirectoryReadModel(ReadTable, Affiliation, null!, null!);
        Assert.False(await missingExplicitProfile.CharacterHasServiceAsync(
            ownerCharacterId, structureId, 1, CancellationToken.None));
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
        const long otherSystemId = 30000143;
        const long otherAllianceId = 99000002;
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
                    [otherSystemId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["solarSystemID"] = otherSystemId,
                        ["claimStructureID"] = 1020003,
                        ["corporationID"] = corporationId,
                        ["allianceID"] = otherAllianceId,
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
        Assert.Equal(new[] { systemId }, await model.ReadAllianceSystemsAsync(allianceId, CancellationToken.None));
        Assert.Empty(await model.ReadAllianceSystemsAsync(0, CancellationToken.None));
    }


    [Fact]
    public async Task Mail_host_adapter_commits_mail_and_notification_before_returning_id()
    {
        const long senderId = 90000031;
        const long recipientId = 90000032;
        var cache = new Dictionary<string, System.Text.Json.Nodes.JsonObject>
        {
            ["mail"] = new()
            {
                ["unrelated"] = new System.Text.Json.Nodes.JsonObject { ["keep"] = true },
                ["messages"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["10"] = new System.Text.Json.Nodes.JsonObject { ["messageID"] = 10, ["senderID"] = senderId },
                    ["11"] = new System.Text.Json.Nodes.JsonObject { ["messageID"] = 11, ["senderID"] = senderId },
                },
                ["mailboxes"] = new System.Text.Json.Nodes.JsonObject
                {
                    [senderId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["unrelated"] = "keep",
                        ["statuses"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["10"] = new System.Text.Json.Nodes.JsonObject { ["statusMask"] = 8, ["labelMask"] = 32 },
                            ["11"] = new System.Text.Json.Nodes.JsonObject { ["statusMask"] = 16, ["labelMask"] = 64 },
                        },
                    },
                },
            },
            ["notifications"] = new()
            {
                ["metadata"] = "keep",
                ["boxes"] = new System.Text.Json.Nodes.JsonObject
                {
                    [recipientId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["byID"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["100"] = new System.Text.Json.Nodes.JsonObject
                            {
                                ["notificationID"] = 100,
                                ["typeID"] = 1004,
                                ["receiverID"] = recipientId,
                            },
                        },
                    },
                },
            },
        };
        var committed = cache.ToDictionary(
            pair => pair.Key,
            pair => (System.Text.Json.Nodes.JsonObject)pair.Value.DeepClone());
        var flushes = new List<string>();
        var liveNotifications = new List<(long RecipientId, Eve.Marshal.PyValue[] Arguments)>();

        System.Text.Json.Nodes.JsonObject ReadCache(string table) =>
            (System.Text.Json.Nodes.JsonObject)cache[table].DeepClone();

        void SetPath(System.Text.Json.Nodes.JsonObject root, string path, System.Text.Json.Nodes.JsonNode? value)
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                if (current[segments[index]] is not System.Text.Json.Nodes.JsonObject child)
                {
                    child = new System.Text.Json.Nodes.JsonObject();
                    current[segments[index]] = child;
                }
                current = child;
            }
            current[segments[^1]] = value?.DeepClone();
        }

        Eve.GameStore.WriteResult Write(
            string table,
            string? path,
            System.Text.Json.Nodes.JsonNode? value)
        {
            if (path is null)
            {
                cache[table] = (System.Text.Json.Nodes.JsonObject)value!.DeepClone();
                return new Eve.GameStore.WriteResult(true, null);
            }
            SetPath(cache[table], path, value);
            return new Eve.GameStore.WriteResult(true, null);
        }

        Task<int> Flush(string table, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            committed[table] = (System.Text.Json.Nodes.JsonObject)cache[table].DeepClone();
            flushes.Add(table);
            return Task.FromResult(1);
        }

        Task<System.Text.Json.Nodes.JsonObject> ReadCommitted(string table, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult((System.Text.Json.Nodes.JsonObject)committed[table].DeepClone());
        }

        var adapter = new PersistenceMailSendHostAdapter(
            ReadCache,
            Write,
            Flush,
            ReadCommitted,
            (_, _, _) => Task.FromResult<PersistenceMailSendRecipients?>(
                new([recipientId], "Sender", 2, new Dictionary<long, int> { [recipientId] = 1 })),
            () => 1_800_000_000_000,
            (characterId, arguments) =>
            {
                Assert.Equal(recipientId, characterId);
                Assert.Contains("notifications", flushes);
                var notificationId = Assert.IsType<Eve.Marshal.PyLong>(arguments[0]).Value;
                var typeId = Assert.IsType<Eve.Marshal.PyInt>(arguments[1]).Value;
                var saved = committed["notifications"]["boxes"]![recipientId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)]!["byID"]![notificationId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)]!;
                Assert.Equal(typeId, saved["typeID"]!.GetValue<int>());
                if (typeId == 1004)
                    Assert.Equal(12, saved["data"]!["msg"]!["messageID"]!.GetValue<long>());
                else
                {
                    Assert.Equal(Eve.Services.Crimewatch.KillRightNotificationTypes.Used, typeId);
                    Assert.Equal(senderId, saved["senderID"]!.GetValue<long>());
                    Assert.Equal(recipientId, saved["receiverID"]!.GetValue<long>());
                }
                liveNotifications.Add((characterId, arguments));
            });
        var session = new Eve.Services.ClientSession { CharacterId = senderId };
        var request = new Eve.Services.Core.MailSendRequest(
            senderId, [recipientId], null, Array.Empty<long>(),
            "Subject", "Body", ReplyToMessageId: 10, ForwardedFromMessageId: 11);

        var messageId = await adapter.SendAsync(request, session, CancellationToken.None);

        Assert.Equal(12L, messageId);
        Assert.True(committed["mail"]["unrelated"]!["keep"]!.GetValue<bool>());
        Assert.Equal("keep", committed["mail"]["mailboxes"]![senderId.ToString(
            System.Globalization.CultureInfo.InvariantCulture)]!["unrelated"]!.GetValue<string>());
        var savedMessage = committed["mail"]["messages"]!["12"]!;
        Assert.Equal("Body", savedMessage["body"]!.GetValue<string>());
        Assert.Empty(savedMessage["toCorpOrAllianceID"]!.AsArray());
        Assert.Equal(0, committed["mail"]["mailboxes"]![senderId.ToString(
            System.Globalization.CultureInfo.InvariantCulture)]!["statuses"]!["12"]!["statusMask"]!.GetValue<int>());
        Assert.Equal(2, committed["mail"]["mailboxes"]![senderId.ToString(
            System.Globalization.CultureInfo.InvariantCulture)]!["statuses"]!["12"]!["labelMask"]!.GetValue<int>());
        Assert.Equal(10L, committed["mail"]["mailboxes"]![senderId.ToString(
            System.Globalization.CultureInfo.InvariantCulture)]!["statuses"]!["10"]!["statusMask"]!.GetValue<long>());
        Assert.Equal(32, committed["mail"]["mailboxes"]![senderId.ToString(
            System.Globalization.CultureInfo.InvariantCulture)]!["statuses"]!["10"]!["labelMask"]!.GetValue<int>());
        Assert.Equal(20L, committed["mail"]["mailboxes"]![senderId.ToString(
            System.Globalization.CultureInfo.InvariantCulture)]!["statuses"]!["11"]!["statusMask"]!.GetValue<long>());
        Assert.Equal(64, committed["mail"]["mailboxes"]![senderId.ToString(
            System.Globalization.CultureInfo.InvariantCulture)]!["statuses"]!["11"]!["labelMask"]!.GetValue<int>());
        Assert.Equal(1, committed["mail"]["mailboxes"]![recipientId.ToString(
            System.Globalization.CultureInfo.InvariantCulture)]!["statuses"]!["12"]!["labelMask"]!.GetValue<int>());
        Assert.Equal("keep", committed["notifications"]["metadata"]!.GetValue<string>());
        Assert.Single(liveNotifications);

        var usedData = new System.Text.Json.Nodes.JsonObject { ["charID"] = senderId };
        await adapter.WriteKillRightNotificationAsync(
            recipientId, senderId, Eve.Services.Crimewatch.KillRightNotificationTypes.Used,
            usedData, "kill-right-used:activation-1", CancellationToken.None);
        await adapter.WriteKillRightNotificationAsync(
            recipientId, senderId, Eve.Services.Crimewatch.KillRightNotificationTypes.Used,
            usedData, "kill-right-used:activation-1", CancellationToken.None);

        var notificationRows = committed["notifications"]["boxes"]![recipientId.ToString(
            System.Globalization.CultureInfo.InvariantCulture)]!["byID"]!.AsObject();
        var usedNotification = Assert.Single(
            notificationRows.Select(pair => pair.Value).OfType<System.Text.Json.Nodes.JsonObject>(),
            notification => notification["idempotencyKey"]?.GetValue<string>() == "kill-right-used:activation-1");
        Assert.Equal(Eve.Services.Crimewatch.KillRightNotificationTypes.Used, usedNotification["typeID"]!.GetValue<int>());
        Assert.Equal(Eve.Services.Crimewatch.KillRightNotificationTypes.MiscGroup, usedNotification["groupID"]!.GetValue<int>());
        Assert.Equal(senderId, usedNotification["data"]!["charID"]!.GetValue<long>());
        Assert.Equal(2, liveNotifications.Count);
    }

    [Fact]
    public async Task Calendar_host_audience_uses_current_corporation_and_alliance_members()
    {
        const long corporationId = 98000041;
        const long secondCorporationId = 98000042;
        const long allianceId = 99000041;
        var corporationMembers = new Dictionary<long, IReadOnlyList<long>>
        {
            [corporationId] = [90000041, 90000042],
            [secondCorporationId] = [90000042, 90000043],
        };
        var allianceCorporations = new Dictionary<long, IReadOnlyList<long>>
        {
            [allianceId] = [corporationId, secondCorporationId],
        };
        var reader = new PersistenceCalendarAudienceReader(
            (ownerId, _) => Task.FromResult(corporationMembers.GetValueOrDefault(ownerId, [])),
            (ownerId, _) => Task.FromResult(allianceCorporations.GetValueOrDefault(ownerId, [])));

        Assert.Equal(new long[] { 90000041, 90000042 },
            await reader.GetMemberCharacterIdsAsync(Eve.Calendar.CalendarTags.Corporation, corporationId));
        Assert.Equal(new long[] { 90000041, 90000042, 90000043 },
            await reader.GetMemberCharacterIdsAsync(Eve.Calendar.CalendarTags.Alliance, allianceId));
        Assert.Empty(await reader.GetMemberCharacterIdsAsync(Eve.Calendar.CalendarTags.Personal, 90000041));
    }


}
