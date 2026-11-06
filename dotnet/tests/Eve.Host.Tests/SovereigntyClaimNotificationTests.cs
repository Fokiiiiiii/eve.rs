using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Characters;
using Eve.Data;
using Eve.GameStore;
using Eve.Host.Persistence.Mail;
using Eve.Host.Persistence.Sovereignty;
using Eve.Marshal;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// ST-9 e: the claim notices of a neutral Sovereignty Hub's new owner (PostgreSQL, the real characters and mailbox). The capturing alliance's members get
/// V24.01 type 43 and the claiming corporation's members get 44, both with corpID and allianceID; a foreign pilot gets none, and a retried handover (the same
/// claim time) writes nothing twice, while a later claim of the same hub is a new notice.
/// </summary>
public sealed class SovereigntyClaimNotificationTests
{
    private const long ClaimingAlliance = 99_600_001;
    private const long ExecutorCorporation = 98_600_001;
    private const long AllianceCorporation = 98_600_002;
    private const long ForeignCorporation = 98_600_003;
    private const long Hub = 1_040_000_000_960;
    private const int SolarSystemId = 30_100_033;
    private const long ClaimTime = 133_700_000_000_000_000;
    private const long NowMs = 1_700_000_000_000;

    [PgFact]
    public async Task The_claiming_members_are_told_once_each_and_a_retry_writes_nothing_twice()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var characterSchema = $"sovclaim_char_{suffix}";
        var mailSchema = $"sovclaim_mail_{suffix}";
        await PgCharacterStore.MigrateAsync(source, characterSchema);
        var characters = new PgCharacterStore(source, characterSchema);
        long executorMember;
        long allianceMember;
        long foreign;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            executorMember = await characters.InsertAsync(connection, transaction, NewCharacter("Executor Member", ExecutorCorporation));
            allianceMember = await characters.InsertAsync(connection, transaction, NewCharacter("Alliance Member", AllianceCorporation));
            foreign = await characters.InsertAsync(connection, transaction, NewCharacter("Foreign Pilot", ForeignCorporation));
            await transaction.CommitAsync();
        }

        var mailStore = new PersistenceStore(source, mailSchema);
        await mailStore.EnsureSchemaAsync();
        var mailOwner = await OwnerSession.StartAsync(
            mailStore, "world", $"sovclaim-test-{suffix}", new AcquireOptions(LeaseMs: 600_000), renewEvery: TimeSpan.FromHours(1));
        var mailCache = new GameStoreCache(mailStore, mailOwner, new GameStoreCacheOptions { ProcessRole = PersistenceRoles.World });
        await mailStore.LoadTableAsync("notifications", createIfMissing: true);
        await mailCache.LoadAsync("notifications");
        var mailbox = new PersistenceTransactionMailbox(
            source, mailStore, mailCache, mailOwner, new SemaphoreSlim(1, 1), () => NowMs, (_, _) => { });
        var notifications = new SovereigntyClaimNotifications(
            (allianceId, _) => Task.FromResult<IReadOnlyList<long>>(
                allianceId == ClaimingAlliance ? [ExecutorCorporation, AllianceCorporation] : []),
            characters, mailbox);

        await notifications.WriteAsync(Hub, ClaimTime, ExecutorCorporation, ClaimingAlliance, CancellationToken.None);

        // the executor corporation is in the alliance: its member gets 43 and 44; the other alliance member gets 43 only
        var executorRecords = await RecordsAsync(source, mailStore, executorMember);
        Assert.Equal(2, executorRecords.Length);
        var allianceRecord = Assert.Single(executorRecords, record => record["typeID"]!.GetValue<int>() == 43);
        Assert.Equal(ClaimingAlliance, allianceRecord["senderID"]!.GetValue<long>());
        Assert.Equal(6, allianceRecord["groupID"]!.GetValue<int>());
        Assert.Equal(ExecutorCorporation, allianceRecord["data"]!["corpID"]!.GetValue<long>());
        Assert.Equal(ClaimingAlliance, allianceRecord["data"]!["allianceID"]!.GetValue<long>());
        var corporationRecord = Assert.Single(executorRecords, record => record["typeID"]!.GetValue<int>() == 44);
        Assert.Equal(ExecutorCorporation, corporationRecord["senderID"]!.GetValue<long>());
        Assert.Equal(ExecutorCorporation, corporationRecord["data"]!["corpID"]!.GetValue<long>());

        var allianceRecords = await RecordsAsync(source, mailStore, allianceMember);
        var onlyRecord = Assert.Single(allianceRecords);
        Assert.Equal(43, onlyRecord["typeID"]!.GetValue<int>());
        Assert.Empty(await RecordsAsync(source, mailStore, foreign));

        // a retried handover for the same claim writes nothing twice
        await notifications.WriteAsync(Hub, ClaimTime, ExecutorCorporation, ClaimingAlliance, CancellationToken.None);
        Assert.Equal(2, (await RecordsAsync(source, mailStore, executorMember)).Length);
        Assert.Single(await RecordsAsync(source, mailStore, allianceMember));

        // the same hub claimed again at a later claim time is a new notice
        await notifications.WriteAsync(Hub, ClaimTime + TimeSpan.TicksPerDay, ExecutorCorporation, ClaimingAlliance, CancellationToken.None);
        Assert.Equal(4, (await RecordsAsync(source, mailStore, executorMember)).Length);

        await mailCache.DisposeAsync();
        await mailOwner.DisposeAsync();
        await using (var drop = source.CreateCommand(
            $"DROP SCHEMA IF EXISTS \"{characterSchema}\" CASCADE; DROP SCHEMA IF EXISTS \"{mailSchema}\" CASCADE"))
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
