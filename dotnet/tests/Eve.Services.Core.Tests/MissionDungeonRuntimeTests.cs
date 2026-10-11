using System.Text.Json;
using Eve.Agents;
using Eve.Data;
using Eve.Sde;
using Npgsql;

namespace Eve.Services.Core.Tests;

/// <summary>
/// The kill mission runtime on the PostgreSQL store: an accepted kill mission starts its dungeon for the pilot in space in the agent's system,
/// the dungeon's outcome decides the mission, and the reward is credited once (a replayed outcome pays nothing).
/// </summary>
public class MissionDungeonRuntimeTests
{
    private const long Pilot = 140000031;
    private const long Agent = 3008500;
    private const long Corporation = 1000002;
    private const long System = 30000001;
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private const string KillMission = """
        {"_key": 9101, "name": {"en": "Kill Step"}, "nameID": 1234, "hasStandingRewards": false,
         "killMission": {"dungeonID": 3509, "objectiveQuantity": 0},
         "missionRewards": {"reward": {"rewardTypeID": 29, "rewardQuantity": 100000}}}
        """;

    private const string TalkMission = """
        {"_key": 9102, "name": {"en": "Talk Step"}, "nameID": 1235, "hasStandingRewards": false}
        """;

    private sealed class FakeHost : IMissionDungeonHost
    {
        public bool InSystem { get; set; } = true;
        public string? StartRefusal { get; set; }
        public List<(long Character, int System, long Dungeon)> Starts { get; } = [];
        public List<(long Character, long Amount, long Counterparty, int RefType, string Key)> Credits { get; } = [];

        public bool IsPilotInSystem(long characterId, int solarSystemId) => InSystem;

        public Task<string?> StartDungeonAsync(long characterId, int solarSystemId, long dungeonId, CancellationToken cancellationToken)
        {
            Starts.Add((characterId, solarSystemId, dungeonId));
            return Task.FromResult(StartRefusal);
        }

        public Task CreditIskAsync(long characterId, long amount, long counterpartyId, int refTypeId, string reason, string operationKey, CancellationToken cancellationToken)
        {
            Credits.Add((characterId, amount, counterpartyId, refTypeId, operationKey));
            return Task.CompletedTask;
        }

        public List<(long Character, long Faction, double Delta)> Standings { get; } = [];

        /// <summary>The store the completion is made in (the production host makes it in the same transaction as the standings).</summary>
        public AgentMissionStore? Store { get; set; }

        public async Task<bool> CompleteMissionAsync(long characterId, long agentId, DateTimeOffset now, string reference,
            IReadOnlyList<(long FactionId, double Delta)> standings, CancellationToken cancellationToken)
        {
            if (Store is null) return false;
            try
            {
                if (await Store.CompleteAsync(characterId, agentId, now, reference, cancellationToken) is null) return false;
            }
            catch (AgentMissionRefusedException)
            {
                return false;
            }
            foreach (var (factionId, delta) in standings) Standings.Add((characterId, factionId, delta));
            return true;
        }
    }

    private static MissionStaticCatalog Catalog() => MissionStaticCatalog.FromRows(
        3405148,
        [new SdeRow(9101, JsonDocument.Parse(KillMission).RootElement.Clone()), new SdeRow(9102, JsonDocument.Parse(TalkMission).RootElement.Clone())],
        []);

    [AgentsPgFact]
    public async Task An_accepted_kill_mission_starts_its_dungeon_for_the_pilot_in_space_and_completion_pays_once()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "mdr_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var host = new FakeHost { Store = store };
        var runtime = new MissionDungeonRuntime(store, Catalog(), host, agent => agent == Agent ? new MissionAgentPlace(System, Corporation) : null, () => Start);
        await store.OfferAsync(Pilot, Agent, new AgentMissionContent(9101, 1234, "Kill Step", "", false), Start, "offer:1");
        await store.AcceptAsync(Pilot, Agent, Start, "accept:1");

        host.InSystem = false;
        Assert.Equal(0, await runtime.SweepAsync());
        Assert.Empty(host.Starts);
        host.InSystem = true;
        Assert.Equal(1, await runtime.SweepAsync());
        Assert.Equal((Pilot, System, 3509L), Assert.Single(host.Starts));

