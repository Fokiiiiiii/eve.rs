using Eve.Characters;

namespace Eve.Characters.Tests;

public sealed class CharacterDeletionStoreTests
{
    private static NewCharacter Pilot(string name) =>
        new(1, name, Gender: 1, RaceId: 1, BloodlineId: 1, AncestryId: 10, SchoolId: 35, TypeId: 1373,
            CorporationId: 1000006, FactionId: 500001, StationId: 60000610, SolarSystemId: 30002775, ConstellationId: 20000406, RegionId: 10000033,
            DollInfo: null, PortraitInfo: null);

    [PgFact]
    public async Task A_character_is_queued_once_deleted_only_after_its_time_and_then_hidden_from_its_account()
    {
        await using var s = await PgSchemas.CreateAsync();
        var characterId = await Insert(s, Pilot("Delete Queue"));
        var store = new PgCharacterStore(s.Source, s.CharactersSchema);
        var now = DateTimeOffset.UtcNow;

        Assert.True(await store.SetDeletePrepareAsync(characterId, 1, now.AddMinutes(10)));
        Assert.False(await store.SetDeletePrepareAsync(characterId, 1, now.AddMinutes(20)));
        Assert.False(await store.SetDeletePrepareAsync(characterId, 2, now.AddMinutes(20)));
        Assert.Equal(now.AddMinutes(10).ToUnixTimeSeconds(), (await store.GetAsync(characterId))!.DeletePrepareAt!.Value.ToUnixTimeSeconds());

        Assert.False(await store.MarkDeletedAsync(characterId, 1, now), "the waiting time has not passed");
        Assert.True(await store.MarkDeletedAsync(characterId, 1, now.AddMinutes(11)));
        Assert.False(await store.MarkDeletedAsync(characterId, 1, now.AddMinutes(11)), "already deleted");

        var deleted = (await store.GetAsync(characterId))!;
        Assert.True(deleted.IsDeleted);
        Assert.False(deleted.IsQueuedForDeletion);
        Assert.DoesNotContain(await store.ForAccountAsync(1), c => c.Id == characterId);
        Assert.Equal(0, await store.CountForAccountAsync(1));
    }

    [PgFact]
    public async Task A_cancelled_queue_lets_the_character_be_queued_again()
    {
        await using var s = await PgSchemas.CreateAsync();
        var characterId = await Insert(s, Pilot("Delete Cancel"));
        var store = new PgCharacterStore(s.Source, s.CharactersSchema);

        Assert.True(await store.SetDeletePrepareAsync(characterId, 1, DateTimeOffset.UtcNow.AddMinutes(10)));
        Assert.True(await store.ClearDeletePrepareAsync(characterId, 1));
        Assert.True(await store.SetDeletePrepareAsync(characterId, 1, DateTimeOffset.UtcNow.AddMinutes(10)));
        Assert.True((await store.GetAsync(characterId))!.IsQueuedForDeletion);
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
