using Eve.Characters;
using Eve.Corporations;
using Eve.Identity;
using Eve.Data;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Planets;
using Eve.Services;
using Eve.Services.Inventory;
using Eve.Services.Planets;
using Eve.Services.Ships;
using Eve.Skills;
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
            var wallets = new WalletTransactions(source, Schema("EVE_WALLET_SCHEMA"));
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await wallets.EnsureAsync(connection, transaction, pilot);
                await wallets.AdjustAsync(connection, transaction, pilot, 10_000_000m, 0, 0,
                    "customs office authority test funds", nowMs, $"customs-authority-funds:{Guid.NewGuid():N}");
                await transaction.CommitAsync();
            }
            var corporationStore = new CorporationStore(source, characters,
                new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")), wallets,
                new PgSkillStore(source, Schema("EVE_SKILLS_SCHEMA")), () => nowMs, Schema("EVE_CORPORATIONS_SCHEMA"));
            var corporation = await corporationStore.CreateAsync(pilot,
                new NewCorporation("Customs Authority Corporation", "CUSAU", "", "", 0.05m),
                $"customs-authority:{Guid.NewGuid():N}");
            var corporationId = corporation.Id;
            var nonDirector = await InsertPilotAsync(source, characters);
            var planetId = await ScalarAsync<long>(source, """
                SELECT p.key::bigint
                FROM sde."mapPlanets" p JOIN sde."mapStars" s ON p.doc->>'orbitID' = s.key
                WHERE s.doc->>'solarSystemID' = '30000142'
                ORDER BY p.key LIMIT 1
                """);
            var officeId = Customs.OfficeId(planetId);
            var beforeRestart = await HostTaxRateAsync(host, pilot, officeId);
            await using (var owner = source.CreateCommand(
                $"UPDATE {Schema("EVE_INVENTORY_SCHEMA")}.items SET owner_id = {corporationId} WHERE id = {officeId}"))
                Assert.Equal(1, await owner.ExecuteNonQueryAsync());
            var denied = await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(
                () => UpdateOfficeRatesAsync(host, nonDirector, corporationId, officeId, 0.03));
            Assert.Equal("CustomsOfficeAccessDenied", denied.Error.UserErrorMessage);
            var overLimit = await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(
                () => UpdateOfficeRatesAsync(host, pilot, corporationId, officeId, 1.01));
            Assert.Equal("CustomsOfficeAccessDenied", overLimit.Error.UserErrorMessage);
            var underLimit = await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(
                () => UpdateOfficeRatesAsync(host, pilot, corporationId, officeId, -0.01));
            Assert.Equal("CustomsOfficeAccessDenied", underLimit.Error.UserErrorMessage);
            await UpdateOfficeRatesAsync(host, pilot, corporationId, officeId, 1.0);
            Assert.Equal(0.10, beforeRestart, 6);
            var rate = await HostTaxRateAsync(host, pilot, officeId);
            Assert.Equal(1.10, rate, 6);

            var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
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
            session.Set("corpid", new PyLong(corporationId));
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

            var customs = Assert.IsAssignableFrom<ICustomsOffices>(planetMgr);
            var office = await items.GetAsync(officeId) ?? throw new InvalidOperationException("customs office item is missing");
            var inventory = new InvBrokerService(items, null!, null!, null!, null!, () => nowMs, new CustomsOfficeTestSpace(office))
            {
                Customs = customs,
            };
            var customerFeesBefore = (await wallets.JournalForAccountAsync(pilot, 1000))
                .Where(row => row.Reason == "planetary customs fee").Sum(row => row.Amount);
            var ownerCreditsBefore = (await wallets.JournalForAccountAsync(corporationId, 1000))
                .Where(row => row.Reason == "planetary customs office tax").Sum(row => row.Amount);
            await ImportExportWithPlanetAsync(inventory, session, officeId, padId,
                new PyDict([new KeyValuePair<PyValue, PyValue>(new PyLong(stockId), new PyLong(1))]),
                new PyDict([new KeyValuePair<PyValue, PyValue>(new PyInt(waterType), new PyLong(1))]), rate);
            var customerFeesAfter = (await wallets.JournalForAccountAsync(pilot, 1000))
                .Where(row => row.Reason == "planetary customs fee").Sum(row => row.Amount);
            var ownerCreditsAfter = (await wallets.JournalForAccountAsync(corporationId, 1000))
                .Where(row => row.Reason == "planetary customs office tax").Sum(row => row.Amount);
            var customerTax = Math.Abs(customerFeesAfter - customerFeesBefore);
            var ownerTax = ownerCreditsAfter - ownerCreditsBefore;
            Assert.True(customerTax > ownerTax);
            Assert.InRange(Math.Abs(customerTax - ownerTax * 1.10m), 0m, 2.20m);
            await host.ShutdownAsync();

            host = await HostComposition.BuildAsync(Settings());
            Assert.Equal(rate, await HostTaxRateAsync(host, pilot, officeId), 6);
            Assert.Equal(1L, await ScalarAsync<long>(source,
                $"SELECT count(*) FROM {Schema("EVE_PLANETS_SCHEMA")}.customs_offices WHERE office_id = {officeId} AND owner_corporation_id = {corporationId}"));
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
            var ownerFounder = await InsertPilotAsync(source, characters);
            var wallets = new WalletTransactions(source, Schema("EVE_WALLET_SCHEMA"));
            var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            const long planetId = 40486618;
            const long shipId = 98_000_012;
            const long ownerAllianceId = 98_000_020;
            const long visitorAllianceId = 98_000_021;
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
                await wallets.EnsureAsync(connection, transaction, ownerFounder);
                await wallets.AdjustAsync(connection, transaction, ownerFounder, 2_000_000m, 0, 0,
                    "planetary customs corporation funds", nowMs, $"poco-corporation-funds:{Guid.NewGuid():N}");
                commandCenterId = await items.CreateAsync(connection, transaction,
                    new NewItem(commandCenterType, pilot, shipId, 5, Singleton: true));
                await using var skills = new NpgsqlCommand($"""
                    INSERT INTO {Schema("EVE_SKILLS_SCHEMA")}.character_skills (character_id, type_id, level, skill_points)
                    VALUES ({pilot}, {consolidationType}, 1, 0), ({pilot}, {commandCenterUpgradeType}, 5, 0)
                    """, connection, transaction);
                await skills.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }

            var corporationStore = new CorporationStore(source, characters,
                new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")), wallets,
                new PgSkillStore(source, Schema("EVE_SKILLS_SCHEMA")), () => nowMs, Schema("EVE_CORPORATIONS_SCHEMA"));
            var visitorCorporation = await corporationStore.CreateAsync(pilot,
                new NewCorporation("Customs Visitor Corporation", "PCVST", "", "", 0.05m),
                $"poco-visitor:{Guid.NewGuid():N}");
            var ownerCorporation = await corporationStore.CreateAsync(ownerFounder,
                new NewCorporation("Customs Owner Corporation", "PCOWN", "", "", 0.05m),
                $"poco-owner:{Guid.NewGuid():N}");

            var session = Session(pilot);
            session.Set("corpid", new PyLong(visitorCorporation.Id));
            var planetMgr = host.Registry.Lookup("planetMgr")!;
            var customs = Assert.IsAssignableFrom<ICustomsOffices>(planetMgr);
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
                    Parameters = { new() { Value = ownerCorporation.Id }, new() { Value = officeId } },
                })
                {
                    Assert.Equal(1, await owner.ExecuteNonQueryAsync());
                }
                await using (var policy = new NpgsqlCommand(
                    $"UPDATE {Schema("EVE_PLANETS_SCHEMA")}.customs_offices SET owner_corporation_id = $1, allow_alliance = false, allow_standings = true, standing_level = 0, tax_rates = $2::jsonb WHERE office_id = $3",
                    connection, transaction)
                {
                    Parameters =
                    {
                        new() { Value = ownerCorporation.Id },
                        new() { Value = """{"corporation":0.03,"alliance":0.04,"standingHigh":0.05,"standingGood":0.06,"standingNeutral":0.07,"standingBad":0.08,"standingHorrible":0.09}""" },
                        new() { Value = officeId },
                    },
                })
                {
                    Assert.Equal(1, await policy.ExecuteNonQueryAsync());
                }
                await using (var ownerAlliance = new NpgsqlCommand(
                    $"UPDATE {Schema("EVE_CORPORATIONS_SCHEMA")}.corporations SET alliance_id = $1 WHERE id = $2", connection, transaction)
                {
                    Parameters = { new() { Value = ownerAllianceId }, new() { Value = ownerCorporation.Id } },
                })
                    Assert.Equal(1, await ownerAlliance.ExecuteNonQueryAsync());
                await using (var visitorAlliance = new NpgsqlCommand(
                    $"UPDATE {Schema("EVE_CORPORATIONS_SCHEMA")}.corporations SET alliance_id = $1 WHERE id = $2", connection, transaction)
                {
                    Parameters = { new() { Value = visitorAllianceId }, new() { Value = visitorCorporation.Id } },
                })
                    Assert.Equal(1, await visitorAlliance.ExecuteNonQueryAsync());
                await using (var visitorCharacter = new NpgsqlCommand(
                    $"UPDATE {Schema("EVE_CHARACTERS_SCHEMA")}.characters SET alliance_id = $1 WHERE id = $2", connection, transaction)
                {
                    Parameters = { new() { Value = visitorAllianceId }, new() { Value = pilot } },
                })
                    Assert.Equal(1, await visitorCharacter.ExecuteNonQueryAsync());
                await using (var contacts = new NpgsqlCommand($"""
                    INSERT INTO {Schema("EVE_CORPORATIONS_SCHEMA")}.corporation_contacts (corporation_id, contact_id, relationship_id)
                    VALUES ($1, $2, 10), ($1, $3, 5), ($1, $4, 0)
                    """, connection, transaction)
                {
                    Parameters =
                    {
                        new() { Value = ownerCorporation.Id }, new() { Value = pilot },
                        new() { Value = visitorCorporation.Id }, new() { Value = visitorAllianceId },
                    },
                })
                    Assert.Equal(3, await contacts.ExecuteNonQueryAsync());
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

            async Task SetOfficePolicyAsync(bool allowAlliance, bool allowStandings, int standingLevel)
            {
                await using var command = source.CreateCommand($"""
                    UPDATE {Schema("EVE_PLANETS_SCHEMA")}.customs_offices
                    SET allow_alliance = $1, allow_standings = $2, standing_level = $3
                    WHERE office_id = $4 AND owner_corporation_id = $5
                    """);
                command.Parameters.AddWithValue(allowAlliance);
                command.Parameters.AddWithValue(allowStandings);
                command.Parameters.AddWithValue(standingLevel);
                command.Parameters.AddWithValue(officeId);
                command.Parameters.AddWithValue(ownerCorporation.Id);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            async Task SetContactAsync(long contactId, int standing)
            {
                await using var command = source.CreateCommand($"""
                    INSERT INTO {Schema("EVE_CORPORATIONS_SCHEMA")}.corporation_contacts (corporation_id, contact_id, relationship_id)
                    VALUES ($1, $2, $3)
                    ON CONFLICT (corporation_id, contact_id) DO UPDATE SET relationship_id = EXCLUDED.relationship_id
                    """);
                command.Parameters.AddWithValue(ownerCorporation.Id);
                command.Parameters.AddWithValue(contactId);
                command.Parameters.AddWithValue(standing);
                await command.ExecuteNonQueryAsync();
            }

            async Task RemoveContactAsync(long contactId)
            {
                await using var command = source.CreateCommand($"DELETE FROM {Schema("EVE_CORPORATIONS_SCHEMA")}.corporation_contacts WHERE corporation_id = $1 AND contact_id = $2");
                command.Parameters.AddWithValue(ownerCorporation.Id);
                command.Parameters.AddWithValue(contactId);
                await command.ExecuteNonQueryAsync();
            }

            async Task SetVisitorCorporationAsync(long corporationId)
            {
                await using var command = source.CreateCommand($"UPDATE {Schema("EVE_CHARACTERS_SCHEMA")}.characters SET corporation_id = $1 WHERE id = $2");
                command.Parameters.AddWithValue(corporationId);
                command.Parameters.AddWithValue(pilot);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            async Task SetVisitorAllianceAsync(long allianceId)
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await using (var corporation = new NpgsqlCommand(
                    $"UPDATE {Schema("EVE_CORPORATIONS_SCHEMA")}.corporations SET alliance_id = $1 WHERE id = $2", connection, transaction)
                {
                    Parameters = { new() { Value = allianceId }, new() { Value = visitorCorporation.Id } },
                })
                    Assert.Equal(1, await corporation.ExecuteNonQueryAsync());
                await using (var character = new NpgsqlCommand(
                    $"UPDATE {Schema("EVE_CHARACTERS_SCHEMA")}.characters SET alliance_id = $1 WHERE id = $2", connection, transaction)
                {
                    Parameters = { new() { Value = allianceId }, new() { Value = pilot } },
                })
                    Assert.Equal(1, await character.ExecuteNonQueryAsync());
                await transaction.CommitAsync();
            }

            Task<long> StockQuantityAsync() => ScalarAsync<long>(source,
                $"SELECT quantity FROM {Schema("EVE_INVENTORY_SCHEMA")}.items WHERE id = {stockId}");
            Task<string> ColonyDocumentAsync() => ScalarAsync<string>(source,
                $"SELECT doc::text FROM {Schema("EVE_PLANETS_SCHEMA")}.colonies WHERE owner_id = {pilot} AND planet_id = {planetId}");
            Task<decimal> CharacterBalanceAsync() => ScalarAsync<decimal>(source,
                $"SELECT COALESCE((SELECT isk FROM {Schema("EVE_WALLET_SCHEMA")}.balances WHERE owner_id = {pilot} AND account_key = 1000), 0)");
            Task<decimal> OwnerBalanceAsync() => ScalarAsync<decimal>(source,
                $"SELECT COALESCE((SELECT isk FROM {Schema("EVE_WALLET_SCHEMA")}.balances WHERE owner_id = {ownerCorporation.Id} AND account_key = 1000), 0)");

            async Task<PyValue> TaxRateResultAsync() => await host.Registry.Lookup("planetOrbitalRegistryBroker")!.CallAsync(new ServiceCall
            {
                Method = "GetTaxRate", Args = [new PyLong(officeId)], Session = session,
            });
            async Task<double> TaxRateAsync() => Assert.IsType<PyReal>(await TaxRateResultAsync()).Value;
            async Task AssertDeniedTransferIsUnchangedAsync(double expectedRate)
            {
                var quantity = await StockQuantityAsync();
                var colony = await ColonyDocumentAsync();
                var characterBalance = await CharacterBalanceAsync();
                var ownerBalance = await OwnerBalanceAsync();
                var denied = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
                    await customs.ImportExportAsync(session, officeId, padId,
                        new Dictionary<long, long> { [stockId] = 1 }, new Dictionary<int, long> { [waterType] = 1 },
                        expectedRate, CancellationToken.None));
                Assert.Equal("CustomsOfficeAccessDenied", denied.Error.UserErrorMessage);
                Assert.Equal(quantity, await StockQuantityAsync());
                Assert.Equal(colony, await ColonyDocumentAsync());
                Assert.Equal(characterBalance, await CharacterBalanceAsync());
                Assert.Equal(ownerBalance, await OwnerBalanceAsync());
            }

            var rate = await TaxRateAsync();
            Assert.Equal(0.05, rate, 6);

            await RemoveContactAsync(pilot);
            Assert.Equal(0.06, await TaxRateAsync(), 6);
            await RemoveContactAsync(visitorCorporation.Id);
            Assert.Equal(0.07, await TaxRateAsync(), 6);
            await SetVisitorCorporationAsync(ownerCorporation.Id);
            Assert.Equal(0.03, await TaxRateAsync(), 6);
            await SetVisitorCorporationAsync(visitorCorporation.Id);
            await SetVisitorAllianceAsync(ownerAllianceId);
            await SetOfficePolicyAsync(allowAlliance: true, allowStandings: false, standingLevel: 1);
            Assert.Equal(0.04, await TaxRateAsync(), 6);
            await SetVisitorAllianceAsync(visitorAllianceId);
            await SetOfficePolicyAsync(allowAlliance: false, allowStandings: true, standingLevel: 0);
            await SetContactAsync(pilot, 10);
            Assert.Equal(rate, await TaxRateAsync(), 6);

            var account = host.Registry.Lookup("account")!;
            var cashBeforeSettlement = Assert.IsType<PyReal>(await account.CallAsync(new ServiceCall
            {
                Method = "GetCashBalance",
                Args = [new PyInt(0)],
                Session = session,
            })).Value;
            var colonyBeforeImport = await ColonyDocumentAsync();
            await customs.ImportExportAsync(session, officeId, padId, new Dictionary<long, long> { [stockId] = 1 },
                new Dictionary<int, long>(), rate, CancellationToken.None);
            Assert.Equal(1L, await StockQuantityAsync());
            var colonyAfterImport = await ColonyDocumentAsync();
            Assert.NotEqual(colonyBeforeImport, colonyAfterImport);
            var importTaxEntries = (await wallets.JournalForAccountAsync(pilot, 1000))
                .Where(row => row.Reason == "planetary customs fee").ToArray();
            Assert.Single(importTaxEntries);
            var importTax = Math.Abs(importTaxEntries[0].Amount);
            Assert.True(importTax > 0);
            await customs.ImportExportAsync(session, officeId, padId, new Dictionary<long, long>(),
                new Dictionary<int, long> { [waterType] = 1 }, rate, CancellationToken.None);
            Assert.Equal(2L, await StockQuantityAsync());
            var customerTaxEntries = (await wallets.JournalForAccountAsync(pilot, 1000))
                .Where(row => row.Reason == "planetary customs fee").ToArray();
            var customerTax = Math.Abs(customerTaxEntries.Sum(row => row.Amount));

            var cash = Assert.IsType<PyReal>(await host.Registry.Lookup("account")!.CallAsync(new ServiceCall
            {
                Method = "GetCashBalance",
                Args = [new PyInt(0)],
                Session = session,
            })).Value;
            Assert.Equal(cashBeforeSettlement - (double)customerTax, cash, 2);
            await host.ShutdownAsync();
            host = await HostComposition.BuildAsync(Settings());
            customs = Assert.IsAssignableFrom<ICustomsOffices>(host.Registry.Lookup("planetMgr")!);
            Assert.Equal(rate, await TaxRateAsync(), 6);

            var reopenedWallets = new WalletTransactions(source, Schema("EVE_WALLET_SCHEMA"));
            Assert.Equal(30m, await ScalarAsync<decimal>(source,
                $"SELECT isk FROM {Schema("EVE_WALLET_SCHEMA")}.balances WHERE owner_id = {ownerCorporation.Id} AND account_key = 1000"));
            var ownerEntries = (await reopenedWallets.JournalForAccountAsync(ownerCorporation.Id, 1000))
                .Where(row => row.Reason == "planetary customs office tax").ToArray();
            Assert.Equal(2, ownerEntries.Length);
            Assert.Equal(30m, ownerEntries.Sum(row => row.Amount));
            Assert.All(ownerEntries, row => Assert.Equal(97, row.RefTypeId));
            Assert.Equal(cashBeforeSettlement - (double)customerTax, Assert.IsType<PyReal>(await host.Registry.Lookup("account")!.CallAsync(new ServiceCall
            {
                Method = "GetCashBalance",
                Args = [new PyInt(0)],
                Session = session,
            })).Value, 2);

            await SetContactAsync(pilot, 0);
            await SetOfficePolicyAsync(allowAlliance: false, allowStandings: true, standingLevel: 1);
            Assert.IsType<PyNone>(await TaxRateResultAsync());
            await AssertDeniedTransferIsUnchangedAsync(rate);

            await SetContactAsync(pilot, 10);
            await SetOfficePolicyAsync(allowAlliance: false, allowStandings: false, standingLevel: 0);
            Assert.IsType<PyNone>(await TaxRateResultAsync());
            await AssertDeniedTransferIsUnchangedAsync(rate);

            await SetOfficePolicyAsync(allowAlliance: false, allowStandings: true, standingLevel: 0);
            Assert.Equal(rate, await TaxRateAsync(), 6);
            await using (var emptyWallet = source.CreateCommand($"UPDATE {Schema("EVE_WALLET_SCHEMA")}.balances SET isk = $1 WHERE owner_id = $2 AND account_key = 1000"))
            {
                emptyWallet.Parameters.AddWithValue(importTax);
                emptyWallet.Parameters.AddWithValue(pilot);
                Assert.Equal(1, await emptyWallet.ExecuteNonQueryAsync());
            }
            var colonyBeforeFailedSettlement = await ColonyDocumentAsync();
            var ownerBalanceBeforeFailedSettlement = await OwnerBalanceAsync();
            var customerTaxEntriesBeforeFailedSettlement = (await reopenedWallets.JournalForAccountAsync(pilot, 1000))
                .Count(row => row.Reason == "planetary customs fee");
            await Assert.ThrowsAsync<InsufficientFundsException>(async () =>
                await customs.ImportExportAsync(session, officeId, padId,
                    new Dictionary<long, long> { [stockId] = 1 }, new Dictionary<int, long> { [waterType] = 1 },
                    rate, CancellationToken.None));
            Assert.Equal(importTax, await CharacterBalanceAsync());
            Assert.Equal(2L, await StockQuantityAsync());
            Assert.Equal(colonyBeforeFailedSettlement, await ColonyDocumentAsync());
            Assert.Equal(ownerBalanceBeforeFailedSettlement, await OwnerBalanceAsync());
            Assert.Equal(customerTaxEntriesBeforeFailedSettlement,
                (await reopenedWallets.JournalForAccountAsync(pilot, 1000)).Count(row => row.Reason == "planetary customs fee"));
            Assert.Equal(ownerEntries.Length, (await reopenedWallets.JournalForAccountAsync(ownerCorporation.Id, 1000))
                .Count(row => row.Reason == "planetary customs office tax"));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    private static async Task<long> InsertPilotAsync(NpgsqlDataSource source, PgCharacterStore characters)
    {
        var pilot = new NewCharacter(AccountId, $"Customs Host Pilot {Guid.NewGuid():N}", Gender: 1, RaceId: 1, BloodlineId: 1, AncestryId: 10, SchoolId: 35, TypeId: 1373,
            CorporationId: CorporationId, FactionId: 500001, StationId: 60000610, SolarSystemId: Jita, ConstellationId: 20000020, RegionId: 10000002,
            DollInfo: null, PortraitInfo: null);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await characters.InsertAsync(connection, transaction, pilot);
        await transaction.CommitAsync();
        return id;
    }

    private static async Task UpdateOfficeRatesAsync(HostRuntime host, long pilot, long corporationId, long officeId, double rate)
    {
        var service = host.Registry.Lookup("planetOrbitalRegistryBroker")!;
        var session = Session(pilot);
        session.Set("corpid", new PyLong(corporationId));
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

    private static async Task ImportExportWithPlanetAsync(
        InvBrokerService inventory, ClientSession session, long officeId, long launchpadId, PyDict imports, PyDict exports, double taxRate)
    {
        var response = Assert.IsType<PyTuple>(await inventory.MachoBindObject(new ServiceCall
        {
            Method = "MachoBindObject",
            Args =
            [
                new PyTuple([new PyLong(officeId)]),
                new PyTuple([
                    new PyStr("ImportExportWithPlanet"),
                    new PyTuple([new PyLong(launchpadId), imports, exports, new PyReal(taxRate)]),
                    PyNone.Instance,
                ]),
            ],
            Session = session,
        }));
        Assert.IsType<PySubStruct>(response.Items[0]);
        Assert.Equal(PyNone.Instance, response.Items[1]);
    }

    private static ClientSession Session(long pilot)
    {
        var session = new ClientSession { CharacterId = pilot };
        session.Set("corpid", new PyLong(CorporationId));
        session.Set("solarsystemid2", new PyLong(Jita));
        return session;
    }

    private sealed class CustomsOfficeTestSpace(Item office) : ISpaceContainers
    {
        public bool IsInSpace(ClientSession session) => true;
        public int? SolarSystemOf(ClientSession session) => (int)office.LocationId;
        public Task<Item?> OpenAsync(ClientSession session, long containerId, CancellationToken cancellationToken) =>
            Task.FromResult(containerId == office.Id ? office : null);
        public Task<long?> MoveAsync(ClientSession session, long itemId, long sourceId, long destinationId, long? quantity,
            CancellationToken cancellationToken) => Task.FromResult<long?>(null);
        public Task<bool?> MoveAllAsync(ClientSession session, IReadOnlyList<long> itemIds, long sourceId, long destinationId,
            CancellationToken cancellationToken) => Task.FromResult<bool?>(null);
        public Task<IReadOnlyList<long>> JettisonAsync(ClientSession session, IReadOnlyList<long> itemIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<long>>([]);
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
