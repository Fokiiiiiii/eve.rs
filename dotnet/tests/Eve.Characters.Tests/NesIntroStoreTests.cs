using Eve.Characters;
using Npgsql;

namespace Eve.Characters.Tests;

public sealed class NesIntroStoreTests
{
    private static NewCharacter Pilot(string name) =>
        new(1, name, Gender: 1, RaceId: 1, BloodlineId: 1, AncestryId: 10, SchoolId: 35, TypeId: 1373,
            CorporationId: 1000006, FactionId: 500001, StationId: 60000610, SolarSystemId: 30002775, ConstellationId: 20000406, RegionId: 10000033,
            DollInfo: null, PortraitInfo: null);

    [PgFact]
    public async Task A_character_is_unset_and_skipping_an_introduction_that_is_not_active_changes_nothing()
    {
        await using var s = await PgSchemas.CreateAsync();
        var characterId = await Insert(s, Pilot("Nes Unset"));
        var store = new NesIntroStore(s.Source, s.CharactersSchema, () => 1_000L);

        Assert.Equal(NesIntroStore.Unset, await store.ReadAsync(characterId));
        Assert.False(await store.SkipAsync(characterId));
        Assert.Equal(NesIntroStore.Unset, await store.ReadAsync(characterId));
    }

    [PgFact]
    public async Task An_active_introduction_is_skipped_once_and_the_state_is_kept()
    {
        await using var s = await PgSchemas.CreateAsync();
        var characterId = await Insert(s, Pilot("Nes Active"));
        await using (var set = s.Source.CreateCommand(
            $"INSERT INTO {s.CharactersSchema}.nes_intro_states(character_id, state, updated_ms) VALUES($1, 1, 0)"))
        {
            set.Parameters.Add(new NpgsqlParameter { Value = characterId });
            await set.ExecuteNonQueryAsync();
        }
        var store = new NesIntroStore(s.Source, s.CharactersSchema, () => 5_000L);

        Assert.True(await store.SkipAsync(characterId));
        Assert.Equal(NesIntroStore.Skipped, await new NesIntroStore(s.Source, s.CharactersSchema, () => 6_000L).ReadAsync(characterId));
        Assert.False(await store.SkipAsync(characterId));
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
