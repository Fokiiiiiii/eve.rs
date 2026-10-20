using Eve.Characters;
using Npgsql;

namespace Eve.Characters.Tests;

public sealed class AchievementTrackerStoreTests
{
    private static NewCharacter Pilot(string name) =>
        new(1, name, Gender: 1, RaceId: 1, BloodlineId: 1, AncestryId: 10, SchoolId: 35, TypeId: 1373,
            CorporationId: 1000006, FactionId: 500001, StationId: 60000610, SolarSystemId: 30002775, ConstellationId: 20000406, RegionId: 10000033,
            DollInfo: null, PortraitInfo: null);

    [PgFact]
    public async Task A_character_without_a_record_has_none_and_what_is_written_is_replaced_and_survives_a_restart()
    {
        await using var s = await PgSchemas.CreateAsync();
        var characterId = await Insert(s, Pilot("Tracker"));
        var store = new AchievementTrackerStore(s.Source, s.CharactersSchema);
        Assert.Null(await store.ReadAsync(characterId));
        Assert.False(await store.HasEverWarpedAsync(characterId));
        await Task.WhenAll(store.RecordWarpAsync(characterId, 500), store.RecordWarpAsync(characterId, 501));
        Assert.True(await store.HasEverWarpedAsync(characterId));

        await store.WriteAsync(characterId, [(9, 134273341290000000L), (10, 134273362100000000L)], [("client_openMap", 3L)], 1_000);
        await store.WriteAsync(characterId, [(16, 134273340850000000L)], [("client_spinCamera", 1L), ("client_openMap", 4L)], 2_000);

        var restartedStore = new AchievementTrackerStore(s.Source, s.CharactersSchema);
        var restarted = await restartedStore.ReadAsync(characterId);
        Assert.True(await restartedStore.HasEverWarpedAsync(characterId));
        Assert.NotNull(restarted);
        Assert.Equal([(16, 134273340850000000L)], restarted!.Completed);
        Assert.Equal([("client_openMap", 4L), ("client_spinCamera", 1L)], restarted.EventCounts);
    }

    [PgFact]
    public async Task An_empty_write_is_a_record_with_no_entries()
    {
        await using var s = await PgSchemas.CreateAsync();
        var characterId = await Insert(s, Pilot("Empty Tracker"));
        var store = new AchievementTrackerStore(s.Source, s.CharactersSchema);

        await store.WriteAsync(characterId, [], [], 5);

        var record = await store.ReadAsync(characterId);
        Assert.NotNull(record);
        Assert.Empty(record!.Completed);
        Assert.Empty(record.EventCounts);
    }

    private static async Task<long> Insert(PgSchemas s, NewCharacter c)
    {
        await using var connection = await s.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await s.Characters.InsertAsync(connection, transaction, c);
        await transaction.CommitAsync();
        return id;
    }
}
