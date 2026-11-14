using System.Net;
using System.Net.Sockets;
using System.Text;
using Eve.Characters;
using Eve.Data;
using Eve.Host.PublicGateway;
using Eve.Identity;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Skills;
using Eve.Sde;
using Eve.Universe;
using Eve.Trade;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>
/// The New Eden Store as the real host composition registers it (HostComposition.BuildAsync), on scratch PostgreSQL schemas.
/// </summary>
[Collection("host-composition")]
public sealed class NewEdenStoreHostTests : IAsyncLifetime
{
    private const int CertificateTypeId = 34132;
    private const int PlexTypeId = 44992;
    private const int CrateTypeId = 91938;
    private const long UsdPlexPackOfferId = 400100;

    /// <summary>The schema variables the host reads, each on its own scratch name (the same list the market host test uses).</summary>
    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_TRADE_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA",
    ];

    private readonly string _prefix = "ns" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _schemas = [];
    private readonly List<HostRuntime> _hosts = [];

    /// <summary>A started host, shut down before the schemas are dropped (its scheduler would otherwise race the DROP).</summary>
    private async Task<HostRuntime> BuildHostAsync()
    {
        var host = await HostComposition.BuildAsync(Settings());
        _hosts.Add(host);
        return host;
    }

    private string Schema(string variable) => variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant() is var name ? $"{_prefix}_{name}" : "";

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
        foreach (var host in _hosts) await host.ShutdownAsync();
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL");
        if (url is not null)
        {
            await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url));
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
        private readonly System.Collections.Concurrent.ConcurrentQueue<PyValue> _packets = new();
        public int Count => _packets.Count;
        public PyValue[] Packets => _packets.ToArray();
        public void Send(PyValue packet) => _packets.Enqueue(packet);
    }

    private sealed class StorePurchaseWireService(IService store, CharacterSessions sessions, long userId, long characterId, long stationId) : IService
    {
        public string Name => "storePurchaseWire";

        public bool HasMethod(string method) => method == "buy_offer";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            call.Session.UserId = userId;
            call.Session.CharacterId = characterId;
            call.Session.Set("stationid", new PyLong(stationId));
            sessions.Enter(characterId, call.Session);
            return store.CallAsync(new ServiceCall
            {
                Method = call.Method,
                Args = call.Args,
                Kwargs = call.Kwargs,
                Session = call.Session,
                CallId = call.CallId,
                Cancellation = call.Cancellation,
            });
        }
    }

    private static List<(string Name, string IdType, PyValue[] Args)> Notifications(Sink sink)
    {
        var result = new List<(string Name, string IdType, PyValue[] Args)>();
        foreach (var packet in sink.Packets.OfType<PyObject>())
        {
            var state = ((PyTuple)packet.Args).Items;
            if (state[0] is not PyInt { Value: MachoMessage.Notification }) continue;
            var address = ((PyTuple)((PyObject)state[2]).Args).Items;
            var body = (PyBuffer)((PyTuple)((PyTuple)state[4]).Items[0]).Items[1];
            var inner = (PyTuple)MarshalCodec.Decode(body.Value.ToArray());
            result.Add((JsSemantics.StringValue(address[1])!, JsSemantics.StringValue(address[3])!,
                [.. ((PyTuple)((PyTuple)inner.Items[1]).Items[1]).Items]));
        }
        return result;
    }

    private async Task<long> CreateQaRedeemTokenAsync(
        IService userSvc, ClientSession session, int typeId, params (string Name, PyValue Value)[] values)
    {
        session.Set("accountRole", new PyLong(GameMasterRoles.Qa));
        var fields = new List<KeyValuePair<PyValue, PyValue>>
        {
            new(new PyStr("typeID"), new PyInt(typeId)),
        };
        fields.AddRange(values.Select(field => new KeyValuePair<PyValue, PyValue>(new PyStr(field.Name), field.Value)));
        var created = Assert.IsType<PyObject>(await userSvc.CallAsync(
            Call(session, "CreateRedeemTokenQA", 800, [], new PyDict(fields))));
        return Integer(ValueField(Assert.IsType<PyDict>(created.Args), "tokenID"));
    }

    private static long Integer(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => throw new InvalidOperationException($"Expected an integer, got {value}."),
    };

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);

    private static byte[] PasswordHash(byte value) => Enumerable.Repeat(value, LoginAttempt.PasswordHashLength).ToArray();

    private static ServiceCall Call(ClientSession session, string method, long callId, PyValue[] args, PyDict? kwargs = null) =>
        new() { Session = session, Method = method, Args = args, Kwargs = kwargs ?? new PyDict([]), CallId = new PyInt(callId) };

    /// <summary>A character with a wallet at the station: the account's PLEX balance starts at zero.</summary>
    private async Task<long> CreatePilotAsync(NpgsqlDataSource source, Station station, string name, long plex = 0, int accountId = 1)
    {
        await using var c = await source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var actor = await new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA")).InsertAsync(c, tx,
            new NewCharacter(accountId, name, 0, 1, 1, 1, 11, 1376, 1000044, null,
                station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId, null, null));
        await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).OpenAsync(c, tx, actor, 10000m, plex);
        await tx.CommitAsync();
        return actor;
    }

    private static async Task<(PyValue Result, List<string> Notifications)> StoreWireCallAsync(
        TestFront front, SessionOpen open, long callId, string offer, long quantity, long characterId, string currency = "PLX", long? targetCharacterId = null)
    {
        var kwargs = Py.Dict(
            ("from_character_id", Py.Number(characterId)),
            ("to_character_id", Py.Number(targetCharacterId ?? characterId)),
            ("message", new PyStr("")),
            ("is_game_time", PyBool.True),
            ("store_id", new PyInt(4)));
        var packet = Packets.CallWithKwargs(
            open, callId, "storePurchaseWire", "buy_offer",
            Py.Tuple(new PyStr(offer), new PyStr(currency), Py.Number(quantity)), kwargs);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(packet)));

        var notifications = new List<string>();
        while (true)
        {
            var sent = Assert.IsType<Send>(await front.ReceiveAsync());
            var outer = Assert.IsType<PyObject>(MarshalCodec.Decode(sent.Payload));
            var state = ((PyTuple)outer.Args).Items;
            if (state[0] is PyInt { Value: MachoMessage.Notification })
            {
                var address = ((PyTuple)((PyObject)state[2]).Args).Items;
                notifications.Add(JsSemantics.StringValue(address[1]) ?? "");
                continue;
            }
            if (state[0] is PyInt { Value: MachoMessage.CallRsp })
            {
                var payload = (PyTuple)state[4];
                var result = payload.Items[0] is PySubStream { Decoded: { } decoded } ? decoded : payload.Items[0];
                return (result, notifications);
            }
        }
    }

    private static (string TypeUrl, byte[] Payload, long? CharacterId) DecodeGatewayNotice(byte[] frame)
    {
        var messages = GatewayEnvelope.TakeFrames([.. frame]);
        var envelope = ProtoReader.Fields(Assert.Single(messages));
        var anyField = envelope.Single(field => field.Number == 5);
        var any = ProtoReader.Fields(anyField.Bytes);
        var target = ProtoReader.Fields(envelope.Single(field => field.Number == 6).Bytes);
        var character = target.FirstOrDefault(field => field.Number == 4);
        return (
            Encoding.UTF8.GetString(any.Single(field => field.Number == 1).Bytes.Span),
            any.Single(field => field.Number == 2).Bytes.ToArray(),
            character.Number == 4 ? checked((long)character.Scalar) : null);
    }

    private async Task<long> PlexBalanceAsync(IService vault, ClientSession session, long callId) =>
        Assert.IsType<PyLong>(await vault.CallAsync(Call(session, "get_account_balance", callId, [new PyStr("PLX")]))).Value;

    private static long Quantity(IReadOnlyList<Item> items, int typeId) =>
        items.Where(item => item.TypeId == typeId).Sum(item => item.Quantity);

    [PgFact]
    public async Task The_registered_host_store_keeps_image_url_present_when_an_offer_has_no_image()
    {
        var host = await BuildHostAsync();
        var store = host.Registry.Lookup("storeManager")!;
        var session = new ClientSession(new Sink()) { SessionId = 402, UserId = 1, CharacterId = 1 };

        var offers = Assert.IsType<PyList>(await store.CallAsync(Call(session, "get_offers", 1, [new PyInt(4)])));
        var omega = Assert.IsType<PyDict>(offers.Items.Single(offer => ValueField(Assert.IsType<PyDict>(offer), "storeOfferID") is PyStr { Value: "omega_30_days" }));

        Assert.Same(PyNone.Instance, ValueField(omega, "imageUrl"));
    }

    [PgFact]
    public async Task Certificate_purchase_without_checkout_returns_none_and_grants_nothing()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var host = await BuildHostAsync();
        var store = host.Registry.Lookup("storeManager")!;
        var vault = host.Registry.Lookup("vaultManager")!;
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var pilot = await CreatePilotAsync(source, station, "Store Pilot");
        var session = new ClientSession(new Sink()) { SessionId = 401, UserId = 1, CharacterId = pilot };
        var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        var before = Quantity(await items.OwnedByAsync(pilot), CertificateTypeId);
        var result = await store.CallAsync(Call(session, "buy_offer", 7,
            [new PyStr("pilot_resculpt_certificate"), new PyStr("PLX"), new PyInt(1)]));
        Assert.Same(PyNone.Instance, result);
        Assert.Equal(before, Quantity(await items.OwnedByAsync(pilot), CertificateTypeId));
        Assert.Equal(0, await PlexBalanceAsync(vault, session, 2));
    }

    [PgFact]
    public async Task Reverse_redeeming_plex_moves_the_item_to_a_station_locked_token_and_claims_it_once()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var host = await BuildHostAsync();
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var otherStation = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First(value => value.Id != station.Id);
        var pilot = await CreatePilotAsync(source, station, "PLEX reverse redeem pilot");
        var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        long plexItemId;
        long otherItemId;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            plexItemId = await items.CreateAsync(connection, transaction,
                new NewItem(PlexTypeId, pilot, station.Id, ItemFlags.Hangar, 15));
            otherItemId = await items.CreateAsync(connection, transaction,
                new NewItem(CertificateTypeId, pilot, station.Id, ItemFlags.Hangar, 1));
            await transaction.CommitAsync();
        }

        var sink = new Sink();
        var session = new ClientSession(sink) { SessionId = 402, UserId = 1, CharacterId = pilot };
        session.Set("stationid", new PyLong(station.Id));
        host.CharacterSessions!.Enter(pilot, session);
        var userSvc = host.Registry.Lookup("userSvc")!;
        var invalidItem = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await userSvc.CallAsync(Call(session, "ReverseRedeem", 9, [new PyLong(otherItemId)])));
        Assert.True(invalidItem.Error.IsUserError);
        Assert.NotNull(await items.GetAsync(otherItemId));

        var result = await userSvc.CallAsync(Call(session, "ReverseRedeem", 10, [new PyLong(plexItemId)]));
        Assert.Same(PyBool.True, result);
        Assert.Null(await items.GetAsync(plexItemId));
        Assert.Equal(0, sink.Count);
        session.RunAfterResponse();
        Assert.Equal(1, sink.Count);

        await userSvc.CallAsync(Call(session, "ReverseRedeem", 18, [new PyLong(plexItemId)]));
        Assert.Equal(1, sink.Count);

        var tokens = Assert.IsType<PyObjectEx>(await userSvc.CallAsync(Call(session, "GetRedeemTokens", 11, [])));
        var tokenFields = Assert.IsType<PyPackedRow>(Assert.Single(tokens.List)).Fields()
            .ToDictionary(field => field.Key, field => field.Value);
        Assert.True(((PyInt)tokenFields["tokenID"]).Value > 0);
        Assert.Equal(new PyInt(15), tokenFields["quantity"]);
        Assert.Equal(new PyInt(station.Id), tokenFields["stationID"]);
        Assert.Same(PyNone.Instance, tokenFields["massTokenID"]);

        var claimToken = Py.Dict(
            ("tokenID", tokenFields["tokenID"]),
            ("massTokenID", PyNone.Instance),
            ("typeID", tokenFields["typeID"]));
        var claimTokenWithMassId = Py.Tuple(tokenFields["tokenID"], tokenFields["tokenID"], tokenFields["typeID"]);
        var claimArgs = new PyValue[] { new PyList([claimToken, claimTokenWithMassId]), new PyLong(pilot) };
        session.Set("stationid", new PyLong(otherStation.Id));
        var wrongStation = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
        {
            await userSvc.CallAsync(Call(session, "ClaimRedeemTokens", 12, claimArgs));
        });
        Assert.True(wrongStation.Error.IsUserError);
        Assert.Single(Assert.IsType<PyObjectEx>(await userSvc.CallAsync(Call(session, "GetRedeemTokens", 13, []))).List);

        session.Set("stationid", new PyLong(station.Id));
        var notificationCount = Notifications(sink).Count;
        var claimed = Assert.IsType<PyDict>(await userSvc.CallAsync(Call(session, "ClaimRedeemTokens", 14, claimArgs)));
        Assert.Equal(notificationCount, Notifications(sink).Count);
        session.RunAfterResponse();
        var delivered = Assert.Single(Notifications(sink).Skip(notificationCount));
        Assert.Equal("OnItemChange", delivered.Name);
        Assert.Empty(Assert.IsType<PyDict>(delivered.Args[1]).Entries);
        Assert.Same(PyNone.Instance, delivered.Args[2]);
        Assert.Empty(Assert.IsType<PyObjectEx>(await userSvc.CallAsync(Call(session, "GetRedeemTokens", 15, []))).List);
        var created = Assert.IsType<PyDict>(Assert.Single(Assert.IsType<PyList>(ValueField(claimed, "itemsCreated")).Items));
        var claimedItem = await items.GetAsync(((PyLong)ValueField(created, "itemID")).Value);
        Assert.NotNull(claimedItem);
        Assert.Equal((PlexTypeId, station.Id, Eve.Inventory.ItemFlags.Hangar, 15L),
            (claimedItem.TypeId, claimedItem.LocationId, claimedItem.Flag, claimedItem.Quantity));

        await userSvc.CallAsync(Call(session, "ReverseRedeem", 16, [new PyLong(plexItemId)]));
        Assert.Empty(Assert.IsType<PyObjectEx>(await userSvc.CallAsync(Call(session, "GetRedeemTokens", 17, []))).List);
    }

    [PgFact]
    public async Task Redeeming_a_blueprint_preserves_copy_research_and_notifies_the_online_inventory()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var host = await BuildHostAsync();
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var pilot = await CreatePilotAsync(source, station, "Redeem blueprint pilot");
        var sink = new Sink();
        var session = new ClientSession(sink) { SessionId = 404, UserId = 1, CharacterId = pilot };
        session.Set("stationid", new PyLong(station.Id));
        host.CharacterSessions!.Enter(pilot, session);
        var userSvc = host.Registry.Lookup("userSvc")!;
        var tokenId = await CreateQaRedeemTokenAsync(userSvc, session, 681,
            ("quantity", new PyInt(1)),
            ("blueprintRuns", new PyInt(5)),
            ("blueprintMaterialLevel", new PyInt(10)),
            ("blueprintProductivityLevel", new PyInt(20)),
            ("label", new PyStr("redeemed blueprint")));
        var request = new PyList([Py.Dict(
            ("tokenID", new PyLong(tokenId)),
            ("massTokenID", new PyLong(tokenId)),
            ("typeID", new PyInt(681)))]);
        var result = Assert.IsType<PyDict>(await userSvc.CallAsync(
            Call(session, "ClaimRedeemTokens", 801, [request, new PyLong(pilot)])));
        var created = Assert.IsType<PyDict>(Assert.Single(Assert.IsType<PyList>(ValueField(result, "itemsCreated")).Items));
        var item = await new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"))
            .GetAsync(Integer(ValueField(created, "itemID")));
        Assert.NotNull(item);
        Assert.True(item.Singleton);
        Assert.True(item.BlueprintCopy);
        Assert.Equal(5, item.BlueprintRuns);
        Assert.Equal(10, item.MaterialEfficiency);
        Assert.Equal(20, item.TimeEfficiency);
        Assert.Equal("redeemed blueprint", item.CustomInfo);
        session.RunAfterResponse();
        Assert.Equal("OnItemChange", Assert.Single(Notifications(sink)).Name);
    }

    [PgFact]
    public async Task Redeeming_the_daily_skill_point_type_grants_the_sde_amount_once()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var host = await BuildHostAsync();
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var pilot = await CreatePilotAsync(source, station, "Daily skill points pilot");
        var sink = new Sink();
        var session = new ClientSession(sink) { SessionId = 405, UserId = 1, CharacterId = pilot };
        session.Set("stationid", new PyLong(station.Id));
        host.CharacterSessions!.Enter(pilot, session);
        var userSvc = host.Registry.Lookup("userSvc")!;
        var tokenId = await CreateQaRedeemTokenAsync(userSvc, session, 49810);
        var skills = new PgSkillStore(source, Schema("EVE_SKILLS_SCHEMA"));
        var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        var previousBalance = await skills.FreeSkillPointsAsync(pilot);
        var request = new PyList([Py.Dict(
            ("tokenID", new PyLong(tokenId)),
            ("massTokenID", new PyLong(tokenId)),
            ("typeID", new PyInt(49810)))]);
        var result = Assert.IsType<PyDict>(await userSvc.CallAsync(
            Call(session, "ClaimRedeemTokens", 802, [request, new PyLong(pilot)])));
        Assert.Equal(new PyInt(100_000), ValueField(result, "skillpoints"));
        Assert.Empty(Assert.IsType<PyList>(ValueField(result, "itemsCreated")).Items);
        Assert.Equal(previousBalance + 100_000, await skills.FreeSkillPointsAsync(pilot));
        Assert.DoesNotContain(await items.OwnedByAsync(pilot), item => item.TypeId == 49810);
        Assert.Empty(Notifications(sink));
        session.RunAfterResponse();
        var notification = Assert.Single(Notifications(sink));
        Assert.Equal("OnFreeSkillPointsChanged", notification.Name);
        Assert.Equal(new PyInt(checked((int)(previousBalance + 100_000))), notification.Args[0]);
        var replay = Assert.IsType<PyDict>(await userSvc.CallAsync(
            Call(session, "ClaimRedeemTokens", 803, [request, new PyLong(pilot)])));
        Assert.Equal(new PyInt(100_000), ValueField(replay, "skillpoints"));
        Assert.Equal(previousBalance + 100_000, await skills.FreeSkillPointsAsync(pilot));
    }

    [PgFact]
    public async Task Expired_and_unavailable_redeem_tokens_return_the_client_error_rows()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var host = await BuildHostAsync();
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var pilot = await CreatePilotAsync(source, station, "Expired redeem pilot");
        var session = new ClientSession(new Sink()) { SessionId = 406, UserId = 1, CharacterId = pilot };
        session.Set("stationid", new PyLong(station.Id));
        host.CharacterSessions!.Enter(pilot, session);
        var userSvc = host.Registry.Lookup("userSvc")!;
        var unavailableId = await CreateQaRedeemTokenAsync(userSvc, session, CertificateTypeId,
            ("available", PyBool.False));
        var expiredId = await CreateQaRedeemTokenAsync(userSvc, session, CertificateTypeId,
            ("expiry", new PyLong(1)));
        var request = new PyList([
            Py.Dict(("tokenID", new PyLong(unavailableId)), ("massTokenID", new PyLong(unavailableId)), ("typeID", new PyInt(CertificateTypeId))),
            Py.Dict(("tokenID", new PyLong(expiredId)), ("massTokenID", new PyLong(expiredId)), ("typeID", new PyInt(CertificateTypeId))),
        ]);
        var result = Assert.IsType<PyDict>(await userSvc.CallAsync(
            Call(session, "ClaimRedeemTokens", 804, [request, new PyLong(pilot)])));
        var errors = Assert.IsType<PyList>(ValueField(result, "errors"));
        Assert.Equal(2, errors.Items.Count);
        foreach (var (error, expectedId) in errors.Items.Zip(new[] { unavailableId, expiredId }))
        {
            var fields = Assert.IsType<PyTuple>(error).Items;
            Assert.Equal(expectedId, Integer(fields[0]));
            var keyval = Assert.IsType<PyDict>(Assert.IsType<PyObject>(fields[1]).Args);
            Assert.Equal(new PyStr("RedeemTokenNotFound"), ValueField(keyval, "msg"));
            Assert.Empty(Assert.IsType<PyDict>(ValueField(keyval, "dict")).Entries);
        }
        Assert.Empty(Assert.IsType<PyList>(ValueField(result, "itemsCreated")).Items);
        Assert.Empty(await new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA")).OwnedByAsync(pilot));
    }

    [PgFact]
    public async Task The_host_registers_the_reference_standings_and_trade_aliases()
    {
        var host = await BuildHostAsync();
        var standings = Assert.IsAssignableFrom<IService>(host.Registry.Lookup("standingMgr"));
        var trade = Assert.IsAssignableFrom<IService>(host.Registry.Lookup("trademgr"));
        Assert.Same(standings, host.Registry.Lookup("standing2"));
        Assert.Same(trade, host.Registry.Lookup("tradeMgr"));
        Assert.True(standings.HasMethod("GetNPCNPCStandings"));
        Assert.True(trade.HasMethod("InitiateTrade"));
        var advertised = new HostMachoNetInfo(host.Registry, () => GlobalConfig.Build(new ClusterOptions())).Services();
        Assert.Contains(advertised, entry => entry.Key == "standing2" && entry.Value is null);
        Assert.Contains(advertised, entry => entry.Key == "tradeMgr" && entry.Value == "station");
    }


    private sealed record DirectTradeContext(
        HostRuntime Host,
        NpgsqlDataSource Source,
        Station Station,
        long AliceId,
        long BobId,
        IService Service,
        ClientSession Alice,
        ClientSession Bob,
        Sink AliceSink,
        Sink BobSink,
        PgItemStore Items,
        TradeAuthority Authority);

    private static long _tradeCallId = 2000;

    private async Task<DirectTradeContext> OpenDirectTradeAsync()
    {
        var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var host = await BuildHostAsync();
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var aliceId = await CreatePilotAsync(source, station, "Trade Alice " + suffix);
        var bobId = await CreatePilotAsync(source, station, "Trade Bob " + suffix);
        var aliceSink = new Sink();
        var bobSink = new Sink();
        var alice = DockedSession(aliceId, station, aliceSink);
        var bob = DockedSession(bobId, station, bobSink);
        host.CharacterSessions!.Enter(aliceId, alice);
        host.CharacterSessions.Enter(bobId, bob);

        var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        var walletSchema = Schema("EVE_WALLET_SCHEMA");
        var authority = new TradeAuthority(source, items, new WalletTransactions(source, walletSchema),
            new PgWalletStore(source, walletSchema), () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Schema("EVE_TRADE_SCHEMA"), new ItemTypeCatalog(new SdeReader(source)));
        var service = Assert.IsAssignableFrom<IService>(host.Registry.Lookup("trademgr"));
        await service.CallAsync(Call(alice, "InitiateTrade", Interlocked.Increment(ref _tradeCallId), [new PyLong(bobId)]));
        return new DirectTradeContext(host, source, station, aliceId, bobId, service, alice, bob, aliceSink, bobSink, items, authority);
    }

    private static async Task<long> CreateTradeItemAsync(DirectTradeContext context, NewItem item)
    {
        await using var connection = await context.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await context.Items.CreateAsync(connection, transaction, item);
        await transaction.CommitAsync();
        return id;
    }

    private static ClientSession DockedSession(long characterId, Station station, Sink sink)
    {
        var session = new ClientSession(sink) { CharacterId = characterId };
        session.Set("stationid", new PyInt(station.Id));
        session.Set("solarsystemid2", new PyInt(station.SolarSystemId));
        return session;
    }

    private static async Task<PyValue> TradeBoundCallAsync(DirectTradeContext context, ClientSession session, long tradeId,
        string method, PyValue[] args, PyDict? kwargs = null)
    {
        var nested = Py.Tuple(new PyStr(method), new PyList(args), kwargs ?? new PyDict([]));
        var bound = await context.Service.CallAsync(Call(session, "MachoBindObject", Interlocked.Increment(ref _tradeCallId),
            [new PyTuple([new PyLong(tradeId)]), nested]));
        return Assert.IsType<PyTuple>(bound).Items[1];
    }

    private static PyValue TradeManifest(long tradeId, decimal aliceIsk, decimal bobIsk, IEnumerable<long> itemIds)
    {
        var items = new PyObjectEx(ObjectExKind.Ex1,
            new PyTuple([new PyToken("__builtin__.set"), new PyTuple([new PyList(itemIds.Select(id => (PyValue)new PyLong(id)).ToList())])]),
            [], []);
        return new PyObjectEx(ObjectExKind.Ex1, new PyStr("KeyVal"), [], [
            new(new PyStr("tradeContainerID"), new PyLong(tradeId)),
            new(new PyStr("money"), new PyList([new PyReal((double)aliceIsk), new PyReal((double)bobIsk)])),
            new(new PyStr("tradeItems"), items),
        ]);
    }

    private static async Task<int> FirstRestrictedTradeTypeAsync(SdeReader sde, ItemTypeCatalog types)
    {
        var lists = await sde.ReadAsync("typeLists", [36L, 142L]);
        foreach (var list in lists)
        {
            var doc = list.Doc;
            var candidates = new List<long>();
            static long[] Values(System.Text.Json.JsonElement doc, string field) =>
                doc.TryGetProperty(field, out var values) && values.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? values.EnumerateArray().Where(value => value.TryGetInt64(out _)).Select(value => value.GetInt64()).ToArray()
                    : [];

            candidates.AddRange(Values(doc, "includedTypeIDs"));
            var groupIds = Values(doc, "includedGroupIDs");
            if (groupIds.Length > 0)
                candidates.AddRange((await sde.ReadByFieldAsync("types", "groupID", groupIds)).Select(row => row.Key));
            var categoryIds = Values(doc, "includedCategoryIDs");
            if (categoryIds.Length > 0)
            {
                var groups = await sde.ReadByFieldAsync("groups", "categoryID", categoryIds);
                candidates.AddRange((await sde.ReadByFieldAsync("types", "groupID", groups.Select(row => row.Key))).Select(row => row.Key));
            }
            foreach (var candidate in candidates.Distinct())
                if (candidate <= int.MaxValue && !await types.IsTradableAsync((int)candidate))
                    return (int)candidate;
        }
        throw new InvalidOperationException("The pinned SDE has no type in the trade-restricted lists.");
    }

    [PgFact]
    public async Task Trade_rpc_invalid_items_and_a_bad_multiadd_leave_persistence_and_notifications_unchanged()
    {
        var context = await OpenDirectTradeAsync();
        await using var source = context.Source;
        var trade = (await context.Authority.OpenViewForAsync(context.AliceId))!;
        var beforeAlicePackets = context.AliceSink.Count;
        var beforeBobPackets = context.BobSink.Count;

        var activeShip = await CreateTradeItemAsync(context,
            new NewItem(34, context.AliceId, context.Station.Id, ItemFlags.Hangar, Singleton: true));
        context.Alice.Set("shipid", new PyLong(activeShip));
        var shipError = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await TradeBoundCallAsync(context, context.Alice, trade.TradeId, "Add",
                [new PyLong(activeShip), new PyLong(context.Station.Id)]));
        Assert.Equal("PeopleAboardShip", shipError.Error.UserErrorMessage);

        var wrap = await CreateTradeItemAsync(context,
            new NewItem(60, context.AliceId, context.Station.Id, ItemFlags.Hangar, Singleton: true));
        var wrapError = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await TradeBoundCallAsync(context, context.Alice, trade.TradeId, "Add",
                [new PyLong(wrap), new PyLong(context.Station.Id)]));
        Assert.Equal("CannotTradeAssetSafety", wrapError.Error.UserErrorMessage);

        var catalog = new ItemTypeCatalog(new SdeReader(context.Source));
        var restrictedType = await FirstRestrictedTradeTypeAsync(new SdeReader(context.Source), catalog);
        var restricted = await CreateTradeItemAsync(context,
            new NewItem(restrictedType, context.AliceId, context.Station.Id, ItemFlags.Hangar));
        var restrictedError = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await TradeBoundCallAsync(context, context.Alice, trade.TradeId, "Add",
                [new PyLong(restricted), new PyLong(context.Station.Id)]));
        Assert.Equal("ItemCannotBeTraded", restrictedError.Error.UserErrorMessage);

        var wrongSourceItem = await CreateTradeItemAsync(context,
            new NewItem(34, context.AliceId, context.Station.Id, ItemFlags.Hangar));
        var sourceError = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await TradeBoundCallAsync(context, context.Alice, trade.TradeId, "Add",
                [new PyLong(wrongSourceItem), new PyLong(context.Station.Id + 1L)]));
        Assert.Equal("CustomNotify", sourceError.Error.UserErrorMessage);

        var foreignItem = await CreateTradeItemAsync(context,
            new NewItem(34, context.BobId, context.Station.Id, ItemFlags.Hangar));
        var ownerError = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await TradeBoundCallAsync(context, context.Alice, trade.TradeId, "Add",
                [new PyLong(foreignItem), new PyLong(context.Station.Id)]));
        Assert.Equal("CustomNotify", ownerError.Error.UserErrorMessage);

        var validItem = await CreateTradeItemAsync(context,
            new NewItem(34, context.AliceId, context.Station.Id, ItemFlags.Hangar));
        var batchError = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await TradeBoundCallAsync(context, context.Alice, trade.TradeId, "MultiAdd",
                [new PyList([new PyLong(validItem), new PyLong(activeShip)]), new PyLong(context.Station.Id)]));
        Assert.Equal("PeopleAboardShip", batchError.Error.UserErrorMessage);

        Assert.Equal(context.Station.Id, (await context.Items.GetAsync(validItem))!.LocationId);
        Assert.Equal(context.Station.Id, (await context.Items.GetAsync(wrongSourceItem))!.LocationId);
        Assert.Equal(context.BobId, (await context.Items.GetAsync(foreignItem))!.OwnerId);
        Assert.Empty((await context.Authority.ViewAsync(trade.TradeId))!.Items);
        Assert.Equal(beforeAlicePackets, context.AliceSink.Count);
        Assert.Equal(beforeBobPackets, context.BobSink.Count);
    }

    [PgFact]
    public async Task Trade_rpc_completes_and_transfers_nested_items_and_abort_and_recovery_restore_items()
    {
        var context = await OpenDirectTradeAsync();
        await using var source = context.Source;
        var trade = (await context.Authority.OpenViewForAsync(context.AliceId))!;
        var container = await CreateTradeItemAsync(context,
            new NewItem(3293, context.AliceId, context.Station.Id, ItemFlags.Hangar, Singleton: true));
        var child = await CreateTradeItemAsync(context,
            new NewItem(34, context.AliceId, container, ItemFlags.Hangar));

        Assert.Equal(PyBool.True, await TradeBoundCallAsync(context, context.Alice, trade.TradeId, "Add",
            [new PyLong(container), new PyLong(context.Station.Id)]));
        Assert.Equal(PyBool.True, await TradeBoundCallAsync(context, context.Bob, trade.TradeId, "OfferMoney", [new PyReal(250)]));
        var manifest = TradeManifest(trade.TradeId, 0m, 250m, [container]);
        Assert.Equal(PyBool.True, await TradeBoundCallAsync(context, context.Alice, trade.TradeId, "MakeOffer", [manifest]));
        Assert.Equal(PyBool.True, await TradeBoundCallAsync(context, context.Bob, trade.TradeId, "MakeOffer", [manifest]));
        Assert.Equal(context.BobId, (await context.Items.GetAsync(container))!.OwnerId);
        Assert.Equal((context.BobId, container), ((await context.Items.GetAsync(child))!.OwnerId, (await context.Items.GetAsync(child))!.LocationId));
        Assert.Equal(9750m, (await new PgWalletStore(context.Source, Schema("EVE_WALLET_SCHEMA")).GetAsync(context.BobId))!.Isk);
        Assert.True(context.AliceSink.Count > 1);
        Assert.True(context.BobSink.Count > 1);

        var reopened = await context.Authority.InitiateAsync(context.AliceId, context.BobId, context.Station.Id, context.Station.SolarSystemId);
        var abortItem = await CreateTradeItemAsync(context,
            new NewItem(34, context.AliceId, context.Station.Id, ItemFlags.Hangar));
        Assert.Equal(PyBool.True, await TradeBoundCallAsync(context, context.Alice, reopened.TradeId, "Add",
            [new PyLong(abortItem), new PyLong(context.Station.Id)]));
        Assert.Equal(PyBool.True, await TradeBoundCallAsync(context, context.Alice, reopened.TradeId, "Abort", []));
        Assert.Equal(context.Station.Id, (await context.Items.GetAsync(abortItem))!.LocationId);

        var recoveryTrade = await context.Authority.InitiateAsync(context.AliceId, context.BobId, context.Station.Id, context.Station.SolarSystemId);
        var recoveryItem = await CreateTradeItemAsync(context,
            new NewItem(34, context.AliceId, context.Station.Id, ItemFlags.Hangar));
        await context.Authority.StageAsync(recoveryTrade.TradeId, context.AliceId, recoveryItem, 0,
            context.Station.Id, context.Alice.GetInt("shipid") ?? 0);
        Assert.Equal(recoveryTrade.TradeId, (await context.Items.GetAsync(recoveryItem))!.LocationId);
        Assert.Single(await context.Authority.RecoverOpenAsync());
        Assert.Equal(context.Station.Id, (await context.Items.GetAsync(recoveryItem))!.LocationId);
    }

    [PgFact]
    public async Task Trade_rpc_concurrent_acceptances_complete_once()
    {
        var context = await OpenDirectTradeAsync();
        await using var source = context.Source;
        var trade = (await context.Authority.OpenViewForAsync(context.AliceId))!;
        var item = await CreateTradeItemAsync(context,
            new NewItem(34, context.AliceId, context.Station.Id, ItemFlags.Hangar));
        await TradeBoundCallAsync(context, context.Alice, trade.TradeId, "Add",
            [new PyLong(item), new PyLong(context.Station.Id)]);
        var manifest = TradeManifest(trade.TradeId, 0m, 0m, [item]);

        var results = await Task.WhenAll(
            TradeBoundCallAsync(context, context.Alice, trade.TradeId, "MakeOffer", [manifest]),
            TradeBoundCallAsync(context, context.Bob, trade.TradeId, "MakeOffer", [manifest]));

        Assert.All(results, result => Assert.Equal(PyBool.True, result));
        Assert.Equal(context.BobId, (await context.Items.GetAsync(item))!.OwnerId);
        Assert.Equal("completed", (await context.Authority.ViewAsync(trade.TradeId))!.Status);
    }

    [PgFact]
    public async Task Seasonal_login_campaign_state_and_claims_are_persisted_and_claim_once()
    {
        var oldCampaign = Environment.GetEnvironmentVariable("EVE_SEASONAL_LOGIN_CAMPAIGN");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Environment.SetEnvironmentVariable("EVE_SEASONAL_LOGIN_CAMPAIGN", System.Text.Json.JsonSerializer.Serialize(new
        {
            campaignId = 2356,
            startDate = today.AddDays(-1).ToString("yyyy-MM-dd"),
            endDate = today.AddDays(1).ToString("yyyy-MM-dd"),
            turnoverTimeUtc = "00:00:00",
            retroClaimEnabled = true,
            alphaRewards = new Dictionary<string, object> { ["0"] = new { typeId = CertificateTypeId, quantity = 1 } },
            omegaRewards = new Dictionary<string, object> { ["0"] = new { typeId = PlexTypeId, quantity = 1 } },
        }));
        try
        {
            var host = await BuildHostAsync();
            await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
            var accountDirectory = new PgAccountDirectory(source, new AccountOptions { AutoCreate = true }, Schema("EVE_IDENTITY_SCHEMA"));
            var account = Assert.IsType<LoggedIn>(await accountDirectory.LoginAsync(
                new LoginAttempt("seasonal-campaign", new byte[] { 9, 9, 9 }, "en", "127.0.0.1"), CancellationToken.None));
            Assert.Equal(1u, account.AccountId);
            accountDirectory.SetSubscriptionEndsAt(account.AccountId, DateTimeOffset.UtcNow.AddDays(-90));
            var pilot = await CreatePilotAsync(source, station, "Seasonal Pilot");
            var session = new ClientSession(new Sink()) { SessionId = 402, UserId = account.AccountId, CharacterId = pilot };
            var service = host.Registry.Lookup("seasonalLoginCampaignManager")!;
            var active = Assert.IsType<PyTuple>(await service.CallAsync(Call(session, "get_active_campaign", 1, [])));
            Assert.Equal(new PyInt(2356), active.Items[0]);
            Assert.Equal(PyBool.True, await service.CallAsync(Call(session, "claim_reward", 2, [])));
            Assert.Equal(PyBool.False, await service.CallAsync(Call(session, "claim_reward", 3, [])));
            accountDirectory.SetSubscriptionEndsAt(account.AccountId, DateTimeOffset.UtcNow.AddDays(90));
            var competingHost = await BuildHostAsync();
            var competingService = competingHost.Registry.Lookup("seasonalLoginCampaignManager")!;
            var competingSession = new ClientSession(new Sink()) { SessionId = 403, UserId = account.AccountId, CharacterId = pilot };
            var concurrentClaims = await Task.WhenAll(
                service.CallAsync(Call(session, "claim_reward", 4, [])).AsTask(),
                competingService.CallAsync(Call(competingSession, "claim_reward", 4, [])).AsTask());
            Assert.Equal(1, concurrentClaims.Count(result => result == PyBool.True));
            Assert.Equal(1, concurrentClaims.Count(result => result == PyBool.False));
            Assert.Equal(PyBool.False, await service.CallAsync(Call(session, "claim_reward", 5, [])));
            await competingHost.ShutdownAsync();
            _hosts.Remove(competingHost);
            await host.ShutdownAsync();
            _hosts.Remove(host);
            host = await BuildHostAsync();
            service = host.Registry.Lookup("seasonalLoginCampaignManager")!;
            var history = Assert.IsType<PyDict>(await service.CallAsync(Call(session, "get_claim_history", 6, [])));
            Assert.Single(history.Entries);
            var redeem = host.Registry.Lookup("userSvc")!;
            var redeemTokens = Assert.IsType<PyObjectEx>(await redeem.CallAsync(Call(session, "GetRedeemTokens", 7, [])));
            Assert.Equal(2, redeemTokens.List.Count);
            var wireTokens = Assert.IsType<PyObjectEx>(MarshalCodec.Decode(MarshalCodec.Encode(redeemTokens)));
            Assert.Equal(2, wireTokens.List.Count);
            var seasonalTokenIds = wireTokens.List
                .Select(row => Integer(Assert.IsType<PyPackedRow>(row).Fields()
                    .Single(field => field.Key == "tokenID").Value))
                .ToArray();
            Assert.All(seasonalTokenIds, tokenId => Assert.True(tokenId > int.MaxValue));
        }
        finally
        {
            Environment.SetEnvironmentVariable("EVE_SEASONAL_LOGIN_CAMPAIGN", oldCampaign);
        }
    }

    [PgFact]
    public async Task A_plex_pack_from_the_fast_checkout_is_credited_to_the_vault_and_its_ok_answer_is_the_client_s_confirmation()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var host = await BuildHostAsync();
        var checkout = host.Registry.Lookup("FastCheckoutService")!;
        var vault = host.Registry.Lookup("vaultManager")!;
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var pilot = await CreatePilotAsync(source, station, "Checkout Pilot");
        var session = new ClientSession(new Sink()) { SessionId = 402, UserId = 1, CharacterId = pilot };
        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await checkout.CallAsync(FastCheckoutBuy(session, UsdPlexPackOfferId, callId: 21)));
        Assert.Equal("FAST_CHECKOUT_UNAVAILABLE", refusal.Error.UserErrorMessage);
        Assert.Equal(0, await PlexBalanceAsync(vault, session, 3));
    }

    [PgFact]
    public async Task Store_checkout_without_settlement_returns_none_without_granting_currency_offers()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var host = await BuildHostAsync();
        var store = host.Registry.Lookup("storeManager")!;
        var vault = host.Registry.Lookup("vaultManager")!;
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var pilot = await CreatePilotAsync(source, station, "Currency Pilot");
        var session = new ClientSession(new Sink()) { SessionId = 403, UserId = 1, CharacterId = pilot };
        var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        foreach (var (currency, quantity, callId) in new[] { ("AUR", 2, 31L), ("XYZ", 1, 32L), ("PLX", 101, 33L), ("PLX", 0, 34L) })
        {
            var result = await store.CallAsync(
                Call(session, "buy_offer", callId, [new PyStr("pilot_resculpt_certificate"), new PyStr(currency), new PyInt(quantity)]));
            Assert.Same(PyNone.Instance, result);
        }
        Assert.Equal(0, Quantity(await items.OwnedByAsync(pilot), CertificateTypeId));
        Assert.Equal(0, await PlexBalanceAsync(vault, session, 35));
    }

    [PgFact]
    public async Task Real_money_store_offers_are_not_granted_without_checkout_settlement()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var host = await BuildHostAsync();
        var store = host.Registry.Lookup("storeManager")!;
        var vault = host.Registry.Lookup("vaultManager")!;
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var pilot = await CreatePilotAsync(source, station, "Bundle Pilot");
        var session = new ClientSession(new Sink()) { SessionId = 404, UserId = 1, CharacterId = pilot };
        var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        var result = await store.CallAsync(
            Call(session, "buy_offer", 43, [new PyStr("cash_plex_1000_gallente"), new PyStr("USD"), new PyInt(2)], Py.Dict(("store_id", new PyInt(9)))));
        Assert.Same(PyNone.Instance, result);
        Assert.Equal(0, await PlexBalanceAsync(vault, session, 44));
        Assert.Equal(0, Quantity(await items.OwnedByAsync(pilot), CrateTypeId));
    }

    [PgFact]
    public async Task Store_purchases_settle_on_the_host_wire_and_replay_receipts_after_restart()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var host = await BuildHostAsync();
        var store = host.Registry.Lookup("storeManager")!;
        var stationCatalog = await StationCatalog.LoadAsync(new SdeReader(source));
        var station = stationCatalog.All.First();
        var recipientGiftStation = stationCatalog.All.Skip(1).First();
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(16);
        using var stopServer = new CancellationTokenSource();
        var serving = host.Server.ServeAsync(listener, stopServer.Token);
        using var front = await TestFront.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);
        await front.SendAsync(new Hello(HostCodec.ProtocolVersion, 0xFFAA));
        Assert.Equal(new HelloAck(HostCodec.ProtocolVersion), await front.ReceiveAsync());
        Assert.IsType<ClusterConfig>(await front.ReceiveAsync());
        var userId = Assert.IsType<Accepted>((await front.AuthenticateAsync("store-purchase-wire", PasswordHash(8), 1)).Outcome).UserId;
        Assert.Equal(1u, userId);

        var pilot = await CreatePilotAsync(source, station, "Store purchase wire pilot", plex: 100);
        var alternate = await CreatePilotAsync(source, station, "Store purchase wire alternate", plex: 2_400);
        var accounts = new PgAccountDirectory(source, new AccountOptions { AutoCreate = true }, Schema("EVE_IDENTITY_SCHEMA"));
        var recipientAccount = Assert.IsType<LoggedIn>(await accounts.LoginAsync(
            new LoginAttempt($"gift{Guid.NewGuid():N}"[..20], PasswordHash(9), "EN", "127.0.0.1"), CancellationToken.None));
        var recipient = await CreatePilotAsync(source, station, "Store gift recipient", accountId: checked((int)recipientAccount.AccountId));
        var wallets = new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA"));
        Assert.Equal(100L, (await wallets.GetAsync(pilot))!.Plex);
        Assert.Equal(2_400L, (await wallets.GetAsync(alternate))!.Plex);
        accounts.SetSubscriptionEndsAt(userId, DateTimeOffset.UtcNow.AddDays(-1));
        accounts.SetSubscriptionEndsAt(recipientAccount.AccountId, DateTimeOffset.UtcNow.AddDays(-1));
        var sessions = Assert.IsType<CharacterSessions>(host.CharacterSessions);
        host.Registry.Register(new StorePurchaseWireService(store, sessions, userId, pilot, station.Id));
        var open = new SessionOpen(17, (userId * 1_000_000UL) + 0xFFAA, userId, 0, "127.0.0.1", "EN", "GB");
        await front.SendAsync(open);
        Assert.IsType<Send>(await front.ReceiveAsync());

        var subscriber = host.PublicGateway.Notices.Subscribe(userId);
        subscriber.Character = pilot;
        var (unsupportedCurrency, unsupportedCurrencyNotifications) =
            await StoreWireCallAsync(front, open, 500, "omega_30_days", 1, pilot, "USD");
        Assert.Same(PyNone.Instance, unsupportedCurrency);
        Assert.Empty(unsupportedCurrencyNotifications);
        Assert.Equal(2_500L, await PlexBalanceAsync(host.Registry.Lookup("vaultManager")!,
            new ClientSession { UserId = userId, CharacterId = pilot }, 500));
        Assert.False(subscriber.Frames.Reader.TryRead(out _));

        var (missingOffer, missingOfferNotifications) = await StoreWireCallAsync(
            front, open, 498, "missing_offer", 1, pilot);
        Assert.Same(PyNone.Instance, missingOffer);
        Assert.Empty(missingOfferNotifications);

        var (alphaMctResult, alphaMctNotifications) = await StoreWireCallAsync(
            front, open, 499, "mct_slot_30_days", 1, pilot);
        Assert.Same(PyNone.Instance, alphaMctResult);
        Assert.Empty(alphaMctNotifications);
        Assert.Equal(2_500L, await PlexBalanceAsync(host.Registry.Lookup("vaultManager")!,
            new ClientSession { UserId = userId, CharacterId = pilot }, 500));
        Assert.Empty(await accounts.TrainingSlotsAsync(userId, CancellationToken.None));
        Assert.False(subscriber.Frames.Reader.TryRead(out _));

        var (omegaResult, omegaNotifications) = await StoreWireCallAsync(front, open, 501, "omega_30_days", 1, pilot);
        var omegaResponse = Assert.IsType<PyDict>(omegaResult);
        Assert.Equal(PyBool.True, ValueField(omegaResponse, "success"));
        Assert.Equal(new PyInt(500), ValueField(omegaResponse, "spent"));
        Assert.Equal(new PyInt(2_000), ValueField(omegaResponse, "balance"));
        Assert.Equal(pilot, Integer(ValueField(omegaResponse, "character_id")));
        Assert.Equal(new[] { "OnSubscriptionTimeUpdateServer", "OnSubscriptionChangedServer" }, omegaNotifications);
        Assert.Equal(0L, (await wallets.GetAsync(pilot))!.Plex);
        Assert.Equal(2_000L, (await wallets.GetAsync(alternate))!.Plex);

        var (gatewayType, gatewayPayload, gatewayCharacter) = DecodeGatewayNotice(await subscriber.Frames.Reader.ReadAsync());
        Assert.Equal("type.evetech.net/eve_public.plex.vault.api.BalanceChangedNotice", gatewayType);
        Assert.Equal(pilot, gatewayCharacter);
        var noticeFields = ProtoReader.Fields(gatewayPayload);
        var attributes = ProtoReader.Fields(noticeFields.Single(field => field.Number == 2).Bytes);
        Assert.Equal(unchecked((ulong)(-50_000L)), ProtoReader.Fields(attributes.Single(field => field.Number == 2).Bytes).Single().Scalar);
        Assert.Equal(200_000UL, ProtoReader.Fields(attributes.Single(field => field.Number == 3).Bytes).Single().Scalar);

        var (mctResult, mctNotifications) = await StoreWireCallAsync(front, open, 502, "mct_slot_30_days", 1, pilot);
        Assert.Equal(new PyInt(485), ValueField(Assert.IsType<PyDict>(mctResult), "spent"));
        Assert.Equal(new[] { "OnMultipleCharactersTrainingUpdated" }, mctNotifications);
        Assert.Single(await accounts.TrainingSlotsAsync(userId, CancellationToken.None), slot => slot.Value > DateTimeOffset.UtcNow);
        _ = await subscriber.Frames.Reader.ReadAsync();

        var alternateSink = new Sink();
        sessions.Enter(alternate, new ClientSession(alternateSink)
        {
            SessionId = 19,
            UserId = userId,
            CharacterId = alternate,
        });
        var (itemResult, itemNotifications) = await StoreWireCallAsync(front, open, 503, "pilot_resculpt_certificate", 1, pilot);
        Assert.Equal(new PyInt(100), ValueField(Assert.IsType<PyDict>(itemResult), "spent"));
        Assert.Equal(new[] { "OnRedeemingTokensUpdated" }, itemNotifications);
        Assert.Equal(new[] { ("OnRedeemingTokensUpdated", "userid") },
            Notifications(alternateSink).Select(notification => (notification.Name, notification.IdType)));
        var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        var redeemSession = new ClientSession(new Sink()) { UserId = userId, CharacterId = pilot };
        var redeemService = host.Registry.Lookup("userSvc")!;
        var pendingTokens = Assert.IsType<PyObjectEx>(await redeemService.CallAsync(Call(redeemSession, "GetRedeemTokens", 504, [])));
        var pendingToken = Assert.IsType<PyPackedRow>(Assert.Single(pendingTokens.List)).Fields()
            .ToDictionary(field => field.Key, field => field.Value);
        Assert.Equal(new PyInt(CertificateTypeId), pendingToken["typeID"]);
        Assert.Equal(new PyInt(1), pendingToken["quantity"]);
        Assert.Equal(0, Quantity(await items.OwnedByAsync(pilot), CertificateTypeId));
        _ = await subscriber.Frames.Reader.ReadAsync();

        var (repeatedResult, repeatedNotifications) = await StoreWireCallAsync(front, open, 503, "pilot_resculpt_certificate", 1, pilot);
        AssertSamePurchaseResponse(itemResult, repeatedResult);
        Assert.Empty(repeatedNotifications);
        Assert.Single(Assert.IsType<PyObjectEx>(await redeemService.CallAsync(Call(redeemSession, "GetRedeemTokens", 505, []))).List);
        Assert.Equal(0, Quantity(await items.OwnedByAsync(pilot), CertificateTypeId));
        Assert.Equal(1_415L, await PlexBalanceAsync(host.Registry.Lookup("vaultManager")!, new ClientSession { UserId = userId, CharacterId = pilot }, 504));
        Assert.False(subscriber.Frames.Reader.TryRead(out _));

        var changedRetry = await StoreWireCallAsync(front, open, 503, "skill_extractor_single", 1, pilot);
        Assert.Same(PyNone.Instance, changedRetry.Result);
        Assert.Empty(changedRetry.Notifications);
        Assert.Equal(1_415L, await PlexBalanceAsync(host.Registry.Lookup("vaultManager")!, new ClientSession { UserId = userId, CharacterId = pilot }, 505));

        var (insufficientFunds, insufficientFundsNotifications) =
            await StoreWireCallAsync(front, open, 507, "pilot_resculpt_certificate", 15, pilot);
        Assert.Same(PyNone.Instance, insufficientFunds);
        Assert.Empty(insufficientFundsNotifications);
        Assert.Single(Assert.IsType<PyObjectEx>(await redeemService.CallAsync(Call(redeemSession, "GetRedeemTokens", 506, []))).List);
        Assert.Equal(0, Quantity(await items.OwnedByAsync(pilot), CertificateTypeId));
        Assert.Equal(1_415L, await PlexBalanceAsync(host.Registry.Lookup("vaultManager")!,
            new ClientSession { UserId = userId, CharacterId = pilot }, 509));
        Assert.False(subscriber.Frames.Reader.TryRead(out _));

        var concurrentSink = new Sink();
        var concurrentSession = new ClientSession(concurrentSink) { SessionId = 18, UserId = userId, CharacterId = pilot };
        concurrentSession.Set("stationid", new PyLong(station.Id));
        sessions.Enter(pilot, concurrentSession);
        var concurrentCall = Call(concurrentSession, "buy_offer", 508,
            [new PyStr("pilot_resculpt_certificate"), new PyStr("PLX"), new PyInt(1)],
            Py.Dict(
                ("from_character_id", Py.Number(pilot)),
                ("to_character_id", Py.Number(pilot)),
                ("message", new PyStr("")),
                ("is_game_time", PyBool.True),
                ("store_id", new PyInt(4))));
        var concurrentResults = await Task.WhenAll(
            store.CallAsync(concurrentCall).AsTask(),
            store.CallAsync(concurrentCall).AsTask());
        AssertSamePurchaseResponse(concurrentResults[0], concurrentResults[1]);
        Assert.Equal(new PyInt(1_315), ValueField(Assert.IsType<PyDict>(concurrentResults[0]), "balance"));
        Assert.Equal(new[] { "OnRedeemingTokensUpdated" }, Notifications(concurrentSink).Select(notification => notification.Name));
        Assert.Equal(2, Assert.IsType<PyObjectEx>(await redeemService.CallAsync(Call(redeemSession, "GetRedeemTokens", 510, []))).List.Count);
        Assert.Equal(0, Quantity(await items.OwnedByAsync(pilot), CertificateTypeId));
        Assert.Equal(1_315L, await PlexBalanceAsync(host.Registry.Lookup("vaultManager")!, concurrentSession, 510));
        _ = await subscriber.Frames.Reader.ReadAsync();
        Assert.False(subscriber.Frames.Reader.TryRead(out _));

        var recipientSink = new Sink();
        var recipientSession = new ClientSession(recipientSink)
        {
            SessionId = 20,
            UserId = recipientAccount.AccountId,
            CharacterId = recipient,
        };
        recipientSession.Set("stationID", new PyInt(recipientGiftStation.Id));
        sessions.Enter(recipient, recipientSession);
        var (giftAlphaMctResult, giftAlphaMctNotifications) = await StoreWireCallAsync(
            front, open, 520, "mct_slot_30_days", 1, pilot, targetCharacterId: recipient);
        Assert.Same(PyNone.Instance, giftAlphaMctResult);
        Assert.Empty(giftAlphaMctNotifications);
        Assert.Empty(await accounts.TrainingSlotsAsync(recipientAccount.AccountId, CancellationToken.None));
        Assert.Empty(Notifications(recipientSink));
        Assert.Equal(1_315L, await PlexBalanceAsync(host.Registry.Lookup("vaultManager")!, concurrentSession, 520));
        Assert.False(subscriber.Frames.Reader.TryRead(out _));
        var buyerSubscriptionEnd = accounts.SubscriptionEndsAt(userId);
        var (giftOmegaResult, giftOmegaNotifications) = await StoreWireCallAsync(
            front, open, 509, "omega_30_days", 1, pilot, targetCharacterId: recipient);
        var giftOmegaResponse = Assert.IsType<PyDict>(giftOmegaResult);
        Assert.Equal(new PyInt(500), ValueField(giftOmegaResponse, "spent"));
        Assert.Equal(new PyInt(815), ValueField(giftOmegaResponse, "balance"));
        Assert.Equal(recipient, Integer(ValueField(giftOmegaResponse, "character_id")));
        Assert.Empty(giftOmegaNotifications);
        Assert.Equal(buyerSubscriptionEnd, accounts.SubscriptionEndsAt(userId));
        Assert.True(accounts.SubscriptionEndsAt(recipientAccount.AccountId) > DateTimeOffset.UtcNow);
        Assert.Equal(new[] { "OnSubscriptionTimeUpdateServer", "OnSubscriptionChangedServer" },
            Notifications(recipientSink).Select(notification => notification.Name));

        var (giftMctResult, giftMctNotifications) = await StoreWireCallAsync(
            front, open, 510, "mct_slot_30_days", 1, pilot, targetCharacterId: recipient);
        var giftMctResponse = Assert.IsType<PyDict>(giftMctResult);
        Assert.Equal(new PyInt(485), ValueField(giftMctResponse, "spent"));
        Assert.Equal(new PyInt(330), ValueField(giftMctResponse, "balance"));
        Assert.Equal(recipient, Integer(ValueField(giftMctResponse, "character_id")));
        Assert.Empty(giftMctNotifications);
        Assert.Single(await accounts.TrainingSlotsAsync(recipientAccount.AccountId, CancellationToken.None),
            slot => slot.Value > DateTimeOffset.UtcNow);
        Assert.Equal(new[]
        {
            "OnSubscriptionTimeUpdateServer",
            "OnSubscriptionChangedServer",
            "OnMultipleCharactersTrainingUpdated",
        }, Notifications(recipientSink).Select(notification => notification.Name));

        var (giftItemResult, giftItemNotifications) = await StoreWireCallAsync(
            front, open, 511, "pilot_resculpt_certificate", 1, pilot, targetCharacterId: recipient);
        var giftItemResponse = Assert.IsType<PyDict>(giftItemResult);
        Assert.Equal(new PyInt(100), ValueField(giftItemResponse, "spent"));
        Assert.Equal(new PyInt(230), ValueField(giftItemResponse, "balance"));
        Assert.Equal(recipient, Integer(ValueField(giftItemResponse, "character_id")));
        Assert.Empty(giftItemNotifications);
        var recipientNotifications = Notifications(recipientSink);
        Assert.Equal(new[]
        {
            "OnSubscriptionTimeUpdateServer",
            "OnSubscriptionChangedServer",
            "OnMultipleCharactersTrainingUpdated",
            "OnItemChange",
        }, recipientNotifications.Select(notification => notification.Name));
        var itemChange = Assert.Single(recipientNotifications, notification => notification.Name == "OnItemChange");
        Assert.Equal("clientID", itemChange.IdType);
        var giftedItem = Assert.IsType<PyPackedRow>(itemChange.Args[0]).Fields()
            .ToDictionary(field => field.Key, field => field.Value);
        Assert.Equal(new PyInt(CertificateTypeId), giftedItem["typeID"]);
        Assert.Equal(new PyInt(recipient), giftedItem["ownerID"]);
        Assert.Equal(new PyInt(recipientGiftStation.Id), giftedItem["locationID"]);
        Assert.Equal(new PyInt(1), giftedItem["quantity"]);
        Assert.Equal(1, Quantity(await items.OwnedByAsync(recipient), CertificateTypeId));
        Assert.Empty(Assert.IsType<PyObjectEx>(await redeemService.CallAsync(
            Call(recipientSession, "GetRedeemTokens", 512, []))).List);
        Assert.Equal(2, Assert.IsType<PyObjectEx>(await redeemService.CallAsync(
            Call(redeemSession, "GetRedeemTokens", 513, []))).List.Count);
        Assert.Equal(230L, await PlexBalanceAsync(host.Registry.Lookup("vaultManager")!, concurrentSession, 514));
        _ = await subscriber.Frames.Reader.ReadAsync();
        _ = await subscriber.Frames.Reader.ReadAsync();
        _ = await subscriber.Frames.Reader.ReadAsync();
        Assert.False(subscriber.Frames.Reader.TryRead(out _));

        await stopServer.CancelAsync();
        listener.Dispose();
        await serving;
        front.Dispose();
        _hosts.Remove(host);
        await host.ShutdownAsync();

        var restarted = await BuildHostAsync();
        var restartedStore = restarted.Registry.Lookup("storeManager")!;
        var replaySink = new Sink();
        var replaySession = new ClientSession(replaySink) { UserId = userId, CharacterId = pilot };
        var replay = await restartedStore.CallAsync(Call(replaySession, "buy_offer", 503,
            [new PyStr("pilot_resculpt_certificate"), new PyStr("PLX"), new PyInt(1)],
            Py.Dict(
                ("from_character_id", Py.Number(pilot)),
                ("to_character_id", Py.Number(pilot)),
                ("message", new PyStr("")),
                ("is_game_time", PyBool.True),
                ("store_id", new PyInt(4)))));
        AssertSamePurchaseResponse(itemResult, MarshalCodec.Decode(MarshalCodec.Encode(replay)));
        Assert.Empty(replaySink.Packets);
        Assert.Equal(2, Assert.IsType<PyObjectEx>(await restarted.Registry.Lookup("userSvc")!.CallAsync(
            Call(replaySession, "GetRedeemTokens", 511, []))).List.Count);
        Assert.Equal(0, Quantity(await items.OwnedByAsync(pilot), CertificateTypeId));

        var giftReplay = await restartedStore.CallAsync(Call(replaySession, "buy_offer", 511,
            [new PyStr("pilot_resculpt_certificate"), new PyStr("PLX"), new PyInt(1)],
            Py.Dict(
                ("from_character_id", Py.Number(pilot)),
                ("to_character_id", Py.Number(recipient)),
                ("message", new PyStr("")),
                ("is_game_time", PyBool.True),
                ("store_id", new PyInt(4)))));
        AssertSamePurchaseResponse(giftItemResult, MarshalCodec.Decode(MarshalCodec.Encode(giftReplay)));
        Assert.Empty(Assert.IsType<PyObjectEx>(await restarted.Registry.Lookup("userSvc")!.CallAsync(
            Call(new ClientSession { UserId = recipientAccount.AccountId, CharacterId = recipient },
                "GetRedeemTokens", 515, []))).List);
        Assert.Equal(1, Quantity(await items.OwnedByAsync(recipient), CertificateTypeId));

        var restartedRedeem = restarted.Registry.Lookup("userSvc")!;
        var tokensToClaim = Assert.IsType<PyObjectEx>(await restartedRedeem.CallAsync(
            Call(replaySession, "GetRedeemTokens", 512, [])));
        var tokenClaims = tokensToClaim.List.Select(token =>
        {
            var fields = Assert.IsType<PyPackedRow>(token).Fields().ToDictionary(field => field.Key, field => field.Value);
            return (PyValue)Py.Dict(
                ("tokenID", fields["tokenID"]),
                ("massTokenID", fields["massTokenID"]),
                ("typeID", fields["typeID"]));
        }).ToArray();
        await restartedRedeem.CallAsync(Call(replaySession, "ClaimRedeemTokens", 513,
            [new PyList(tokenClaims), new PyLong(pilot)], Py.Dict(("useHomeStation", PyBool.True))));
        Assert.Equal(2, Quantity(await items.OwnedByAsync(pilot), CertificateTypeId));
        Assert.Empty(Assert.IsType<PyObjectEx>(await restartedRedeem.CallAsync(
            Call(replaySession, "GetRedeemTokens", 514, []))).List);
        Assert.Equal(2, Quantity(await items.OwnedByAsync(pilot), CertificateTypeId));
        Assert.Empty(Assert.IsType<PyObjectEx>(await restartedRedeem.CallAsync(
            Call(new ClientSession { UserId = recipientAccount.AccountId, CharacterId = recipient },
                "GetRedeemTokens", 518, []))).List);
        Assert.Equal(1, Quantity(await items.OwnedByAsync(recipient), CertificateTypeId));
        Assert.Equal(230L, await PlexBalanceAsync(restarted.Registry.Lookup("vaultManager")!, replaySession, 506));
    }

    [Fact]
    public void The_client_global_config_carries_the_store_keys_with_the_values_eve_js_serves()
    {
        var entries = GlobalConfig.Build(new ClusterOptions()).Entries
            .ToDictionary(entry => JsSemantics.StringValue(entry.Key) ?? "", entry => entry.Value);

        Assert.Equal(new PyStr("0"), entries["fake_fast_checkout_enabled"]);
        Assert.Equal(new PyStr("FAST_CHECKOUT_UNAVAILABLE"), entries["fake_fast_checkout_response"]);
        Assert.Equal(new PyStr("0"), entries["fake_vgs_enabled"]);
        Assert.Equal(new PyStr("0"), entries["fake_china_funnel_enabled"]);
        Assert.Equal(new PyStr(""), entries["fake_buy_plex_offer_url"]);
        Assert.Equal(new PyStr("1"), entries["use_shell_execute_to_buy_plex_offer"]);
        Assert.Equal(new PyStr("[]"), entries["fake_fast_checkout_plex_offers"]);
    }

    private static ServiceCall FastCheckoutBuy(ClientSession session, long offerId, long callId) => new()
    {
        Method = "BuyOffer",
        Args = [],
        Kwargs = Py.Dict(
            ("offer", Py.KeyVal(("id", new PyInt(offerId)))),
            ("password", new PyStr("not checked, as in EVE.js")),
            ("purchaseTraceID", new PyStr("trace")),
            ("journeyID", new PyStr("journey"))),
        Session = session,
        CallId = new PyInt(callId),
    };

    private static PyValue ValueField(PyDict fields, string name) =>
        fields.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;

    private static void AssertSamePurchaseResponse(PyValue expected, PyValue actual)
    {
        var expectedFields = Assert.IsType<PyDict>(expected);
        var actualFields = Assert.IsType<PyDict>(actual);
        Assert.Equal(expectedFields.Entries.Count, actualFields.Entries.Count);
        foreach (var entry in expectedFields.Entries)
        {
            var name = JsSemantics.StringValue(entry.Key);
            Assert.NotNull(name);
            Assert.Equal(entry.Value, ValueField(actualFields, name!));
        }
    }

    private static string Field(PyDict fields, string name) =>
        JsSemantics.StringValue(fields.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value) ?? "";
}
