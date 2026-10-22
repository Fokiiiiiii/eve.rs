using Eve.Characters;
using Eve.Identity;
using Eve.Data;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Planets;
using Eve.Services;
using Eve.Services.Inventory;
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
            var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
            const long planetId = 40486618;
            const long shipId = 98_000_012;
            const int commandCenterType = 2524;
            const int commandCenterUpgradeType = 2505;
            const int consolidationType = 2495;
            const int launchpadType = 2544;
            const int waterType = 3645;
            long commandCenterId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await wallets.EnsureAsync(connection, transaction, pilot);
                await wallets.AdjustAsync(connection, transaction, pilot, 10_000_000m, 0, 0,
                    "planetary customs test funds", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), $"poco-test-funds:{Guid.NewGuid():N}");
                await wallets.EnsureAsync(connection, transaction, CorporationId);
                commandCenterId = await items.CreateAsync(connection, transaction,
                    new NewItem(commandCenterType, pilot, shipId, 5, Singleton: true));
                await using var skills = new NpgsqlCommand($"""
                    INSERT INTO {Schema("EVE_SKILLS_SCHEMA")}.character_skills (character_id, type_id, level, skill_points)
                    VALUES ({pilot}, {consolidationType}, 1, 0), ({pilot}, {commandCenterUpgradeType}, 5, 0)
                    """, connection, transaction);
                await skills.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }

            var session = Session(pilot);
            var planetMgr = host.Registry.Lookup("planetMgr")!;
            async Task UpdateNetwork(params PyValue[] changes)
            {
                var nested = PyTuple.Of(new PyStr("UserUpdateNetwork"), PyTuple.Of(PyList.Of(changes)), PyNone.Instance);
                await planetMgr.CallAsync(new ServiceCall
                {
                    Method = "MachoBindObject",
                    Args = [new PyLong(planetId), nested],
                    Session = session,
                });
            }

            await UpdateNetwork(PyTuple.Of(new PyInt(1),
                PyTuple.Of(new PyLong(commandCenterId), new PyInt(commandCenterType), new PyReal(0.5), new PyReal(1.0))));
            await UpdateNetwork(PyTuple.Of(new PyInt(9), PyTuple.Of(new PyLong(commandCenterId), new PyInt(5))));
            await UpdateNetwork(PyTuple.Of(new PyInt(1),
                PyTuple.Of(PyTuple.Of(new PyInt(1), new PyInt(3)), new PyInt(launchpadType), new PyReal(0.5), new PyReal(1.02))));

            var officeId = Customs.OfficeId(planetId);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using (var owner = new NpgsqlCommand(
                    $"UPDATE {Schema("EVE_INVENTORY_SCHEMA")}.items SET owner_id = $1 WHERE id = $2", connection, transaction)
                {
                    Parameters = { new() { Value = CorporationId }, new() { Value = officeId } },
                })
                {
                    Assert.Equal(1, await owner.ExecuteNonQueryAsync());
                }
                await using (var policy = new NpgsqlCommand(
                    $"UPDATE {Schema("EVE_PLANETS_SCHEMA")}.customs_offices SET owner_corporation_id = $1, tax_rates = $2::jsonb WHERE office_id = $3",
                    connection, transaction)
                {
                    Parameters =
                    {
                        new() { Value = CorporationId },
                        new() { Value = """{"corporation":0.05,"alliance":0.05,"standingHigh":0.05,"standingGood":0.05,"standingNeutral":0.05,"standingBad":0.05,"standingHorrible":0.05}""" },
                        new() { Value = officeId },
                    },
                })
                {
                    Assert.Equal(1, await policy.ExecuteNonQueryAsync());
                }
                await transaction.CommitAsync();
            }

            var padId = await ScalarAsync<long>(source, $"""
                SELECT (pin->>'Id')::bigint
                FROM {Schema("EVE_PLANETS_SCHEMA")}.colonies
                CROSS JOIN LATERAL jsonb_array_elements(doc->'Pins') AS pins(pin)
                WHERE owner_id = {pilot} AND planet_id = {planetId} AND (pin->>'TypeId')::integer = {launchpadType}
                """);
            long stockId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                stockId = await items.CreateAsync(connection, transaction, new NewItem(waterType, pilot, officeId, 4, Quantity: 2));
                await transaction.CommitAsync();
            }
            var registry = host.Registry.Lookup("planetOrbitalRegistryBroker")!;
            var rate = Assert.IsType<PyReal>(await registry.CallAsync(new ServiceCall
            {
                Method = "GetTaxRate",
                Args = [new PyLong(officeId)],
                Session = session,
            })).Value;
            Assert.Equal(0.05, rate, 6);

            var customs = Assert.IsAssignableFrom<ICustomsOffices>(planetMgr);
            var account = host.Registry.Lookup("account")!;
            var cashBeforeSettlement = Assert.IsType<PyReal>(await account.CallAsync(new ServiceCall
            {
                Method = "GetCashBalance",
                Args = [new PyInt(0)],
                Session = session,
            })).Value;
            await customs.ImportExportAsync(session, officeId, padId, new Dictionary<long, long> { [stockId] = 1 },
                new Dictionary<int, long>(), rate, CancellationToken.None);
            await customs.ImportExportAsync(session, officeId, padId, new Dictionary<long, long>(),
                new Dictionary<int, long> { [waterType] = 1 }, rate, CancellationToken.None);

            var cash = Assert.IsType<PyReal>(await host.Registry.Lookup("account")!.CallAsync(new ServiceCall
            {
                Method = "GetCashBalance",
                Args = [new PyInt(0)],
                Session = session,
            })).Value;
            Assert.Equal(cashBeforeSettlement - 30, cash, 2);
            await host.ShutdownAsync();
            host = await HostComposition.BuildAsync(Settings());

            var reopenedWallets = new WalletTransactions(source, Schema("EVE_WALLET_SCHEMA"));
            Assert.Equal(30m, await ScalarAsync<decimal>(source,
                $"SELECT isk FROM {Schema("EVE_WALLET_SCHEMA")}.balances WHERE owner_id = {CorporationId} AND account_key = 1000"));
            var ownerEntries = (await reopenedWallets.JournalForAccountAsync(CorporationId, 1000))
                .Where(row => row.Reason == "planetary customs office tax").ToArray();
            Assert.Equal(2, ownerEntries.Length);
            Assert.Equal(30m, ownerEntries.Sum(row => row.Amount));
            Assert.All(ownerEntries, row => Assert.Equal(97, row.RefTypeId));
            Assert.Equal(cashBeforeSettlement - 30, Assert.IsType<PyReal>(await host.Registry.Lookup("account")!.CallAsync(new ServiceCall
            {
                Method = "GetCashBalance",
                Args = [new PyInt(0)],
                Session = session,
            })).Value, 2);
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