        await runtime.OnDungeonOutcomeAsync("completed", Pilot, "dungeon.3509", 77, default);
        var credit = Assert.Single(host.Credits);
        Assert.Equal((Pilot, 100000L, Corporation, 33), (credit.Character, credit.Amount, credit.Counterparty, credit.RefType));
        Assert.Equal("agent-mission:" + Pilot + ":9101:dungeon:77", credit.Key);
        Assert.Equal(AgentMissionState.Completed, (await store.ReadAsync(Pilot, Agent))!.State);

        await runtime.OnDungeonOutcomeAsync("completed", Pilot, "dungeon.3509", 77, default);
        Assert.Single(host.Credits);
    }

    [AgentsPgFact]
    public async Task A_failed_dungeon_fails_the_mission_and_pays_nothing()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "mdr_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var host = new FakeHost { Store = store };
        var runtime = new MissionDungeonRuntime(store, Catalog(), host, _ => new MissionAgentPlace(System, Corporation), () => Start);
        await store.OfferAsync(Pilot, Agent, new AgentMissionContent(9101, 1234, "Kill Step", "", false), Start, "offer:2");
        await store.AcceptAsync(Pilot, Agent, Start, "accept:2");

        await runtime.OnDungeonOutcomeAsync("failed", Pilot, "dungeon.3509", 78, default);

        Assert.Empty(host.Credits);
        Assert.Equal(AgentMissionState.Failed, (await store.ReadAsync(Pilot, Agent))!.State);
    }

    [AgentsPgFact]
    public async Task A_talk_step_and_another_dungeon_are_not_touched()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "mdr_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var host = new FakeHost { Store = store };
        var runtime = new MissionDungeonRuntime(store, Catalog(), host, _ => new MissionAgentPlace(System, Corporation), () => Start);
        await store.OfferAsync(Pilot, Agent, new AgentMissionContent(9102, 1235, "Talk Step", "", false), Start, "offer:3");
        await store.AcceptAsync(Pilot, Agent, Start, "accept:3");

        Assert.Equal(0, await runtime.SweepAsync());
        await runtime.OnDungeonOutcomeAsync("completed", Pilot, "dungeon.3509", 79, default);

        Assert.Empty(host.Starts);
        Assert.Empty(host.Credits);
        Assert.Equal(AgentMissionState.Accepted, (await store.ReadAsync(Pilot, Agent))!.State);
    }

    // A kill mission whose completion moves the standing toward a faction by the SDE's 7.0 (the wiki's +0.7).
    private const string StandingMission = """
        {"_key": 9201, "name": {"en": "Standing Step"}, "nameID": 1236, "killMission": {"dungeonID": 3509, "objectiveQuantity": 0},
         "extraStandings": [{"_key": 500003, "_value": 7.0}]}
        """;

    [AgentsPgFact]
    public async Task A_completion_moves_the_standing_once_and_a_replay_moves_it_no_more()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "mdr_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var host = new FakeHost { Store = store };
        var catalog = MissionStaticCatalog.FromRows(3405148, [new SdeRow(9201, JsonDocument.Parse(StandingMission).RootElement.Clone())], []);
        var runtime = new MissionDungeonRuntime(store, catalog, host, _ => new MissionAgentPlace(System, Corporation), () => Start);
        await store.OfferAsync(Pilot, Agent, new AgentMissionContent(9201, 1236, "Standing Step", "", false), Start, "offer:4");
        await store.AcceptAsync(Pilot, Agent, Start, "accept:4");

        await runtime.OnDungeonOutcomeAsync("completed", Pilot, "dungeon.3509", 80, default);
        await runtime.OnDungeonOutcomeAsync("completed", Pilot, "dungeon.3509", 80, default);

        var standing = Assert.Single(host.Standings);
        Assert.Equal((Pilot, 500003L), (standing.Character, standing.Faction));
        Assert.Equal(0.7, standing.Delta, 6);
    }

    private sealed class Cleanup(NpgsqlDataSource source, string schema) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using (var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"))
            {
                await drop.ExecuteNonQueryAsync();
            }
            await source.DisposeAsync();
        }
    }
}
