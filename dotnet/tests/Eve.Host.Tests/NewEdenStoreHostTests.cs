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
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>
/// The New Eden Store as the real host composition registers it (HostComposition.BuildAsync), on scratch PostgreSQL schemas: a purchase
/// is granted at no charge (the vault, the hangar), a repeated call grants nothing, and the client's FastCheckoutService answers "OK".
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
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
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
        public void Send(PyValue packet)
        {
        }
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
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url));
        var host = await BuildHostAsync();
        var store = host.Registry.Lookup("storeManager") ?? throw new InvalidOperationException("storeManager is not registered");
        var vault = host.Registry.Lookup("vaultManager") ?? throw new InvalidOperationException("vaultManager is not registered");
        var stations = await StationCatalog.LoadAsync(new SdeReader(source));
        var station = stations.All.First();
        var pilot = await CreatePilotAsync(source, station, "Store Pilot");
        var session = new ClientSession(new Sink()) { SessionId = 401, UserId = 1, CharacterId = pilot };
        var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));

        Assert.Equal(0, await PlexBalanceAsync(vault, session, 1));
        var result = await store.CallAsync(Call(session, "buy_offer", 7, [new PyStr("pilot_resculpt_certificate"), new PyStr("PLX"), new PyInt(1)]));

        Assert.Equal(PyBool.True, result);
        Assert.Equal(0, await PlexBalanceAsync(vault, session, 2));
        Assert.Equal(1, Quantity(await items.OwnedByAsync(pilot), CertificateTypeId));
        Assert.Equal(station.Id, (await items.OwnedByAsync(pilot)).Single(item => item.TypeId == CertificateTypeId).LocationId);

        var replay = await store.CallAsync(Call(session, "buy_offer", 7, [new PyStr("pilot_resculpt_certificate"), new PyStr("PLX"), new PyInt(1)]));
        Assert.Equal(PyBool.True, replay);
        Assert.Equal(1, Quantity(await items.OwnedByAsync(pilot), CertificateTypeId));
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
            Assert.Equal(PyBool.True, await service.CallAsync(Call(session, "claim_reward", 4, [])));
            Assert.Equal(PyBool.False, await service.CallAsync(Call(session, "claim_reward", 5, [])));
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
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url));
        var host = await BuildHostAsync();
        var checkout = host.Registry.Lookup("FastCheckoutService") ?? throw new InvalidOperationException("FastCheckoutService is not registered");
        var vault = host.Registry.Lookup("vaultManager") ?? throw new InvalidOperationException("vaultManager is not registered");
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var pilot = await CreatePilotAsync(source, station, "Checkout Pilot");
        var session = new ClientSession(new Sink()) { SessionId = 402, UserId = 1, CharacterId = pilot };

        var answer = Assert.IsType<PyDict>(await checkout.CallAsync(FastCheckoutBuy(session, UsdPlexPackOfferId, callId: 21)));
        Assert.Equal(new PyStr("OK"), answer.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == "Message").Value);
        Assert.Equal(100, await PlexBalanceAsync(vault, session, 3));

        await checkout.CallAsync(FastCheckoutBuy(session, UsdPlexPackOfferId, callId: 21));
        Assert.Equal(100, await PlexBalanceAsync(vault, session, 4));
    }

    [PgFact]
    public async Task Store_purchases_in_other_currencies_are_granted_free_and_quantity_and_currency_are_validated()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url));
        var host = await BuildHostAsync();
        var store = host.Registry.Lookup("storeManager") ?? throw new InvalidOperationException("storeManager is not registered");
        var vault = host.Registry.Lookup("vaultManager") ?? throw new InvalidOperationException("vaultManager is not registered");
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var pilot = await CreatePilotAsync(source, station, "Currency Pilot");
        var session = new ClientSession(new Sink()) { SessionId = 403, UserId = 1, CharacterId = pilot };
        var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));

        await store.CallAsync(Call(session, "buy_offer", 31, [new PyStr("pilot_resculpt_certificate"), new PyStr("AUR"), new PyInt(2)]));
        Assert.Equal(2, Quantity(await items.OwnedByAsync(pilot), CertificateTypeId));
        Assert.Equal(0, await PlexBalanceAsync(vault, session, 5));

        await Assert.ThrowsAsync<NotSupportedException>(async () => await store.CallAsync(
            Call(session, "buy_offer", 32, [new PyStr("pilot_resculpt_certificate"), new PyStr("XYZ"), new PyInt(1)])));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.CallAsync(
            Call(session, "buy_offer", 33, [new PyStr("pilot_resculpt_certificate"), new PyStr("PLX"), new PyInt(101)])));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.CallAsync(
            Call(session, "buy_offer", 34, [new PyStr("pilot_resculpt_certificate"), new PyStr("PLX"), new PyInt(0)])));
        Assert.Equal(2, Quantity(await items.OwnedByAsync(pilot), CertificateTypeId));
    }

    [PgFact]
    public async Task A_real_money_bundle_is_granted_in_full_at_no_charge()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url));
        var host = await BuildHostAsync();
        var store = host.Registry.Lookup("storeManager") ?? throw new InvalidOperationException("storeManager is not registered");
        var vault = host.Registry.Lookup("vaultManager") ?? throw new InvalidOperationException("vaultManager is not registered");
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var pilot = await CreatePilotAsync(source, station, "Bundle Pilot");
        var session = new ClientSession(new Sink()) { SessionId = 404, UserId = 1, CharacterId = pilot };
        var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));

        var nineOffers = Assert.IsType<PyList>(await store.CallAsync(Call(session, "get_offers", 42, [new PyInt(9)]))).Items;
        var bundleFields = nineOffers.OfType<PyDict>()
            .Single(fields => Field(fields, "storeOfferID") == "cash_plex_1000_gallente");
        var offerId = bundleFields.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == "id").Value;
        var kwargs = Py.Dict(("store_id", new PyInt(9)));
        Assert.Equal(PyBool.True, await store.CallAsync(Call(session, "buy_offer", 43, [offerId, new PyStr("USD"), new PyInt(2)], kwargs)));

        Assert.Equal(2000, await PlexBalanceAsync(vault, session, 7));
        Assert.Equal(2, Quantity(await items.OwnedByAsync(pilot), CrateTypeId));
        Assert.Equal(0, Quantity(await items.OwnedByAsync(pilot), PlexTypeId));
    }

    [Fact]
    public void The_client_global_config_carries_the_store_keys_with_the_values_eve_js_serves()
    {
        var entries = GlobalConfig.Build(new ClusterOptions()).Entries
            .ToDictionary(entry => JsSemantics.StringValue(entry.Key) ?? "", entry => entry.Value);

        Assert.Equal(new PyStr("1"), entries["fake_fast_checkout_enabled"]);
        Assert.Equal(new PyStr("OK"), entries["fake_fast_checkout_response"]);
        Assert.Equal(new PyStr("1"), entries["fake_vgs_enabled"]);
        Assert.Equal(new PyStr("0"), entries["fake_china_funnel_enabled"]);
        Assert.Equal(new PyStr(""), entries["fake_buy_plex_offer_url"]);
        Assert.Equal(new PyStr("1"), entries["use_shell_execute_to_buy_plex_offer"]);
        Assert.Equal(new PyStr("[[100,\"USD\",5],[200,\"USD\",10],[500,\"USD\",25],[1000,\"USD\",45],[1500,\"USD\",65],[3000,\"USD\",125],[6000,\"USD\",240],[12000,\"USD\",420],[20000,\"USD\",650]]"),
            entries["fake_fast_checkout_plex_offers"]);
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
