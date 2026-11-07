using Eve.Characters;
using Eve.Corporations;
using Eve.Data;
using Eve.Host;
using Eve.Fitting;
using Eve.Identity;
using Eve.Inventory;
using Eve.Inventory.Mutation;
using Eve.Macho;
using Eve.Market;
using Eve.Marshal;
using Eve.Skills;
using Eve.Structures;
using Eve.Services;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class ReprocessingHostTests : IAsyncLifetime
{
    private sealed class Capture : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private const int AccountId = 98_000_031;
    private const long StationId = 60_003_760;
    private const int Jita = 30_000_142;
    private const int Veldspar = 1230;
    private const int Tritanium = 34;
    private readonly string _prefix = "rp" + Guid.NewGuid().ToString("N")[..10];
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
    public async Task Reprocess_uses_the_registered_host_service_and_keeps_the_settlement_after_restart()
    {
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var character = await InsertPilotAsync(source, characters);
            var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
            var wallet = new WalletTransactions(source, Schema("EVE_WALLET_SCHEMA"));
            var balances = new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA"));
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long input;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await wallet.EnsureAsync(connection, transaction, character);
                await wallet.AdjustAsync(connection, transaction, character, 100_000m, 0, 0,
                    "reprocessing host test funds", now, $"reprocess-host-funds:{Guid.NewGuid():N}");
                input = await items.CreateAsync(connection, transaction,
                    new NewItem(Veldspar, character, StationId, ItemFlags.Hangar, 100));
                await transaction.CommitAsync();
            }

            var session = Session(character);
            var service = host.Registry.Lookup("reprocessingSvc") ?? throw new InvalidOperationException("reprocessingSvc is not registered");
            var call = PlantCall(session, "Reprocess",
                new PyList([new PyLong(input)]), new PyLong(StationId), new PyLong(character), new PyLong(StationId),
                new PyInt(ItemFlags.Hangar));
            var response = (PyTuple)await service.CallAsync(call);
            var result = (PyList)response.Items[1];
            var processed = Assert.IsType<PyList>(result.Items[0]);
            Assert.Equal(input, Assert.IsType<PyLong>(Assert.Single(processed.Items)).Value);
            Assert.Contains(Assert.IsType<PyDict>(result.Items[1]).Entries,
                entry => entry.Key is PyInt type && type.Value == Tritanium && entry.Value is PyLong quantity && quantity.Value > 0);

            await host.ShutdownAsync();
            host = await HostComposition.BuildAsync(Settings());

            Assert.Null(await items.GetAsync(input));
            var yields = await items.InLocationAsync(StationId, character, default);
            Assert.Contains(yields, item => item.TypeId == Tritanium && item.Flag == ItemFlags.Hangar);
            Assert.NotNull(await balances.GetAsync(character));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [PgFact]
    public async Task Reprocess_persists_corporation_hangar_yields_and_checks_member_role_and_office()
    {
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var founder = await InsertPilotAsync(source, characters);
            var member = await InsertPilotAsync(source, characters);
            var walletStore = new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA"));
            var walletTransactions = new WalletTransactions(source, Schema("EVE_WALLET_SCHEMA"));
            var skills = new PgSkillStore(source, Schema("EVE_SKILLS_SCHEMA"));
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await walletStore.OpenAsync(connection, transaction, founder, 5_000_000m, now);
                await skills.GrantAsync(connection, transaction, founder, [new CharacterSkill(3363, 1, 0)]);
                await walletStore.OpenAsync(connection, transaction, member, 100_000m, now);
                await transaction.CommitAsync();
            }

            var corporations = new CorporationStore(source, characters, walletStore, walletTransactions, skills,
                () => now, Schema("EVE_CORPORATIONS_SCHEMA"));
            var corporation = await corporations.CreateAsync(founder,
                new NewCorporation("Reprocessing Office Test", "ROT", "", "", 0.05m), $"reprocess-office:{founder}");
            var application = await corporations.ApplyAsync(member, corporation.Id, "");
            await corporations.RespondAsync(founder, application, 6, "", "");

            var offices = new CorporationOfficeStore(source, Schema("EVE_CORPORATIONS_SCHEMA"));
            var office = await offices.WriteAsync((connection, transaction) => offices.RentAsync(
                connection, transaction, corporation.Id, 900_003_760,
                new CorporationOfficeStation(StationId, 1531, 1_000_001, Jita, 0.9),
                CorporationOfficeStore.MinimumRentIsk, now));
            var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
            long founderInput;
            long memberInput;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                founderInput = await items.CreateAsync(connection, transaction,
                    new NewItem(Veldspar, founder, StationId, ItemFlags.Hangar, 100));
                memberInput = await items.CreateAsync(connection, transaction,
                    new NewItem(Veldspar, member, StationId, ItemFlags.Hangar, 100));
                await transaction.CommitAsync();
            }

            var service = host.Registry.Lookup("reprocessingSvc") ?? throw new InvalidOperationException("reprocessingSvc is not registered");
            await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.CallAsync(PlantCall(Session(founder), "Reprocess",
                new PyList([new PyLong(founderInput)]), new PyLong(StationId), new PyLong(corporation.Id),
                new PyLong(office.OfficeFolderId + 1), new PyInt(115))));
            Assert.NotNull(await items.GetAsync(founderInput));
            Assert.Empty(await items.InLocationAsync(office.OfficeFolderId, corporation.Id, default));

            var capture = new Capture();
            var founderSession = Session(founder, capture);
            var dispatcher = new PacketDispatcher(host.Registry, () => now);
            Assert.True(await dispatcher.DispatchAsync(ClientCallPacket(1,
                new PyList([new PyLong(founderInput)]), new PyLong(StationId), new PyLong(corporation.Id),
                new PyLong(office.OfficeFolderId), new PyInt(115)), founderSession));
            var founderResponsePacket = capture.Packets.Single(packet => packet is PyObject { Name: PyStr name } && name.Value.EndsWith(".CallRsp", StringComparison.Ordinal));
            var founderResponse = Assert.IsType<PyTuple>(Result(founderResponsePacket));
            var founderResult = Assert.IsType<PyList>(founderResponse.Items[1]);
            Assert.Equal(founderInput, Assert.IsType<PyLong>(Assert.Single(Assert.IsType<PyList>(founderResult.Items[0]).Items)).Value);
            var corporationYields = await items.InLocationAsync(office.OfficeFolderId, corporation.Id, default);
            Assert.Contains(corporationYields, item => item.TypeId == Tritanium && item.Flag == 115);

            long corporationInput;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                corporationInput = await items.CreateAsync(connection, transaction,
                    new NewItem(Veldspar, corporation.Id, office.OfficeFolderId, 115, 100));
                await transaction.CommitAsync();
            }
            founderSession.Set("corpid", new PyLong(corporation.Id));
            var founderQuotesResponse = Assert.IsType<PyTuple>(await service.CallAsync(PlantCall(founderSession, "GetQuotes",
                new PyList([new PyLong(corporationInput)]), new PyInt(0))));
            Assert.Single(Assert.IsType<PyDict>(Assert.IsType<PyList>(founderQuotesResponse.Items[1]).Items[2]).Entries);

            var memberSession = Session(member);
            memberSession.Set("corpid", new PyLong(corporation.Id));
            var memberQuotesResponse = Assert.IsType<PyTuple>(await service.CallAsync(PlantCall(memberSession, "GetQuotes",
                new PyList([new PyLong(corporationInput)]), new PyInt(0))));
            Assert.Empty(Assert.IsType<PyDict>(Assert.IsType<PyList>(memberQuotesResponse.Items[1]).Items[2]).Entries);
            Assert.NotNull(await items.GetAsync(corporationInput));

            var corporationResultResponse = Assert.IsType<PyTuple>(await service.CallAsync(PlantCall(founderSession, "Reprocess",
                new PyList([new PyLong(corporationInput)]), new PyLong(office.OfficeFolderId), new PyLong(corporation.Id),
                new PyLong(office.OfficeFolderId), new PyInt(115))));
            var corporationResult = Assert.IsType<PyList>(corporationResultResponse.Items[1]);
            Assert.Equal(corporationInput, Assert.IsType<PyLong>(Assert.Single(Assert.IsType<PyList>(corporationResult.Items[0]).Items)).Value);
            Assert.Null(await items.GetAsync(corporationInput));
            Assert.Contains(await items.InLocationAsync(office.OfficeFolderId, corporation.Id, default), item => item.TypeId == Tritanium && item.Flag == 115);

            var yieldsBeforeDeniedCall = (await items.InLocationAsync(office.OfficeFolderId, corporation.Id, default)).Count;
            await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.CallAsync(PlantCall(Session(member), "Reprocess",
                new PyList([new PyLong(memberInput)]), new PyLong(StationId), new PyLong(corporation.Id),
                new PyLong(office.OfficeFolderId), new PyInt(115))));
            Assert.NotNull(await items.GetAsync(memberInput));
            Assert.Equal(yieldsBeforeDeniedCall, (await items.InLocationAsync(office.OfficeFolderId, corporation.Id, default)).Count);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [PgFact]
    public async Task Reprocess_uses_a_structure_service_profile_and_rejects_offline_or_inaccessible_structures()
    {
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var character = await InsertPilotAsync(source, characters);
            var outsider = await InsertPilotAsync(source, characters);
            const long structureId = 1_000_000_000_999;
            var walletStore = new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA"));
            var walletTransactions = new WalletTransactions(source, Schema("EVE_WALLET_SCHEMA"));
            var skills = new PgSkillStore(source, Schema("EVE_SKILLS_SCHEMA"));
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await walletStore.OpenAsync(connection, transaction, character, 5_000_000m, now);
                await skills.GrantAsync(connection, transaction, character, [new CharacterSkill(3363, 1, 0)]);
                await transaction.CommitAsync();
            }
            var corporations = new CorporationStore(source, characters, walletStore, walletTransactions,
                skills, () => now,
                Schema("EVE_CORPORATIONS_SCHEMA"));
            var corporation = await corporations.CreateAsync(character,
                new NewCorporation("Structure Reprocessing Test", "SRT", "", "", 0.05m), $"reprocess-structure:{character}");
            var corporationId = corporation.Id;
            await using (var scheduler = new Eve.Scheduler.DurableScheduler(source, () => now, schema: Schema("EVE_SCHEDULER_SCHEMA")))
            {
                var relations = new Eve.Relations.RelationStore(source, scheduler,
                    new Eve.Events.EventBus(source, scheduler, schema: Schema("EVE_EVENTS_SCHEMA")), Schema("EVE_RELATIONS_SCHEMA"));
                await relations.SetAsync(Eve.Relations.RelationKinds.CorporationMember.Name, character, corporationId, 1,
                    "structure reprocessing test affiliation");
            }
            var structures = new StructureStore(source, Schema("EVE_STRUCTURES_SCHEMA"));
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await structures.CreateAsync(connection, transaction,
                    new NewStructure(structureId, 35836, Jita, 0, 0, 0, 0, corporationId, 0, "Reprocessing Test", ""));
                await structures.SaveAsync(connection, transaction, new StructureSnapshot
                {
                    StructureId = structureId,
                    State = StructureState.ShieldVulnerable,
                    Upkeep = UpkeepState.FullPower,
                    CoreInstalled = true,
                });
                await structures.SetServiceAsync(connection, transaction, structureId, StructureServiceIds.Reprocessing,
                    ServiceState.Online, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                await using var tax = new NpgsqlCommand(
                    $"UPDATE {Schema("EVE_STRUCTURES_SCHEMA")}.structures SET doc = doc || '{{\"industryTaxRate\":0.05}}'::jsonb WHERE structure_id = $1",
                    connection, transaction);
                tax.Parameters.AddWithValue(structureId);
                await tax.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }

            var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
            var ownerBalanceBeforeSettlement = (await walletStore.GetAsync(corporationId))!.Isk;
            long input;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                input = await items.CreateAsync(connection, transaction,
                    new NewItem(Veldspar, character, structureId, ItemFlags.Hangar, 100));
                await items.CreateAsync(connection, transaction,
                    new NewItem(46640, corporationId, structureId, 92, 1));
                await transaction.CommitAsync();
            }

            var service = host.Registry.Lookup("reprocessingSvc") ?? throw new InvalidOperationException("reprocessingSvc is not registered");
            var notices = new Capture();
            var session = StructureSession(character, structureId, corporationId, notices);
            var response = Assert.IsType<PyTuple>(await service.CallAsync(PlantCallAt(structureId, session, "GetQuotes",
                new PyList([new PyLong(input)]), new PyInt(0))));
            var quoteResults = Assert.IsType<PyList>(response.Items[1]);
            Assert.Equal(0.05, Number(quoteResults.Items[0]), 6);
            var efficiencies = Assert.IsType<PyDict>(quoteResults.Items[1]);
            Assert.Equal(0.55915, Number(efficiencies.Entries.Single(entry => entry.Key is PyInt type && type.Value == Veldspar).Value), 6);
            var quote = Assert.Single(Assert.IsType<PyDict>(quoteResults.Items[2]).Entries).Value;
            Assert.Equal(0.55915, Number(QuoteField(quote, "efficiency")), 6);

            var reprocessed = Assert.IsType<PyTuple>(await service.CallAsync(PlantCallAt(structureId, session, "Reprocess",
                new PyList([new PyLong(input)]), new PyLong(structureId), new PyLong(character),
                new PyLong(structureId), new PyInt(ItemFlags.Hangar))));
            Assert.Single(Assert.IsType<PyList>(Assert.IsType<PyList>(reprocessed.Items[1]).Items[0]).Items);
            Assert.Null(await items.GetAsync(input));
            Assert.Contains(await items.InLocationAsync(structureId, character, default), item => item.TypeId == Tritanium);
            Assert.Equal(2, notices.Packets.Count);
            Assert.True((await walletStore.GetAsync(character))!.Isk < 5_000_000m);
            Assert.True((await walletStore.GetAsync(corporationId))!.Isk > ownerBalanceBeforeSettlement);

            var outputCountAfterSettlement = (await items.InLocationAsync(structureId, character, default)).Count(item => item.TypeId == Tritanium);
            var balanceAfterSettlement = (await walletStore.GetAsync(character))!.Isk;
            var ownerBalanceAfterSettlement = (await walletStore.GetAsync(corporationId))!.Isk;
            await host.ShutdownAsync();
            host = await HostComposition.BuildAsync(Settings());
            service = host.Registry.Lookup("reprocessingSvc") ?? throw new InvalidOperationException("reprocessingSvc is not registered after restart");
            var retry = Assert.IsType<PyTuple>(await service.CallAsync(PlantCallAt(structureId, session, "Reprocess",
                new PyList([new PyLong(input)]), new PyLong(structureId), new PyLong(character),
                new PyLong(structureId), new PyInt(ItemFlags.Hangar))));
            var retryResult = Assert.IsType<PyList>(retry.Items[1]);
            Assert.Empty(Assert.IsType<PyList>(retryResult.Items[0]).Items);
            Assert.Empty(Assert.IsType<PyDict>(retryResult.Items[1]).Entries);
            Assert.Equal(outputCountAfterSettlement, (await items.InLocationAsync(structureId, character, default)).Count(item => item.TypeId == Tritanium));
            Assert.Equal(balanceAfterSettlement, (await walletStore.GetAsync(character))!.Isk);
            Assert.Equal(ownerBalanceAfterSettlement, (await walletStore.GetAsync(corporationId))!.Isk);
            Assert.Equal(2, notices.Packets.Count);

            long protectedInput;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                protectedInput = await items.CreateAsync(connection, transaction,
                    new NewItem(Veldspar, character, structureId, ItemFlags.Hangar, 100));
                await structures.SetServiceAsync(connection, transaction, structureId, StructureServiceIds.Reprocessing,
                    ServiceState.Offline, 0);
                await transaction.CommitAsync();
            }
            var outputCountBeforeOfflineAttempt = (await items.InLocationAsync(structureId, character, default)).Count(item => item.TypeId == Tritanium);
            await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(PlantCallAt(structureId, session, "Reprocess",
                new PyList([new PyLong(protectedInput)]), new PyLong(structureId), new PyLong(character),
                new PyLong(structureId), new PyInt(ItemFlags.Hangar))).AsTask());
            Assert.NotNull(await items.GetAsync(protectedInput));
            Assert.Equal(outputCountBeforeOfflineAttempt, (await items.InLocationAsync(structureId, character, default)).Count(item => item.TypeId == Tritanium));

            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await structures.SetServiceAsync(connection, transaction, structureId, StructureServiceIds.Reprocessing,
                    ServiceState.Online, now);
                await transaction.CommitAsync();
            }
            var outsiderSession = StructureSession(outsider, structureId, 98_000_031);
            await Assert.ThrowsAsync<MachoWrappedException>(() => service.CallAsync(PlantCallAt(structureId, outsiderSession, "Reprocess",
                new PyList([new PyLong(protectedInput)]), new PyLong(structureId), new PyLong(outsider),
                new PyLong(structureId), new PyInt(ItemFlags.Hangar))).AsTask());
            Assert.NotNull(await items.GetAsync(protectedInput));
            Assert.Equal(outputCountBeforeOfflineAttempt, (await items.InLocationAsync(structureId, character, default)).Count(item => item.TypeId == Tritanium));
            Assert.Equal(2, notices.Packets.Count);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [PgFact]
    public async Task Repair_quotes_and_repairs_a_mutated_module_through_the_registered_host_service()
    {
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var character = await InsertPilotAsync(source, characters);
            var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
            var modules = new PgModuleStateStore(source, Schema("EVE_FITTING_SCHEMA"));
            var prices = new AdjustedPriceStore(source, Schema("EVE_MARKET_SCHEMA"));
            var wallets = new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA"));
            var dogma = await Eve.Dogma.DogmaStatic.LoadAsync(new Eve.Sde.SdeReader(source));
            await dogma.EnsureTypesAsync([12052]);
            var hpAttributeId = dogma.AttributeId("hp") ?? throw new InvalidOperationException("SDE has no hp attribute");
            var sourceHp = dogma.Type(12052)!.Attributes[hpAttributeId];
            var mutatedHp = sourceHp * 1.25;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await prices.ReplaceAsync([new AdjustedPriceRow(47408, 1000, null)], now);
            long mutatedItem;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await wallets.OpenAsync(connection, transaction, character, 100_000m, now);
                mutatedItem = await items.CreateAsync(connection, transaction,
                    new NewItem(47408, character, StationId, ItemFlags.Hangar, 1, Singleton: true));
                await modules.SetModuleConditionsAsync(connection, transaction,
                    new Dictionary<long, ModuleCondition> { [mutatedItem] = new(0.5, 0.25, 0.5, false) });
                await items.Mutations.InsertAsync(connection, transaction, new ItemMutationRecord(
                    mutatedItem, 47297, 9_000_001, 12052, 9_000_002, character, now,
                    new Dictionary<int, double> { [hpAttributeId] = 1.25 },
                    new Dictionary<int, double> { [hpAttributeId] = sourceHp },
                    new Dictionary<int, double> { [hpAttributeId] = mutatedHp }));
                await transaction.CommitAsync();
            }

            await host.ShutdownAsync();
            host = await HostComposition.BuildAsync(Settings());
            var repair = host.Registry.Lookup("repairSvc") ?? throw new InvalidOperationException("repairSvc is not registered");
            var notices = new Capture();
            var session = Session(character, notices);
            var quotes = Assert.IsType<PyTuple>(await repair.CallAsync(RepairCall(session, "GetRepairQuotes", new PyList([new PyLong(mutatedItem)]))));
            var quote = Assert.Single(Assert.IsType<PyDict>(quotes.Items[1]).Entries).Value;
            var row = Assert.Single(Assert.IsType<PyList>(quote).Items);
            Assert.True(Number(QuoteField(row, "damage")) > 0);
            Assert.Equal(mutatedHp, Number(QuoteField(row, "maxHealth")), 6);

            var balanceBefore = (await wallets.GetAsync(character))!.Isk;
            await repair.CallAsync(RepairCall(session, "RepairItemsInStation", new PyList([new PyLong(mutatedItem)]), PyNone.Instance));
            Assert.Equal(ModuleCondition.Mended, (await modules.ModuleConditionsAsync([mutatedItem])).GetValueOrDefault(mutatedItem));
            Assert.True((await wallets.GetAsync(character))!.Isk < balanceBefore);
            Assert.Equal(["OnModuleAttributeChanges", "OnItemChange"], NotificationNames(notices));
            await using var journalConnection = await source.OpenConnectionAsync();
            await using var journal = new NpgsqlCommand(
                $"SELECT count(*) FROM {Schema("EVE_WALLET_SCHEMA")}.journal WHERE owner_id = $1 AND ref_type_id = $2",
                journalConnection);
            journal.Parameters.AddWithValue(character);
            journal.Parameters.AddWithValue(Eve.Services.Station.RepairService.RepairBillRefType);
            Assert.Equal(1L, (long)(await journal.ExecuteScalarAsync())!);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [PgFact]
    public async Task Reprocess_rejects_personal_output_targets_the_client_cannot_select_without_mutation_or_item_notices()
    {
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url));
            var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
            var character = await InsertPilotAsync(source, characters);
            var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
            var wallets = new WalletTransactions(source, Schema("EVE_WALLET_SCHEMA"));
            var balances = new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA"));
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long input;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await wallets.EnsureAsync(connection, transaction, character);
                await wallets.AdjustAsync(connection, transaction, character, 100_000m, 0, 0,
                    "invalid reprocessing target test funds", now, $"reprocess-invalid-target:{Guid.NewGuid():N}");
                input = await items.CreateAsync(connection, transaction,
                    new NewItem(Veldspar, character, StationId, ItemFlags.Hangar, 100));
                await transaction.CommitAsync();
            }

            var beforeBalance = (await balances.GetAsync(character))!.Isk;
            var service = host.Registry.Lookup("reprocessingSvc") ?? throw new InvalidOperationException("reprocessingSvc is not registered");
            var targets = new (PyValue Location, PyValue Flag)[]
            {
                (new PyLong(StationId), new PyInt(ItemFlags.Cargo)),
                (new PyLong(StationId + 1), new PyInt(ItemFlags.Hangar)),
                (PyNone.Instance, new PyInt(ItemFlags.Hangar)),
                (new PyLong(StationId), PyNone.Instance),
            };
            foreach (var (location, flag) in targets)
            {
                var capture = new Capture();
                await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.CallAsync(PlantCall(Session(character, capture), "Reprocess",
                    new PyList([new PyLong(input)]), new PyLong(StationId), new PyLong(character), location, flag)));
                Assert.Empty(capture.Packets);
                Assert.Equal(100, (await items.GetAsync(input))!.Quantity);
                Assert.DoesNotContain(await items.InLocationAsync(StationId, character, default), item => item.TypeId == Tritanium);
                Assert.Equal(beforeBalance, (await balances.GetAsync(character))!.Isk);
            }
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    private static ServiceCall PlantCall(ClientSession session, string method, params PyValue[] args) => new()
    {
        Method = "MachoBindObject",
        Args =
        [
            new PyLong(StationId),
            new PyTuple([new PyStr(method), new PyTuple(args), PyNone.Instance]),
        ],
        Session = session,
    };

    private static ServiceCall PlantCallAt(long location, ClientSession session, string method, params PyValue[] args) => new()
    {
        Method = "MachoBindObject",
        Args =
        [
            new PyLong(location),
            new PyTuple([new PyStr(method), new PyTuple(args), PyNone.Instance]),
        ],
        Session = session,
    };

    private static ClientSession StructureSession(long character, long structureId, long corporationId, IPacketSink? sink = null)
    {
        var session = sink is null
            ? new ClientSession { SessionId = 42, CharacterId = character }
            : new ClientSession(sink) { SessionId = 42, CharacterId = character };
        session.Set("structureid", new PyLong(structureId));
        session.Set("corpid", new PyLong(corporationId));
        session.Set("solarsystemid2", new PyLong(Jita));
        return session;
    }

    private static ServiceCall RepairCall(ClientSession session, string method, params PyValue[] args) => new()
    {
        Method = "MachoBindObject",
        Args =
        [
            new PyTuple([new PyLong(StationId), new PyLong(Jita), new PyInt(1)]),
            new PyTuple([new PyStr(method), new PyTuple(args), PyNone.Instance]),
        ],
        Session = session,
    };

    private static PyValue QuoteField(PyValue quote, string name) => Assert.IsType<PyDict>(Assert.IsType<PyObject>(quote).Args)
        .Entries.Single(entry => entry.Key is PyStr key && key.Value == name).Value;

    private static double Number(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number => number.Value,
        _ => throw new InvalidOperationException(value.ToString()),
    };

    private static async Task<long> InsertPilotAsync(NpgsqlDataSource source, PgCharacterStore characters)
    {
        var pilot = new NewCharacter(AccountId, $"Reprocessing Host Pilot {Guid.NewGuid():N}", Gender: 1, RaceId: 1, BloodlineId: 1, AncestryId: 10,
            SchoolId: 35, TypeId: 1373, CorporationId: 98_000_030, FactionId: 500001, StationId: StationId, SolarSystemId: Jita,
            ConstellationId: 20000020, RegionId: 10000002, DollInfo: null, PortraitInfo: null);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await characters.InsertAsync(connection, transaction, pilot);
        await transaction.CommitAsync();
        return id;
    }

    private static PyValue ClientCallPacket(int callId, params PyValue[] reprocessArgs)
    {
        var source = new MachoAddress(AddressKind.Client, CallId: new PyInt(callId), ClientId: new PyInt(1));
        var destination = new MachoAddress(AddressKind.Node, Service: "reprocessingSvc",
            CallId: new PyInt(callId), NodeId: new PyInt(MachoMessage.ProxyNodeId));
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        var nested = new PyTuple([new PyStr("Reprocess"), new PyTuple(reprocessArgs), PyNone.Instance]);
        var body = new PyTuple([PyNone.Instance, new PyStr("MachoBindObject"), new PyTuple([new PyLong(StationId), nested]), kwargs]);
        var payload = new PyTuple([new PyTuple([PyBool.True, new PySubStream(body)])]);
        return new PyObject(
            new PyStr("carbon.common.script.net.machoNetPacket.CallReq"),
            new PyTuple([
                new PyInt(MachoMessage.CallReq), source.Encode(), destination.Encode(),
                new PyInt(7), payload, new PyDict([]),
                PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance,
                PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance,
            ]));
    }

    private static PyValue Result(PyValue response)
    {
        var packet = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(response).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<PyInt>(packet[0]).Value);
        return Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(packet[4]).Items[0]).Decoded!;
    }

    private static ClientSession Session(long character, IPacketSink? sink = null)
    {
        var session = sink is null ? new ClientSession { SessionId = 42, CharacterId = character } : new ClientSession(sink) { SessionId = 42, CharacterId = character };
        session.Set("stationid", new PyLong(StationId));
        session.Set("solarsystemid2", new PyLong(Jita));
        return session;
    }

    private static string[] NotificationNames(Capture capture) => capture.Packets.OfType<PyObject>().Select(packet =>
    {
        var args = Assert.IsType<PyTuple>(packet.Args).Items;
        Assert.Equal(MachoMessage.Notification, Assert.IsType<PyInt>(args[0]).Value);
        var address = Assert.IsType<PyObject>(args[2]);
        return Assert.IsType<PyStr>(Assert.IsType<PyTuple>(address.Args).Items[1]).Value;
    }).ToArray();

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);
}
