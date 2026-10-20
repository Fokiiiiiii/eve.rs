using System.Text.Json.Nodes;
using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Skills;
using Npgsql;

namespace Eve.Services.Skills.Tests;

/// <summary>Skips a test, loudly, when there is no PostgreSQL to test against (see tools/dev/pg18.sh).</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (Rig.Url is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }
}

internal sealed class Capture : IPacketSink
{
    public List<PyValue> Sent { get; } = [];

    public void Send(PyValue packet) => Sent.Add(packet);
}

/// <summary>The skill services over a scratch schema and a small catalog.</summary>
internal sealed class Rig : IAsyncDisposable
{
    public static readonly string? Url = Environment.GetEnvironmentVariable("EVE_PG_URL");

    public const long ClockMs = 1_700_000_000_123;

    public static readonly SkillCatalog Catalog = new(
        [new SkillType(3300, "Gunnery", 255, "Gunnery", true, 1), new SkillType(3301, "Small Hybrid Turret", 255, "Gunnery", true, 1), new SkillType(3315, "Repair Systems", 1210, "Armor", false, 4)],
        new Dictionary<int, IReadOnlyList<StartingSkill>>());

    private readonly string _schema = $"sks_{Guid.NewGuid():N}"[..20];

    private Rig(NpgsqlDataSource source, SkillCatalog catalog, TrainingSettings? settings = null,
            Eve.Scheduler.LocalTimers? localTimers = null, bool scheduleAnnouncer = false, Func<long, bool>? isAlphaClone = null, Eve.Dogma.DogmaStatic? loadedDogma = null)
    {
        Source = source;
        CatalogForTest = catalog;
        Store = new PgSkillStore(source, _schema);
        ItemStore = new Eve.Inventory.PgItemStore(source, _schema + "i");
        WalletStore = new Eve.Wallet.PgWalletStore(source, _schema + "w");
        Wallet = new Eve.Wallet.WalletTransactions(source, _schema + "w");
        Dogma = loadedDogma ?? TestDogma();
        Training = new CharacterTraining(source, CatalogForTest, Store, new PgTrainingStore(source, _schema),
            settings ?? new TrainingSettings { Attribute = Store.LearningAttribute }, () => Now);
        Sessions = new CharacterSessions();
        Announcer = new TrainingAnnouncer(Training, CatalogForTest, Sessions, null, () => Now,
            schedule: scheduleAnnouncer, localTimers: localTimers);
        Announcer.Attach();
        Handler = new SkillHandlerService(
            Store, CatalogForTest, Training, new Eve.Characters.PgCharacterStore(source, _schema + "c"), ItemStore, Dogma, Announcer, () => Now,
            SkillHandlerService.CreateAugmentationReader(ItemStore, Dogma, Store, () => Now, () => 0d),
            () => 0d, Wallet, isAlphaClone);
        Door = new SkillMgr2Service();
        Hook = new SkillsLoginHook(Training, CatalogForTest, Sessions, Announcer, () => Now);
    }

    public long CharacterId { get; private set; }

    public SkillCatalog CatalogForTest { get; }

    private static Eve.Dogma.DogmaStatic TestDogma() => new([], [
        new Eve.Dogma.EffectDef(7000, "implantMemory", Eve.Dogma.EffectDef.Passive,
            [new Eve.Dogma.Modifier(Eve.Dogma.ModifierFunc.Item, "charID", Eve.Dogma.Operation.ModAdd, 166, 177)]),
        // an implant that adds its bonus (attribute 9950) to the smuggling chance (445) of its pilot
        new Eve.Dogma.EffectDef(7003, "implantSmuggling", Eve.Dogma.EffectDef.Passive,
            [new Eve.Dogma.Modifier(Eve.Dogma.ModifierFunc.Item, "charID", Eve.Dogma.Operation.ModAdd, 445, 9950)]),
        new Eve.Dogma.EffectDef(7001, "boosterPrimary", Eve.Dogma.EffectDef.Activation,
            [new Eve.Dogma.Modifier(Eve.Dogma.ModifierFunc.Item, "charID", Eve.Dogma.Operation.ModAdd, 164, 1126)]),
        new Eve.Dogma.EffectDef(7002, "boosterSideEffect", Eve.Dogma.EffectDef.Activation,
            [new Eve.Dogma.Modifier(Eve.Dogma.ModifierFunc.Item, "charID", Eve.Dogma.Operation.ModAdd, 165, 1127)])
            { ChanceAttributeId = 9999 },
    ], [
        // SDE 3396210: Memory Augmentation - Basic (9941), categoryImplant, implantness=2, memoryBonus=3.
        new Eve.Dogma.TypeDef(9941, 745, 20, new Dictionary<int, double> { [331] = 2, [177] = 3 }, [7000]),
        new Eve.Dogma.TypeDef(9942, 745, 20, new Dictionary<int, double> { [331] = 2, [9950] = 0.25 }, [7003]),
        // Pinned client dogma constants: boosterness=1087 and boosterDuration=330.
        new Eve.Dogma.TypeDef(2838, 988, 20, new Dictionary<int, double>
        {
            [1087] = 1, [330] = 60_000, [1126] = 4, [1127] = -2, [9999] = 0.5,
        }, [7001, 7002]),
    ]);

    /// <summary>The clock of the rig: it stands still until a test moves it.</summary>
    public long Now = ClockMs;

    public void Wait(TimeSpan time) => Now += (long)time.TotalMilliseconds;

    public CharacterTraining Training { get; }

    public CharacterSessions Sessions { get; }

    public TrainingAnnouncer Announcer { get; }

    public Eve.Characters.PgCharacterStore Characters => new(Source, _schema + "c");
    public NpgsqlDataSource Source { get; }

    public string SchemaForTest => _schema;

    public PgSkillStore Store { get; }

    public Eve.Inventory.PgItemStore ItemStore { get; }
public Eve.Wallet.PgWalletStore WalletStore { get; }
    public Eve.Wallet.WalletTransactions Wallet { get; }

    public Eve.Dogma.DogmaStatic Dogma { get; }

    public SkillHandlerService Handler { get; }

    public SkillMgr2Service Door { get; }

    public SkillsLoginHook Hook { get; }

    public static async Task<Rig> CreateAsync(TrainingSettings? settings = null,
            Eve.Scheduler.LocalTimers? localTimers = null, bool scheduleAnnouncer = false,
            Func<long, bool>? isAlphaClone = null, bool useCurrentSdeCatalog = false, int accountId = 34)
        {
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url!));
            var catalog = Catalog;
            Eve.Dogma.DogmaStatic? loadedDogma = null;
            if (useCurrentSdeCatalog)
            {
                var sde = new Eve.Sde.SdeReader(source);
                if (await sde.BuildAsync() is null)
                    throw new InvalidOperationException("The seeded SDE is required for this skills test.");
                catalog = await SkillCatalog.LoadAsync(sde);
                loadedDogma = await Eve.Dogma.DogmaStatic.LoadAsync(sde);
            }
            var rig = new Rig(source, catalog, settings, localTimers, scheduleAnnouncer, isAlphaClone, loadedDogma);
            await PgSkillStore.MigrateAsync(source, rig._schema);
            await Eve.Characters.PgCharacterStore.MigrateAsync(source, rig._schema + "c");
            await Eve.Inventory.PgItemStore.MigrateAsync(source, rig._schema + "i");
            await Eve.Wallet.PgWalletStore.MigrateAsync(source, rig._schema + "w");
            rig.CharacterId = await rig.CreateCharacterAsync("Skills Test Character", accountId);
            return rig;
        }

    public async Task GrantAsync(long characterId, params (int Type, int Level, long Points)[] skills)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Store.GrantAsync(connection, transaction, characterId, skills.Select(s => new CharacterSkill(s.Type, s.Level, s.Points)).ToList());
        await transaction.CommitAsync();
    }

    public async Task GrantTrackedAsync(long characterId, params (int Type, int Level, long Points)[] skills)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Store.GrantAsync(connection, transaction, characterId,
            skills.Select(s => new CharacterSkill(s.Type, s.Level, s.Points)).ToList(),
            trackRecentChanges: true);
        await transaction.CommitAsync();
    }

    public async Task<long> GrantFreeSkillPointsAsync(long characterId, long points)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var balance = await Store.GrantFreeSkillPointsAsync(connection, transaction, characterId, points);
        await transaction.CommitAsync();
        return balance;
    }

    public async Task<long> CreateItemAsync(Eve.Inventory.NewItem item)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var itemId = await ItemStore.CreateAsync(connection, transaction, item);
        await transaction.CommitAsync();
        return itemId;
    }

    public async Task<long> CreateCharacterAsync(string name, int accountId = 34)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var characterId = await Characters.InsertAsync(connection, transaction,
                new Eve.Characters.NewCharacter(accountId, name, 0, 1, 1, 1, 11, 1376,
                    1000044, null, 60_000_001, 30_000_001, 20_000_001, 10_000_001, null, null));
            await transaction.CommitAsync();
            return characterId;
        }

public async Task FundWalletAsync(long ownerId, decimal isk)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await WalletStore.OpenAsync(connection, transaction, ownerId, isk, 0);
        await transaction.CommitAsync();
    }

    public static (ClientSession Session, Capture Sink) Session(long? characterId, long userId = 34)
    {
        var sink = new Capture();
        var session = new ClientSession(sink) { SessionId = 26_859_804_000_001, ClientId = 34_065_450, UserId = userId };
        if (characterId is { } id)
        {
            session.CharacterId = id;
        }
        return (session, sink);
    }

    public static async Task<PyValue> CallAsync(IService service, ClientSession session, string method, params PyValue[] args) =>
        await service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session });

    public async ValueTask DisposeAsync()
    {
        await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {_schema} CASCADE; DROP SCHEMA IF EXISTS {_schema}c CASCADE; DROP SCHEMA IF EXISTS {_schema}i CASCADE; DROP SCHEMA IF EXISTS {_schema}w CASCADE");
        await command.ExecuteNonQueryAsync();
        await Source.DisposeAsync();
    }
}

public class SkillWireTests
{
    [Fact]
    public void A_skill_item_id_is_made_from_the_character_and_the_type_as_the_reference_makes_it()
    {
        Assert.Equal(14_000_003_803_300, SkillWire.ItemId(140_000_038, 3300)); // recorded
        Assert.Equal(14_000_007_403_315, SkillWire.ItemId(140_000_074, 3315)); // recorded
        Assert.NotEqual(SkillWire.ItemId(140_000_038, 3301), SkillWire.ItemId(140_000_039, 3301));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100_000)]
    public void A_type_that_would_run_into_the_next_characters_ids_is_refused(int typeId) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SkillWire.ItemId(1, typeId));

    [Fact]
    public void The_notification_carries_the_bytes_the_reference_sent_for_the_same_skill()
    {
        // OnServerSkillsChanged as recorded from the reference for character 140000038: Gunnery at level 4, 45255 points
        var recorded = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "scenarios", "login_to_character.json")))!;
        var packet = recorded["afterSelect"]![1]!["$tuple"]!;
        var buffer = Convert.FromHexString((string)packet[4]!["$tuple"]![0]!["$tuple"]![1]!["$buffer"]!);

        var decoded = (PyTuple)MarshalCodec.Decode(buffer);
        var time = ((PyLong)((PyTuple)((PyTuple)decoded.Items[1]).Items[1]).Items[2]).Value;

        var entry = SkillWire.EntryDictionary(140_000_038, Rig.Catalog, [new CharacterSkill(3300, 4, 45_255)]);
        var built = ClientNotification.Build(34, MachoMessage.ProxyNodeId, "OnServerSkillsChanged", "charid", [entry, PyNone.Instance, new PyLong(time)], 0);

        var sent = (PyTuple)((PyObject)built).Args;
        var inner = (PyBuffer)((PyTuple)((PyTuple)sent.Items[4]).Items[0]).Items[1];
        Assert.Equal(buffer, inner.Value.ToArray());
    }
}

