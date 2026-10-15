using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Characters;
using Eve.Data;
using Eve.Events;
using Eve.GameStore;
using Eve.Host.Persistence.Mail;
using Eve.Host.Persistence.Sovereignty;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services.Space.Entosis;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// ST-9 e: the owner's notices of an entosis capture (PostgreSQL, the real event bus, the real mailbox and characters). The capture's events reach the defending
/// alliance's members as notices of V24.01 types 147 (capture started) and 160 (structures reinforced), and a redelivery of the events writes nothing twice.
/// </summary>
public sealed class EntosisOwnerNotificationTests
{
    private const long DefenderAlliance = 99_500_001;
    private const long DefenderCorporation = 98_500_001;
    private const long ForeignCorporation = 98_500_002;
    private const long Hub = 1_040_000_000_950;
    private const int SolarSystemId = 30_100_032;
    private const long StartedMs = 1_700_000_000_000;
    private const long ExitMs = 1_700_200_000_000;

    [PgFact]
    public async Task The_defending_members_are_told_of_a_started_capture_and_a_reinforcement_once_each_across_a_redelivery()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var characterSchema = $"entn_char_{suffix}";
        var mailSchema = $"entn_mail_{suffix}";
        var eventsSchema = $"entn_evt_{suffix}";
        var schedulerSchema = $"entn_sch_{suffix}";
        await PgCharacterStore.MigrateAsync(source, characterSchema);
        var characters = new PgCharacterStore(source, characterSchema);
        long member;
        long otherMember;
        long foreign;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            member = await characters.InsertAsync(connection, transaction, NewCharacter("Defender Member", DefenderCorporation));
            otherMember = await characters.InsertAsync(connection, transaction, NewCharacter("Second Member", DefenderCorporation));
            foreign = await characters.InsertAsync(connection, transaction, NewCharacter("Foreign Pilot", ForeignCorporation));
            await transaction.CommitAsync();
        }

        var mailStore = new PersistenceStore(source, mailSchema);
        await mailStore.EnsureSchemaAsync();
        var mailOwner = await OwnerSession.StartAsync(
            mailStore, "world", $"entosis-owner-test-{suffix}", new AcquireOptions(LeaseMs: 600_000), renewEvery: TimeSpan.FromHours(1));
        var mailCache = new GameStoreCache(mailStore, mailOwner, new GameStoreCacheOptions { ProcessRole = PersistenceRoles.World });
        await mailStore.LoadTableAsync("notifications", createIfMissing: true);
        await mailCache.LoadAsync("notifications");
        var mailbox = new PersistenceTransactionMailbox(
            source, mailStore, mailCache, mailOwner, new SemaphoreSlim(1, 1), () => StartedMs, (_, _) => { });

        await DurableScheduler.MigrateAsync(source, schedulerSchema);
        await EventBus.MigrateAsync(source, eventsSchema);
        var scheduler = new DurableScheduler(source, () => StartedMs, null, schedulerSchema);
        var bus = new EventBus(source, scheduler, null, eventsSchema, _ => { });
        new EntosisOwnerNotifications(
            (allianceId, _) => Task.FromResult<IReadOnlyList<long>>(allianceId == DefenderAlliance ? [DefenderCorporation] : []),
            characters, mailbox).Subscribe(bus);
        await bus.PumpAsync();

        var key = Hub.ToString(CultureInfo.InvariantCulture);
        await bus.PublishAsync(EntosisCaptureService.CaptureStartedTopic,
            new EntosisCaptureStarted(Hub, SolarSystemId, DefenderAlliance, 1, StartedMs), key);
        await bus.PublishAsync(EntosisCaptureService.ReinforcedTopic,
            new EntosisHubReinforced(Hub, SolarSystemId, DefenderAlliance, ExitMs, StartedMs), key);
        await bus.PumpAsync();

        foreach (var recipient in new[] { member, otherMember })
        {
            var records = await RecordsAsync(source, mailStore, recipient);
            Assert.Equal(2, records.Length);
            var started = Assert.Single(records, record => record["typeID"]!.GetValue<int>() == 147);
            Assert.Equal(DefenderAlliance, started["senderID"]!.GetValue<long>());
            Assert.Equal(6, started["groupID"]!.GetValue<int>());
            Assert.Equal(32_458, started["data"]!["structureTypeID"]!.GetValue<int>());
            Assert.Equal(SolarSystemId, started["data"]!["solarSystemID"]!.GetValue<int>());
            var reinforced = Assert.Single(records, record => record["typeID"]!.GetValue<int>() == 160);
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(ExitMs).UtcDateTime.ToFileTimeUtc(), reinforced["data"]!["decloakTime"]!.GetValue<long>());
            Assert.Equal(2, reinforced["data"]!["campaignEventType"]!.GetValue<int>());
        }
        Assert.Empty(await RecordsAsync(source, mailStore, foreign));

        // a redelivery of both events (the subscriber's cursor rewound) writes no second notice
        await using (var rewind = source.CreateCommand(
            $"UPDATE {eventsSchema}.subscribers SET cursor_id = 0 WHERE name = 'sov.entosis-owner-notifications'"))
        {
            Assert.Equal(1, await rewind.ExecuteNonQueryAsync());
        }
        await bus.PumpAsync();
        Assert.Equal(2, (await RecordsAsync(source, mailStore, member)).Length);
        Assert.Equal(2, (await RecordsAsync(source, mailStore, otherMember)).Length);

        await mailCache.DisposeAsync();
        await mailOwner.DisposeAsync();
        await using (var drop = source.CreateCommand(
            $"DROP SCHEMA IF EXISTS \"{characterSchema}\" CASCADE; DROP SCHEMA IF EXISTS \"{mailSchema}\" CASCADE; "
            + $"DROP SCHEMA IF EXISTS \"{eventsSchema}\" CASCADE; DROP SCHEMA IF EXISTS \"{schedulerSchema}\" CASCADE"))
        {
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<JsonObject[]> RecordsAsync(NpgsqlDataSource source, PersistenceStore store, long recipient)
    {
        var recipientKey = recipient.ToString(CultureInfo.InvariantCulture);
        var rowKey = RowCodec.Explode("notifications", new JsonObject
        {
            ["boxes"] = new JsonObject { [recipientKey] = new JsonObject() },
        }).Keys.Single(key => key != "boxes");
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var json = await store.ReadRowAsync(connection, transaction, "notifications", rowKey);
        await transaction.CommitAsync();
        var box = json is null ? null : JsonNode.Parse(json) as JsonObject;
        return box?["byID"] is JsonObject records ? records.Select(pair => (JsonObject)pair.Value!).ToArray() : [];
    }

    private static NewCharacter NewCharacter(string name, long corporationId) =>
        new(1, name, 0, 1, 1, 1, 1, 1373, corporationId, null, 0, SolarSystemId, 20_000_020, 10_000_002, null, null);
}
