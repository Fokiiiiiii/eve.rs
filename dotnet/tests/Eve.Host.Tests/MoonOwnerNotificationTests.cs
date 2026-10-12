using System.Text.Json.Nodes;
using Eve.Characters;
using Eve.Data;
using Eve.GameStore;
using Eve.Marshal;
using Eve.Host.Persistence.Mail;
using Eve.Host.Persistence.Structures;
using Eve.Structures;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class MoonOwnerNotificationTests
{
    private const long CorporationId = 98_000_001;
    private const long ForeignCorporationId = 98_000_002;
    private const long StructureId = 99_000_001;
    private const long ExtractionId = 99_000_101;
    private const long ManualCancelAliasExtractionId = 99_000_102;
    private const long MoonId = 40_000_001;
    private const int SolarSystemId = 30_000_001;
    private const int StructureTypeId = 35_835;
    private const long StartMs = 1_700_000_000_000;
    private const long NowMs = 1_700_000_123_456;
    private const long DurationSeconds = MoonExtractions.MinimumDurationSeconds;

    [Fact]
    public async Task Moon_event_notifications_match_client_data_and_only_reach_owner_corporation_characters()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.OnlineCharacterIds.Add(fixture.ActorCharacterId);

        Assert.Equal(2, await fixture.Worker.OnStartedAsync(fixture.StartedEvent()));
        Assert.Equal(2, await fixture.Worker.OnChunkArrivedAsync(fixture.ChunkArrivedEvent()));
        Assert.Equal(2, await fixture.Worker.OnFractureStartedAsync(fixture.FractureStartedEvent()));
        Assert.Equal(2, await fixture.Worker.OnEndedAsync(fixture.EndedEvent(MoonExtractions.Cancelled, fixture.ActorCharacterId)));
        Assert.Equal(2, await fixture.Worker.OnEndedAsync(fixture.EndedEvent(MoonExtractions.Decayed, 0)));
        Assert.Equal(2, await fixture.Worker.OnEndedAsync(fixture.EndedEvent("manualcancel", fixture.ActorCharacterId, ManualCancelAliasExtractionId)));

        Assert.Equal(0, await fixture.Worker.OnEndedAsync(fixture.EndedEvent(MoonExtractions.Offlined, 0)));
        Assert.Equal(0, await fixture.Worker.OnEndedAsync(fixture.EndedEvent(MoonExtractions.Fractured, fixture.ActorCharacterId)));

        var actorBox = Assert.IsType<JsonObject>(await fixture.ReadPersistedBoxAsync(fixture.ActorCharacterId));
        var offlineBox = Assert.IsType<JsonObject>(await fixture.ReadPersistedBoxAsync(fixture.OfflineCharacterId));
        Assert.Null(await fixture.ReadPersistedBoxAsync(fixture.ForeignCharacterId));

        Assert.Equal(new[] { 202, 203, 203, 204, 205, 206 }, TypeIds(actorBox));
        Assert.Equal(TypeIds(actorBox), TypeIds(offlineBox));
        Assert.Equal(6, fixture.LiveRecipientIds.Count(id => id == fixture.ActorCharacterId));
        Assert.Equal(6, fixture.LivePayloads.Count);
        Assert.DoesNotContain(fixture.OfflineCharacterId, fixture.LiveRecipientIds);
        Assert.DoesNotContain(fixture.ForeignCharacterId, fixture.LiveRecipientIds);

        var wire = fixture.LivePayloads[0].Arguments;
        Assert.Equal(5, wire.Length);
        Assert.IsType<PyLong>(wire[0]);
        Assert.IsType<PyInt>(wire[1]);
        Assert.IsType<PyLong>(wire[2]);
        Assert.IsType<PyLong>(wire[3]);
        Assert.IsType<PyDict>(wire[4]);

        var started = Record(actorBox, 202);
        AssertCommon(started, fixture.ActorCharacterId);
        var startedData = Assert.IsType<JsonObject>(started["data"]);
        Assert.Equal(fixture.StructureLink, startedData["structureLink"]!.GetValue<string>());
        Assert.Equal(fixture.StructureName, startedData["structureName"]!.GetValue<string>());
        Assert.Equal(StructureTypeId, startedData["structureTypeID"]!.GetValue<long>());
        Assert.Equal(StructureId, startedData["structureID"]!.GetValue<long>());
        Assert.Equal(SolarSystemId, startedData["solarSystemID"]!.GetValue<long>());
        Assert.Equal(MoonId, startedData["moonID"]!.GetValue<long>());
        Assert.Equal(fixture.ActorCharacterId, startedData["startedBy"]!.GetValue<long>());
        Assert.Equal(fixture.ActorLink, startedData["startedByLink"]!.GetValue<string>());
        Assert.Equal(FileTime(fixture.ChunkAvailableMs), startedData["readyTime"]!.GetValue<long>());
        Assert.Equal(FileTime(fixture.NaturalDecayMs), startedData["autoTime"]!.GetValue<long>());
        Assert.Equal(123.5, OreVolumes(startedData)["45492"]!.GetValue<double>());
        Assert.Equal(27.25, OreVolumes(startedData)["45493"]!.GetValue<double>());

        var cancelled = Records(actorBox, 203);
        Assert.Equal(2, cancelled.Length);
        Assert.All(cancelled, record =>
        {
            var data = Assert.IsType<JsonObject>(record["data"]);
            Assert.Equal(fixture.ActorCharacterId, data["cancelledBy"]!.GetValue<long>());
            Assert.Equal(fixture.ActorLink, data["cancelledByLink"]!.GetValue<string>());
        });

        var finishedData = Assert.IsType<JsonObject>(Record(actorBox, 204)["data"]);
        Assert.Equal(FileTime(fixture.NaturalDecayMs), finishedData["autoTime"]!.GetValue<long>());
        Assert.Equal(2, OreVolumes(finishedData).Count);

        var laserData = Assert.IsType<JsonObject>(Record(actorBox, 205)["data"]);
        Assert.Equal(fixture.ActorCharacterId, laserData["firedBy"]!.GetValue<long>());
        Assert.Equal(fixture.ActorLink, laserData["firedByLink"]!.GetValue<string>());
        Assert.Equal(2, OreVolumes(laserData).Count);

        var automaticData = Assert.IsType<JsonObject>(Record(actorBox, 206)["data"]);
        Assert.Equal(2, OreVolumes(automaticData).Count);
    }

    [Fact]
    public async Task Retries_concurrency_and_restart_do_not_duplicate_persisted_owner_notifications()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.OnlineCharacterIds.Add(fixture.ActorCharacterId);
        var started = fixture.StartedEvent();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Worker.OnStartedAsync(started)));
        Assert.Equal(2, results.Sum());
        Assert.Equal(0, await fixture.Worker.OnStartedAsync(started));
        Assert.Single(Records(Assert.IsType<JsonObject>(await fixture.ReadPersistedBoxAsync(fixture.ActorCharacterId)), 202));
        Assert.Single(Records(Assert.IsType<JsonObject>(await fixture.ReadPersistedBoxAsync(fixture.OfflineCharacterId)), 202));

        fixture.LiveRecipientIds.Clear();
        fixture.LivePayloads.Clear();
        await fixture.RestartMailboxAsync();
        Assert.Equal(0, await fixture.Worker.OnStartedAsync(started));
        Assert.Empty(fixture.LiveRecipientIds);
        Assert.Empty(fixture.LivePayloads);
        Assert.Single(Records(Assert.IsType<JsonObject>(await fixture.ReadPersistedBoxAsync(fixture.ActorCharacterId)), 202));
        Assert.Single(Records(Assert.IsType<JsonObject>(await fixture.ReadPersistedBoxAsync(fixture.OfflineCharacterId)), 202));
    }

    [Fact]
    public async Task Event_delivery_commits_the_inbox_with_its_cursor_and_publishes_only_after_commit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var schema = "moon_event_" + Guid.NewGuid().ToString("N")[..12];
        await Eve.Events.EventBus.MigrateAsync(fixture.Source, schema);
        await Eve.Scheduler.DurableScheduler.MigrateAsync(fixture.Source, schema);
        var scheduler = new Eve.Scheduler.DurableScheduler(fixture.Source, () => NowMs, null, schema);
        var bus = new Eve.Events.EventBus(fixture.Source, scheduler, null, schema);
        var attempts = 0;
        bus.Subscribe("test.rollback", [Eve.Structures.MoonExtractions.StartedTopic], async (context, token) =>
        {
            await fixture.Worker.OnStartedAsync(context.Event.Payload<Eve.Structures.MoonExtractionStarted>()!, token, context);
            Assert.Empty(fixture.LivePayloads);
            Assert.Null(await fixture.ReadPersistedBoxAsync(fixture.ActorCharacterId));
            if (++attempts == 1)
                throw new InvalidOperationException("force rollback");
        });
        await bus.PumpAsync();
        await bus.PublishAsync(Eve.Structures.MoonExtractions.StartedTopic, fixture.StartedEvent());
        await bus.PumpAsync();
        Assert.Equal(1, attempts);
        Assert.Null(await fixture.ReadPersistedBoxAsync(fixture.ActorCharacterId));
        Assert.Empty(fixture.LivePayloads);
        Assert.Equal(1, fixture.MutationGate.CurrentCount);

        await using (var connection = await fixture.Source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"UPDATE {schema}.subscribers SET retry_at_ms = 0", connection))
            await command.ExecuteNonQueryAsync();
        await bus.PumpAsync();
        Assert.Equal(2, attempts);
        Assert.Single(Records((await fixture.ReadPersistedBoxAsync(fixture.ActorCharacterId))!, 202));
        Assert.Single(Records((await fixture.ReadPersistedBoxAsync(fixture.OfflineCharacterId))!, 202));
        Assert.Single(fixture.LivePayloads);
        Assert.Equal(1, fixture.MutationGate.CurrentCount);
        await bus.PumpAsync();
        Assert.Single(fixture.LivePayloads);

        fixture.Worker.Subscribe(bus);
        await bus.PumpAsync();
        await bus.PublishAsync(Eve.Structures.MoonExtractions.ChunkArrivedTopic, fixture.ChunkArrivedEvent());
        await bus.PumpAsync();
        Assert.Single(Records((await fixture.ReadPersistedBoxAsync(fixture.ActorCharacterId))!, 204));
        Assert.Single(Records((await fixture.ReadPersistedBoxAsync(fixture.OfflineCharacterId))!, 204));
        Assert.Equal(2, fixture.LivePayloads.Count);
        Assert.Equal(1, fixture.MutationGate.CurrentCount);
    }

    private static int[] TypeIds(JsonObject box) =>
        (box["byID"] as JsonObject ?? new JsonObject()).Select(pair => pair.Value)
            .OfType<JsonObject>()
            .Select(record => record["typeID"]!.GetValue<int>())
            .Order()
            .ToArray();

    private static JsonObject Record(JsonObject box, int typeId) =>
        Records(box, typeId).First();

    private static JsonObject[] Records(JsonObject box, int typeId) =>
        (box["byID"] as JsonObject ?? new JsonObject()).Select(pair => pair.Value)
            .OfType<JsonObject>()
            .Where(record => record["typeID"]!.GetValue<int>() == typeId)
            .ToArray();

    private static JsonObject OreVolumes(JsonObject data) => Assert.IsType<JsonObject>(data["oreVolumeByType"]);

    private static void AssertCommon(JsonObject record, long recipientId)
    {
        Assert.Equal(CorporationId, record["senderID"]!.GetValue<long>());
        Assert.Equal(recipientId, record["receiverID"]!.GetValue<long>());
        Assert.Equal(7, record["groupID"]!.GetValue<int>());
        Assert.False(record["processed"]!.GetValue<bool>());
        Assert.Equal(FileTime(NowMs), record["created"]!.GetValue<long>());
    }

    private static long FileTime(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToFileTimeUtc();

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _mailSchema;
        private readonly SemaphoreSlim _mutationGate = new(1, 1);
        private readonly NpgsqlDataSource _source;

        private Fixture(
            string mailSchema,
            NpgsqlDataSource source,
            PersistenceStore mailStore,
            OwnerSession mailOwner,
            GameStoreCache mailCache,
            StructureStore structures,
            MoonExtractionStore extractions,
            PgCharacterStore characters,
            long actorCharacterId,
            long offlineCharacterId,
            long foreignCharacterId)
        {
            _mailSchema = mailSchema;
            _source = source;
            MailStore = mailStore;
            MailOwner = mailOwner;
            MailCache = mailCache;
            Structures = structures;
            Extractions = extractions;
            Characters = characters;
            ActorCharacterId = actorCharacterId;
            OfflineCharacterId = offlineCharacterId;
            ForeignCharacterId = foreignCharacterId;
            OnlineCharacterIds.Add(actorCharacterId);
            CreateWorker();
        }

        public NpgsqlDataSource Source => _source;
        public SemaphoreSlim MutationGate => _mutationGate;
        public PersistenceStore MailStore { get; }
        public OwnerSession MailOwner { get; private set; }
        public GameStoreCache MailCache { get; private set; }
        public StructureStore Structures { get; }
        public MoonExtractionStore Extractions { get; }
        public PgCharacterStore Characters { get; }
        public long ActorCharacterId { get; }
        public long OfflineCharacterId { get; }
        public long ForeignCharacterId { get; }
        public HashSet<long> OnlineCharacterIds { get; } = [];
        public List<long> LiveRecipientIds { get; } = [];
        public List<(long RecipientId, PyValue[] Arguments)> LivePayloads { get; } = [];
        public MoonOwnerNotifications Worker { get; private set; } = null!;

        public string StructureName => "Athanor Alpha";
        public string StructureLink => $"<a href=\"showinfo:{StructureTypeId}//{StructureId}\">{StructureName}</a>";
        public string ActorLink => $"<a href=\"showinfo:1373//{ActorCharacterId}\">Moon Pilot</a>";
        public long ChunkAvailableMs => StartMs + DurationSeconds * 1000;
        public long NaturalDecayMs => ChunkAvailableMs + 7_200_000;

        public static async Task<Fixture> CreateAsync()
        {
            var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL")
                ?? throw new InvalidOperationException("EVE_PG_URL is not set");
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(pgUrl));
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var structureSchema = $"moon_note_{suffix}";
            var characterSchema = $"moon_char_{suffix}";
            var mailSchema = $"moon_mail_{suffix}";
            await StructureStore.MigrateAsync(source, structureSchema);
            await PgCharacterStore.MigrateAsync(source, characterSchema);

            var structures = new StructureStore(source, structureSchema);
            var extractions = new MoonExtractionStore(source, structureSchema);
            var characters = new PgCharacterStore(source, characterSchema);
            long actorCharacterId;
            long offlineCharacterId;
            long foreignCharacterId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                actorCharacterId = await characters.InsertAsync(connection, transaction,
                    NewCharacter("Moon Pilot", CorporationId));
                offlineCharacterId = await characters.InsertAsync(connection, transaction,
                    NewCharacter("Offline Member", CorporationId));
                foreignCharacterId = await characters.InsertAsync(connection, transaction,
                    NewCharacter("Foreign Member", ForeignCorporationId));
                await structures.CreateAsync(connection, transaction, new NewStructure(
                    StructureId, StructureTypeId, SolarSystemId, 0, 0, 0, 0,
                    CorporationId, 0, "Athanor Alpha", ""));
                await extractions.InsertExtractionAsync(connection, transaction, Extraction(ExtractionId, "active", 0, ""));
                await extractions.InsertExtractionAsync(connection, transaction,
                    Extraction(ManualCancelAliasExtractionId, "completed", StartMs + 10_000, "cancelled"));
                await transaction.CommitAsync();
            }

            var mailStore = new PersistenceStore(source, mailSchema);
            await mailStore.EnsureSchemaAsync();
            var mailOwner = await OwnerSession.StartAsync(
                mailStore, "world", $"moon-owner-test-{suffix}", new AcquireOptions(LeaseMs: 600_000), renewEvery: TimeSpan.FromHours(1));
            var mailCache = new GameStoreCache(mailStore, mailOwner, new GameStoreCacheOptions { ProcessRole = PersistenceRoles.World });
            await mailStore.LoadTableAsync("notifications", createIfMissing: true);
            await mailCache.LoadAsync("notifications");
            return new Fixture(mailSchema, source, mailStore, mailOwner, mailCache, structures, extractions,
                characters, actorCharacterId, offlineCharacterId, foreignCharacterId);
        }

        public MoonExtractionStarted StartedEvent() =>
            new(ExtractionId, StructureId, SolarSystemId, CorporationId, ActorCharacterId);

        public MoonChunkArrived ChunkArrivedEvent() =>
            new(ExtractionId, StructureId, SolarSystemId, CorporationId);

        public MoonFractureStarted FractureStartedEvent() =>
            new(ExtractionId, StructureId, SolarSystemId, CorporationId, ActorCharacterId);

        public MoonExtractionEnded EndedEvent(string reason, long actorCharacterId, long extractionId = ExtractionId) =>
            new(extractionId, StructureId, SolarSystemId, CorporationId, reason, StartMs + 20_000, actorCharacterId);

        public async Task<JsonObject?> ReadPersistedBoxAsync(long recipientCharacterId)
        {
            await using var connection = await _source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var recipientKey = recipientCharacterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var rowKey = RowCodec.Explode("notifications", new JsonObject
            {
                ["boxes"] = new JsonObject { [recipientKey] = new JsonObject() },
            }).Keys.Single(key => key != "boxes");
            var json = await MailStore.ReadRowAsync(connection, transaction, "notifications", rowKey);
            await transaction.CommitAsync();
            return json is null ? null : JsonNode.Parse(json) as JsonObject;
        }

        public async Task RestartMailboxAsync()
        {
            await MailCache.DisposeAsync();
            await MailOwner.DisposeAsync();
            MailOwner = await OwnerSession.StartAsync(
                MailStore, "world", $"moon-owner-test-restart-{Guid.NewGuid():N}",
                new AcquireOptions(LeaseMs: 600_000), renewEvery: TimeSpan.FromHours(1));
            MailCache = new GameStoreCache(MailStore, MailOwner, new GameStoreCacheOptions { ProcessRole = PersistenceRoles.World });
            await MailCache.LoadAsync("notifications");
            CreateWorker();
        }

        private void CreateWorker()
        {
            var mailbox = new PersistenceTransactionMailbox(
                _source, MailStore, MailCache, MailOwner, _mutationGate, () => NowMs,
                (recipient, arguments) =>
                {
                    if (!MailCache.Read("notifications", $"/boxes/{recipient}").Success)
                        throw new InvalidOperationException("OnNotificationReceived ran before the mailbox cache update.");
                    if (OnlineCharacterIds.Contains(recipient))
                    {
                        LiveRecipientIds.Add(recipient);
                        LivePayloads.Add((recipient, arguments));
                    }
                });
            Worker = new MoonOwnerNotifications(_source, Structures, Extractions, Characters, mailbox);
        }

        private static NewCharacter NewCharacter(string name, long corporationId) =>
            new(1, name, 0, 1, 1, 1, 1, 1373, corporationId, null,
                0, SolarSystemId, 20_000_020, 10_000_002, null, null);

        private static MoonExtraction Extraction(long extractionId, string state, long completedMs, string completedReason) =>
            new(extractionId, StructureId, MoonId, SolarSystemId, CorporationId, StartMs,
                StartMs + DurationSeconds * 1000, StartMs + DurationSeconds * 1000 + 7_200_000,
                DurationSeconds, 1.0, 0, state, 0, 0, 0,
                new Dictionary<long, double> { [45_492] = 123.5, [45_493] = 27.25 },
                StartMs, completedMs, completedReason, 0);

        public async ValueTask DisposeAsync()
        {
            await MailCache.DisposeAsync();
            await MailOwner.DisposeAsync();
            await MailStore.DisposeAsync();
            await _source.DisposeAsync();
            _mutationGate.Dispose();
        }
    }
}
