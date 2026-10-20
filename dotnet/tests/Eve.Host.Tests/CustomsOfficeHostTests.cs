using Eve.Characters;
using Eve.Identity;
using Eve.Data;
using Eve.Marshal;
using Eve.Planets;
using Eve.Services;
using Eve.Services.Planets;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class CustomsOfficeHostTests : IAsyncLifetime
{
    private const long CorporationId = 98_000_010;
    private const int AccountId = 98_000_011;
    private const int Jita = 30_000_142;
    private readonly string _prefix = "co" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _schemas = [];

    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA", "EVE_TRADE_SCHEMA", "EVE_BOOKMARKS_SCHEMA", "EVE_ESS_SCHEMA",
        "EVE_CONTRABAND_SCHEMA", "EVE_PLANETS_SCHEMA",
    ];

    private static string Url => Environment.GetEnvironmentVariable("EVE_PG_URL")!;
    private string Schema(string variable) => $"{_prefix}_{variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant()}";

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
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is { } url)
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

    [PgFact]
    public async Task Customs_office_rates_are_written_through_the_host_and_survive_restart()
        {
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var pilot = await InsertPilotAsync(source, characters);
            var planetId = await ScalarAsync<long>(source, """
                SELECT p.key::bigint
                FROM sde."mapPlanets" p JOIN sde."mapStars" s ON p.doc->>'orbitID' = s.key
                WHERE s.doc->>'solarSystemID' = '30000142'
                ORDER BY p.key LIMIT 1
                """);
            var officeId = Customs.OfficeId(planetId);
            var beforeRestart = await HostTaxRateAsync(host, pilot, officeId);
            await using (var owner = source.CreateCommand(
                $"UPDATE {Schema("EVE_INVENTORY_SCHEMA")}.items SET owner_id = {CorporationId} WHERE id = {officeId}"))
                Assert.Equal(1, await owner.ExecuteNonQueryAsync());
            await UpdateOfficeRatesAsync(host, pilot, officeId, 0.03);
            Assert.Equal(0.10, beforeRestart, 6);
            Assert.Equal(0.13, await HostTaxRateAsync(host, pilot, officeId), 6);
            await host.ShutdownAsync();

            host = await HostComposition.BuildAsync(Settings());
            Assert.Equal(0.13, await HostTaxRateAsync(host, pilot, officeId), 6);
            Assert.Equal(1L, await ScalarAsync<long>(source,
                $"SELECT count(*) FROM {Schema("EVE_PLANETS_SCHEMA")}.customs_offices WHERE office_id = {officeId} AND owner_corporation_id = {CorporationId}"));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }
    [PgFact]
    public async Task A_customs_office_tax_credit_journals_corporation_income_and_survives_host_restart()
    {
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var pilot = await InsertPilotAsync(source, characters);
            var wallets = new WalletTransactions(source, Schema("EVE_WALLET_SCHEMA"));
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await wallets.EnsureAsync(connection, transaction, CorporationId);
                await wallets.AdjustAsync(connection, transaction, CorporationId, 17.25m, pilot, 97,
                    "planetary customs office tax", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), $"poco-tax-test:{Guid.NewGuid():N}");
                await transaction.CommitAsync();
            }

            await host.ShutdownAsync();
            host = await HostComposition.BuildAsync(Settings());
            var reopenedWallets = new WalletTransactions(source, Schema("EVE_WALLET_SCHEMA"));
            Assert.Equal(17.25m, await ScalarAsync<decimal>(source,
                $"SELECT isk FROM {Schema("EVE_WALLET_SCHEMA")}.balances WHERE owner_id = {CorporationId} AND account_key = 1000"));
            var entry = Assert.Single(await reopenedWallets.JournalForAccountAsync(CorporationId, 1000),
                row => row.Reason == "planetary customs office tax");
            Assert.Equal(17.25m, entry.Amount);
            Assert.Equal(97, entry.RefTypeId);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    private static async Task<long> InsertPilotAsync(NpgsqlDataSource source, PgCharacterStore characters)
    {
        var pilot = new NewCharacter(AccountId, "Customs Host Pilot", Gender: 1, RaceId: 1, BloodlineId: 1, AncestryId: 10, SchoolId: 35, TypeId: 1373,
            CorporationId: CorporationId, FactionId: 500001, StationId: 60000610, SolarSystemId: Jita, ConstellationId: 20000020, RegionId: 10000002,
            DollInfo: null, PortraitInfo: null);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await characters.InsertAsync(connection, transaction, pilot);
        await transaction.CommitAsync();
        return id;
    }

    private static async Task UpdateOfficeRatesAsync(HostRuntime host, long pilot, long officeId, double rate)
    {
        var service = host.Registry.Lookup("planetOrbitalRegistryBroker")!;
        var session = Session(pilot);
        session.Set("corprole", new PyInt(1));
        var rates = Py.Dict(
            ("corporation", new PyReal(rate)), ("alliance", new PyReal(0.05)),
            ("standingHigh", new PyReal(0.05)), ("standingGood", new PyReal(0.05)),
            ("standingNeutral", new PyReal(0.05)), ("standingBad", new PyReal(0.05)),
            ("standingHorrible", new PyReal(0.05)));
        await service.CallAsync(new ServiceCall
        {
            Method = "UpdateSettings",
            Args = [new PyLong(officeId), new PyInt(0), rates, new PyInt(0), PyBool.True, PyBool.True, PyNone.Instance],
            Session = session,
        });
    }

    private static async Task<double> HostTaxRateAsync(HostRuntime host, long pilot, long officeId)
    {
        var service = host.Registry.Lookup("planetOrbitalRegistryBroker")!;
        var result = await service.CallAsync(new ServiceCall
        {
            Method = "GetTaxRate", Args = [new PyLong(officeId)], Session = Session(pilot),
        });
        return Assert.IsType<PyReal>(result).Value;
    }

    private static ClientSession Session(long pilot)
    {
        var session = new ClientSession { CharacterId = pilot };
        session.Set("corpid", new PyLong(CorporationId));
        session.Set("solarsystemid2", new PyLong(Jita));
        return session;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);
}