public class ClientNotificationTests
{
    [Fact]
    public void A_notification_has_the_shape_the_reference_sends_and_counts_from_zero()
    {
        var (session, sink) = Rig.Session(140_000_038);
        session.SendNotification("OnServerSkillsChanged", "charid", new PyInt(1), PyNone.Instance);
        session.SendNotification("OnServerSkillsChanged", "charid", new PyInt(2));

        var first = (PyTuple)((PyObject)sink.Sent[0]).Args;
        Assert.Equal(14, first.Items.Count);
        Assert.Equal(new PyInt(12), first.Items[0]);
        Assert.Equal(new PyTuple([new PyInt(1), new PyInt(65_450), PyNone.Instance, PyNone.Instance]), ((PyObject)first.Items[1]).Args);
        Assert.Equal(new PyTuple([new PyInt(4), new PyStr("OnServerSkillsChanged"), new PyList([]), new PyStr("charid")]), ((PyObject)first.Items[2]).Args);
        Assert.Equal(new PyInt(34), first.Items[3]);
        Assert.Equal(new PyDict([new(new PyStr("sn"), new PyInt(0))]), first.Items[5]);
        Assert.All(first.Items.Skip(6), item => Assert.Equal(PyNone.Instance, item));
        Assert.Equal(new PyDict([new(new PyStr("sn"), new PyInt(1))]), ((PyTuple)((PyObject)sink.Sent[1]).Args).Items[5]);

        // the arguments are a marshalled stream of their own: (0, (1, args))
        var inner = (PyBuffer)((PyTuple)((PyTuple)first.Items[4]).Items[0]).Items[1];
        var decoded = (PyTuple)MarshalCodec.Decode(inner.Value.ToArray());
        Assert.Equal(new PyInt(0), decoded.Items[0]);
        var args = (PyTuple)((PyTuple)decoded.Items[1]).Items[1];
        Assert.Equal(2, args.Items.Count);
        Assert.Equal(new PyInt(1), args.Items[0]);
    }

    [Fact]
    public async Task Concurrent_notifications_are_each_numbered_once()
    {
        var (session, sink) = Rig.Session(1);
        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => session.SendNotification("OnX", "charid"))));
        var numbers = sink.Sent.Select(p => ((PyInt)((PyDict)((PyTuple)((PyObject)p).Args).Items[5]).Entries[0].Value).Value).ToArray();
        Assert.Equal(Enumerable.Range(0, 200).Select(i => (long)i), numbers);
    }
}

public class SkillHandlerTests
{

    private static string CustomNotifyOf(MachoWrappedException error)
        {
            var payload = Assert.IsType<PyObjectEx>(error.Error.Payload);
            var header = Assert.IsType<PyTuple>(payload.Header);
            var details = Assert.IsType<PyTuple>(header.Items[1]);
            var values = Assert.IsType<PyDict>(details.Items[1]);
            var notify = Assert.Single(values.Entries, entry => JsSemantics.StringValue(entry.Key) == "notify").Value;
            return Assert.IsType<PyStr>(notify).Value;
        }

[PgFact]
    public async Task Direct_skill_purchase_charges_the_published_list_price_and_injects_the_skill()
    {
        await using var rig = await Rig.CreateAsync(useCurrentSdeCatalog: true);
        var characterId = rig.CharacterId;
        var skill = rig.CatalogForTest.Skills.Single(type => type.TypeId == 3300);
        Assert.True(skill.AvailableForPurchase);
        Assert.True(skill.BasePrice > 0);
        await rig.FundWalletAsync(characterId, 10_000_000m);
        var session = Rig.Session(characterId).Session;
        var price = decimal.Round(skill.BasePrice * 1.3m, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(PyBool.True, await Rig.CallAsync(rig.Handler, session, "IsSkillAvailableForPurchase", new PyInt(skill.TypeId)));
        Assert.Equal(Py.Number((double)(skill.BasePrice * 1.3m)), await Rig.CallAsync(rig.Handler, session, "GetDirectPurchasePrice", new PyInt(skill.TypeId)));

        var result = Assert.IsType<PyTuple>(await Rig.CallAsync(rig.Handler, session, "PurchaseSkills", new PyList([new PyInt(skill.TypeId)])));
        Assert.Equal([new PyInt(skill.TypeId)], result.Items);
        Assert.Contains(await rig.Store.ForCharacterAsync(characterId), trained => trained.TypeId == skill.TypeId && trained.Level == 0);
        var journal = await rig.Wallet.JournalAsync(characterId);
        var debit = Assert.Single(journal, entry => entry.OperationKey.Contains("skill-purchase", StringComparison.Ordinal));
        Assert.Equal(-price, debit.Amount);
    }

    [PgFact]
    public async Task Skill_catalog_maps_the_current_sde_skill_injector_item_names()
    {
        await using var rig = await Rig.CreateAsync(useCurrentSdeCatalog: true);
        var catalog = rig.CatalogForTest;

        Assert.Equal<int?>(670, catalog.ItemTypeId("Capsule"));
        Assert.Equal<int?>(40519, catalog.ItemTypeId("Skill Extractor"));
        Assert.Equal<int?>(40520, catalog.ItemTypeId("Large Skill Injector"));
        Assert.Equal<int?>(45635, catalog.ItemTypeId("Small Skill Injector"));
        Assert.Equal<int?>(46375, catalog.ItemTypeId("Daily Alpha Injector"));
        Assert.Null(catalog.ItemTypeId("Daily Alpha Skill Injector"));
        Assert.Null(catalog.ItemTypeId("Skill Injector"));
    }

    [PgFact]
    public async Task Skill_bulk_item_operations_refuse_more_than_500_raw_entries_before_mutation()
    {
        await using var rig = await Rig.CreateAsync(useCurrentSdeCatalog: true);
        var characterId = rig.CharacterId;
        const long station = 60_000_001;
        var session = Rig.Session(characterId).Session;
        session.Set("stationid", new PyInt(station));

        var itemIds = new PyList([.. Enumerable.Range(1, 501).Select(id => (PyValue)new PyLong(id))]);
        var injectionError = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            Rig.CallAsync(rig.Handler, session, "InjectSkillIntoBrain", itemIds));
        Assert.Equal("SkillBulkLimitExceeded", injectionError.Error.UserErrorMessage);
        Assert.Empty(await rig.Store.ForCharacterAsync(characterId));

        var capsuleType = rig.CatalogForTest.ItemTypeId("Capsule");
        var extractorType = rig.CatalogForTest.ItemTypeId("Skill Extractor");
        var injectorType = rig.CatalogForTest.ItemTypeId("Large Skill Injector");
        Assert.NotNull(capsuleType);
        Assert.NotNull(extractorType);
        Assert.NotNull(injectorType);
        var capsule = await rig.CreateItemAsync(new Eve.Inventory.NewItem(capsuleType.Value, characterId, station, 4, 1, true));
        var extractor = await rig.CreateItemAsync(new Eve.Inventory.NewItem(extractorType.Value, characterId, station, 4, 1, true));
        session.Set("shipid", new PyLong(capsule));
        var targets = new PyDict([.. Enumerable.Range(1, 501).Select(typeId =>
            new KeyValuePair<PyValue, PyValue>(new PyInt(typeId), new PyLong(long.MaxValue))) ]);

        var extractionError = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            Rig.CallAsync(rig.Handler, session, "ExtractSkills", new PyLong(extractor), targets));
        Assert.Equal("SkillBulkLimitExceeded", extractionError.Error.UserErrorMessage);
        Assert.NotNull(await rig.ItemStore.GetAsync(extractor));
        Assert.DoesNotContain(await rig.ItemStore.InLocationAsync(station, characterId), item => item.TypeId == injectorType.Value);
    }

[PgFact]
    public async Task Injecting_a_skill_book_consumes_the_owned_book_and_adds_the_skill()
    {
        await using var rig = await Rig.CreateAsync();
        var characterId = rig.CharacterId;
        var skill = Rig.Catalog.Skills.First(type => type.Published && type.TypeId != 33092);
        var book = await rig.CreateItemAsync(new Eve.Inventory.NewItem(skill.TypeId, characterId, 60_000_001, 4, 1, true));
        var session = Rig.Session(characterId).Session;
        session.Set("stationid", new PyInt(60_000_001));

        var result = Assert.IsType<PyList>(await Rig.CallAsync(rig.Handler, session, "InjectSkillIntoBrain", new PyList([new PyLong(book)])));
        Assert.Equal([new PyInt(skill.TypeId)], result.Items);
        Assert.Null(await rig.ItemStore.GetAsync(book));
        Assert.Contains(await rig.Store.ForCharacterAsync(characterId), trained => trained.TypeId == skill.TypeId && trained.Level == 0);
    }

