using Eve.Characters;
using Eve.Dogma;
using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Skills;
using Eve.Sde;
using Eve.Skills;
using Xunit;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class SkillsHostTests
{
    [PgFact]
    public async Task Expert_system_expiry_updates_virtual_skills_and_dogma_through_the_composed_host()
    {
        const long startMs = 1_800_000_000_000;
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync(startMs);
        var characterId = await rig.CreateCharacterAsync();
        var nowMs = rig.NowUnixMs;
        var expiresAtMs = nowMs + 1_000;
        var expertCatalog = ExpertSystemCatalog.LoadEmbedded();
        var candidates = expertCatalog.Types
            .Where(type => !type.Hidden && !type.Retired && type.SkillsGranted.Count > 0)
            .OrderBy(type => type.TypeId).ToArray();
        var expertPair = (from left in candidates
                          from right in candidates
                          where left.TypeId < right.TypeId
                          let overlap = left.SkillsGranted.Join(right.SkillsGranted,
                              leftGrant => leftGrant.TypeId, rightGrant => rightGrant.TypeId, (leftGrant, _) => leftGrant)
                              .OrderBy(grant => grant.TypeId).FirstOrDefault()
                          where overlap is not null
                          select (Left: left, Right: right, Grant: overlap)).First();
        var expertType = expertPair.Left;
        var grant = expertPair.Grant!;
        var grantLevel = Math.Max(grant.Level,
            expertPair.Right.SkillsGranted.Single(candidate => candidate.TypeId == grant.TypeId).Level);

        var sde = new SdeReader(rig.Source);
        var skillCatalog = await SkillCatalog.LoadAsync(sde);
        Assert.NotNull(skillCatalog.Get(grant.TypeId));
        var creation = await CreationCatalog.LoadAsync(sde, rig.Station);
        var shipTypeId = creation.Race(1)!.ShipTypeId;
        long shipId;
        var itemStore = new PgItemStore(rig.Source, rig.InventorySchema);
        var expertStore = new PgExpertSystemStore(rig.Source, rig.SkillsSchema);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            shipId = await itemStore.CreateAsync(connection, transaction,
                new NewItem(shipTypeId, characterId, rig.Station, ItemFlags.Hangar, 1, Singleton: true));
            await rig.Characters.SetShipAsync(connection, transaction, characterId, shipId);
            await expertStore.UpsertAsync(connection, transaction,
                new ExpertSystemEntry(characterId, expertType.TypeId, nowMs, expiresAtMs, null, "item", nowMs));
            await expertStore.UpsertAsync(connection, transaction,
                new ExpertSystemEntry(characterId, expertPair.Right.TypeId, nowMs, expiresAtMs, null, "item", nowMs));
            await transaction.CommitAsync();
        }

        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = characterId, Sink = sink };
        session.Set("charid", new PyLong(characterId));
        session.Set("shipid", new PyLong(shipId));
        session.Set("stationid", new PyLong(rig.Station));
        rig.Sessions.Enter(characterId, session);

        var skillHandler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));
        var activeSkills = Assert.IsType<PyDict>(await CallAsync(skillHandler, "GetAllSkills", session));
        var activeGrant = Assert.Single(activeSkills.Entries, entry => entry.Key == new PyInt(grant.TypeId));
        Assert.Equal(new PyInt(grantLevel), SkillLevel(activeGrant.Value));

        rig.AdvanceTo(expiresAtMs);
        var expertSystemMgr = Assert.IsType<ExpertSystemMgrService>(rig.Registry.Lookup("expertSystemMgr"));
        await expertSystemMgr.ExpireDueAsync(CancellationToken.None);

        Assert.Empty(await expertStore.ForCharacterAsync(characterId));
        var notifications = Notifications(sink);
        var names = notifications.Select(notification => notification.Name).ToArray();
        Assert.Equal(2, names.Count(name => name == "OnNotificationReceived"));
        var expertSystemsIndex = Array.IndexOf(names, "OnExpertSystemsUpdated");
        Assert.True(expertSystemsIndex >= 0, string.Join(", ", names));
        Assert.Equal(PyBool.False, notifications[expertSystemsIndex].Args[1]);
        Assert.Equal(new PyInt(expertType.TypeId), notifications[expertSystemsIndex].Args[2]);
        Assert.DoesNotContain(new PyInt(expertType.TypeId), Assert.IsType<PyDict>(notifications[expertSystemsIndex].Args[0]).Entries.Select(entry => entry.Key));

        var serverSkillsIndex = Array.IndexOf(names, "OnServerSkillsChanged");
        Assert.True(serverSkillsIndex > expertSystemsIndex, string.Join(", ", names));
        var changedSkill = Assert.Single(Assert.IsType<PyDict>(notifications[serverSkillsIndex].Args[0]).Entries,
            entry => entry.Key == new PyInt(grant.TypeId));
        Assert.Equal(new PyInt(0), SkillLevel(changedSkill.Value));
        Assert.Equal("OnSkillsChanged", names[serverSkillsIndex + 1]);

        var dogmaIndex = Array.IndexOf(names, "OnServerBrainUpdated");
        Assert.True(dogmaIndex > serverSkillsIndex, string.Join(", ", names));
        Assert.Equal(2, names.Count(name => name == "OnExpertSystemExpired"));
        var expiryEvents = notifications.TakeLast(2).ToArray();
        Assert.Equal(["OnExpertSystemExpired", "OnExpertSystemExpired"], expiryEvents.Select(notification => notification.Name));
        Assert.Equal([expertType.TypeId, expertPair.Right.TypeId], expiryEvents.Select(notification => Number(notification.Args[0])));
        Assert.DoesNotContain(new PyInt(grant.TypeId), Assert.IsType<PyDict>(
            await CallAsync(skillHandler, "GetAllSkills", session)).Entries.Select(entry => entry.Key));

        await rig.RestartAsync();
        var restartedManager = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("expertSystemMgr"));
        var installed = Assert.IsType<PyDict>(await CallAsync(restartedManager, "GetMyExpertSystems",
            new ClientSession { CharacterId = characterId }));
        Assert.Empty(installed.Entries);
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    public async Task Combining_small_skill_injectors_leaves_the_remainder_and_creates_one_large_injector(long inputQuantity, long expectedRemainder)
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var characterId = await rig.CreateCharacterAsync();
        var catalog = await SkillCatalog.LoadAsync(new SdeReader(rig.Source));
        var smallTypeId = catalog.ItemTypeId("Small Skill Injector")!.Value;
        var largeTypeId = catalog.ItemTypeId("Large Skill Injector")!.Value;
        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        var smallStackId = await CreateItemAsync(rig, new NewItem(
            smallTypeId, characterId, rig.Station, ItemFlags.Hangar, inputQuantity, Singleton: false));
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = characterId, Sink = sink };
        session.Set("stationid", new PyLong(rig.Station));
        var skillHandler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));

        Assert.IsType<PyNone>(await BoundAsync(skillHandler, session, "CombineSkillInjector",
            new PyLong(smallStackId), new PyLong(inputQuantity)));

        Assert.Equal(expectedRemainder, (await items.GetAsync(smallStackId))!.Quantity);
        var produced = Assert.Single(await items.InLocationAsync(rig.Station, characterId), item => item.TypeId == largeTypeId);
        Assert.Equal(1, produced.Quantity);
        await rig.RestartAsync();
        Assert.Equal((expectedRemainder, 1L), (
            (await new PgItemStore(rig.Source, rig.InventorySchema).GetAsync(smallStackId))!.Quantity,
            Assert.Single(await new PgItemStore(rig.Source, rig.InventorySchema).InLocationAsync(rig.Station, characterId),
                item => item.TypeId == largeTypeId).Quantity));
    }

    [Theory]
    [InlineData(2, 2, 6, 3)]
    [InlineData(2, 1, 3, 9)]
    public async Task Splitting_skill_injectors_sends_the_consumed_and_created_item_changes_to_the_client(
        long inputQuantity, long splitQuantity, long expectedOutputQuantity, int expectedSourceDifference)
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var characterId = await rig.CreateCharacterAsync();
        var catalog = await SkillCatalog.LoadAsync(new SdeReader(rig.Source));
        var largeTypeId = catalog.ItemTypeId("Large Skill Injector")!.Value;
        var smallTypeId = catalog.ItemTypeId("Small Skill Injector")!.Value;
        var dogma = await DogmaStatic.LoadAsync(new SdeReader(rig.Source));
        await dogma.EnsureTypesAsync([largeTypeId, smallTypeId]);
        var smallType = dogma.Type(smallTypeId)!;
        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        var largeStackId = await CreateItemAsync(rig, new NewItem(
            largeTypeId, characterId, rig.Station, ItemFlags.Hangar, inputQuantity, Singleton: false));
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = characterId, Sink = sink };
        session.Set("stationid", new PyLong(rig.Station));
        var skillHandler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));

        Assert.IsType<PyNone>(await BoundAsync(skillHandler, session, "SplitSkillInjector",
            new PyLong(largeStackId), new PyLong(splitQuantity)));

        var expectedRemainder = inputQuantity - splitQuantity;
        Assert.Equal(expectedRemainder == 0, await items.GetAsync(largeStackId) is null);
        if (expectedRemainder > 0)
            Assert.Equal(expectedRemainder, (await items.GetAsync(largeStackId))!.Quantity);
        var produced = Assert.Single(await items.InLocationAsync(rig.Station, characterId), item => item.TypeId == smallTypeId);
        Assert.Equal(expectedOutputQuantity, produced.Quantity);
        var notifications = Notifications(sink);
        Assert.Equal(["OnItemChange", "OnItemChange"], notifications.Select(notification => notification.Name));
        Assert.Equal(3, notifications[0].Args.Length);
        var sourceRow = Assert.IsType<PyPackedRow>(notifications[0].Args[0]);
        Assert.Equal(largeStackId, ItemId(sourceRow.Values[0]));
        if (expectedSourceDifference == 3)
        {
            Assert.Equal(6, ItemId(sourceRow.Values[3]));
            Assert.Equal(inputQuantity, Number(sourceRow.Values[5]));
        }
        else
        {
            Assert.Equal(rig.Station, ItemId(sourceRow.Values[3]));
            Assert.Equal(expectedRemainder, Number(sourceRow.Values[5]));
        }
        Assert.Equal(new PyInt(expectedSourceDifference), Assert.Single(Assert.IsType<PyDict>(notifications[0].Args[1]).Entries).Key);
        if (expectedSourceDifference == 9)
            Assert.Equal(inputQuantity, Number(Assert.Single(Assert.IsType<PyDict>(notifications[0].Args[1]).Entries).Value));
        var outputRow = Assert.IsType<PyPackedRow>(notifications[1].Args[0]);
        Assert.Equal(produced.Id, ItemId(outputRow.Values[0]));
        Assert.Equal(smallTypeId, Number(outputRow.Values[1]));
        Assert.Equal(rig.Station, ItemId(outputRow.Values[3]));
        Assert.Equal(expectedOutputQuantity, Number(outputRow.Values[5]));
        Assert.Equal(smallType.GroupId, Number(outputRow.Values[6]));
        Assert.Equal(smallType.CategoryId, Number(outputRow.Values[7]));
        Assert.Equal(3, notifications[1].Args.Length);
        Assert.Empty(Assert.IsType<PyDict>(notifications[1].Args[1]).Entries);

        await rig.RestartAsync();
        Assert.Single(await new PgItemStore(rig.Source, rig.InventorySchema).InLocationAsync(rig.Station, characterId),
            item => item.TypeId == smallTypeId && item.Quantity == expectedOutputQuantity);
    }

    [PgFact]
    public async Task Combining_skill_injectors_sends_the_consumed_and_created_item_changes_to_the_client()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var characterId = await rig.CreateCharacterAsync();
        var catalog = await SkillCatalog.LoadAsync(new SdeReader(rig.Source));
        var smallTypeId = catalog.ItemTypeId("Small Skill Injector")!.Value;
        var largeTypeId = catalog.ItemTypeId("Large Skill Injector")!.Value;
        var dogma = await DogmaStatic.LoadAsync(new SdeReader(rig.Source));
        await dogma.EnsureTypesAsync([smallTypeId, largeTypeId]);
        var largeType = dogma.Type(largeTypeId)!;
        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        var smallStackId = await CreateItemAsync(rig, new NewItem(
            smallTypeId, characterId, rig.Station, ItemFlags.Hangar, 4, Singleton: false));
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = characterId, Sink = sink };
        session.Set("stationid", new PyLong(rig.Station));
        var skillHandler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));

        Assert.IsType<PyNone>(await BoundAsync(skillHandler, session, "CombineSkillInjector",
            new PyLong(smallStackId), new PyInt(3)));

        Assert.Equal(1, (await items.GetAsync(smallStackId))!.Quantity);
        var produced = Assert.Single(await items.InLocationAsync(rig.Station, characterId), item => item.TypeId == largeTypeId);
        Assert.Equal(1, produced.Quantity);
        var notifications = Notifications(sink);
        Assert.Equal(["OnItemChange", "OnItemChange"], notifications.Select(notification => notification.Name));
        Assert.Equal(3, notifications[0].Args.Length);
        var sourceRow = Assert.IsType<PyPackedRow>(notifications[0].Args[0]);
        Assert.Equal(smallStackId, ItemId(sourceRow.Values[0]));
        Assert.Equal(1, Number(sourceRow.Values[5]));
        Assert.Equal(new PyInt(9), Assert.Single(Assert.IsType<PyDict>(notifications[0].Args[1]).Entries).Key);
        Assert.Equal(new PyInt(4), Assert.Single(Assert.IsType<PyDict>(notifications[0].Args[1]).Entries).Value);
        var outputRow = Assert.IsType<PyPackedRow>(notifications[1].Args[0]);
        Assert.Equal(produced.Id, ItemId(outputRow.Values[0]));
        Assert.Equal(largeTypeId, Number(outputRow.Values[1]));
        Assert.Equal(1, Number(outputRow.Values[5]));
        Assert.Equal(largeType.GroupId, Number(outputRow.Values[6]));
        Assert.Equal(largeType.CategoryId, Number(outputRow.Values[7]));
        Assert.Equal(3, notifications[1].Args.Length);
        Assert.Empty(Assert.IsType<PyDict>(notifications[1].Args[1]).Entries);
    }

    [PgFact]
    public async Task Concurrent_skill_injector_combines_lock_the_stack_and_consume_only_one_valid_batch()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var characterId = await rig.CreateCharacterAsync();
        var catalog = await SkillCatalog.LoadAsync(new SdeReader(rig.Source));
        var smallTypeId = catalog.ItemTypeId("Small Skill Injector")!.Value;
        var largeTypeId = catalog.ItemTypeId("Large Skill Injector")!.Value;
        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        var smallStackId = await CreateItemAsync(rig, new NewItem(
            smallTypeId, characterId, rig.Station, ItemFlags.Hangar, 4, Singleton: false));
        var sink = new RecordingPacketSink();
        var session = new ClientSession { CharacterId = characterId, Sink = sink };
        session.Set("stationid", new PyLong(rig.Station));
        var skillHandler = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("skillHandler"));

        async Task<bool> CombineAsync()
        {
            try
            {
                Assert.IsType<PyNone>(await BoundAsync(skillHandler, session, "CombineSkillInjector",
                    new PyLong(smallStackId), new PyLong(4)));
                return true;
            }
            catch (MachoWrappedException)
            {
                return false;
            }
        }

        var outcomes = await Task.WhenAll(CombineAsync(), CombineAsync());
        Assert.Single(outcomes, succeeded => succeeded);
        Assert.Single(outcomes, succeeded => !succeeded);
        Assert.Equal(1, (await items.GetAsync(smallStackId))!.Quantity);
        Assert.Single(await items.InLocationAsync(rig.Station, characterId), item => item.TypeId == largeTypeId && item.Quantity == 1);
        await Assert.ThrowsAsync<MachoWrappedException>(() => BoundAsync(skillHandler, session, "CombineSkillInjector",
            new PyLong(smallStackId), new PyLong(4)));
        Assert.Equal(["OnItemChange", "OnItemChange"], Notifications(sink).Select(notification => notification.Name));

        await rig.RestartAsync();
        var restartedItems = new PgItemStore(rig.Source, rig.InventorySchema);
        Assert.Equal(1, (await restartedItems.GetAsync(smallStackId))!.Quantity);
        Assert.Single(await restartedItems.InLocationAsync(rig.Station, characterId), item => item.TypeId == largeTypeId && item.Quantity == 1);
    }

    private static async Task<long> CreateItemAsync(CharacterHostTests.CharacterHostRig rig, NewItem item)
    {
        var items = new PgItemStore(rig.Source, rig.InventorySchema);
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var itemId = await items.CreateAsync(connection, transaction, item);
        await transaction.CommitAsync();
        return itemId;
    }

    private static async Task<PyValue> CallAsync(IService service, string method, ClientSession session, params PyValue[] args) =>
        await service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session });

    private static async Task<PyValue> BoundAsync(IService service, ClientSession session, string method, params PyValue[] args)
    {
        var nested = Py.Tuple(new PyStr(method), Py.Tuple(args), PyNone.Instance);
        var bound = Assert.IsType<PyTuple>(await CallAsync(service, "MachoBindObject", session,
            Py.Tuple(new PyInt(0), new PyInt(3)), nested));
        return bound.Items[1];
    }

    private static PyValue SkillLevel(PyValue skill) =>
        ((PyTuple)((PyTuple)Assert.IsType<PyObjectEx>(skill).Header).Items[1]).Items[1];

    private static long ItemId(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => throw new Xunit.Sdk.XunitException($"Expected an item ID, got {value.GetType().Name}"),
    };

    private static long Number(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => throw new Xunit.Sdk.XunitException($"Expected an integer, got {value.GetType().Name}"),
    };

    private static IReadOnlyList<RecordedNotification> Notifications(RecordingPacketSink sink)
    {
        var events = new List<RecordedNotification>();
        foreach (var packet in sink.Sent.OfType<PyObject>())
        {
            var state = Assert.IsType<PyTuple>(packet.Args).Items;
            if (state[0] is not PyInt { Value: MachoMessage.Notification })
                continue;
            var address = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(state[2]).Args).Items;
            var body = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(state[4]).Items[0]).Items[1]);
            var inner = Assert.IsType<PyTuple>(MarshalCodec.Decode(body.Value.ToArray()));
            var args = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(inner.Items[1]).Items[1]).Items;
            events.Add(new RecordedNotification(Assert.IsType<PyStr>(address[1]).Value, [.. args]));
        }
        return events;
    }

    private sealed record RecordedNotification(string Name, PyValue[] Args);

    private sealed class RecordingPacketSink : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];
        public void Send(PyValue packet) => Sent.Add(packet);
    }
}
