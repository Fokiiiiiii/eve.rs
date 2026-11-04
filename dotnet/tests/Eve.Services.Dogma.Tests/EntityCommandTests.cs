using Eve.Data;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Slash;
using Npgsql;

namespace Eve.Services.Dogma.Tests;

/// <summary>
/// <c>/entity deploy</c>: the entity category only (the Blood Raider frigate, typeID 36939, is SDE category 11), the amount range, the names, and the refusal of
/// space; the spawner is a fake, and the words are the commands'.
/// </summary>
public class EntityCommandTests
{
    private const int BloodRaiderFrigate = 36939;
    private const int Rifter = 587;

    private sealed class FakeSpawns : ISpaceSpawns
    {
        private long _next = 9100;

        public SpawnResult? Refusal { get; set; }

        public List<(int TypeId, int Count, double Deviation)> Deploys { get; } = [];

        public Task<SpawnResult> SpawnHullsAsync(ClientSession session, int typeId, int count, double deviationMeters, CancellationToken cancellationToken)
        {
            Deploys.Add((typeId, count, deviationMeters));
            if (Refusal is { } refusal) return Task.FromResult(refusal);
            return Task.FromResult(new SpawnResult(Enumerable.Range(0, count).Select(_ => _next++).ToArray()));
        }

        public Task<SpawnedView?> SpawnedAsync(ClientSession session, CancellationToken cancellationToken) => Task.FromResult<SpawnedView?>(null);

        public Task<int> RemoveAsync(IReadOnlyList<long> shipIds, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    [PgFact]
    public async Task Deploy_makes_entities_of_the_entity_category_and_refuses_the_others()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Rig.Url!));
        var fake = new FakeSpawns();
        var commands = new EntityCommands(new ItemTypeNames(new SdeReader(source)), fake);

        Assert.Equal("Select a character before using /entity.", await commands.RunAsync(new ClientSession(), "deploy 2 36939", default));
        Assert.Equal(EntityCommands.DeployUsage, await commands.RunAsync(Rig.Session(1001), "deploy", default));
        Assert.Equal("Spawn amount must be between 1 and 50.", await commands.RunAsync(Rig.Session(1001), "deploy 0 36939", default));
        Assert.Equal("Rifter is not an entity type. /entity deploy makes entities only.", await commands.RunAsync(Rig.Session(1001), $"deploy 1 {Rifter}", default));
        Assert.Empty(fake.Deploys);

        Assert.Equal("Deployed 2 Blood Raider Frigate in space around you: 9100, 9101.", await commands.RunAsync(Rig.Session(1001), $"deploy 2 {BloodRaiderFrigate}", default));
        Assert.Equal((BloodRaiderFrigate, 2, 0d), Assert.Single(fake.Deploys));

        fake.Refusal = new SpawnResult([], SpawnResult.NotInSpace);
        Assert.Equal("You must be in space before using /entity.", await commands.RunAsync(Rig.Session(1001), $"deploy 1 {BloodRaiderFrigate}", default));
    }
}