[PgFact]
    public async Task Skill_injectors_grant_diminishing_free_points_and_consume_the_stack()
    {
        await using var rig = await Rig.CreateAsync(useCurrentSdeCatalog: true);
        var characterId = rig.CharacterId;
        var skill = rig.CatalogForTest.Skills.Single(type => type.TypeId == 3300).TypeId;
        await rig.GrantAsync(characterId, (skill, 5, 4_950_000));
        var injectorType = rig.CatalogForTest.ItemTypeId("Small Skill Injector");
        Assert.NotNull(injectorType);
        var injector = await rig.CreateItemAsync(new Eve.Inventory.NewItem(injectorType.Value, characterId, 60_000_001, 4, 2, false));
        var session = Rig.Session(characterId).Session;

        Assert.Equal(new PyInt(180_000), await Rig.CallAsync(rig.Handler, session, "GetDiminishedSpFromInjectors", new PyInt(injectorType.Value), new PyInt(2), new PyInt(0)));
        Assert.Equal(new PyInt(180_000), await Rig.CallAsync(rig.Handler, session, "InjectSkillpoints", new PyLong(injector), new PyInt(2)));
        Assert.Null(await rig.ItemStore.GetAsync(injector));
    }


    [PgFact]
    public async Task Injector_diminishing_total_saturates_without_overflowing_authoritative_point_balances()
    {
        await using var rig = await Rig.CreateAsync(useCurrentSdeCatalog: true);
        var characterId = rig.CharacterId;
        var skill = rig.CatalogForTest.Skills.Single(type => type.TypeId == 3300).TypeId;
        await rig.GrantAsync(characterId, (skill, 5, 4_950_000));
        await rig.GrantFreeSkillPointsAsync(characterId, long.MaxValue - 50_000);
        var injectorType = rig.CatalogForTest.ItemTypeId("Small Skill Injector");
        Assert.NotNull(injectorType);
        var injector = await rig.CreateItemAsync(new Eve.Inventory.NewItem(injectorType.Value, characterId, 60_000_001, 4, 2, false));
        var session = Rig.Session(characterId).Session;

        Assert.Equal(new PyInt(30_000), await Rig.CallAsync(rig.Handler, session, "InjectSkillpoints", new PyLong(injector), new PyInt(1)));
        Assert.Equal(long.MaxValue - 20_000, await rig.Store.FreeSkillPointsAsync(characterId));
        Assert.Equal(1, (await rig.ItemStore.GetAsync(injector))?.Quantity);
    }

    [PgFact]
    public async Task Injector_preview_uses_per_item_large_and_small_bands_and_non_diminishing_uses()
    {
        await using var rig = await Rig.CreateAsync(useCurrentSdeCatalog: true);
        var character = rig.CharacterId;
        var large = rig.CatalogForTest.ItemTypeId("Large Skill Injector")!.Value;
        var small = rig.CatalogForTest.ItemTypeId("Small Skill Injector")!.Value;
        Assert.Equal(40520, large);
        Assert.Equal(45635, small);
        var session = Rig.Session(character).Session;
        async Task<PyValue> Preview(int typeId, int quantity = 1, int nonDiminishing = 0) =>
            await Rig.CallAsync(rig.Handler, session, "GetDiminishedSpFromInjectors",
                new PyInt(typeId), new PyInt(quantity), new PyInt(nonDiminishing));

        Assert.Equal(new PyInt(500_000), await Preview(large));
        Assert.Equal(new PyInt(100_000), await Preview(small));
        await rig.GrantFreeSkillPointsAsync(character, 4_501_000);
        Assert.Equal(new PyInt(900_000), await Preview(large, 2));
        await rig.GrantFreeSkillPointsAsync(character, 449_000);
        Assert.Equal(new PyInt(180_000), await Preview(small, 2));
        await rig.GrantFreeSkillPointsAsync(character, 50_000);
        Assert.Equal(new PyInt(400_000), await Preview(large));
        Assert.Equal(new PyInt(80_000), await Preview(small));
        Assert.Equal(new PyInt(800_000), await Preview(large, 2));
        Assert.Equal(new PyInt(160_000), await Preview(small, 2));
        Assert.Equal(new PyInt(900_000), await Preview(large, 2, 1));
        Assert.Equal(new PyInt(180_000), await Preview(small, 2, 1));
        await rig.GrantFreeSkillPointsAsync(character, 44_999_999);
        Assert.Equal(new PyInt(400_000), await Preview(large));
        Assert.Equal(new PyInt(80_000), await Preview(small));
        await rig.GrantFreeSkillPointsAsync(character, 1);
        Assert.Equal(new PyInt(300_000), await Preview(large));
        Assert.Equal(new PyInt(60_000), await Preview(small));
        await rig.GrantFreeSkillPointsAsync(character, 29_999_999);
        Assert.Equal(new PyInt(300_000), await Preview(large));
        Assert.Equal(new PyInt(60_000), await Preview(small));

        await rig.GrantFreeSkillPointsAsync(character, 1);
        Assert.Equal(new PyInt(150_000), await Preview(large));
        Assert.Equal(new PyInt(30_000), await Preview(small));
    }

    [PgFact]
    public async Task Daily_alpha_injector_is_limited_to_alpha_characters_and_once_per_day()
    {
        long? seenAlphaAccount = null;
        await using (var alphaRig = await Rig.CreateAsync(
            isAlphaClone: accountId => { seenAlphaAccount = accountId; return true; },
            useCurrentSdeCatalog: true,
            accountId: 77_001))
        {
            var characterId = alphaRig.CharacterId;
            var dailyAlphaType = alphaRig.CatalogForTest.ItemTypeId("Daily Alpha Injector");
            Assert.NotNull(dailyAlphaType);
            var session = Rig.Session(characterId, userId: 77_001).Session;
            var first = await alphaRig.CreateItemAsync(new Eve.Inventory.NewItem(dailyAlphaType.Value, characterId, 60_000_001, 4, 1, true));
            Assert.Equal(new PyInt(50_000), await Rig.CallAsync(alphaRig.Handler, session, "InjectSkillpoints", new PyLong(first), new PyInt(1)));
            Assert.Equal(77_001, seenAlphaAccount);
            var second = await alphaRig.CreateItemAsync(new Eve.Inventory.NewItem(dailyAlphaType.Value, characterId, 60_000_001, 4, 1, true));
            var cooldown = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
                await Rig.CallAsync(alphaRig.Handler, session, "InjectSkillpoints", new PyLong(second), new PyInt(1)));
            Assert.Equal("A Daily Alpha Injector can only be used once per day.", CustomNotifyOf(cooldown));
        }

        await using (var omegaRig = await Rig.CreateAsync(isAlphaClone: _ => false, useCurrentSdeCatalog: true))
        {
            var characterId = omegaRig.CharacterId;
            var dailyAlphaType = omegaRig.CatalogForTest.ItemTypeId("Daily Alpha Injector");
            Assert.NotNull(dailyAlphaType);
            var session = Rig.Session(characterId).Session;
            var item = await omegaRig.CreateItemAsync(new Eve.Inventory.NewItem(dailyAlphaType.Value, characterId, 60_000_001, 4, 1, true));
            var denied = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
                await Rig.CallAsync(omegaRig.Handler, session, "InjectSkillpoints", new PyLong(item), new PyInt(1)));
            Assert.Equal("That skill injector cannot be used.", CustomNotifyOf(denied));
        }
    }

    [PgFact]
    public async Task Alpha_clones_can_extract_only_omega_restricted_skills()
    {
        long? seenAlphaAccount = null;
        await using var rig = await Rig.CreateAsync(
            isAlphaClone: accountId => { seenAlphaAccount = accountId; return true; },
            useCurrentSdeCatalog: true,
            accountId: 77_003);
        var characterId = rig.CharacterId;
        const long station = 60_000_001;
        var catalog = rig.CatalogForTest;
        var character = await rig.Characters.GetAsync(characterId)
            ?? throw new InvalidOperationException("The test character is missing.");
        var drones = catalog.Skills.Single(skill => skill.Name == "Drones");
        var gunnery = catalog.Skills.Single(skill => skill.Name == "Gunnery");
        Assert.Equal(5, catalog.AlphaMaxLevel(character.RaceId, drones.TypeId));
        Assert.Equal(5, catalog.AlphaMaxLevel(character.RaceId, gunnery.TypeId));

        var alphaSkillIds = new HashSet<int> { drones.TypeId, gunnery.TypeId };
        var omegaOnly = catalog.Skills.First(skill =>
            skill.Published
            && skill.Rank >= 2
            && catalog.AlphaMaxLevel(character.RaceId, skill.TypeId) == 0
            && !catalog.Skills.Any(dependent => dependent.Needs.Any(requirement => requirement.TypeId == skill.TypeId))
            && !skill.Needs.Any(requirement => alphaSkillIds.Contains(requirement.TypeId)));
        Assert.Equal(0, catalog.AlphaMaxLevel(character.RaceId, omegaOnly.TypeId));

        var dronesPoints = Eve.Skills.SkillPoints.ForTraining(drones.Rank, 5);
        var gunneryPoints = 500_000L - dronesPoints;
        Assert.InRange(gunneryPoints, 1, Eve.Skills.SkillPoints.ForTraining(gunnery.Rank, 5));
        var selected = new HashSet<int> { drones.TypeId, gunnery.TypeId, omegaOnly.TypeId };
        var dependents = catalog.Skills
            .Where(skill => skill.Needs.Any(requirement => selected.Contains(requirement.TypeId)))
            .Select(skill => skill.TypeId)
            .ToHashSet();
        var allocations = new List<(int Type, int Level, long Points)>
        {
            (drones.TypeId, Eve.Skills.SkillPoints.LevelAt(drones.Rank, dronesPoints), dronesPoints),
            (gunnery.TypeId, Eve.Skills.SkillPoints.LevelAt(gunnery.Rank, gunneryPoints), gunneryPoints),
            (omegaOnly.TypeId, Eve.Skills.SkillPoints.LevelAt(omegaOnly.Rank, 500_000), 500_000),
        };
        var total = allocations.Sum(skill => skill.Points);
        foreach (var skill in catalog.Skills.Where(skill =>
                     !selected.Contains(skill.TypeId) && !dependents.Contains(skill.TypeId)))
        {
            if (total >= 5_500_000) break;
            var points = Math.Min(Eve.Skills.SkillPoints.ForTraining(skill.Rank, 5), 5_500_000 - total);
            if (points <= 0) continue;
            allocations.Add((skill.TypeId, Eve.Skills.SkillPoints.LevelAt(skill.Rank, points), points));
            total += points;
        }
        Assert.True(total >= 5_500_000, $"SDE build did not provide enough eligible test skills: {total} SP.");
        await rig.GrantAsync(characterId, [.. allocations]);

        var capsuleType = catalog.ItemTypeId("Capsule");
        var extractorType = catalog.ItemTypeId("Skill Extractor");
        Assert.NotNull(capsuleType);
        Assert.NotNull(extractorType);
        var capsule = await rig.CreateItemAsync(new Eve.Inventory.NewItem(capsuleType.Value, characterId, station, 4, 1, true));
        var alphaExtractor = await rig.CreateItemAsync(new Eve.Inventory.NewItem(extractorType.Value, characterId, station, 4, 1, true));
        var omegaExtractor = await rig.CreateItemAsync(new Eve.Inventory.NewItem(extractorType.Value, characterId, station, 4, 1, true));
        var session = Rig.Session(characterId, userId: 77_003).Session;
        session.Set("stationid", new PyInt(station));
        session.Set("shipid", new PyLong(capsule));

        var denied = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await Rig.CallAsync(rig.Handler, session, "ExtractSkills",
                new PyLong(alphaExtractor),
                new PyDict([
                    new(new PyInt(drones.TypeId), new PyLong(dronesPoints)),
                    new(new PyInt(gunnery.TypeId), new PyLong(gunneryPoints))
                ])));
        Assert.Equal("Alpha clones may only extract skills that require Omega clone state.", CustomNotifyOf(denied));
        Assert.NotNull(await rig.ItemStore.GetAsync(alphaExtractor));

        Assert.IsType<PyNone>(await Rig.CallAsync(rig.Handler, session, "ExtractSkills",
            new PyLong(omegaExtractor),
            new PyDict([new(new PyInt(omegaOnly.TypeId), new PyLong(500_000))])));
        Assert.Equal(77_003, seenAlphaAccount);
        Assert.Null(await rig.ItemStore.GetAsync(omegaExtractor));
        Assert.Equal(0, (await rig.Store.ForCharacterAsync(characterId)).Single(skill => skill.TypeId == omegaOnly.TypeId).SkillPoints);
    }

    [PgFact]
    public async Task ExtractSkills_consumes_an_extractor_and_creates_an_injector_after_removing_exactly_500000_points()
    {
        long? seenAlphaAccount = null;
        await using var rig = await Rig.CreateAsync(isAlphaClone: accountId =>
        {
            seenAlphaAccount = accountId;
            return false;
        }, useCurrentSdeCatalog: true, accountId: 77_002);
        var characterId = rig.CharacterId;
        var catalog = rig.CatalogForTest;
        var catalogSkills = catalog.Skills;
        var target = catalogSkills.First(skill => skill.Rank >= 2
            && !catalogSkills.Any(dependent => dependent.Needs.Any(requirement => requirement.TypeId == skill.TypeId)));
        var allocations = new List<(int Type, int Level, long Points)>();
        var targetPoints = Eve.Skills.SkillPoints.ForTraining(target.Rank, 5);
        allocations.Add((target.TypeId, 5, targetPoints));
        var total = targetPoints;
        foreach (var skill in catalogSkills.Where(skill => skill.TypeId != target.TypeId))
        {
            if (total >= 5_500_000) break;
            var points = Math.Min(Eve.Skills.SkillPoints.ForTraining(skill.Rank, 5), 5_500_000 - total);
            if (points <= 0) continue;
            allocations.Add((skill.TypeId, Eve.Skills.SkillPoints.LevelAt(skill.Rank, points), points));
            total += points;
        }
        Assert.True(total >= 5_500_000);
        await rig.GrantAsync(characterId, [.. allocations]);

        const long station = 60_000_001;
        var capsuleType = catalog.ItemTypeId("Capsule");
        var extractorType = catalog.ItemTypeId("Skill Extractor");
        var injectorType = catalog.ItemTypeId("Large Skill Injector");
        Assert.NotNull(capsuleType);
        Assert.NotNull(extractorType);
        Assert.NotNull(injectorType);
        var capsule = await rig.CreateItemAsync(new Eve.Inventory.NewItem(capsuleType.Value, characterId, station, 4, 1, true));
        var extractor = await rig.CreateItemAsync(new Eve.Inventory.NewItem(extractorType.Value, characterId, station, 4, 1, true));
        var session = Rig.Session(characterId, userId: 77_002).Session;
        session.Set("stationid", new PyInt(station));
        session.Set("shipid", new PyLong(capsule));

        Assert.IsType<PyNone>(await Rig.CallAsync(rig.Handler, session, "ExtractSkills",
            new PyLong(extractor),
            new PyList([Py.Tuple(new PyInt(target.TypeId), new PyLong(long.MaxValue - 5)), Py.Tuple(new PyInt(target.TypeId), new PyLong(10))])));
        Assert.Equal(77_002, seenAlphaAccount);

        Assert.Null(await rig.ItemStore.GetAsync(extractor));
        var remaining = (await rig.Store.ForCharacterAsync(characterId)).Single(skill => skill.TypeId == target.TypeId);
        Assert.Equal(targetPoints - 500_000, remaining.SkillPoints);
        Assert.Equal(Eve.Skills.SkillPoints.LevelAt(target.Rank, targetPoints - 500_000), remaining.Level);
        var stationItems = await rig.ItemStore.InLocationAsync(station, characterId);
        Assert.Contains(stationItems, item => item.TypeId == injectorType.Value);
    }

    [PgFact]
    public async Task Daily_alpha_injector_is_spent_until_the_next_11_utc_downtime()
    {
        await using var rig = await Rig.CreateAsync(useCurrentSdeCatalog: true);
        var typeId = rig.CatalogForTest.ItemTypeId("Daily Alpha Injector");
        Assert.NotNull(typeId);
        const long day = 86_400_000;
        const long midnight = 19_675 * day;
        const long downtime = midnight + 11 * 3_600_000;
        Task<bool> Use(long at) => rig.Store.InTransactionAsync(
            (connection, transaction, token) => rig.Store.TryUseDailyAlphaSkillInjectorAsync(connection, transaction, rig.CharacterId, typeId.Value, at, token),
            CancellationToken.None);

        Assert.True(await Use(downtime - 1));           // the first use, just before the downtime: that use is spent at the downtime
        Assert.True(await Use(downtime));                // the downtime resets it
        Assert.False(await Use(downtime + 3_600_000));   // spent until the next downtime
        Assert.False(await Use(downtime + day - 1));
        Assert.True(await Use(downtime + day));
    }

    [Fact]
    public void The_daily_downtime_is_the_first_11_utc_after_the_moment()
    {
        const long day = 86_400_000;
        const long downtime = 19_675 * day + 11 * 3_600_000;
        Assert.Equal(downtime, SkillInjectorDowntime.NextAfterMs(downtime - 1));
        Assert.Equal(downtime + day, SkillInjectorDowntime.NextAfterMs(downtime));
        Assert.Equal(downtime - day, SkillInjectorDowntime.PreviousAtOrBeforeMs(downtime - 1));
        Assert.Equal(downtime, SkillInjectorDowntime.PreviousAtOrBeforeMs(downtime + 1));
    }

    [PgFact]
    public async Task Daily_alpha_clock_answers_zero_then_the_next_downtime_after_a_use_and_keeps_it_across_a_restart()
    {
        await using var rig = await Rig.CreateAsync(isAlphaClone: _ => true, useCurrentSdeCatalog: true, accountId: 77_004);
        var characterId = rig.CharacterId;
        var session = Rig.Session(characterId, userId: 77_004).Session;
        var clock = new AlphaInjectorMgrService(rig.Store, rig.CatalogForTest);
        Assert.Equal(new PyLong(0), await Rig.CallAsync(clock, session, "GetNextAvailableInjection"));

        var dailyAlphaType = rig.CatalogForTest.ItemTypeId("Daily Alpha Injector");
        Assert.NotNull(dailyAlphaType);
        var item = await rig.CreateItemAsync(new Eve.Inventory.NewItem(dailyAlphaType.Value, characterId, 60_000_001, 4, 1, true));
        Assert.Equal(new PyInt(50_000), await Rig.CallAsync(rig.Handler, session, "InjectSkillpoints", new PyLong(item), new PyInt(1)));
        var next = new PyLong(Py.FileTimeFromUnixMs(SkillInjectorDowntime.NextAfterMs(rig.Now)));
        Assert.Equal(next, await Rig.CallAsync(clock, session, "GetNextAvailableInjection"));

        var restarted = new AlphaInjectorMgrService(new PgSkillStore(rig.Source, rig.SchemaForTest), rig.CatalogForTest);
        Assert.Equal(next, await Rig.CallAsync(restarted, session, "GetNextAvailableInjection"));
    }

    [PgFact]
    public async Task GetSkillChangesForISIS_answers_an_empty_change_list_when_no_ISIS_skill_changes_are_pending()
    {
        await using var rig = await Rig.CreateAsync();
        var result = Assert.IsType<PyList>(await Rig.CallAsync(rig.Handler, Rig.Session(rig.CharacterId).Session, "GetSkillChangesForISIS"));
        Assert.Empty(result.Items);
    }

    [PgFact]
    public async Task GetSkillChangesForISIS_returns_sorted_cumulative_positive_deltas_for_four_reads_then_clears()
    {
        await using var rig = await Rig.CreateAsync();
        var characterId = rig.CharacterId;
        await rig.GrantAsync(characterId, (3436, 1, 100), (3300, 1, 50));
        await rig.GrantTrackedAsync(characterId, (3436, 1, 130), (3300, 1, 75));
        await rig.GrantTrackedAsync(characterId, (3436, 1, 150), (3300, 1, 100));

        for (var read = 0; read < 4; read++)
        {
            var changes = Assert.IsType<PyList>(await Rig.CallAsync(
                rig.Handler, Rig.Session(characterId).Session, "GetSkillChangesForISIS"));
            Assert.Collection(changes.Items,
                entry => Assert.Equal(Py.Tuple(new PyInt(3300), new PyInt(50)), entry),
                entry => Assert.Equal(Py.Tuple(new PyInt(3436), new PyInt(50)), entry));
        }

        var consumed = Assert.IsType<PyList>(await Rig.CallAsync(
            rig.Handler, Rig.Session(characterId).Session, "GetSkillChangesForISIS"));
        Assert.Empty(consumed.Items);
    }

