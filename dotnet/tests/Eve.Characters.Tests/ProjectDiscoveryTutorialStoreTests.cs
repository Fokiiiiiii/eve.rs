using Eve.Characters;
using Npgsql;

namespace Eve.Characters.Tests;

public sealed class ProjectDiscoveryTutorialStoreTests
{
    private static NewCharacter Pilot(string name) =>
        new(1, name, Gender: 1, RaceId: 1, BloodlineId: 1, AncestryId: 10, SchoolId: 35, TypeId: 1373,
            CorporationId: 1000006, FactionId: 500001, StationId: 60000610, SolarSystemId: 30002775, ConstellationId: 20000406, RegionId: 10000033,
            DollInfo: null, PortraitInfo: null);

    [PgFact]
    public async Task A_character_without_a_tutorial_has_none_and_the_written_tutorial_survives_a_restart()
    {
        await using var s = await PgSchemas.CreateAsync();
        var characterId = await Insert(s, Pilot("Tutorial Store"));
        var store = new ProjectDiscoveryTutorialStore(s.Source, s.CharactersSchema);
        Assert.Null(await store.ReadAsync(characterId));

        await store.WriteAsync(characterId, new ProjectDiscoveryTutorial(false, 3), 1_000);
        await store.WriteAsync(characterId, new ProjectDiscoveryTutorial(true, 8), 2_000);

        var restarted = await new ProjectDiscoveryTutorialStore(s.Source, s.CharactersSchema).ReadAsync(characterId);
        Assert.Equal(new ProjectDiscoveryTutorial(true, 8), restarted);
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
