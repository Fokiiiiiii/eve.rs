using Eve.Characters;
using Eve.AccessGroups;
using Eve.Corporations;
using Eve.Identity;
using Eve.Data;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Planets;
using Eve.Services;
using Eve.Services.Inventory;
using Eve.Services.Planets;
using Eve.Skills;
using Eve.Structures;
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
        "EVE_CONTRABAND_SCHEMA", "EVE_PLANETS_SCHEMA", "EVE_ACCESS_GROUPS_SCHEMA",
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
                await wallets.AdjustAsync(connection, transaction, pilot, 2_000_000m, 0, 0,
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
            await UpdateOfficeRatesAsync(host, pilot, corporationId, officeId, 0.03);
            Assert.Equal(0.10, beforeRestart, 6);
            Assert.Equal(0.13, await HostTaxRateAsync(host, pilot, officeId), 6);
            await host.ShutdownAsync();

            host = await HostComposition.BuildAsync(Settings());
            Assert.Equal(0.13, await HostTaxRateAsync(host, pilot, officeId), 6);
            Assert.Equal(1L, await ScalarAsync<long>(source,
                $"SELECT count(*) FROM {Schema("EVE_PLANETS_SCHEMA")}.customs_offices WHERE office_id = {officeId} AND owner_corporation_id = {corporationId}"));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }
    [PgFact]
    public async Task Orbital_settings_read_update_and_survive_restart_for_customs_offices_and_skyhooks()
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
                await wallets.AdjustAsync(connection, transaction, pilot, 2_000_000m, 0, 0,
                    "orbital settings authority test funds", nowMs, $"orbital-settings-funds:{Guid.NewGuid():N}");
                await transaction.CommitAsync();
            }
            var corporationStore = new CorporationStore(source, characters,
                new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")), wallets,
                new PgSkillStore(source, Schema("EVE_SKILLS_SCHEMA")), () => nowMs, Schema("EVE_CORPORATIONS_SCHEMA"));
            var corporation = await corporationStore.CreateAsync(pilot,
                new NewCorporation("Orbital Settings Corporation", "ORBST", "", "", 0.05m),
                $"orbital-settings-corp:{Guid.NewGuid():N}");
            var corporationId = corporation.Id;
            var nonOwner = await InsertPilotAsync(source, characters);
            var planetId = await ScalarAsync<long>(source, """
                SELECT p.key::bigint
                FROM sde."mapPlanets" p JOIN sde."mapStars" s ON p.doc->>'orbitID' = s.key
                WHERE s.doc->>'solarSystemID' = '30000142'
                ORDER BY p.key LIMIT 1
                """);
            var officeId = Customs.OfficeId(planetId);
            _ = await HostTaxRateAsync(host, pilot, officeId);
            await using (var owner = source.CreateCommand(
                $"UPDATE {Schema("EVE_INVENTORY_SCHEMA")}.items SET owner_id = {corporationId} WHERE id = {officeId}"))
                Assert.Equal(1, await owner.ExecuteNonQueryAsync());

            const long skyhookId = 98_000_101;
            var structures = new StructureStore(source, Schema("EVE_STRUCTURES_SCHEMA"));
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                Assert.True(await structures.CreateAsync(connection, transaction,
                    new NewStructure(skyhookId, 81080, Jita, 0, 0, 0, 0, corporationId, 0, "Orbital Settings Skyhook", "")));
                await using var deployed = new NpgsqlCommand($"UPDATE {Schema("EVE_STRUCTURES_SCHEMA")}.structures SET state = 110, upkeep = 1 WHERE structure_id = $1",
                    connection, transaction) { Parameters = { new() { Value = skyhookId } } };
                Assert.Equal(1, await deployed.ExecuteNonQueryAsync());
                await transaction.CommitAsync();
            }

            var affiliation = new Affiliation(pilot, corporationId, 0);
            var accessGroups = new AccessGroupStore(source, Schema("EVE_ACCESS_GROUPS_SCHEMA"), () => nowMs);
            var groupId = (await accessGroups.CreateAsync(affiliation, "Orbital settings", "Skyhook access")).Result.GroupId;
            var pocoRates = Rates(0.23, 0.19);
            var skyhookRates = Rates(0.31, 0.27);

            var deniedPoco = await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(() =>
                UpdateOrbitalSettingsAsync(host, nonOwner, corporationId, 1, officeId, 7, pocoRates, 4, false, true, null));
            Assert.Equal("CustomsOfficeAccessDenied", deniedPoco.Error.UserErrorMessage);
            var deniedSkyhook = await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(() =>
                UpdateOrbitalSettingsAsync(host, pilot, CorporationId, 1, skyhookId, 21, skyhookRates, -3, true, false, groupId));
            Assert.Equal("CrpAccessDenied", deniedSkyhook.Error.UserErrorMessage);
            var invalidHour = await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(() =>
                UpdateOrbitalSettingsAsync(host, pilot, corporationId, 1, officeId, 24, pocoRates, 4, false, true, null));
            Assert.Equal("CustomsOfficeNotFound", invalidHour.Error.UserErrorMessage);
            var invalidTaxShape = await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(() =>
                UpdateOrbitalSettingsAsync(host, pilot, corporationId, 1, officeId, 7, Py.Dict(("corporation", new PyReal(0.23))), 4, false, true, null));
            Assert.Equal("CustomsOfficeNotFound", invalidTaxShape.Error.UserErrorMessage);
            var invalidTaxRate = await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(() =>
                UpdateOrbitalSettingsAsync(host, pilot, corporationId, 1, officeId, 7, Rates(1.01, 0.19), 4, false, true, null));
            Assert.Equal("CustomsOfficeNotFound", invalidTaxRate.Error.UserErrorMessage);
            Assert.Equal(0L, await ScalarAsync<long>(source, $"""
                SELECT count(*) FROM {Schema("EVE_EVENTS_SCHEMA")}.outbox WHERE topic = 'structure.reinforcement_changed'
                """));

            await UpdateOrbitalSettingsAsync(host, pilot, corporationId, 1, officeId, 7, pocoRates, 4, false, true, null);
            await UpdateOrbitalSettingsAsync(host, pilot, corporationId, 1, skyhookId, 21, skyhookRates, -3, true, false, groupId);
            await Task.WhenAll(
                UpdateOrbitalSettingsAsync(host, pilot, corporationId, 1, skyhookId, 21, skyhookRates, -3, true, false, groupId),
                UpdateOrbitalSettingsAsync(host, pilot, corporationId, 1, skyhookId, 21, skyhookRates, -3, true, false, groupId));
            Assert.Equal(1L, await ScalarAsync<long>(source, $"""
                SELECT count(*) FROM {Schema("EVE_EVENTS_SCHEMA")}.outbox WHERE topic = 'structure.reinforcement_changed'
                """));
            AssertOrbitalSettings(await HostSettingsInfoAsync(host, pilot, officeId), 7, 0.23, 0.19, 4, false, true, null);
            AssertOrbitalSettings(await HostSettingsInfoAsync(host, pilot, skyhookId), 21, 0.31, 0.27, -3, true, false, groupId);
            Assert.Equal(1L, await ScalarAsync<long>(source, $"""
                SELECT count(*) FROM {Schema("EVE_STRUCTURES_SCHEMA")}.structures
                WHERE structure_id = {skyhookId} AND reinforce_hour = 18 AND pending_hour = 21 AND pending_effective_ms > 0
                  AND doc->>'aclGroupID' = '{groupId}' AND doc->'taxRates'->>'corporation' = '0.31'
                """));

            await host.ShutdownAsync();
            host = await HostComposition.BuildAsync(Settings());
            AssertOrbitalSettings(await HostSettingsInfoAsync(host, pilot, officeId), 7, 0.23, 0.19, 4, false, true, null);
            AssertOrbitalSettings(await HostSettingsInfoAsync(host, pilot, skyhookId), 21, 0.31, 0.27, -3, true, false, groupId);
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

    private static PyValue Rates(double corporation, double alliance) => Py.KeyVal(
        ("corporation", new PyReal(corporation)), ("alliance", new PyReal(alliance)),
        ("standingHigh", new PyReal(0.17)), ("standingGood", new PyReal(0.13)),
        ("standingNeutral", new PyReal(0.09)), ("standingBad", new PyReal(0.07)),
        ("standingHorrible", new PyReal(0.03)));

    private static async Task UpdateOrbitalSettingsAsync(
        HostRuntime host, long pilot, long corporationId, long roles, long orbitalId, int reinforceHour,
        PyValue rates, int standingLevel, bool allowAlliance, bool allowStandings, long? accessGroupId)
    {
        var session = Session(pilot);
        session.Set("corpid", new PyLong(corporationId));
        session.Set("corprole", new PyLong(roles));
        var wireArgs = RoundTrip(Py.Tuple(
            new PyLong(orbitalId), new PyInt(reinforceHour), rates, new PyInt(standingLevel),
            allowAlliance ? PyBool.True : PyBool.False,
            allowStandings ? PyBool.True : PyBool.False,
            accessGroupId is { } groupId ? new PyLong(groupId) : PyNone.Instance));
        if (rates is PyObject)
        {
            var keyVal = Assert.IsType<PyObject>(wireArgs.Items[2]);
            Assert.Equal("util.KeyVal", Eve.Macho.JsSemantics.StringValue(keyVal.Name));
            var taxRates = Assert.IsType<PyDict>(keyVal.Args);
            Assert.Equal(7, taxRates.Entries.Count);
            Assert.Equal(
                new[] { "corporation", "alliance", "standingHigh", "standingGood", "standingNeutral", "standingBad", "standingHorrible" }.Order().ToArray(),
                taxRates.Entries.Select(entry => Eve.Macho.JsSemantics.StringValue(entry.Key)).Order().ToArray());
            Assert.All(taxRates.Entries, entry => Assert.IsType<PyReal>(entry.Value));
        }
        var result = await HostOrbitalCallAsync(host, session, "UpdateSettings", wireArgs);
        Assert.Equal(PyNone.Instance, result);
    }

    private static async Task<PyTuple> HostSettingsInfoAsync(HostRuntime host, long pilot, long orbitalId) =>
        Assert.IsType<PyTuple>(await HostOrbitalCallAsync(host, Session(pilot), "GetSettingsInfo", RoundTrip(Py.Tuple(new PyLong(orbitalId)))));

    private static async Task<PyValue> HostOrbitalCallAsync(HostRuntime host, ClientSession session, string method, PyTuple args)
    {
        var response = Assert.IsType<PyTuple>(await host.Registry.Lookup("planetOrbitalRegistryBroker")!.CallAsync(new ServiceCall
        {
            Method = "MachoBindObject",
            Args = WireArgs(new PyLong(Jita), RoundTrip(Py.Tuple(new PyStr(method), args, PyNone.Instance))),
            Session = session,
        }));
        Assert.IsType<PySubStruct>(response.Items[0]);
        return RoundTrip(response.Items[1]);
    }

    private static IReadOnlyList<PyValue> WireArgs(params PyValue[] values) => RoundTrip(Py.Tuple(values)).Items;

    private static PyValue RoundTrip(PyValue value) => MarshalCodec.Decode(MarshalCodec.Encode(value));

    private static PyTuple RoundTrip(PyTuple value) =>
        Assert.IsType<PyTuple>(RoundTrip((PyValue)value));

    private static void AssertOrbitalSettings(
        PyTuple settings, int reinforceHour, double corporationRate, double allianceRate, int standingLevel,
        bool allowAlliance, bool allowStandings, long? accessGroupId)
    {
        Assert.Equal(6, settings.Items.Count);
        Assert.Equal(reinforceHour, Assert.IsType<PyInt>(settings.Items[0]).Value);
        var keyVal = Assert.IsType<PyObject>(settings.Items[1]);
        Assert.Equal("util.KeyVal", Eve.Macho.JsSemantics.StringValue(keyVal.Name));
        var taxRates = Assert.IsType<PyDict>(keyVal.Args);
        var expectedRates = new (string Name, double Value)[]
        {
            ("corporation", corporationRate), ("alliance", allianceRate),
            ("standingHigh", 0.17), ("standingGood", 0.13), ("standingNeutral", 0.09),
            ("standingBad", 0.07), ("standingHorrible", 0.03),
        };
        Assert.Equal(expectedRates.Select(rate => rate.Name).Order(),
            taxRates.Entries.Select(entry => Eve.Macho.JsSemantics.StringValue(entry.Key)).Order());
        foreach (var (name, value) in expectedRates)
        {
            var rate = taxRates.Entries.Single(entry => Eve.Macho.JsSemantics.StringValue(entry.Key) == name);
            Assert.Equal(value, Assert.IsType<PyReal>(rate.Value).Value, 6);
        }
        Assert.Equal(standingLevel, Assert.IsType<PyInt>(settings.Items[2]).Value);
        Assert.Equal(allowAlliance ? PyBool.True : PyBool.False, settings.Items[3]);
        Assert.Equal(allowStandings ? PyBool.True : PyBool.False, settings.Items[4]);
        if (accessGroupId is { } groupId)
            Assert.Equal(groupId, Assert.IsType<PyLong>(settings.Items[5]).Value);
        else
            Assert.IsType<PyNone>(settings.Items[5]);
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
        var rates = Py.KeyVal(
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
