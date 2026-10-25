using Eve.Characters;
using Eve.Data;
using Eve.Identity;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Sde;
using Eve.Universe;
using Eve.Trade;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>
/// The New Eden Store as the real host composition registers it (HostComposition.BuildAsync), on scratch PostgreSQL schemas: a purchase
/// requires an atomic payment settlement provider; store checkout paths are disabled until one is connected.
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
        public void Send(PyValue packet) => _packets.Enqueue(packet);
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);

    private static ServiceCall Call(ClientSession session, string method, long callId, PyValue[] args, PyDict? kwargs = null) =>
        new() { Session = session, Method = method, Args = args, Kwargs = kwargs ?? new PyDict([]), CallId = new PyInt(callId) };

    /// <summary>A character with a wallet at the station: the account's PLEX balance starts at zero.</summary>
    private async Task<long> CreatePilotAsync(NpgsqlDataSource source, Station station, string name)
    {
        await using var c = await source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var actor = await new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA")).InsertAsync(c, tx,
            new NewCharacter(1, name, 0, 1, 1, 1, 11, 1376, 1000044, null,
                station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId, null, null));
        await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).OpenAsync(c, tx, actor, 10000m, 0);
        await tx.CommitAsync();
        return actor;
    }

    private async Task<long> PlexBalanceAsync(IService vault, ClientSession session, long callId) =>
        Assert.IsType<PyLong>(await vault.CallAsync(Call(session, "get_account_balance", callId, [new PyStr("PLX")]))).Value;

    private static long Quantity(IReadOnlyList<Item> items, int typeId) =>
        items.Where(item => item.TypeId == typeId).Sum(item => item.Quantity);

    [PgFact]
    public async Task A_certificate_bought_for_plx_is_granted_into_the_hangar_at_no_charge_and_a_replay_grants_nothing()
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
        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(async () => await store.CallAsync(Call(session, "buy_offer", 7,
            [new PyStr("pilot_resculpt_certificate"), new PyStr("PLX"), new PyInt(1)])));
        Assert.Equal("STORE_CHECKOUT_UNAVAILABLE", refusal.Error.UserErrorMessage);
        Assert.Equal(before, Quantity(await items.OwnedByAsync(pilot), CertificateTypeId));
        Assert.Equal(0, await PlexBalanceAsync(vault, session, 2));
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
    public async Task Store_purchases_in_other_currencies_are_granted_free_and_quantity_and_currency_are_validated()
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
            var refusal = await Assert.ThrowsAsync<MachoWrappedException>(async () => await store.CallAsync(
                Call(session, "buy_offer", callId, [new PyStr("pilot_resculpt_certificate"), new PyStr(currency), new PyInt(quantity)])));
            Assert.Equal("STORE_CHECKOUT_UNAVAILABLE", refusal.Error.UserErrorMessage);
        }
        Assert.Equal(0, Quantity(await items.OwnedByAsync(pilot), CertificateTypeId));
        Assert.Equal(0, await PlexBalanceAsync(vault, session, 35));
    }

    [PgFact]
    public async Task A_real_money_bundle_is_granted_in_full_at_no_charge()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var host = await BuildHostAsync();
        var store = host.Registry.Lookup("storeManager")!;
        var vault = host.Registry.Lookup("vaultManager")!;
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var pilot = await CreatePilotAsync(source, station, "Bundle Pilot");
        var session = new ClientSession(new Sink()) { SessionId = 404, UserId = 1, CharacterId = pilot };
        var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(async () => await store.CallAsync(
            Call(session, "buy_offer", 43, [new PyStr("cash_plex_1000_gallente"), new PyStr("USD"), new PyInt(2)], Py.Dict(("store_id", new PyInt(9))))));
        Assert.Equal("STORE_CHECKOUT_UNAVAILABLE", refusal.Error.UserErrorMessage);
        Assert.Equal(0, await PlexBalanceAsync(vault, session, 44));
        Assert.Equal(0, Quantity(await items.OwnedByAsync(pilot), CrateTypeId));
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

    private static string Field(PyDict fields, string name) =>
        JsSemantics.StringValue(fields.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value) ?? "";
}
