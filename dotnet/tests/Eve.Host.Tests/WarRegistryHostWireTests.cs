using Eve.Characters;
using Eve.Corporations;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class WarRegistryHostWireTests : IAsyncLifetime
{
    private const long Declarer = 98_001_001;
    private const long Defender = 98_001_002;
    private const long Ally = 98_001_003;
    private const long NowMs = 1_800_000_000_000;

    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA", "EVE_TRADE_SCHEMA", "EVE_BOOKMARKS_SCHEMA", "EVE_ESS_SCHEMA",
    ];

    private readonly string _prefix = "wrh" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _schemas = [];

    private string Schema(string variable) =>
        $"{_prefix}_{variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant()}";

    public Task InitializeAsync()
    {
        foreach (var variable in SchemaVariables)
        {
            _saved[variable] = Environment.GetEnvironmentVariable(variable);
            var schema = Schema(variable);
            _schemas.Add(schema);
            Environment.SetEnvironmentVariable(variable, schema);
        }
        foreach (var (name, value) in new[] { ("EVE_SIM", "off"), ("EVE_MARKET_SEED", "skip"), ("EVE_WORMHOLES", "off") })
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL");
        if (url is not null)
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
            foreach (var schema in _schemas)
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
    }

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];
        public void Send(PyValue packet) => Sent.Add(packet);
    }

    private sealed record Notice(string Name, PyTuple Args);

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);

    [PgFact]
    public async Task Registered_war_registry_creates_and_accepts_an_ally_offer_through_macho_with_persisted_state_and_notifications()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL is required");
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var characterSchema = Schema("EVE_CHARACTERS_SCHEMA");
        var walletSchema = Schema("EVE_WALLET_SCHEMA");
        var corporationSchema = Schema("EVE_CORPORATIONS_SCHEMA");
        await PgCharacterStore.MigrateAsync(source, characterSchema);
        await PgWalletStore.MigrateAsync(source, walletSchema);
        await CorporationStore.MigrateAsync(source, corporationSchema);

        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var characters = new PgCharacterStore(source, characterSchema);
        long declarerCharacter;
        long defenderCharacter;
        long allyCharacter;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            declarerCharacter = await InsertCharacterAsync(characters, connection, transaction, Declarer, station.Id,
                station.SolarSystemId, station.ConstellationId, station.RegionId, "War Registry Declarer");
            defenderCharacter = await InsertCharacterAsync(characters, connection, transaction, Defender, station.Id,
                station.SolarSystemId, station.ConstellationId, station.RegionId, "War Registry Defender");
            allyCharacter = await InsertCharacterAsync(characters, connection, transaction, Ally, station.Id,
                station.SolarSystemId, station.ConstellationId, station.RegionId, "War Registry Ally");
            await SeedCorporationsAsync(connection, transaction,
                (Declarer, declarerCharacter, "WRA"), (Defender, defenderCharacter, "WRD"), (Ally, allyCharacter, "WRL"));
            var wallets = new WalletTransactions(source, walletSchema);
            foreach (var id in new[] { Declarer, Defender, Ally }) await wallets.EnsureAsync(connection, transaction, id);
            await wallets.AdjustAsync(connection, transaction, new WalletAccount(Defender, 1001), 20_000m, 0, 0,
                "host wire test seed", NowMs, "war-registry-host-seed", "war-registry-host-seed");
            await transaction.CommitAsync();
        }

        HostRuntime? host = null;
        try
        {
            host = await HostComposition.BuildAsync(Settings(), () => NowMs);
            Assert.NotNull(host.Registry.Lookup("warRegistry"));
            var warId = await SeedWarAsync(source, Schema("EVE_RELATIONS_SCHEMA"), Declarer, Defender);

            var declarerSink = new Sink();
            var defenderSink = new Sink();
            var allySink = new Sink();
            var declarerSession = Session(declarerCharacter, Declarer, declarerSink, 1000);
            var defenderSession = Session(defenderCharacter, Defender, defenderSink, 1001);
            var allySession = Session(allyCharacter, Ally, allySink, 1000);
            host.CharacterSessions!.Enter(declarerCharacter, declarerSession);
            host.CharacterSessions.Enter(defenderCharacter, defenderSession);
            host.CharacterSessions.Enter(allyCharacter, allySession);
            var dispatcher = new PacketDispatcher(host.Registry, () => NowMs);
            var callId = 0;

            Assert.True(await dispatcher.DispatchAsync(BoundCall(++callId, allyCharacter, Ally, "CreateWarAllyOffer",
                new PyLong(warId), new PyReal(5_000), new PyLong(Defender), new PyStr("Support requested")), allySession));
            Assert.Contains(102, Notices(defenderSink).Where(notice => notice.Name == "OnNotificationReceived").Select(NotificationType));
            Assert.Contains(126, Notices(allySink).Where(notice => notice.Name == "OnNotificationReceived").Select(NotificationType));

            Assert.True(await dispatcher.DispatchAsync(BoundCall(++callId, defenderCharacter, Defender, "GetNegotiations"), defenderSession));
            var pending = Assert.IsType<PyList>(BindResult(LastResponse(defenderSink)));
            var offer = Assert.Single(pending.Items);
            Assert.Equal(InventoryWire.Id(warId), Field(offer, "warID"));
            Assert.Equal(InventoryWire.Id(Ally), Field(offer, "ownerID1"));
            Assert.Equal(InventoryWire.Id(Defender), Field(offer, "ownerID2"));
            Assert.Equal(new PyReal(5_000), Field(offer, "iskValue"));
            Assert.Equal(new PyWString("Support requested"), Field(offer, "description"));
            var offerId = Field(offer, "warNegotiationID");

            Assert.True(await dispatcher.DispatchAsync(BoundCall(++callId, defenderCharacter, Defender,
                "AcceptAllyNegotiation", offerId), defenderSession));
            var acceptedPacket = LastResponse(defenderSink);
            var responseIndex = defenderSink.Sent.IndexOf(acceptedPacket);
            var warChangedIndex = defenderSink.Sent.FindIndex(packet =>
                MachoPacket.Decode(packet) is { Type: MachoMessage.Notification, Dest.BroadcastId: "OnWarChanged" });
            Assert.True(responseIndex >= 0 && warChangedIndex > responseIndex, "OnWarChanged must be sent after the RPC response");
            Assert.Contains(127, Notices(defenderSink).Where(notice => notice.Name == "OnNotificationReceived").Select(NotificationType));
            Assert.Contains(99, Notices(defenderSink).Where(notice => notice.Name == "OnNotificationReceived").Select(NotificationType));

            Assert.True(await dispatcher.DispatchAsync(BoundCall(++callId, defenderCharacter, Defender,
                "GetWarNegotiation", offerId), defenderSession));
            var accepted = Assert.IsType<PyObject>(BindResult(LastResponse(defenderSink)));
            Assert.Equal(new PyInt(1), Field(accepted, "negotiationState"));

            var walletsAfter = new WalletTransactions(source, walletSchema);
            Assert.Equal(15_000m, await walletsAfter.AccountBalanceAsync(new WalletAccount(Defender, 1001)));
            Assert.Equal(5_000m, await walletsAfter.AccountBalanceAsync(new WalletAccount(Ally)));
            await using var allyRelation = source.CreateCommand($"SELECT count(*) FROM {Schema("EVE_RELATIONS_SCHEMA")}.relations WHERE kind='war.ally' AND subject=$1 AND object=$2");
            allyRelation.Parameters.Add(new NpgsqlParameter { Value = Ally });
            allyRelation.Parameters.Add(new NpgsqlParameter { Value = warId });
            Assert.Equal(1L, (long)(await allyRelation.ExecuteScalarAsync())!);

            Assert.True(await dispatcher.DispatchAsync(BoundCall(++callId, defenderCharacter, Defender,
                "CreateSurrenderNegotiation", new PyLong(warId), new PyReal(0), new PyStr("Surrender terms")), defenderSession));
            Assert.True(await dispatcher.DispatchAsync(BoundCall(++callId, defenderCharacter, Defender, "GetNegotiations"), defenderSession));
            var surrender = Assert.Single(Assert.IsType<PyList>(BindResult(LastResponse(defenderSink))).Items);
            Assert.Equal(new PyInt(2), Field(surrender, "warNegotiationTypeID"));
            Assert.True(await dispatcher.DispatchAsync(BoundCall(++callId, declarerCharacter, Declarer,
                "AcceptSurrender", Field(surrender, "warNegotiationID")), declarerSession));

            Assert.True(await dispatcher.DispatchAsync(DirectCall(++callId, defenderCharacter,
                "peaceTreatyMgr", "GetPeaceTreatiesForSession"), defenderSession));
            var treatySets = Assert.IsType<PyTuple>(Result(LastResponse(defenderSink)));
            var outgoingTreaties = Assert.IsType<PyObjectEx>(treatySets.Items[0]);
            var incomingTreaties = Assert.IsType<PyObjectEx>(treatySets.Items[1]);
            var outgoingTreaty = Assert.IsType<PyPackedRow>(Assert.Single(outgoingTreaties.List));
            var incomingTreaty = Assert.IsType<PyPackedRow>(Assert.Single(incomingTreaties.List));
            Assert.Equal(Declarer, Integer(outgoingTreaty.Values[0]));
            Assert.Equal(new PyInt(4), outgoingTreaty.Values[2]);
            Assert.Equal(warId, Integer(outgoingTreaty.Values[3]));
            Assert.Equal(Defender, Integer(incomingTreaty.Values[0]));
            Assert.Equal(new PyInt(4), incomingTreaty.Values[2]);

            await host.ShutdownAsync();
            host = null;
        }
        finally
        {
            if (host is not null) await host.ShutdownAsync();
        }
    }

    private static async Task<long> InsertCharacterAsync(PgCharacterStore store, NpgsqlConnection connection,
        NpgsqlTransaction transaction, long corporationId, long stationId, int systemId, int constellationId, int regionId, string name) =>
        await store.InsertAsync(connection, transaction, new NewCharacter(1, $"{name} {Guid.NewGuid():N}"[..32], 0, 1, 1, 1, 11, 1376,
            corporationId, null, stationId, systemId, constellationId, regionId, null, null));

    private static async Task SeedCorporationsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        params (long CorporationId, long CharacterId, string Ticker)[] corps)
    {
        foreach (var (corporationId, characterId, ticker) in corps)
        {
            await using var names = new NpgsqlCommand($"INSERT INTO {Environment.GetEnvironmentVariable("EVE_CORPORATIONS_SCHEMA")}.owner_names(owner_id,name_key,ticker_key) VALUES($1,$2,$3)", connection, transaction);
            names.Parameters.Add(new NpgsqlParameter { Value = corporationId });
            names.Parameters.Add(new NpgsqlParameter { Value = $"war wire {corporationId}" });
            names.Parameters.Add(new NpgsqlParameter { Value = ticker });
            await names.ExecuteNonQueryAsync();
            await using var corporation = new NpgsqlCommand($"""
                INSERT INTO {Environment.GetEnvironmentVariable("EVE_CORPORATIONS_SCHEMA")}.corporations
                    (id,name,ticker,ceo_id,creator_id,station_id,solar_system_id,tax_rate,lp_tax_rate,description,url,member_limit,created_ms)
                VALUES($1,$2,$3,$4,$4,0,0,0,0,'','',100,0)
                """, connection, transaction);
            corporation.Parameters.Add(new NpgsqlParameter { Value = corporationId });
            corporation.Parameters.Add(new NpgsqlParameter { Value = $"War Wire {corporationId}" });
            corporation.Parameters.Add(new NpgsqlParameter { Value = ticker });
            corporation.Parameters.Add(new NpgsqlParameter { Value = characterId });
            await corporation.ExecuteNonQueryAsync();
            await using var member = new NpgsqlCommand($"INSERT INTO {Environment.GetEnvironmentVariable("EVE_CORPORATIONS_SCHEMA")}.members(corporation_id,character_id,started_ms) VALUES($1,$2,0)", connection, transaction);
            member.Parameters.Add(new NpgsqlParameter { Value = corporationId });
            member.Parameters.Add(new NpgsqlParameter { Value = characterId });
            await member.ExecuteNonQueryAsync();
        }
    }

    private static async Task<long> SeedWarAsync(NpgsqlDataSource source, string schema, long declarer, long defender)
    {
        var filetime = Py.FileTimeFromUnixMs(NowMs).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using var command = source.CreateCommand($"""
            INSERT INTO {schema}.relations(kind,subject,object,value,attrs,since_ms,updated_ms)
            VALUES('war',$1,$2,1,jsonb_build_object('timeDeclared',$3,'timeStarted',$3,'mutual',0,'openForAllies',0),$4,$4)
            RETURNING id
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = declarer });
        command.Parameters.Add(new NpgsqlParameter { Value = defender });
        command.Parameters.Add(new NpgsqlParameter { Value = filetime });
        command.Parameters.Add(new NpgsqlParameter { Value = NowMs });
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static ClientSession Session(long characterId, long corporationId, Sink sink, int accountKey)
    {
        var session = new ClientSession(sink) { SessionId = characterId, ClientId = characterId, CharacterId = characterId };
        session.Set("charid", new PyLong(characterId));
        session.Set("corpid", new PyLong(corporationId));
        session.Set("corporationID", new PyLong(corporationId));
        session.Set("corpaccountkey", new PyInt(accountKey));
        return session;
    }

    private static PyValue BoundCall(int callId, long characterId, long ownerId, string method, params PyValue[] args)
    {
        var bound = Py.Tuple(new PyLong(ownerId), new PyInt(1));
        var nested = Py.Tuple(new PyStr(method), new PyTuple(args), new PyDict([]));
        return ClientCallPacket(callId, characterId, "MachoBindObject", bound, nested);
    }

    private static PyValue ClientCallPacket(int callId, long characterId, string method, params PyValue[] arguments)
        => ClientCallPacket(callId, characterId, "warRegistry", method, arguments);

    private static PyValue DirectCall(int callId, long characterId, string service, string method, params PyValue[] arguments)
        => ClientCallPacket(callId, characterId, service, method, arguments);

    private static PyValue ClientCallPacket(int callId, long characterId, string service, string method, params PyValue[] arguments)
    {
        var source = new MachoAddress(AddressKind.Client, CallId: new PyInt(callId), ClientId: new PyLong(characterId));
        var destination = new MachoAddress(AddressKind.Node, Service: service,
            CallId: new PyInt(callId), NodeId: new PyInt(MachoMessage.ProxyNodeId));
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        var body = new PyTuple([PyNone.Instance, new PyStr(method), new PyTuple(arguments), kwargs]);
        var payload = new PyTuple([new PyTuple([PyBool.True, new PySubStream(body)])]);
        return new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"), new PyTuple([
            new PyInt(MachoMessage.CallReq), source.Encode(), destination.Encode(), new PyInt(7), payload,
            new PyDict([]), PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance,
            PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance,
        ]));
    }

    private static PyValue LastResponse(Sink sink) => sink.Sent.Last(packet => MachoPacket.Decode(packet)?.Type == MachoMessage.CallRsp);

    private static PyValue Result(PyValue response)
    {
        var fields = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<PyInt>(fields[0]).Value);
        return Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(fields[4]).Items[0]).Decoded!;
    }

    private static PyValue BindResult(PyValue response) => Assert.IsType<PyTuple>(Result(response)).Items[1];

    private static PyValue Field(PyValue value, string name) => Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries
        .Single(entry => entry.Key == new PyStr(name)).Value;

    private static List<Notice> Notices(Sink sink)
    {
        var notices = new List<Notice>();
        foreach (var packet in sink.Sent.OfType<PyObject>())
        {
            var fields = Assert.IsType<PyTuple>(packet.Args).Items;
            if (fields[0] is not PyInt { Value: MachoMessage.Notification }) continue;
            var address = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(fields[2]).Args).Items;
            var envelope = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(fields[4]).Items[0]).Items;
            var call = Assert.IsType<PyTuple>(MarshalCodec.Decode(Assert.IsType<PyBuffer>(envelope[1]).Value));
            var args = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(call.Items[1]).Items[1]);
            notices.Add(new(JsSemantics.StringValue(address[1]) ?? "", args));
        }
        return notices;
    }

    private static int NotificationType(Notice notice) => (int)Assert.IsType<PyInt>(notice.Args.Items[1]).Value;

    private static long Integer(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => throw new Xunit.Sdk.XunitException($"Expected a wire integer, got {value.GetType().Name}"),
    };
}