[PgFact]
    public async Task Large_skill_injectors_split_and_combine_as_three_small_injectors()
    {
        await using var rig = await Rig.CreateAsync(useCurrentSdeCatalog: true);
        var characterId = rig.CharacterId;
        var small = rig.CatalogForTest.ItemTypeId("Small Skill Injector");
        var large = rig.CatalogForTest.ItemTypeId("Large Skill Injector");
        Assert.NotNull(small);
        Assert.NotNull(large);
        var original = await rig.CreateItemAsync(new Eve.Inventory.NewItem(large.Value, characterId, 60_000_001, 4, 1, true));
        var session = Rig.Session(characterId).Session;
        session.Set("stationid", new PyInt(60_000_001));

        Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.Handler, session, "SplitSkillInjector", new PyLong(original), new PyInt(1)));
        var smallStack = Assert.Single(await rig.ItemStore.InLocationAsync(60_000_001, characterId));
        Assert.Equal((small.Value, 3L), (smallStack.TypeId, smallStack.Quantity));
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.Handler, session, "CombineSkillInjector", new PyLong(smallStack.Id), new PyInt(3)));
        var combined = Assert.Single(await rig.ItemStore.InLocationAsync(60_000_001, characterId));
        Assert.Equal((large.Value, 1L), (combined.TypeId, combined.Quantity));
    }

    [PgFact]
    public async Task The_skills_of_a_character_come_as_entries_in_type_order_with_their_item_fields()
    {
        await using var rig = await Rig.CreateAsync();
        var characterId = rig.CharacterId;
        await rig.GrantAsync(characterId, (3315, 0, 0), (3300, 4, 45_255), (3301, 1, 250));
        var (session, _) = Rig.Session(characterId);

        var skills = (PyDict)await Rig.CallAsync(rig.Handler, session, "GetSkills");
        Assert.Equal([3300L, 3301, 3315], skills.Entries.Select(e => ((PyInt)e.Key).Value));

        var gunnery = (PyObjectEx)skills.Entries[0].Value;
        var header = (PyTuple)gunnery.Header;
        Assert.Equal(new PyToken(SkillWire.EntryClass), header.Items[0]);
        Assert.Equal(new PyTuple([new PyInt(3300), new PyInt(4), new PyInt(45_255), new PyInt(1), PyNone.Instance]), header.Items[1]);
        var state = ((PyDict)header.Items[2]).Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value);
        Assert.Equal(["itemID", "ownerID", "locationID", "flagID", "groupID", "categoryID", "groupName", "published", "inTraining"], ((PyDict)header.Items[2]).Entries.Select(e => JsSemantics.StringValue(e.Key)!));
        Assert.Equal(new PyLong(SkillWire.ItemId(characterId, 3300)), state["itemID"]);
        Assert.Equal((new PyInt(characterId), new PyInt(characterId), new PyInt(7), new PyInt(255), new PyInt(16)), ((PyValue)state["ownerID"], state["locationID"], state["flagID"], state["groupID"], state["categoryID"]));
        Assert.Equal((new PyStr("Gunnery"), PyBool.True, PyBool.False), ((PyValue)state["groupName"], state["published"], state["inTraining"]));

        // an unpublished skill says so, and an untrained one is there at level 0
        var repair = (PyTuple)((PyTuple)((PyObjectEx)skills.Entries[2].Value).Header).Items[1];
        Assert.Equal(new PyTuple([new PyInt(3315), new PyInt(0), new PyInt(0), new PyInt(4), PyNone.Instance]), repair);
        Assert.Equal(PyBool.False, ((PyDict)((PyTuple)((PyObjectEx)skills.Entries[2].Value).Header).Items[2]).Entries.Single(e => JsSemantics.StringValue(e.Key) == "published").Value);
    }

    [PgFact]
    public async Task The_whole_list_is_the_same_as_the_trained_list_and_the_skill_info_holds_both_with_the_points()
    {
        await using var rig = await Rig.CreateAsync();
        var characterId = rig.CharacterId;
        await rig.GrantAsync(characterId, (3300, 4, 45_255), (3301, 1, 250));
        var (session, _) = Rig.Session(characterId);
        var skills = await Rig.CallAsync(rig.Handler, session, "GetSkills");
        Assert.Equal(skills, await Rig.CallAsync(rig.Handler, session, "GetAllSkills"));
        Assert.Equal(new PyInt(45_505), await Rig.CallAsync(rig.Handler, session, "GetSkillPoints"));

        var info = ((PyDict)((PyObject)await Rig.CallAsync(rig.Handler, session, "GetMySkillInfo")).Args)
            .Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value);
        Assert.Equal(skills, info["skills"]);
        Assert.Equal((new PyInt(45_505), new PyInt(0), new PyList([])), ((PyValue)info["skillPoints"], info["freeSkillPoints"], info["queue"]));
    }

    [PgFact]
    public async Task A_character_with_no_skills_has_none_and_no_points()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _) = Rig.Session(9);
        Assert.Equal(new PyDict([]), await Rig.CallAsync(rig.Handler, session, "GetSkills"));
        Assert.Equal(new PyInt(0), await Rig.CallAsync(rig.Handler, session, "GetSkillPoints"));
    }

    [PgFact]
    public async Task Characters_do_not_see_each_others_skills()
    {
        await using var rig = await Rig.CreateAsync();
        var firstCharacterId = rig.CharacterId;
        var otherCharacterId = await rig.CreateCharacterAsync("Skills Other Character");
        await rig.GrantAsync(firstCharacterId, (3300, 4, 45_255));
        await rig.GrantAsync(otherCharacterId, (3301, 1, 250));
        var (otherSession, _) = Rig.Session(otherCharacterId);
        Assert.Equal([3301L], ((PyDict)await Rig.CallAsync(rig.Handler, otherSession, "GetSkills")).Entries.Select(e => ((PyInt)e.Key).Value));
    }

    [PgFact]
    public async Task Without_a_character_the_skill_questions_are_answered_with_none()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _) = Rig.Session(null);
        foreach (var method in new[] { "GetSkills", "GetAllSkills", "GetSkillPoints", "GetMySkillInfo" })
        {
            Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.Handler, session, method));
        }
    }

    [PgFact]
    public async Task The_fixed_answers_are_the_recorded_ones()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _) = Rig.Session(rig.CharacterId);
        async Task<PyValue> Ask(string method) => await Rig.CallAsync(rig.Handler, session, method);

        Assert.Equal(new PyInt(0), await Ask("GetFreeSkillPoints"));
        Assert.Equal(new PyList([]), await Ask("GetSkillQueue"));
        Assert.Equal(new PyList([]), await Ask("GetMySkillQueue"));
        Assert.Equal(new PyTuple([new PyList([]), new PyInt(0)]), await Ask("GetSkillQueueAndFreePoints"));
        Assert.Equal(new PyDict(new[] { 164, 165, 166, 167, 168 }.Select(id => new KeyValuePair<PyValue, PyValue>(new PyInt(id), new PyInt(20))).ToList()), await Ask("GetAttributes"));
        Assert.Equal(new PyList([]), await Ask("GetCharacterAttributeModifiers"));
        Assert.Equal(new PyDict([]), await Ask("GetBoosters"));
        Assert.Equal(new PyDict([]), await Ask("GetImplants"));
        Assert.Equal(new PyList([]), await Ask("GetSkillHistory"));
        Assert.Equal(new PyDict([]), await Ask("GetFreeSkillPointsAppliedToQueue"));
        Assert.Equal(new PyDict([]), await Ask("GetFreeSkillPointsAppliedToSkills"));
        var respec = ((PyDict)((PyObject)await Ask("GetRespecInfo")).Args).Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value);
        Assert.Equal((new PyInt(3), PyNone.Instance, PyNone.Instance), ((PyValue)respec["freeRespecs"], respec["lastRespecDate"], respec["nextTimedRespec"]));
    }

    [PgFact]
    public async Task RespecCharacter_persists_attributes_uses_the_timed_remap_first_and_notifies_the_client()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, sink) = Rig.Session(rig.CharacterId);
        var attributes = new PyDict(
        [
            new(new PyInt(164), new PyInt(27)),
            new(new PyInt(165), new PyInt(21)),
            new(new PyInt(166), new PyInt(17)),
            new(new PyInt(167), new PyInt(17)),
            new(new PyInt(168), new PyInt(17)),
        ]);

        Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.Handler, session, "RespecCharacter", attributes));
        Assert.Equal(attributes, await Rig.CallAsync(rig.Handler, session, "GetAttributes"));

        var respec = ((PyDict)((PyObject)await Rig.CallAsync(rig.Handler, session, "GetRespecInfo")).Args)
            .Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value);
        var now = Py.FileTimeFromUnixMs(Rig.ClockMs);
        Assert.Equal(new PyInt(3), respec["freeRespecs"]);
        Assert.Equal(new PyLong(now), respec["lastRespecDate"]);
        Assert.Equal(new PyLong(now + 365L * 24 * 60 * 60 * 10_000_000), respec["nextTimedRespec"]);
        Assert.Contains(sink.Sent, packet =>
            packet is PyObject { Name: PyStr name, Args: PyTuple args } &&
            name.Value.EndsWith("Notification", StringComparison.Ordinal) &&
            Eve.Macho.MachoAddress.Decode(args.Items[2]).BroadcastId == "OnRespecInfoChanged");
    }

    [PgFact]
    public async Task A_second_remap_uses_a_bonus_without_advancing_the_timed_remap()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _) = Rig.Session(rig.CharacterId);
        PyDict Map(int charisma, int intelligence, int memory, int perception, int willpower) => new(
        [
            new(new PyInt(164), new PyInt(charisma)),
            new(new PyInt(165), new PyInt(intelligence)),
            new(new PyInt(166), new PyInt(memory)),
            new(new PyInt(167), new PyInt(perception)),
            new(new PyInt(168), new PyInt(willpower)),
        ]);

        await Rig.CallAsync(rig.Handler, session, "RespecCharacter", Map(27, 21, 17, 17, 17));
        var first = ((PyDict)((PyObject)await Rig.CallAsync(rig.Handler, session, "GetRespecInfo")).Args)
            .Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value);
        rig.Wait(TimeSpan.FromHours(1));
        await Rig.CallAsync(rig.Handler, session, "RespecCharacter", Map(21, 27, 17, 17, 17));
        var second = ((PyDict)((PyObject)await Rig.CallAsync(rig.Handler, session, "GetRespecInfo")).Args)
            .Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value);

        Assert.Equal(new PyInt(2), second["freeRespecs"]);
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(Rig.ClockMs + 3_600_000L)), second["lastRespecDate"]);
        Assert.Equal(first["nextTimedRespec"], second["nextTimedRespec"]);
    }

    [PgFact]
    public async Task Concurrent_retries_for_the_same_remap_consume_only_one_respec()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _) = Rig.Session(rig.CharacterId);
        var attributes = new PyDict(
        [
            new(new PyInt(164), new PyInt(27)),
            new(new PyInt(165), new PyInt(21)),
            new(new PyInt(166), new PyInt(17)),
            new(new PyInt(167), new PyInt(17)),
            new(new PyInt(168), new PyInt(17)),
        ]);

        await Task.WhenAll(
            Rig.CallAsync(rig.Handler, session, "RespecCharacter", attributes),
            Rig.CallAsync(rig.Handler, session, "RespecCharacter", attributes));
        var respec = ((PyDict)((PyObject)await Rig.CallAsync(rig.Handler, session, "GetRespecInfo")).Args)
            .Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value);
        Assert.Equal(new PyInt(3), respec["freeRespecs"]);
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(Rig.ClockMs)), respec["lastRespecDate"]);
    }

    [PgFact]
    public async Task An_invalid_remap_is_refused_without_using_a_respec()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _) = Rig.Session(rig.CharacterId);
        var invalid = new PyDict(
        [
            new(new PyInt(164), new PyInt(27)),
            new(new PyInt(165), new PyInt(20)),
            new(new PyInt(166), new PyInt(17)),
            new(new PyInt(167), new PyInt(17)),
            new(new PyInt(168), new PyInt(17)),
        ]);

        await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(
            () => Rig.CallAsync(rig.Handler, session, "RespecCharacter", invalid));
        var respec = ((PyDict)((PyObject)await Rig.CallAsync(rig.Handler, session, "GetRespecInfo")).Args)
            .Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value);
        Assert.Equal(new PyInt(3), respec["freeRespecs"]);
        Assert.Equal(PyNone.Instance, respec["lastRespecDate"]);
        Assert.Equal(PyNone.Instance, respec["nextTimedRespec"]);
    }

    [PgFact]
    public async Task Smuggling_chance_is_the_attribute_445_of_the_pilot_after_its_implants_and_is_zero_without_one()
    {
        await using var rig = await Rig.CreateAsync();
        var characterId = rig.CharacterId;
        Assert.Equal(0d, await SkillHandlerService.SmugglingChanceAsync(rig.ItemStore, rig.Dogma, rig.Store, characterId, CancellationToken.None));

        await rig.CreateItemAsync(new Eve.Inventory.NewItem(9942, characterId, characterId, 89, Singleton: true));
        Assert.Equal(0.25d, await SkillHandlerService.SmugglingChanceAsync(rig.ItemStore, rig.Dogma, rig.Store, characterId, CancellationToken.None));
    }

    [PgFact]
    public async Task Booster_activation_persists_side_effects_and_expiry_and_the_real_reader_returns_them()
    {
        await using var rig = await Rig.CreateAsync();
        var characterId = rig.CharacterId;
        const long stationId = 600001;
        var implantId = await rig.CreateItemAsync(new Eve.Inventory.NewItem(9941, characterId, characterId, 89, Singleton: true));
        var boosterStackId = await rig.CreateItemAsync(new Eve.Inventory.NewItem(
            2838, characterId, stationId, 4, Quantity: 3, Singleton: false));

        Assert.Equal(true, await rig.Handler.TryActivateBoosterAsync(
            characterId, boosterStackId, "boosterPrimary", stationId, null));

        var remainder = await rig.ItemStore.GetAsync(boosterStackId);
        Assert.Equal(2, remainder!.Quantity);
        var activeItem = Assert.Single(await rig.ItemStore.InLocationAsync(characterId, characterId), item => item.Flag == 88);
        var nowFileTime = Py.FileTimeFromUnixMs(rig.Now);
        var state = await rig.Store.ActiveSkillBoosterAsync(characterId, activeItem.Id, nowFileTime);
        Assert.NotNull(state);
        Assert.Equal(new[] { 7002 }, state.SideEffectIds);

        var (session, _) = Rig.Session(characterId);
        async Task<PyValue> Ask(string method, params PyValue[] args) =>
            await Rig.CallAsync(rig.Handler, session, method, args);

        Assert.Equal(
            new PyDict([new KeyValuePair<PyValue, PyValue>(
                new PyInt(2),
                Py.KeyVal(("typeID", new PyInt(9941)), ("itemID", new PyLong(implantId))))]),
            await Ask("GetImplants"));
        Assert.Equal(
            new PyDict([new KeyValuePair<PyValue, PyValue>(
                new PyInt(2838),
                Py.KeyVal(
                    ("boosterTypeID", new PyInt(2838)),
                    ("expiryTime", new PyLong(state.ExpiryFileTime)),
                    ("sideEffectIDs", new PyList([new PyInt(7002)]))))]),
            await Ask("GetBoosters"));
        Assert.Equal(new PyList([Py.Tuple(
                new PyLong(activeItem.Id), new PyInt(2838), new PyInt((int)Eve.Dogma.Operation.ModAdd), Py.Number(4))]),
            await Ask("GetCharacterAttributeModifiers", new PyInt(164)));
        Assert.Equal(new PyList([Py.Tuple(
                new PyLong(activeItem.Id), new PyInt(2838), new PyInt((int)Eve.Dogma.Operation.ModAdd), Py.Number(-2))]),
            await Ask("GetCharacterAttributeModifiers", new PyInt(165)));
        Assert.Equal(new PyList([Py.Tuple(
                new PyLong(implantId), new PyInt(9941), new PyInt((int)Eve.Dogma.Operation.ModAdd), Py.Number(3))]),
            await Ask("GetCharacterAttributeModifiers", new PyInt(166)));
        var allModifiers = Assert.IsType<PyList>(await Ask("GetCharacterAttributeModifiers"));
        Assert.Equal(3, allModifiers.Items.Count);

        rig.Wait(TimeSpan.FromMinutes(2));
        Assert.Equal(new PyDict([]), await Ask("GetBoosters"));
        Assert.Null(await rig.ItemStore.GetAsync(activeItem.Id));
        Assert.Null(await rig.Store.ActiveSkillBoosterAsync(
            characterId, activeItem.Id, Py.FileTimeFromUnixMs(rig.Now)));
    }

    [PgFact]
    public async Task Empty_augmentation_ownership_returns_the_empty_wire_shapes()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _) = Rig.Session(rig.CharacterId);

        Assert.Equal(new PyDict([]), await Rig.CallAsync(rig.Handler, session, "GetImplants"));
        Assert.Equal(new PyDict([]), await Rig.CallAsync(rig.Handler, session, "GetBoosters"));
        Assert.Equal(new PyList([]), await Rig.CallAsync(rig.Handler, session, "GetCharacterAttributeModifiers", new PyInt(164)));

        var (unbound, _) = Rig.Session(null);
        Assert.Equal(new PyList([]), await Rig.CallAsync(rig.Handler, unbound, "GetCharacterAttributeModifiers", new PyInt(164)));
    }

    [PgFact]
    public async Task A_live_legacy_booster_is_materialized_from_Dogma_instead_of_becoming_an_empty_snapshot()
    {
        await using var rig = await Rig.CreateAsync();
        var characterId = rig.CharacterId;
        var activeItemId = await rig.CreateItemAsync(new Eve.Inventory.NewItem(
            2838, characterId, characterId, 88, Singleton: true));
        var (session, _) = Rig.Session(characterId);

        var boosters = await Rig.CallAsync(rig.Handler, session, "GetBoosters");
        var state = await rig.Store.ActiveSkillBoosterAsync(
            characterId, activeItemId, Py.FileTimeFromUnixMs(rig.Now));

        Assert.NotNull(state);
        Assert.True(state.ExpiryFileTime > Py.FileTimeFromUnixMs(rig.Now));
        Assert.Equal(new[] { 7002 }, state.SideEffectIds);
        Assert.Equal(
            new PyDict([new KeyValuePair<PyValue, PyValue>(
                new PyInt(2838),
                Py.KeyVal(
                    ("boosterTypeID", new PyInt(2838)),
                    ("expiryTime", new PyLong(state.ExpiryFileTime)),
                    ("sideEffectIDs", new PyList([new PyInt(7002)]))))]),
            boosters);
    }

    [PgFact]
    public async Task Activation_rejects_a_same_slot_legacy_booster_before_consuming_the_cargo_stack()
    {
        await using var rig = await Rig.CreateAsync();
        var characterId = rig.CharacterId;
        const long stationId = 600001;
        var legacyActiveId = await rig.CreateItemAsync(new Eve.Inventory.NewItem(
            2838, characterId, characterId, 88, Singleton: true));
        var cargoStackId = await rig.CreateItemAsync(new Eve.Inventory.NewItem(
            2838, characterId, stationId, 4, Quantity: 2, Singleton: false));

        Assert.Null(await rig.Store.ActiveSkillBoosterAsync(
            characterId, legacyActiveId, Py.FileTimeFromUnixMs(rig.Now)));
        await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(async () =>
            await rig.Handler.TryActivateBoosterAsync(
                characterId, cargoStackId, "boosterPrimary", stationId, null));
        Assert.NotNull(await rig.Store.ActiveSkillBoosterAsync(
            characterId, legacyActiveId, Py.FileTimeFromUnixMs(rig.Now)));

        var legacyItem = await rig.ItemStore.GetAsync(legacyActiveId);
        var cargoItem = await rig.ItemStore.GetAsync(cargoStackId);
        Assert.NotNull(legacyItem);
        Assert.Equal(88, legacyItem.Flag);
        Assert.NotNull(cargoItem);
        Assert.Equal(2, cargoItem.Quantity);
        Assert.Equal(stationId, cargoItem.LocationId);
        Assert.Equal(4, cargoItem.Flag);
        Assert.Null(await rig.Store.ActiveSkillBoosterAsync(
            characterId, cargoStackId, Py.FileTimeFromUnixMs(rig.Now)));
    }

    [PgFact]
    public async Task Free_skill_point_balance_previews_and_selected_application_are_durable_and_atomic()
    {
        await using var rig = await Rig.CreateAsync();
        var characterId = rig.CharacterId;
        await rig.GrantAsync(characterId, (3301, 1, 250));
        await rig.GrantFreeSkillPointsAsync(characterId, 2_000);
        var (session, _) = Rig.Session(characterId);
        var selected = new PyList([Py.Tuple(new PyInt(3301), new PyInt(2))]);

        Assert.Equal(new PyInt(2_000), await Rig.CallAsync(rig.Handler, session, "GetFreeSkillPoints"));
        Assert.Equal(
            new PyDict([new KeyValuePair<PyValue, PyValue>(new PyInt(3301), new PyInt(1_165))]),
            await Rig.CallAsync(rig.Handler, session, "GetFreeSkillPointsAppliedToSkills", selected));

        Assert.Equal(new PyInt(835), await Rig.CallAsync(rig.Handler, session, "ApplyFreeSkillPointsToSkills", selected));
        Assert.Equal(new PyInt(835), await Rig.CallAsync(rig.Handler, session, "GetFreeSkillPoints"));
        Assert.Equal(new PyInt(1_415), await Rig.CallAsync(rig.Handler, session, "GetSkillPoints"));

        var info = ((PyDict)((PyObject)await Rig.CallAsync(rig.Handler, session, "GetMySkillInfo")).Args)
            .Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value);
        Assert.Equal(new PyInt(835), info["freeSkillPoints"]);
        Assert.Equal(new PyDict([]), await Rig.CallAsync(rig.Handler, session, "GetFreeSkillPointsAppliedToSkills", selected));

        Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.Handler, session, "ApplyFreeSkillPoints", new PyInt(3301), new PyInt(836)));
        Assert.Equal(new PyInt(835), await Rig.CallAsync(rig.Handler, session, "GetFreeSkillPoints"));
        Assert.Equal(new PyInt(1_415), await Rig.CallAsync(rig.Handler, session, "GetSkillPoints"));
    }

    [PgFact]
    public async Task Queue_free_skill_points_follow_saved_order_and_apply_with_the_balance()
    {
        await using var rig = await Rig.CreateAsync();
        var characterId = rig.CharacterId;
        await rig.GrantAsync(characterId, (3300, 1, 250), (3301, 1, 250));
        await rig.GrantFreeSkillPointsAsync(characterId, 1_500);
        var saved = await rig.Training.SaveQueueAsync(
            characterId, 34, [new QueueEntry(3300, 2), new QueueEntry(3301, 2)], activate: false, [characterId]);
        Assert.IsType<QueueSaved>(saved);

        var (session, _) = Rig.Session(characterId);
        Assert.Equal(
            new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyInt(3300), new PyInt(1_165)),
                new KeyValuePair<PyValue, PyValue>(new PyInt(3301), new PyInt(335)),
            ]),
            await Rig.CallAsync(rig.Handler, session, "GetFreeSkillPointsAppliedToQueue"));

        Assert.Equal(new PyInt(0), await Rig.CallAsync(rig.Handler, session, "ApplyFreeSkillPointsToQueue"));
        var remainingQueue = (PyList)await Rig.CallAsync(rig.Handler, session, "GetMySkillQueue");
        Assert.Single(remainingQueue.Items);
        var remainingLine = ((PyDict)((PyObject)remainingQueue.Items[0]).Args).Entries
            .ToDictionary(entry => JsSemantics.StringValue(entry.Key)!, entry => entry.Value);
        Assert.Equal((new PyInt(3301), new PyInt(2)),
            ((PyValue)remainingLine["trainingTypeID"], (PyValue)remainingLine["trainingToLevel"]));
        Assert.Equal(new PyInt(0), await Rig.CallAsync(rig.Handler, session, "GetFreeSkillPoints"));
        Assert.Equal(new PyInt(2_000), await Rig.CallAsync(rig.Handler, session, "GetSkillPoints"));
        Assert.Equal(new PyDict([]), await Rig.CallAsync(rig.Handler, session, "GetFreeSkillPointsAppliedToQueue"));
    }

    [PgFact]
    public async Task Knowing_a_skill_and_being_a_skill_are_different_questions()
    {
        await using var rig = await Rig.CreateAsync();
        var characterId = rig.CharacterId;
        await rig.GrantAsync(characterId, (3300, 1, 250));
        var (session, _) = Rig.Session(characterId);
        Assert.Equal(PyBool.True, await Rig.CallAsync(rig.Handler, session, "IsSkillInjected", new PyInt(3300)));
        Assert.Equal(PyBool.False, await Rig.CallAsync(rig.Handler, session, "IsSkillInjected", new PyInt(3301)));
        Assert.Equal(PyBool.True, await Rig.CallAsync(rig.Handler, session, "IsSkill", new PyInt(3301)));
        Assert.Equal(PyBool.False, await Rig.CallAsync(rig.Handler, session, "IsSkill", new PyInt(587)));
        Assert.Equal(PyBool.False, await Rig.CallAsync(rig.Handler, session, "IsSkill", new PyStr("x")));
    }

    [Fact]
    public async Task The_door_hands_out_a_moniker_for_the_character_and_falls_back_to_the_account()
    {
        var door = new SkillMgr2Service();
        var (withCharacter, _) = Rig.Session(140_000_073);
        var moniker = (PyObject)await Rig.CallAsync(door, withCharacter, "GetMySkillHandler");
        Assert.Equal("carbon.common.script.net.moniker.Moniker", JsSemantics.StringValue(moniker.Name));
        Assert.Equal(new PyTuple([new PyStr("skillHandler"), PyNone.Instance, new PyInt(140_000_073), PyNone.Instance]), moniker.Args);

        var (accountOnly, _) = Rig.Session(null, userId: 34);
        Assert.Equal(new PyInt(34), ((PyTuple)((PyObject)await Rig.CallAsync(door, accountOnly, "GetMySkillHandler")).Args).Items[2]);
    }

    [PgFact]
    public async Task Binding_answers_a_bound_object_and_the_result_of_the_nested_call()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.GrantAsync(7, (3300, 4, 45_255));
        var (session, _) = Rig.Session(7);
        Assert.Equal(new PyInt(MachoMessage.ProxyNodeId), await Rig.CallAsync(rig.Handler, session, "MachoResolveObject", new PyInt(7), new PyInt(0)));

        var bound = (PyTuple)await Rig.CallAsync(rig.Handler, session, "MachoBindObject", new PyInt(7), new PyTuple([new PyStr("GetSkillPoints"), new PyTuple([]), PyNone.Instance]));
        var oid = (PyTuple)((PySubStream)((PySubStruct)bound.Items[0]).Value).Decoded!;
        Assert.StartsWith($"N={MachoMessage.ProxyNodeId}:", JsSemantics.StringValue(oid.Items[0]));
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(Rig.ClockMs)), oid.Items[1]);
        Assert.Equal(new PyInt(45_255), bound.Items[1]);

        var again = (PyTuple)await Rig.CallAsync(rig.Handler, session, "MachoBindObject", new PyInt(7), PyNone.Instance);
        Assert.Equal(PyNone.Instance, again.Items[1]); // no nested call: just the bound object
        Assert.NotEqual(
            JsSemantics.StringValue(oid.Items[0]),
            JsSemantics.StringValue(((PyTuple)((PySubStream)((PySubStruct)again.Items[0]).Value).Decoded!).Items[0]));
    }

    [PgFact]
    public async Task A_nested_call_to_a_method_the_handler_has_not_is_answered_with_none()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, _) = Rig.Session(7);
        var bound = (PyTuple)await Rig.CallAsync(rig.Handler, session, "MachoBindObject", new PyInt(7), new PyTuple([new PyStr("NoSuchMethod"), new PyTuple([]), PyNone.Instance]));
        Assert.Equal(PyNone.Instance, bound.Items[1]);
    }

    [PgFact]
    public async Task Entering_sends_one_unchanged_skill_of_the_character()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.GrantAsync(7, (3301, 1, 250), (3300, 4, 45_255));
        var (session, sink) = Rig.Session(7);
        await rig.Hook.OnCharacterEnteredAsync(session, 7, CancellationToken.None);

        var packet = Assert.Single(sink.Sent);
        var body = (PyTuple)((PyObject)packet).Args;
        Assert.Equal(new PyInt(12), body.Items[0]);
        var inner = (PyTuple)MarshalCodec.Decode(((PyBuffer)((PyTuple)((PyTuple)body.Items[4]).Items[0]).Items[1]).Value.ToArray());
        var args = (PyTuple)((PyTuple)inner.Items[1]).Items[1];
        Assert.Equal([3300L], ((PyDict)args.Items[0]).Entries.Select(e => ((PyInt)e.Key).Value)); // the first by type, one only
        Assert.Equal(PyNone.Instance, args.Items[1]);
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(Rig.ClockMs)), args.Items[2]);
    }

    [PgFact]
    public async Task A_character_with_no_skills_gets_no_notification()
    {
        await using var rig = await Rig.CreateAsync();
        var (session, sink) = Rig.Session(7);
        await rig.Hook.OnCharacterEnteredAsync(session, 7, CancellationToken.None);
        Assert.Empty(sink.Sent);
    }

    [PgFact]
    public async Task Free_points_notify_only_committed_changed_skills_and_balance_and_a_retry_is_silent()
    {
        await using var rig = await Rig.CreateAsync();
        var character = rig.CharacterId;
        await rig.GrantAsync(character, (3301, 1, 250), (3300, 4, 45255));
        await rig.GrantFreeSkillPointsAsync(character, 2_000);
        var (session, sink) = Rig.Session(character);
        rig.Sessions.Enter(character, session);
        var committed = new List<TrainingUpdate>();
        rig.Training.Settled += async update =>
        {
            if (update.FreeSkillPoints is null) return;
            Assert.Equal(update.FreeSkillPoints.Value, await rig.Store.FreeSkillPointsAsync(character));
            Assert.Equal(update.ChangedSkills!.Single(), (await rig.Store.ForCharacterAsync(character)).Single(s => s.TypeId == 3301));
            committed.Add(update);
        };
        var selected = new PyList([Py.Tuple(new PyInt(3301), new PyInt(2)), Py.Tuple(new PyInt(3301), new PyInt(2))]);
        Assert.Equal(new PyInt(835), await Rig.CallAsync(rig.Handler, session, "ApplyFreeSkillPointsToSkills", selected));
        var notes = Notes.Of(sink);
        Assert.Equal(["OnServerSkillsChanged", "OnNewSkillQueueSaved", "OnFreeSkillPointsChanged"], notes.Select(n => n.Name));
        Assert.Equal(["charid", "charid", "charid"], notes.Select(n => n.IdType));
        var changes = Assert.IsType<PyDict>(notes[0].Args[0]);
        Assert.Equal(3301, Notes.Number(Assert.Single(changes.Entries).Key));
        var entry = Assert.IsType<PyObjectEx>(changes.Entries[0].Value);
        Assert.Equal(2, Assert.IsType<PyTuple>(entry.Header).Items.Count);
        Assert.Equal(PyNone.Instance, notes[0].Args[1]);
        Assert.IsType<PyLong>(notes[0].Args[2]);
        Assert.Equal(new PyInt(835), Assert.Single(notes[2].Args));
        Assert.Empty(Assert.Single(committed).Finished);
        Assert.Equal(new PyInt(835), await Rig.CallAsync(rig.Handler, session, "ApplyFreeSkillPointsToSkills", selected));
        Assert.Empty(Notes.Of(sink));
        Assert.Single(committed);
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.Handler, session, "ApplyFreeSkillPoints", new PyInt(3301), new PyInt(836)));
        Assert.Empty(Notes.Of(sink));
        // A partial application changes points even though no level finishes.
        Assert.Equal(new PyInt(735), await Rig.CallAsync(rig.Handler, session, "ApplyFreeSkillPoints", new PyInt(3301), new PyInt(100)));
        var partial = Notes.Of(sink);
        Assert.Equal(["OnServerSkillsChanged", "OnNewSkillQueueSaved", "OnFreeSkillPointsChanged"], partial.Select(n => n.Name));
        Assert.Equal(1515, (await rig.Store.ForCharacterAsync(character)).Single(s => s.TypeId == 3301).SkillPoints);
    }

    [PgFact]
    public async Task Free_points_preserve_running_skill_and_remove_completed_paused_queue_entries()
    {
        foreach (var running in new[] { false, true })
        {
            await using var rig = await Rig.CreateAsync();
            var character = rig.CharacterId;
            await rig.GrantAsync(character, (3301, 1, 250), (3315, 0, 0));
            await rig.GrantFreeSkillPointsAsync(character, 2_000);
            var (session, sink) = Rig.Session(character);
            rig.Sessions.Enter(character, session);
            await ((IService)rig.Handler).CallAsync(new ServiceCall
            {
                Method = "SaveNewQueue", Session = session,
                Args = [new PyList([Py.Tuple(new PyInt(3301), new PyInt(2)), Py.Tuple(new PyInt(3315), new PyInt(1))])],
                Kwargs = new PyDict([new(new PyStr("activate"), PyValue.Of(running))])
            });
            Notes.Of(sink);
            await Rig.CallAsync(rig.Handler, session, "ApplyFreeSkillPointsToQueue");
            var notes = Notes.Of(sink);
            Assert.Equal(["OnServerSkillsChanged", "OnNewSkillQueueSaved", "OnFreeSkillPointsChanged"], notes.Select(n => n.Name));
            var changes = Assert.IsType<PyDict>(notes[0].Args[0]);
            Assert.Equal(running ? new long[] { 3315 } : [3301, 3315], changes.Entries.Select(e => Notes.Number(e.Key)));
            var view = await rig.Training.ViewAsync(character, session.UserId!.Value);
            Assert.Equal(running, view.State.Running);
            Assert.Single(view.Queue);
            Assert.Equal(running ? 3301 : 3315, view.Queue[0].TypeId);
            if (running)
            {
                Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.Handler, session, "ApplyFreeSkillPoints", new PyInt(3301), new PyInt(1)));
                Assert.Empty(Notes.Of(sink));
                Assert.Equal(250, (await rig.Store.ForCharacterAsync(character)).Single(s => s.TypeId == 3301).SkillPoints);
            }
        }
    }

    [PgFact]
    public async Task Injector_notifies_committed_inventory_then_free_balance_and_failed_retries_are_silent()
    {
        await using var rig = await Rig.CreateAsync(useCurrentSdeCatalog: true);
        var character = rig.CharacterId;
        const long initialFreePoints = 123;
        const long awardPerLargeInjector = 500_000;
        await rig.GrantFreeSkillPointsAsync(character, initialFreePoints);
        var typeId = rig.CatalogForTest.ItemTypeId("Large Skill Injector")!.Value;
        var itemId = await rig.CreateItemAsync(new Eve.Inventory.NewItem(typeId, character, 60_000_001, 4, 2, false));
        var (session, sink) = Rig.Session(character);
        rig.Sessions.Enter(character, session);
        Assert.Equal(new PyInt(awardPerLargeInjector), await Rig.CallAsync(rig.Handler, session, "InjectSkillpoints", new PyLong(itemId), new PyInt(1)));
        var first = Notes.Of(sink);
        Assert.Equal(["OnItemChange", "OnFreeSkillPointsChanged"], first.Select(n => n.Name));
        Assert.Equal(["clientID", "charid"], first.Select(n => n.IdType));
        Assert.IsType<PyPackedRow>(first[0].Args[0]);
        var diff = Assert.IsType<PyDict>(first[0].Args[1]);
        Assert.Equal(new PyInt(9), Assert.Single(diff.Entries).Key);
        Assert.Equal(2, Notes.Number(diff.Entries[0].Value));
        Assert.Equal(new PyInt(initialFreePoints + awardPerLargeInjector), Assert.Single(first[1].Args));
        Assert.Equal(1, (await rig.ItemStore.GetAsync(itemId))!.Quantity);
        Assert.Equal(initialFreePoints + awardPerLargeInjector, await rig.Store.FreeSkillPointsAsync(character));
        Assert.Equal(new PyInt(awardPerLargeInjector), await Rig.CallAsync(rig.Handler, session, "InjectSkillpoints", new PyLong(itemId), new PyInt(1)));
        var removed = Notes.Of(sink);
        Assert.Equal(["OnItemChange", "OnFreeSkillPointsChanged"], removed.Select(n => n.Name));
        var removal = Assert.IsType<PyDict>(removed[0].Args[1]);
        Assert.Equal(new PyInt(3), Assert.Single(removal.Entries).Key);
        Assert.Equal(60_000_001, Notes.Number(removal.Entries[0].Value));
        Assert.Null(await rig.ItemStore.GetAsync(itemId));
        Assert.Equal(new PyInt(initialFreePoints + awardPerLargeInjector * 2), Assert.Single(removed[1].Args));
        Assert.Equal(initialFreePoints + awardPerLargeInjector * 2, await rig.Store.FreeSkillPointsAsync(character));
        await Assert.ThrowsAsync<MachoWrappedException>(() => Rig.CallAsync(rig.Handler, session, "InjectSkillpoints", new PyLong(itemId), new PyInt(1)));
        Assert.Empty(Notes.Of(sink));
        Assert.Equal(initialFreePoints + awardPerLargeInjector * 2, await rig.Store.FreeSkillPointsAsync(character));
    }

    [PgFact]
    public async Task Purchase_returns_a_tuple_notifies_new_skills_and_refusal_or_retry_changes_nothing()
    {
        await using var rig = await Rig.CreateAsync(useCurrentSdeCatalog: true);
        var character = rig.CharacterId;
        var (session, sink) = Rig.Session(character);
        rig.Sessions.Enter(character, session);
        var requested = new PyList([new PyInt(3300)]);
        Assert.Empty(Assert.IsType<PyTuple>(await Rig.CallAsync(rig.Handler, session, "PurchaseSkills", new PyList([]))).Items);
        Assert.Empty(Notes.Of(sink));
        await rig.FundWalletAsync(character, 1m);
        await Assert.ThrowsAsync<MachoWrappedException>(() => Rig.CallAsync(rig.Handler, session, "PurchaseSkills", requested));
        Assert.Empty(Notes.Of(sink));
        Assert.Empty(await rig.Store.ForCharacterAsync(character));
        Assert.Empty(await rig.Wallet.JournalAsync(character));
        // Fund a different wallet state without duplicating its opening row.
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Wallet.AdjustAsync(connection, transaction, character, 10_000_000m, 0, 57, "Test funding", rig.Now, "test-funding");
            await transaction.CommitAsync();
        }
        var walletChanges = new List<Eve.Wallet.WalletBalanceChange>();
        rig.Wallet.BalanceCommitted += walletChanges.Add;
        var result = Assert.IsType<PyTuple>(await Rig.CallAsync(rig.Handler, session, "PurchaseSkills", requested));
        Assert.Equal([new PyInt(3300)], result.Items);
        var notes = Notes.Of(sink);
        Assert.Equal(["OnServerSkillsChanged", "OnSkillsChanged"], notes.Select(n => n.Name));
        Assert.Equal(["charid", "clientID"], notes.Select(n => n.IdType));
        Assert.Equal(3300, Notes.Number(Assert.Single(Assert.IsType<PyDict>(notes[0].Args[0]).Entries).Key));
        Assert.Equal(3, Assert.IsType<PyTuple>(Assert.IsType<PyObjectEx>(Assert.IsType<PyDict>(notes[0].Args[0]).Entries[0].Value).Header).Items.Count);
        Assert.Single(walletChanges);
        Assert.Empty((await rig.Training.ViewAsync(character, session.UserId!.Value)).Queue);
        await Assert.ThrowsAsync<MachoWrappedException>(() => Rig.CallAsync(rig.Handler, session, "PurchaseSkills", requested));
        Assert.Empty(Notes.Of(sink));
        Assert.Single(walletChanges);
        Assert.Single(await rig.Wallet.JournalAsync(character), j => j.OperationKey.Contains("skill-purchase", StringComparison.Ordinal));
    }

    [PgFact]
    public async Task Concurrent_free_point_target_retries_commit_and_announce_once()
    {
        await using var rig = await Rig.CreateAsync();
        var character = rig.CharacterId;
        await rig.GrantAsync(character, (3301, 1, 250));
        await rig.GrantFreeSkillPointsAsync(character, 2_000);
        var (session, sink) = Rig.Session(character);
        rig.Sessions.Enter(character, session);
        var targets = new PyList([Py.Tuple(new PyInt(3301), new PyInt(2))]);
        var results = await Task.WhenAll(
            Rig.CallAsync(rig.Handler, session, "ApplyFreeSkillPointsToSkills", targets),
            Rig.CallAsync(rig.Handler, session, "ApplyFreeSkillPointsToSkills", targets));
        Assert.All(results, result => Assert.Equal(new PyInt(835), result));
        Assert.Equal(["OnServerSkillsChanged", "OnNewSkillQueueSaved", "OnFreeSkillPointsChanged"], Notes.Of(sink).Select(n => n.Name));
        Assert.Equal(835, await rig.Store.FreeSkillPointsAsync(character));
        Assert.Equal(1415, Assert.Single(await rig.Store.ForCharacterAsync(character)).SkillPoints);
    }

    [PgFact]
    public async Task Concurrent_injector_consumption_commits_once_and_keeps_the_running_queue()
    {
        await using var rig = await Rig.CreateAsync(
            settings: new TrainingSettings { HasOmega = _ => false },
            isAlphaClone: _ => true,
            useCurrentSdeCatalog: true);
        var character = rig.CharacterId;
        const int queuedSkillType = 3301;
        const long awardPerLargeInjector = 500_000;
        var queuedSkill = rig.CatalogForTest.Get(queuedSkillType)!;
        var gunnery = rig.CatalogForTest.Get(3300)!;
        var requirement = Assert.Single(queuedSkill.Needs);
        Assert.Equal((3300, 1), (requirement.TypeId, requirement.Level));
        Assert.Equal(5, rig.CatalogForTest.AlphaMaxLevel(1, queuedSkillType));
        await rig.GrantAsync(character,
            (gunnery.TypeId, requirement.Level, SkillPoints.ForTraining(gunnery.Rank, requirement.Level)),
            (queuedSkillType, 1, SkillPoints.ForTraining(queuedSkill.Rank, 1)));
        var (session, sink) = Rig.Session(character);
        rig.Sessions.Enter(character, session);
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.Handler, session, "SaveNewQueue", new PyList([Py.Tuple(new PyInt(queuedSkillType), new PyInt(2))])));
        Notes.Of(sink);
        var queue = await rig.Training.ViewAsync(character, 34);
        Assert.True(queue.State.Running);
        Assert.NotNull(queue.State.ActiveStart);
        Assert.Equal((queuedSkillType, 2), (Assert.Single(queue.State.Entries).TypeId, queue.State.Entries[0].ToLevel));
        Assert.Single(queue.Queue);
        var item = await rig.CreateItemAsync(new Eve.Inventory.NewItem(
            rig.CatalogForTest.ItemTypeId("Large Skill Injector")!.Value, character, 60_000_001, 4, 1, false));
        async Task<bool> TryInject()
        {
            try
            {
                Assert.Equal(new PyInt(awardPerLargeInjector), await Rig.CallAsync(rig.Handler, session, "InjectSkillpoints", new PyLong(item), new PyInt(1)));
                return true;
            }
            catch (MachoWrappedException) { return false; }
        }
        var outcomes = await Task.WhenAll(TryInject(), TryInject());
        Assert.Single(outcomes, success => success);
        Assert.Equal(["OnItemChange", "OnFreeSkillPointsChanged"], Notes.Of(sink).Select(n => n.Name));
        Assert.Equal(awardPerLargeInjector, await rig.Store.FreeSkillPointsAsync(character));
        Assert.Null(await rig.ItemStore.GetAsync(item));
        var after = await rig.Training.ViewAsync(character, 34);
        Assert.Equal(queue.State.Running, after.State.Running);
        Assert.Equal(queue.State.ActiveStart, after.State.ActiveStart);
        Assert.Equal(queue.State.Entries.Select(e => (e.TypeId, e.ToLevel)), after.State.Entries.Select(e => (e.TypeId, e.ToLevel)));
        Assert.Equal(queue.Queue, after.Queue);
    }

    [PgFact]
    public async Task Concurrent_purchase_retries_commit_once_and_keep_existing_training()
    {
        await using var rig = await Rig.CreateAsync(
            settings: new TrainingSettings { HasOmega = _ => false },
            isAlphaClone: _ => true,
            useCurrentSdeCatalog: true);
        var character = rig.CharacterId;
        const int queuedSkillType = 3301;
        var queuedSkill = rig.CatalogForTest.Get(queuedSkillType)!;
        var gunnery = rig.CatalogForTest.Get(3300)!;
        var requirement = Assert.Single(queuedSkill.Needs);
        Assert.Equal((3300, 1), (requirement.TypeId, requirement.Level));
        Assert.Equal(5, rig.CatalogForTest.AlphaMaxLevel(1, queuedSkillType));
        await rig.GrantAsync(character,
            (gunnery.TypeId, requirement.Level, SkillPoints.ForTraining(gunnery.Rank, requirement.Level)),
            (queuedSkillType, 1, SkillPoints.ForTraining(queuedSkill.Rank, 1)));
        const int purchasedType = 3327; // Spaceship Command remains unowned.
        Assert.True(rig.CatalogForTest.Get(purchasedType) is { Published: true, AvailableForPurchase: true });
        await rig.FundWalletAsync(character, 10_000_000m);
        var (session, sink) = Rig.Session(character);
        rig.Sessions.Enter(character, session);
        Assert.Equal(PyNone.Instance, await Rig.CallAsync(rig.Handler, session, "SaveNewQueue", new PyList([Py.Tuple(new PyInt(queuedSkillType), new PyInt(2))])));
        Notes.Of(sink);
        var queue = await rig.Training.ViewAsync(character, 34);
        Assert.True(queue.State.Running);
        Assert.NotNull(queue.State.ActiveStart);
        Assert.Equal((queuedSkillType, 2), (Assert.Single(queue.State.Entries).TypeId, queue.State.Entries[0].ToLevel));
        Assert.Single(queue.Queue);
        var changes = new List<Eve.Wallet.WalletBalanceChange>();
        rig.Wallet.BalanceCommitted += changes.Add;
        async Task<bool> TryPurchase()
        {
            try
            {
                var result = Assert.IsType<PyTuple>(await Rig.CallAsync(rig.Handler, session, "PurchaseSkills", new PyList([new PyInt(purchasedType)])));
                Assert.Equal([new PyInt(purchasedType)], result.Items);
                return true;
            }
            catch (MachoWrappedException) { return false; }
        }
        var outcomes = await Task.WhenAll(TryPurchase(), TryPurchase());
        Assert.Single(outcomes, success => success);
        Assert.Equal(["OnServerSkillsChanged", "OnSkillsChanged"], Notes.Of(sink).Select(n => n.Name));
        Assert.Single(changes);
        Assert.Single(await rig.Wallet.JournalAsync(character));
        var after = await rig.Training.ViewAsync(character, 34);
        Assert.Equal(queue.State.Running, after.State.Running);
        Assert.Equal(queue.State.ActiveStart, after.State.ActiveStart);
        Assert.Equal(queue.State.Entries.Select(e => (e.TypeId, e.ToLevel)), after.State.Entries.Select(e => (e.TypeId, e.ToLevel)));
        Assert.Equal(queue.Queue, after.Queue);
        Assert.Equal(SkillPoints.ForTraining(queuedSkill.Rank, 1), (await rig.Store.ForCharacterAsync(character)).Single(s => s.TypeId == queuedSkillType).SkillPoints);
    }
}
