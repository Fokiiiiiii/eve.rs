using System.Globalization;
using System.Text.Json.Nodes;
using Eve.GameStore;
using Eve.Characters;
using Eve.Data;
using Eve.Dogma;
using Eve.Events;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Core;
using Eve.Structures;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class StructureDeliveryBackendTests
{
    private const int SystemID = 30_000_142;
    private const long StructureID = 98_000_099;
    private const long CorporationID = 98_000_001;

    [Fact]
    public async Task Personal_delivery_moves_only_the_owned_hangar_subset_and_notifies_both_owners_after_commit()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateStructureAsync("Astrahus");
        fixture.Session.Set("structureid", new PyLong(StructureID));
        var recipientSink = new Sink();
        var recipient = new ClientSession(recipientSink) { CharacterId = fixture.RecipientID };
        fixture.Sessions.Enter(fixture.RecipientID, recipient);
        var strangerSink = new Sink();
        fixture.Sessions.Enter(fixture.StrangerID, new ClientSession(strangerSink) { CharacterId = fixture.StrangerID });
        var valid = await fixture.GiveAsync("Tritanium", fixture.ActorID, StructureID, ItemFlags.Hangar, 12);
        var foreign = await fixture.GiveAsync("Tritanium", fixture.RecipientID, StructureID, ItemFlags.Hangar);
        var fitted = await fixture.GiveAsync("Damage Control II", fixture.ActorID, StructureID, ItemFlags.LowSlot0, singleton: true);
        var remote = await fixture.GiveAsync("Tritanium", fixture.ActorID, StructureID + 1, ItemFlags.Hangar);
        var assembled = await fixture.GiveAsync("Rifter", fixture.ActorID, StructureID, ItemFlags.Hangar, singleton: true);
        fixture.SenderSink.BeforeSend = recipientSink.BeforeSend = () =>
            Assert.Equal(fixture.RecipientID, fixture.Items.GetAsync(valid).GetAwaiter().GetResult()!.OwnerId);

        await fixture.Backend().DeliverAsync(fixture.Call("Deliver"), StructureID, fixture.RecipientID,
            [valid, assembled, valid, foreign, fitted, remote, 0, -1]);

        await AssertItemAsync(fixture, valid, fixture.RecipientID, StructureID, 173, 12);
        await AssertItemAsync(fixture, assembled, fixture.RecipientID, StructureID, 173);
        Assert.True((await fixture.Items.GetAsync(assembled))!.Singleton);
        await AssertItemAsync(fixture, foreign, fixture.RecipientID, StructureID, ItemFlags.Hangar);
        await AssertItemAsync(fixture, fitted, fixture.ActorID, StructureID, ItemFlags.LowSlot0);
        await AssertItemAsync(fixture, remote, fixture.ActorID, StructureID + 1, ItemFlags.Hangar);
        Assert.Equal(2, fixture.SenderSink.Sent.Count);
        Assert.Equal(3, recipientSink.Sent.Count);
        Assert.Empty(strangerSink.Sent);
        Assert.Single(fixture.PublishedInbox);
        Assert.Equal("inbox", fixture.NotificationOrder.Last());
        await fixture.Bus.PumpAsync();
        Assert.Equal(new[] { valid, assembled }, fixture.MovedEvents.Select(item => item.ItemId));
        Assert.All(fixture.MovedEvents, item =>
        {
            Assert.Equal(fixture.ActorID, item.PreviousOwnerId);
            Assert.Equal(fixture.RecipientID, item.OwnerId);
        });
        await fixture.Backend().DeliverAsync(fixture.Call("Deliver"), StructureID, fixture.RecipientID, [valid, assembled]);
        await fixture.Bus.PumpAsync();
        Assert.Equal(2, fixture.MovedEvents.Count);
        Assert.Equal(3, recipientSink.Sent.Count);
    }

    [Fact]
    public async Task Corporate_or_unknown_recipient_is_refused_without_moves_or_notices()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateStructureAsync("Astrahus");
        fixture.Session.Set("structureid", new PyLong(StructureID));
        var item = await fixture.GiveAsync("Tritanium", fixture.ActorID, StructureID, ItemFlags.Hangar);
        await Assert.ThrowsAsync<MachoWrappedException>(() =>
            fixture.Backend().DeliverAsync(fixture.Call("Deliver"), StructureID, CorporationID, [item]).AsTask());
        await AssertItemAsync(fixture, item, fixture.ActorID, StructureID, ItemFlags.Hangar);
        await fixture.Bus.PumpAsync();
        Assert.Empty(fixture.MovedEvents);
        Assert.Empty(fixture.SenderSink.Sent);
    }

    [Fact]
    public async Task Cargo_deposit_without_docking_permissions_returns_distinct_committed_ids_and_reconstructed_retry_is_empty()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateStructureAsync("Astrahus");
        var ship = await fixture.ActiveShipAsync();
        var valid = await fixture.GiveAsync("Tritanium", fixture.ActorID, ship, ItemFlags.Cargo, 20);
        var foreign = await fixture.GiveAsync("Tritanium", fixture.RecipientID, ship, ItemFlags.Cargo);
        var fitted = await fixture.GiveAsync("Damage Control II", fixture.ActorID, ship, ItemFlags.LowSlot0);
        var droneBay = await fixture.GiveAsync("Warrior I", fixture.ActorID, ship, 87);
        var remote = await fixture.GiveAsync("Tritanium", fixture.ActorID, ship + 1, ItemFlags.Cargo);
        fixture.SurfaceDistance = StructureDeliveryBackend.CargoDepositRangeMeters;
        fixture.SenderSink.BeforeSend = () =>
            Assert.Equal(StructureID, fixture.Items.GetAsync(valid).GetAwaiter().GetResult()!.LocationId);

        var moved = await fixture.Backend().DropOffItemsAsync(fixture.Call(), [valid, valid, foreign, fitted, droneBay, remote, 0], ship, StructureID);
        Assert.Equal(new[] { valid }, moved);
        await AssertItemAsync(fixture, valid, fixture.ActorID, StructureID, 173, 20);
        await AssertItemAsync(fixture, foreign, fixture.RecipientID, ship, ItemFlags.Cargo);
        await AssertItemAsync(fixture, fitted, fixture.ActorID, ship, ItemFlags.LowSlot0);
        await AssertItemAsync(fixture, droneBay, fixture.ActorID, ship, 87);
        await AssertItemAsync(fixture, remote, fixture.ActorID, ship + 1, ItemFlags.Cargo);
        Assert.Equal((ship, StructureID), fixture.LastRangeRequest);
        Assert.Empty(fixture.PublishedInbox);
        Assert.Empty(await fixture.InboxRowsAsync(fixture.ActorID));
        Assert.Single(fixture.SenderSink.Sent);
        Assert.Empty(await fixture.Backend().DropOffItemsAsync(fixture.Call(), [valid], ship, StructureID));
        await fixture.Bus.PumpAsync();
        Assert.Single(fixture.MovedEvents);
        Assert.Single(fixture.SenderSink.Sent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1.0)]
    [InlineData(10_000.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task Unreachable_or_out_of_range_cargo_changes_nothing(double? surfaceDistance)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateStructureAsync("Astrahus");
        var ship = await fixture.ActiveShipAsync();
        var item = await fixture.GiveAsync("Tritanium", fixture.ActorID, ship, ItemFlags.Cargo);
        fixture.SurfaceDistance = surfaceDistance;
        await Assert.ThrowsAsync<MachoWrappedException>(() =>
            fixture.Backend().DropOffItemsAsync(fixture.Call(), [item], ship, StructureID).AsTask());
        await AssertItemAsync(fixture, item, fixture.ActorID, ship, ItemFlags.Cargo);
        await fixture.Bus.PumpAsync();
        Assert.Empty(fixture.MovedEvents);
        Assert.Empty(fixture.SenderSink.Sent);
    }

    [Fact]
    public async Task A_system_transition_during_proximity_query_is_refused_without_moves_events_or_notices()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateStructureAsync("Astrahus");
        var ship = await fixture.ActiveShipAsync();
        var item = await fixture.GiveAsync("Tritanium", fixture.ActorID, ship, ItemFlags.Cargo);
        fixture.OnDistanceRead = () => fixture.Session.Set("solarsystemid", new PyInt(SystemID + 1));
        await Assert.ThrowsAsync<MachoWrappedException>(() =>
            fixture.Backend().DropOffItemsAsync(fixture.Call(), [item], ship, StructureID).AsTask());
        await AssertItemAsync(fixture, item, fixture.ActorID, ship, ItemFlags.Cargo);
        await fixture.Bus.PumpAsync();
        Assert.Empty(fixture.MovedEvents);
        Assert.Empty(fixture.SenderSink.Sent);
    }

    [Fact]
    public async Task Session_cannot_substitute_another_ship_for_the_persisted_active_ship()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateStructureAsync("Astrahus");
        await fixture.ActiveShipAsync();
        var otherShip = await fixture.GiveAsync("Rifter", fixture.RecipientID, SystemID, 0, singleton: true);
        fixture.Session.Set("shipid", new PyLong(otherShip));
        var item = await fixture.GiveAsync("Tritanium", fixture.ActorID, otherShip, ItemFlags.Cargo);
        await Assert.ThrowsAsync<MachoWrappedException>(() =>
            fixture.Backend().DropOffItemsAsync(fixture.Call(), [item], otherShip, StructureID).AsTask());
        await AssertItemAsync(fixture, item, fixture.ActorID, otherShip, ItemFlags.Cargo);
        Assert.Empty(fixture.SenderSink.Sent);
    }

    [Fact]
    public async Task Metenox_accepts_SDE_fuel_blocks_and_Magmatic_Gas_into_the_owners_fuel_bay_only()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateStructureAsync("Metenox Moon Drill");
        var ship = await fixture.ActiveShipAsync();
        var block = await fixture.GiveAsync("Helium Fuel Block", fixture.ActorID, ship, ItemFlags.Cargo);
        var gas = await fixture.GiveAsync("Magmatic Gas", fixture.ActorID, ship, ItemFlags.Cargo, 8);
        var ozone = await fixture.GiveAsync("Liquid Ozone", fixture.ActorID, ship, ItemFlags.Cargo);
        var ice = await fixture.GiveAsync("Superionic Ice", fixture.ActorID, ship, ItemFlags.Cargo);
        var moved = await fixture.Backend().DropOffItemsAsync(fixture.Call(), [block, gas, ozone, ice], ship, StructureID);
        Assert.Equal(new[] { block, gas }, moved);
        await AssertItemAsync(fixture, block, CorporationID, StructureID, 172);
        await AssertItemAsync(fixture, gas, CorporationID, StructureID, 172, 8);
        await AssertItemAsync(fixture, ozone, fixture.ActorID, ship, ItemFlags.Cargo);
        await AssertItemAsync(fixture, ice, fixture.ActorID, ship, ItemFlags.Cargo);
        await fixture.Bus.PumpAsync();
        Assert.Equal(2, fixture.MovedEvents.Count);
        Assert.All(fixture.MovedEvents, item => Assert.Equal(CorporationID, item.OwnerId));
    }

    [Fact]
    public async Task A_later_database_failure_rolls_back_the_whole_batch_and_its_outbox_and_sends_no_notices()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateStructureAsync("Astrahus");
        var ship = await fixture.ActiveShipAsync();
        var first = await fixture.GiveAsync("Tritanium", fixture.ActorID, ship, ItemFlags.Cargo);
        var second = await fixture.GiveAsync("Tritanium", fixture.ActorID, ship, ItemFlags.Cargo);
        await fixture.FailUpdateAsync(second);
        await Assert.ThrowsAsync<PostgresException>(() =>
            fixture.Backend().DropOffItemsAsync(fixture.Call(), [first, second], ship, StructureID).AsTask());
        await AssertItemAsync(fixture, first, fixture.ActorID, ship, ItemFlags.Cargo);
        await AssertItemAsync(fixture, second, fixture.ActorID, ship, ItemFlags.Cargo);
        await fixture.Bus.PumpAsync();
        Assert.Empty(fixture.MovedEvents);
        Assert.Empty(fixture.SenderSink.Sent);
    }

    [Fact]
    public async Task Concurrent_deposits_of_one_item_commit_once()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateStructureAsync("Astrahus");
        var ship = await fixture.ActiveShipAsync();
        var item = await fixture.GiveAsync("Tritanium", fixture.ActorID, ship, ItemFlags.Cargo);
        var results = await Task.WhenAll(
            fixture.Backend().DropOffItemsAsync(fixture.Call(), [item], ship, StructureID).AsTask(),
            fixture.Backend().DropOffItemsAsync(fixture.Call(), [item], ship, StructureID).AsTask());
        Assert.Single(results, result => result.Count == 1);
        Assert.Single(results, result => result.Count == 0);
        await AssertItemAsync(fixture, item, fixture.ActorID, StructureID, 173);
        await fixture.Bus.PumpAsync();
        Assert.Single(fixture.MovedEvents);
        Assert.Single(fixture.SenderSink.Sent);
    }

    [Fact]
    public async Task Offline_inbox_survives_reload_and_summarizes_only_committed_types_in_client_quantity_type_order()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateStructureAsync("Astrahus");
        fixture.Session.Set("structureid", new PyLong(StructureID));
        var first = await fixture.GiveAsync("Tritanium", fixture.ActorID, StructureID, ItemFlags.Hangar, 7);
        var second = await fixture.GiveAsync("Tritanium", fixture.ActorID, StructureID, ItemFlags.Hangar, 5);
        var ship = await fixture.GiveAsync("Rifter", fixture.ActorID, StructureID, ItemFlags.Hangar, singleton: true);
        var foreign = await fixture.GiveAsync("Tritanium", fixture.StrangerID, StructureID, ItemFlags.Hangar, 900);
        await fixture.Backend().DeliverAsync(fixture.Call("Deliver"), StructureID, fixture.RecipientID,
            [first, second, ship, first, foreign]);

        var saved = Assert.IsType<JsonObject>(Assert.Single((await fixture.InboxRowsAsync(fixture.RecipientID))).Value);
        Assert.Equal(199, saved["typeID"]!.GetValue<int>());
        Assert.Equal(4, saved["groupID"]!.GetValue<int>());
        Assert.False(saved["processed"]!.GetValue<bool>());
        Assert.Equal(fixture.ActorID, saved["senderID"]!.GetValue<long>());
        Assert.Equal(fixture.RecipientID, saved["receiverID"]!.GetValue<long>());
        var data = Assert.IsType<JsonObject>(saved["data"]);
        Assert.Equal(StructureID, data["structureID"]!.GetValue<long>());
        Assert.Equal(SystemID, data["solarsystemID"]!.GetValue<int>());
        Assert.Equal(fixture.TypeID("Astrahus"), data["structureTypeID"]!.GetValue<int>());
        Assert.Equal(fixture.ActorID, data["charID"]!.GetValue<long>());
        Assert.Equal("showinfo", data["structureShowInfoData"]![0]!.GetValue<string>());
        Assert.Equal(fixture.TypeID("Astrahus"), data["structureShowInfoData"]![1]!.GetValue<int>());
        Assert.Equal(StructureID, data["structureShowInfoData"]![2]!.GetValue<long>());
        var summary = Assert.IsType<JsonArray>(data["listOfTypesAndQty"]);
        var expected = new[] { (Type: fixture.TypeID("Tritanium"), Quantity: 12L), (Type: fixture.TypeID("Rifter"), Quantity: 1L) }
            .OrderBy(row => row.Type).ToArray();
        Assert.Equal(expected.Select(row => row.Type), summary.Select(row => row![1]!.GetValue<int>()));
        Assert.Equal(expected.Select(row => row.Quantity), summary.Select(row => row![0]!.GetValue<long>()));

        var published = Assert.Single(fixture.PublishedInbox);
        Assert.Equal(fixture.RecipientID, published.Recipient);
        Assert.Equal(5, published.Args.Length);
        Assert.Equal(new PyLong(saved["notificationID"]!.GetValue<long>()), published.Args[0]);
        Assert.Equal(new PyInt(199), published.Args[1]);
        Assert.Equal(new PyLong(fixture.ActorID), published.Args[2]);
        Assert.Equal(Py.FileTimeLong(saved["created"]!.GetValue<long>()), published.Args[3]);
        var wireData = Assert.IsType<PyDict>(published.Args[4]);
        var wireSummary = Assert.IsType<PyList>(wireData.Entries.Single(pair => pair.Key == new PyStr("listOfTypesAndQty")).Value);
        Assert.Equal(new PyLong(12), Assert.IsType<PyList>(wireSummary.Items.Single(row =>
            Assert.IsType<PyList>(row).Items[1] == new PyInt(fixture.TypeID("Tritanium")))).Items[0]);

        var service = new NotificationMgrService(async (id, _) => new JsonObject { ["byID"] = await fixture.InboxRowsAsync(id) }, () => 1_000_000);
        var restoredSession = new ClientSession(new Sink()) { CharacterId = fixture.RecipientID };
        restoredSession.Set("charid", new PyLong(fixture.RecipientID));
        var restored = Assert.IsType<PyList>(await service.CallAsync(new ServiceCall
            { Method = "GetAllNotifications", Args = [], Session = restoredSession }));
        Assert.Single(restored.Items);
        await fixture.Backend().DeliverAsync(fixture.Call("Deliver"), StructureID, fixture.RecipientID, [first, second, ship]);
        Assert.Single(await fixture.InboxRowsAsync(fixture.RecipientID));
        Assert.Single(fixture.PublishedInbox);
        Assert.Equal(3, fixture.SenderSink.Sent.Count);
    }

    [Fact]
    public async Task Inbox_failure_after_its_write_rolls_back_items_inbox_outbox_and_all_live_notifications()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateStructureAsync("Astrahus");
        fixture.Session.Set("structureid", new PyLong(StructureID));
        var item = await fixture.GiveAsync("Tritanium", fixture.ActorID, StructureID, ItemFlags.Hangar);
        fixture.FailInboxPersistence = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Backend().DeliverAsync(fixture.Call("Deliver"), StructureID, fixture.RecipientID, [item]).AsTask());
        await AssertItemAsync(fixture, item, fixture.ActorID, StructureID, ItemFlags.Hangar);
        Assert.Empty(await fixture.InboxRowsAsync(fixture.RecipientID));
        await fixture.Bus.PumpAsync();
        Assert.Empty(fixture.MovedEvents);
        Assert.Empty(fixture.SenderSink.Sent);
        Assert.Empty(fixture.PublishedInbox);
    }

    [Fact]
    public async Task A_missing_inbox_writer_refuses_delivery_before_inventory_mutation()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateStructureAsync("Astrahus");
        fixture.Session.Set("structureid", new PyLong(StructureID));
        var item = await fixture.GiveAsync("Tritanium", fixture.ActorID, StructureID, ItemFlags.Hangar);
        await Assert.ThrowsAsync<MachoWrappedException>(() =>
            fixture.Backend(withInbox: false).DeliverAsync(fixture.Call("Deliver"), StructureID, fixture.RecipientID, [item]).AsTask());
        await AssertItemAsync(fixture, item, fixture.ActorID, StructureID, ItemFlags.Hangar);
        Assert.Empty(await fixture.InboxRowsAsync(fixture.RecipientID));
        Assert.Empty(fixture.SenderSink.Sent);
    }

    [Fact]
    public async Task Concurrent_personal_deliveries_create_one_inbox_entry_and_one_set_of_item_notices()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateStructureAsync("Astrahus");
        fixture.Session.Set("structureid", new PyLong(StructureID));
        var item = await fixture.GiveAsync("Tritanium", fixture.ActorID, StructureID, ItemFlags.Hangar);
        await Task.WhenAll(
            fixture.Backend().DeliverAsync(fixture.Call("Deliver"), StructureID, fixture.RecipientID, [item]).AsTask(),
            fixture.Backend().DeliverAsync(fixture.Call("Deliver"), StructureID, fixture.RecipientID, [item]).AsTask());
        Assert.Single(await fixture.InboxRowsAsync(fixture.RecipientID));
        Assert.Single(fixture.PublishedInbox);
        Assert.Single(fixture.SenderSink.Sent);
        await fixture.Bus.PumpAsync();
        Assert.Single(fixture.MovedEvents);
    }

    [Fact]
    public async Task Post_commit_publisher_failure_leaves_a_durable_inbox_and_retry_does_not_duplicate_item_notices()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateStructureAsync("Astrahus");
        fixture.Session.Set("structureid", new PyLong(StructureID));
        var item = await fixture.GiveAsync("Tritanium", fixture.ActorID, StructureID, ItemFlags.Hangar);
        fixture.FailInboxPublish = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Backend().DeliverAsync(fixture.Call("Deliver"), StructureID, fixture.RecipientID, [item]).AsTask());
        await AssertItemAsync(fixture, item, fixture.RecipientID, StructureID, 173);
        Assert.Single(await fixture.InboxRowsAsync(fixture.RecipientID));
        Assert.Single(fixture.SenderSink.Sent);
        fixture.FailInboxPublish = false;
        await fixture.Backend().DeliverAsync(fixture.Call("Deliver"), StructureID, fixture.RecipientID, [item]);
        Assert.Single(await fixture.InboxRowsAsync(fixture.RecipientID));
        Assert.Single(fixture.SenderSink.Sent);
        Assert.Empty(fixture.PublishedInbox);
    }

    [Fact]
    public async Task Failed_item_notice_still_publishes_the_committed_delivery_inbox()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateStructureAsync("Astrahus");
        fixture.Session.Set("structureid", new PyLong(StructureID));
        var item = await fixture.GiveAsync("Tritanium", fixture.ActorID, StructureID, ItemFlags.Hangar);
        fixture.SenderSink.BeforeSend = () => throw new InvalidOperationException("The sender disconnected.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Backend()
            .DeliverAsync(fixture.Call("Deliver"), StructureID, fixture.RecipientID, [item]).AsTask());
        await AssertItemAsync(fixture, item, fixture.RecipientID, StructureID, 173);
        Assert.Single(await fixture.InboxRowsAsync(fixture.RecipientID));
        Assert.Single(fixture.PublishedInbox);
    }

    private static async Task AssertItemAsync(Fixture fixture, long id, long owner, long location, int flag, long quantity = 1)
    {
        var item = Assert.IsType<Item>(await fixture.Items.GetAsync(id));
        Assert.Equal(owner, item.OwnerId);
        Assert.Equal(location, item.LocationId);
        Assert.Equal(flag, item.Flag);
        Assert.Equal(quantity, item.Quantity);
    }

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];
        public Action? BeforeSend { get; set; }
        public Action? OnSend { get; set; }
        public void Send(PyValue packet) { BeforeSend?.Invoke(); Sent.Add(packet); OnSend?.Invoke(); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string[] _schemas;
        private readonly IReadOnlyList<SdeTypeSummary> _types;
        private readonly DogmaStatic _dogma;
        private readonly StructureRulesSource _rules;
        private readonly PgCharacterStore _characters;
        private readonly DurableScheduler _scheduler;
        private readonly PersistenceStore _mailboxes;
        private OwnerFence _inboxFence = null!;

        private Fixture(NpgsqlDataSource source, string[] schemas, IReadOnlyList<SdeTypeSummary> types,
            DogmaStatic dogma, StructureRulesSource rules, long actor, long recipient, long stranger)
        {
            Source = source; _schemas = schemas; _types = types; _dogma = dogma; _rules = rules;
            _mailboxes = new PersistenceStore(source, schemas[5], clockMs: () => 1_000_000);
            SenderSink.OnSend = () => NotificationOrder.Add("item");
            Items = new PgItemStore(source, schemas[0]);
            Structures = new StructureStore(source, schemas[1]);
            _characters = new PgCharacterStore(source, schemas[2]);
            _scheduler = new DurableScheduler(source, () => 1_000_000, new SchedulerOptions { Concurrency = 1 }, schemas[3]);
            Bus = new EventBus(source, _scheduler, schema: schemas[4]);
            Bus.Subscribe("structure-delivery-test", [StructureDeliveryBackend.ItemMovedTopic], async (context, _) =>
            {
                MovedEvents.Add(context.Event.Payload<StructureDeliveryBackend.ItemMoved>()!);
                await Task.CompletedTask;
            }, StartAt.Beginning);
            ActorID = actor; RecipientID = recipient; StrangerID = stranger;
            Session = new ClientSession(SenderSink) { CharacterId = actor };
            Sessions.Enter(actor, Session);
        }

        public NpgsqlDataSource Source { get; }
        public PgItemStore Items { get; }
        public StructureStore Structures { get; }
        public EventBus Bus { get; }
        public CharacterSessions Sessions { get; } = new();
        public ClientSession Session { get; }
        public Sink SenderSink { get; } = new();
        public List<StructureDeliveryBackend.ItemMoved> MovedEvents { get; } = [];
        public List<(long Recipient, PyValue[] Args)> PublishedInbox { get; } = [];
        public List<string> NotificationOrder { get; } = [];
        public bool FailInboxPersistence { get; set; }
        public bool FailInboxPublish { get; set; }
        public long ActorID { get; }
        public long RecipientID { get; }
        public long StrangerID { get; }
        public double? SurfaceDistance { get; set; } = 0;
        public Action? OnDistanceRead { get; set; }
        public (long Ship, long Structure) LastRangeRequest { get; private set; }

        public static async Task<Fixture> CreateAsync()
        {
            var url = Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is not set");
            var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
            var sde = new SdeReader(source);
            Assert.NotNull(await sde.BuildAsync());
            var dogma = await DogmaStatic.LoadAsync(sde);
            var rules = await StructureRulesSource.LoadAsync(sde);
            var types = await sde.ReadTypeSummariesAsync();
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var schemas = new[] { "sd_i_" + suffix, "sd_s_" + suffix, "sd_c_" + suffix, "sd_q_" + suffix, "sd_e_" + suffix, "sd_n_" + suffix };
            await PgItemStore.MigrateAsync(source, schemas[0]);
            await StructureStore.MigrateAsync(source, schemas[1]);
            await PgCharacterStore.MigrateAsync(source, schemas[2]);
            await DurableScheduler.MigrateAsync(source, schemas[3]);
            await EventBus.MigrateAsync(source, schemas[4]);
            var characters = new PgCharacterStore(source, schemas[2]);
            await using var connection = await source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            async Task<long> CharacterAsync(string name) =>
                await characters.InsertAsync(connection, transaction,
                    new NewCharacter(1, name, 0, 1, 1, 1, 1, 1373, CorporationID, null,
                        0, SystemID, 20_000_020, 10_000_002, null, null));
            var actor = await CharacterAsync("Delivery Sender");
            var recipient = await CharacterAsync("Delivery Recipient");
            var stranger = await CharacterAsync("Delivery Stranger");
            await transaction.CommitAsync();
            var fixture = new Fixture(source, schemas, types, dogma, rules, actor, recipient, stranger);
            await fixture._mailboxes.EnsureSchemaAsync();
            fixture._inboxFence = await fixture._mailboxes.AcquireOwnerAsync("world", "structure-delivery-test", new AcquireOptions(LeaseMs: 600_000));
            await fixture._mailboxes.ApplyChangesAsync("notifications", [new RowUpsert("boxes", "{}")], [], fixture._inboxFence);
            return fixture;
        }

        public StructureDeliveryBackend Backend(bool withInbox = true) => new(
            Source, Items, Structures, _rules, _dogma, _characters, Sessions, Bus,
            async (id, token) => await Structures.GetAsync(id, token) is not null
                ? new PersistedStructureInfo(id, true, new PyDict([])) : null,
            (_, ship, structure, _) =>
            {
                LastRangeRequest = (ship, structure);
                OnDistanceRead?.Invoke();
                return ValueTask.FromResult(SurfaceDistance);
            }, withInbox ? SaveInboxAsync : null, () => 1_000_000);

        private async ValueTask<StructureDeliveryBackend.DeliveryInboxCommit> SaveInboxAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction,
            StructureDeliveryBackend.DeliveryInboxNotification notification, CancellationToken token)
        {
            // Exercise the actual shared mailbox table and transaction-aware persistence foundation.
            // The injected production writer must use the mailbox owner's gate/fence and cache policy.
            await using (var rowLock = new NpgsqlCommand("SELECT key FROM " + _schemas[5] +
                ".notifications WHERE key = 'boxes' FOR UPDATE", connection, transaction))
                await rowLock.ExecuteScalarAsync(token);
            var boxes = JsonNode.Parse((await _mailboxes.ReadRowAsync(connection, transaction, "notifications", "boxes", token))!)!.AsObject();
            var ids = boxes.SelectMany(entry => entry.Value is JsonObject mailbox && mailbox["byID"] is JsonObject rows
                ? rows.Select(row => long.Parse(row.Key, CultureInfo.InvariantCulture)) : Enumerable.Empty<long>());
            var id = checked(ids.DefaultIfEmpty(0).Max() + 1);
            var recipientKey = notification.RecipientCharacterID.ToString(CultureInfo.InvariantCulture);
            var box = boxes[recipientKey] as JsonObject;
            if (box is null) boxes[recipientKey] = box = new JsonObject();
            var byID = box["byID"] as JsonObject;
            if (byID is null) box["byID"] = byID = new JsonObject();
            byID[id.ToString(CultureInfo.InvariantCulture)] = notification.ToRecord(id);
            await _mailboxes.ApplyChangesAsync(connection, transaction, "notifications",
                [new RowUpsert("boxes", boxes.ToJsonString())], [], _inboxFence, token);
            if (FailInboxPersistence) throw new InvalidOperationException("Injected failure after inbox persistence.");
            return new StructureDeliveryBackend.DeliveryInboxCommit(id, async args =>
            {
                var committed = await InboxRowsAsync(notification.RecipientCharacterID);
                Assert.NotNull(committed[id.ToString(CultureInfo.InvariantCulture)]);
                if (FailInboxPublish) throw new InvalidOperationException("Injected post-commit publisher failure.");
                NotificationOrder.Add("inbox");
                PublishedInbox.Add((notification.RecipientCharacterID, args));
                Sessions.Of(notification.RecipientCharacterID)?.SendNotification("OnNotificationReceived", "clientID", args);
            });
        }

        public async Task<JsonObject> InboxRowsAsync(long recipient)
        {
            // A new persistence reader proves reconnect/restart does not depend on the writer's cache.
            await using var reopened = new PersistenceStore(Source, _schemas[5]);
            var table = await reopened.LoadTableAsync("notifications");
            return (table["boxes"]?[recipient.ToString(CultureInfo.InvariantCulture)]?["byID"] as JsonObject)?.DeepClone().AsObject() ?? new JsonObject();
        }

        public ServiceCall Call(string method = "DropOffItems") => new() { Method = method, Args = [], Session = Session };

        public async Task CreateStructureAsync(string type)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await Structures.CreateAsync(connection, transaction,
                new NewStructure(StructureID, TypeID(type), SystemID, 0, 0, 0, 0, CorporationID, 0, "Delivery Test", ""));
            await transaction.CommitAsync();
        }

        public async Task<long> ActiveShipAsync()
        {
            var ship = await GiveAsync("Rifter", ActorID, SystemID, 0, singleton: true);
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await _characters.SetShipAsync(connection, transaction, ActorID, ship);
            await transaction.CommitAsync();
            Session.Set("shipid", new PyLong(ship));
            Session.Set("solarsystemid", new PyInt(SystemID));
            return ship;
        }

        public async Task<long> GiveAsync(string type, long owner, long location, int flag, long quantity = 1, bool singleton = false)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var id = await Items.CreateAsync(connection, transaction, new NewItem(TypeID(type), owner, location, flag, quantity, singleton));
            await transaction.CommitAsync();
            return id;
        }

        public int TypeID(string name) => _types.Single(type => type.Name == name).TypeId;

        public async Task FailUpdateAsync(long id)
        {
            await using var connection = await Source.OpenConnectionAsync();
            var function = "CREATE FUNCTION " + _schemas[0] + ".reject_item_update() RETURNS trigger LANGUAGE plpgsql AS $body$ " +
                "BEGIN IF NEW.id = " + id.ToString(CultureInfo.InvariantCulture) +
                " THEN RAISE EXCEPTION 'injected delivery update failure'; END IF; RETURN NEW; END; $body$";
            await using (var command = new NpgsqlCommand(function, connection)) await command.ExecuteNonQueryAsync();
            var trigger = "CREATE TRIGGER reject_item_update BEFORE UPDATE ON " + _schemas[0] +
                ".items FOR EACH ROW EXECUTE FUNCTION " + _schemas[0] + ".reject_item_update()";
            await using (var command = new NpgsqlCommand(trigger, connection)) await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _scheduler.StopAsync();
            await using (var connection = await Source.OpenConnectionAsync())
                foreach (var schema in _schemas.Reverse())
                    await Sql.ExecuteAsync(connection, null, "DROP SCHEMA " + schema + " CASCADE", CancellationToken.None);
            await Source.DisposeAsync();
        }
    }
}
