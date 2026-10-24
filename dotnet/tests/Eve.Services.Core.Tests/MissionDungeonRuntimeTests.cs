using System.Text.Json;
using Eve.Agents;
using Eve.Data;
using Eve.Sde;
using Eve.Universe;
using Npgsql;

namespace Eve.Services.Core.Tests;

/// <summary>
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

        /// <summary>The pilot's items by type (the production host keeps them in the items store).</summary>
        public Dictionary<int, long> Held { get; } = [];
        public List<(long Character, int TypeId, long Quantity, long Station)> Grants { get; } = [];
        public List<(long Character, int TypeId, long Quantity)> Consumed { get; } = [];

        public Task GrantObjectiveItemAsync(long characterId, int typeId, long quantity, long stationId, CancellationToken cancellationToken)
        {
            Grants.Add((characterId, typeId, quantity, stationId));
            Held[typeId] = Math.Max(Held.GetValueOrDefault(typeId), quantity);
            return Task.CompletedTask;
        }

        public Task<long> HeldQuantityAsync(long characterId, int typeId, CancellationToken cancellationToken) =>
            Task.FromResult(Held.GetValueOrDefault(typeId));

        public List<(long Character, int TypeId, long Quantity, bool Singleton, long Station)> Cargo { get; } = [];

        public Task GrantCargoAsync(long characterId, long missionId, int typeId, long quantity, bool singleton, long stationId, CancellationToken cancellationToken)
        {
            Cargo.Add((characterId, typeId, quantity, singleton, stationId));
            Held[typeId] = Held.GetValueOrDefault(typeId) + quantity;
            return Task.CompletedTask;
        }

        public List<(long Mission, long Character)> TakenBack { get; } = [];

        public Task TakeCargoAsync(long characterId, long missionId, int typeId, CancellationToken cancellationToken)
        {
            TakenBack.Add((missionId, characterId));
            Held[typeId] = 0;
            return Task.CompletedTask;
        }

        public async Task<bool> CompleteMissionAsync(long characterId, long agentId, DateTimeOffset now, string reference,
            IReadOnlyList<MissionStandingGain> standings, IReadOnlyList<(int TypeId, long Quantity, string? Tag)> consume, long loyaltyPoints, long issuerCorporationId, CancellationToken cancellationToken)
        {
            if (Store is null) return false;
            if (consume.Any(need => Held.GetValueOrDefault(need.TypeId) < need.Quantity)) return false;
            try
            {
                if (await Store.CompleteAsync(characterId, agentId, now, reference, cancellationToken) is null) return false;
            }
            catch (AgentMissionRefusedException)
            {
                return false;
            }
            foreach (var (typeId, quantity, _) in consume)
            {
                Held[typeId] -= quantity;
                Consumed.Add((characterId, typeId, quantity));
            }
            foreach (var gain in standings) Standings.Add((characterId, gain.EntityId, gain.Amount));
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
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
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

    private const string BonusKillMission = """
        {"_key": 9104, "name": {"en": "Bonus Kill Step"}, "nameID": 1238, "hasStandingRewards": false,
         "killMission": {"dungeonID": 3509, "objectiveQuantity": 0},
         "missionRewards": {"reward": {"rewardTypeID": 29, "rewardQuantity": 100000},
                            "bonusReward": {"rewardTypeID": 29, "rewardQuantity": 40000}, "bonusTimeInterval": 5}}
        """;

    [AgentsPgFact]
    public async Task A_mission_time_bonus_is_paid_only_when_completed_before_its_deadline()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "mdr_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var host = new FakeHost { Store = store };
        var catalog = MissionStaticCatalog.FromRows(3405148,
            [new SdeRow(9104, JsonDocument.Parse(BonusKillMission).RootElement.Clone())], []);
        var completedAt = Start.AddMinutes(4);
        var runtime = new MissionDungeonRuntime(store, catalog, host,
            _ => new MissionAgentPlace(System, Corporation), () => completedAt);
        await store.OfferAsync(Pilot, Agent, new AgentMissionContent(9104, 1238, "Bonus Kill Step", "", false), Start, "offer:bonus");
        await store.AcceptAsync(Pilot, Agent, Start, "accept:bonus");
        await store.OfferAsync(Pilot + 1, Agent, new AgentMissionContent(9104, 1238, "Bonus Kill Step", "", false), Start, "offer:late");
        await store.AcceptAsync(Pilot + 1, Agent, Start, "accept:late");
        Assert.Equal(2, await runtime.SweepAsync());

        await runtime.OnDungeonOutcomeAsync("completed", Pilot, "dungeon.3509", 79, default);
        completedAt = Start.AddMinutes(5);
        await runtime.OnDungeonOutcomeAsync("completed", Pilot + 1, "dungeon.3509", 80, default);

        Assert.Equal(new[] { 100000L, 40000L, 100000L }, host.Credits.Select(credit => credit.Amount));
        Assert.Equal(new[] { 33, 34, 33 }, host.Credits.Select(credit => credit.RefType));
        Assert.Equal(AgentMissionState.Completed, (await store.ReadAsync(Pilot, Agent))!.State);
        Assert.Equal(AgentMissionState.Completed, (await store.ReadAsync(Pilot + 1, Agent))!.State);
    }

    [AgentsPgFact]
    public async Task A_game_master_completion_pays_the_accepted_mission_once_and_refuses_one_that_is_not_accepted()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "mdr_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var host = new FakeHost { Store = store };
        var runtime = new MissionDungeonRuntime(store, Catalog(), host, agent => agent == Agent ? new MissionAgentPlace(System, Corporation) : null, () => Start);
        await store.OfferAsync(Pilot, Agent, new AgentMissionContent(9101, 1234, "Kill Step", "", false), Start, "offer:1");

        // an offer is not a mission the pilot holds: nothing is paid and the offer stays
        Assert.False(await runtime.GmCompleteAsync(Pilot, Agent));
        Assert.Equal(AgentMissionState.Offered, (await store.ReadAsync(Pilot, Agent))!.State);
        Assert.Empty(host.Credits);

        await store.AcceptAsync(Pilot, Agent, Start, "accept:1");
        Assert.True(await runtime.GmCompleteAsync(Pilot, Agent));
        var credit = Assert.Single(host.Credits);
        Assert.Equal((Pilot, 100000L, Corporation, 33), (credit.Character, credit.Amount, credit.Counterparty, credit.RefType));
        Assert.Equal("agent-mission:" + Pilot + ":9101:gm:9101", credit.Key);
        Assert.Equal(AgentMissionState.Completed, (await store.ReadAsync(Pilot, Agent))!.State);

        Assert.False(await runtime.GmCompleteAsync(Pilot, Agent));
        Assert.Single(host.Credits);
    }

    private const string BasicKillMission = """
        {"_key": 9103, "name": {"en": "Basic Kill Step"}, "nameID": 1237, "hasStandingRewards": false,
         "killMission": {"dungeonID": 3509, "objectiveQuantity": 0}}
        """;

    private const string BasicLevel4Tag = """
        {"schema": "eve.rs.mission-level-tags.v1", "tags": [{"missionID": 9103, "level": 4, "provenance": "client", "confidence": "high"}]}
        """;

    [AgentsPgFact]
    public async Task A_basic_level_4_mission_uses_the_configured_isk_and_standing_reward_once()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "mdr_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var host = new FakeHost { Store = store };
        var catalog = MissionStaticCatalog.FromRows(3405148, [new SdeRow(9103, JsonDocument.Parse(BasicKillMission).RootElement.Clone())], [],
            levels: MissionLevelTags.FromJson(BasicLevel4Tag, "test"));
        var basicReward = new ApproximateMissionBasicReward(920_000, 0.031, 0.062);
        var rewardConfiguration = new ApproximateMissionRewardConfiguration(
            BasicMissionRewards: new Dictionary<int, ApproximateMissionBasicReward> { [4] = basicReward });
        var runtime = new MissionDungeonRuntime(store, catalog, host, agent => new MissionAgentPlace(System, Corporation), () => Start,
            missionRewardConfiguration: rewardConfiguration);
        await store.OfferAsync(Pilot, Agent, new AgentMissionContent(9103, 1237, "Basic Kill Step", "", false), Start, "offer:b");
        await store.AcceptAsync(Pilot, Agent, Start, "accept:b");
        Assert.Equal(1, await runtime.SweepAsync());

        await runtime.OnDungeonOutcomeAsync("completed", Pilot, "dungeon.3509", 78, default);
        var credit = Assert.Single(host.Credits);
        Assert.Equal((Pilot, 920_000L, Corporation), (credit.Character, credit.Amount, credit.Counterparty));

        Assert.Equal(new[] { (Pilot, Agent, 0.031), (Pilot, Corporation, 0.062) }, host.Standings.Select(s => (s.Character, s.Faction, s.Delta)));
        Assert.Equal(AgentMissionState.Completed, (await store.ReadAsync(Pilot, Agent))!.State);
    }

    [Fact]
    public void A_diminishing_gain_moves_the_standing_a_fraction_of_the_distance_to_ten_and_an_sde_gain_is_added_as_it_is()
    {
        Assert.Equal(0.14, new MissionStandingGain(1, 0.028, true).DeltaFrom(5.0), 10);
        Assert.Equal(0.7, new MissionStandingGain(1, 0.7, false).DeltaFrom(5.0), 10);
    }

    [AgentsPgFact]
    public async Task A_failed_dungeon_fails_the_mission_and_pays_nothing()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
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
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
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

    private const string StandingMission = """
        {"_key": 9201, "name": {"en": "Standing Step"}, "nameID": 1236, "killMission": {"dungeonID": 3509, "objectiveQuantity": 0},
         "extraStandings": [{"_key": 500003, "_value": 7.0}]}
        """;

    [AgentsPgFact]
    public async Task A_completion_moves_the_standing_once_and_a_replay_moves_it_no_more()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
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

    private const string FetchMission = """
        {"_key": 9301, "name": {"en": "Fetch Step"}, "nameID": 1238, "hasStandingRewards": false,
         "killMission": {"dungeonID": 3509, "objectiveTypeID": 30755, "objectiveQuantity": 1},
         "missionRewards": {"reward": {"rewardTypeID": 29, "rewardQuantity": 100000}}}
        """;

    private const int Datacore = 30755;
    private const long AgentStation = 60015249;

    [AgentsPgFact]
    public async Task A_dungeon_completion_of_a_fetch_step_grants_the_item_at_the_agents_station_and_completes_nothing()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "mdr_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var host = new FakeHost { Store = store };
        var catalog = MissionStaticCatalog.FromRows(3405148, [new SdeRow(9301, JsonDocument.Parse(FetchMission).RootElement.Clone())], []);
        var runtime = new MissionDungeonRuntime(store, catalog, host, _ => new MissionAgentPlace(System, Corporation, AgentStation), () => Start);
        await store.OfferAsync(Pilot, Agent, new AgentMissionContent(9301, 1238, "Fetch Step", "", false), Start, "offer:f1");
        await store.AcceptAsync(Pilot, Agent, Start, "accept:f1");

        await runtime.OnDungeonOutcomeAsync("completed", Pilot, "dungeon.3509", 81, default);

        Assert.Equal((Pilot, Datacore, 1L, AgentStation), Assert.Single(host.Grants));
        Assert.Empty(host.Credits);
        Assert.Empty(host.Standings);
        Assert.Equal(AgentMissionState.Accepted, (await store.ReadAsync(Pilot, Agent))!.State);
    }

    [AgentsPgFact]
    public async Task A_fetch_step_whose_objective_is_held_starts_no_second_dungeon_so_leaving_does_not_fail_it()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "mdr_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var host = new FakeHost { Store = store };
        var catalog = MissionStaticCatalog.FromRows(3405148, [new SdeRow(9301, JsonDocument.Parse(FetchMission).RootElement.Clone())], []);
        var runtime = new MissionDungeonRuntime(store, catalog, host, _ => new MissionAgentPlace(System, Corporation, AgentStation), () => Start);
        await store.OfferAsync(Pilot, Agent, new AgentMissionContent(9301, 1238, "Fetch Step", "", false), Start, "offer:f3");
        await store.AcceptAsync(Pilot, Agent, Start, "accept:f3");
        Assert.Equal(1, await runtime.SweepAsync());
        await runtime.OnDungeonOutcomeAsync("completed", Pilot, "dungeon.3509", 83, default);

        // the next sweep, with the pilot still in the system, starts no dungeon again
        Assert.Equal(0, await runtime.SweepAsync());
        Assert.Single(host.Starts);
        Assert.Equal(AgentMissionState.Accepted, (await store.ReadAsync(Pilot, Agent))!.State);
    }

    [AgentsPgFact]
    public async Task A_turn_in_at_the_agent_consumes_the_item_and_completes_the_step_with_its_reward()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "mdr_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var host = new FakeHost { Store = store };
        var catalog = MissionStaticCatalog.FromRows(3405148, [new SdeRow(9301, JsonDocument.Parse(FetchMission).RootElement.Clone())], []);
        var runtime = new MissionDungeonRuntime(store, catalog, host, _ => new MissionAgentPlace(System, Corporation, AgentStation), () => Start);
        await store.OfferAsync(Pilot, Agent, new AgentMissionContent(9301, 1238, "Fetch Step", "", false), Start, "offer:f2");
        await store.AcceptAsync(Pilot, Agent, Start, "accept:f2");
        await runtime.OnDungeonOutcomeAsync("completed", Pilot, "dungeon.3509", 82, default);

        await runtime.OnConversationAsync(Pilot, Agent, together: true);

        Assert.Equal(AgentMissionState.Completed, (await store.ReadAsync(Pilot, Agent))!.State);
        Assert.Equal((Pilot, Datacore, 1L), Assert.Single(host.Consumed));
        Assert.Equal(0, host.Held.GetValueOrDefault(Datacore));
        Assert.Equal(100000L, Assert.Single(host.Credits).Amount);
    }

    [AgentsPgFact]
    public async Task A_turn_in_with_nothing_held_or_at_another_agent_completes_nothing()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "mdr_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var host = new FakeHost { Store = store };
        var catalog = MissionStaticCatalog.FromRows(3405148, [new SdeRow(9301, JsonDocument.Parse(FetchMission).RootElement.Clone())], []);
        var runtime = new MissionDungeonRuntime(store, catalog, host, _ => new MissionAgentPlace(System, Corporation, AgentStation), () => Start);
        await store.OfferAsync(Pilot, Agent, new AgentMissionContent(9301, 1238, "Fetch Step", "", false), Start, "offer:f3");
        await store.AcceptAsync(Pilot, Agent, Start, "accept:f3");

        await runtime.OnConversationAsync(Pilot, Agent, together: true);
        await runtime.OnDungeonOutcomeAsync("completed", Pilot, "dungeon.3509", 83, default);
        await runtime.OnConversationAsync(Pilot, Agent + 1, together: true);

        Assert.Equal(AgentMissionState.Accepted, (await store.ReadAsync(Pilot, Agent))!.State);
        Assert.Empty(host.Consumed);
        Assert.Empty(host.Credits);
        // the replay of the dungeon's outcome grants nothing more (the holding is already one)
        await runtime.OnDungeonOutcomeAsync("completed", Pilot, "dungeon.3509", 83, default);
        Assert.Equal(1, host.Held.GetValueOrDefault(Datacore));
    }

    private const string CourierMission = """
        {"_key": 9401, "name": {"en": "Courier Step"}, "nameID": 1239, "hasStandingRewards": false,
         "courierMission": {"objectiveTypeID": 30755, "objectiveQuantity": 2, "objectiveSingleton": 0},
         "expirationTime": 10080,
         "missionRewards": {"reward": {"rewardTypeID": 29, "rewardQuantity": 100000}}}
        """;

    private const long DestinationStation = 60000100;

    private static Station Dest(long id, int system) => new(id, 1531, "Dest", 1000002, "", null, "", 0, 0, system, "", 0, "", 0, "", 0.5, 0, 0);

    private static MissionDungeonRuntime CourierRuntime(AgentMissionStore store, FakeHost host, Station? destination) => new(store,
        MissionStaticCatalog.FromRows(3405148, [new SdeRow(9401, JsonDocument.Parse(CourierMission).RootElement.Clone())], []),
        host, _ => new MissionAgentPlace(System, Corporation, AgentStation), () => Start, (missionId, characterId, place, level) => destination);

    [AgentsPgFact]
    public async Task A_courier_accepted_hands_the_cargo_over_at_the_agents_station()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "mdr_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var host = new FakeHost { Store = store };
        var runtime = CourierRuntime(store, host, null);
        await store.OfferAsync(Pilot, Agent, new AgentMissionContent(9401, 1239, "Courier Step", "", false), Start, "offer:c1");
        await store.AcceptAsync(Pilot, Agent, Start, "accept:c1");

        await runtime.OnAcceptedAsync(Pilot, Agent);

        Assert.Equal((Pilot, Datacore, 2L, false, AgentStation), Assert.Single(host.Cargo));
    }

    [AgentsPgFact]
    public async Task A_courier_delivered_at_its_destination_completes_consumes_the_cargo_and_pays()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "mdr_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var host = new FakeHost { Store = store };
        var runtime = CourierRuntime(store, host, Dest(DestinationStation, (int)System));
        await store.OfferAsync(Pilot, Agent, new AgentMissionContent(9401, 1239, "Courier Step", "", false), Start, "offer:c2");
        await store.AcceptAsync(Pilot, Agent, Start, "accept:c2");
        await runtime.OnAcceptedAsync(Pilot, Agent);

        await runtime.OnDockedAsync(Pilot, 60000200);
        Assert.Equal(AgentMissionState.Accepted, (await store.ReadAsync(Pilot, Agent))!.State);
        Assert.Empty(host.Consumed);

        await runtime.OnDockedAsync(Pilot, DestinationStation);

        Assert.Equal(AgentMissionState.Completed, (await store.ReadAsync(Pilot, Agent))!.State);
        Assert.Equal((Pilot, Datacore, 2L), Assert.Single(host.Consumed));
        Assert.Equal(100000L, Assert.Single(host.Credits).Amount);
    }

    [AgentsPgFact]
    public async Task A_courier_past_its_data_expiry_fails_and_one_before_it_does_not()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "mdr_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var host = new FakeHost { Store = store };
        var runtime = CourierRuntime(store, host, null);
        await store.OfferAsync(Pilot, Agent, new AgentMissionContent(9401, 1239, "Courier Step", "", false), Start, "offer:c3");
        await store.AcceptAsync(Pilot, Agent, Start, "accept:c3");
        await runtime.OnAcceptedAsync(Pilot, Agent);

        Assert.Equal(0, await runtime.ExpireCouriersAsync(Start + TimeSpan.FromDays(6)));
        Assert.Equal(AgentMissionState.Accepted, (await store.ReadAsync(Pilot, Agent))!.State);
        Assert.Empty(host.TakenBack);

        Assert.Equal(1, await runtime.ExpireCouriersAsync(Start + TimeSpan.FromDays(8)));
        Assert.Equal(AgentMissionState.Failed, (await store.ReadAsync(Pilot, Agent))!.State);

        Assert.Equal((9401L, Pilot), Assert.Single(host.TakenBack));
        Assert.Empty(host.Standings);
    }

    // The same courier with expirationTime 0 (73 courier records of build 3405148 carry 0): no time limit is given.
    private static MissionDungeonRuntime ZeroExpiryCourierRuntime(AgentMissionStore store, FakeHost host) => new(store,
        MissionStaticCatalog.FromRows(3405148, [new SdeRow(9402, JsonDocument.Parse(CourierMission.Replace("9401", "9402").Replace("10080", "0")).RootElement.Clone())], []),
        host, _ => new MissionAgentPlace(System, Corporation, AgentStation), () => Start, (missionId, characterId, place, level) => null);

    [AgentsPgFact]
    public async Task A_courier_whose_data_gives_zero_expiry_keeps_the_default_and_is_not_failed_at_once()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "mdr_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var host = new FakeHost { Store = store };
        var runtime = ZeroExpiryCourierRuntime(store, host);
        await store.OfferAsync(Pilot, Agent, new AgentMissionContent(9402, 1239, "Courier Step", "", false), Start, "offer:c5");
        await store.AcceptAsync(Pilot, Agent, Start, "accept:c5");
        await runtime.OnAcceptedAsync(Pilot, Agent);

        Assert.Equal(0, await runtime.ExpireCouriersAsync(Start + TimeSpan.FromMinutes(5)));
        Assert.Equal(AgentMissionState.Accepted, (await store.ReadAsync(Pilot, Agent))!.State);
        Assert.Empty(host.TakenBack);

        Assert.Equal(1, await runtime.ExpireCouriersAsync(Start + MissionCourierRules.DefaultExpiry + TimeSpan.FromMinutes(1)));
        Assert.Equal(AgentMissionState.Failed, (await store.ReadAsync(Pilot, Agent))!.State);
    }

    [AgentsPgFact]
    public async Task A_courier_quit_takes_the_cargo_back_and_pays_nothing()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "mdr_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var host = new FakeHost { Store = store };
        var runtime = CourierRuntime(store, host, null);
        await store.OfferAsync(Pilot, Agent, new AgentMissionContent(9401, 1239, "Courier Step", "", false), Start, "offer:c4");
        await store.AcceptAsync(Pilot, Agent, Start, "accept:c4");
        await runtime.OnAcceptedAsync(Pilot, Agent);

        await store.QuitAsync(Pilot, Agent, Start + TimeSpan.FromHours(1), "quit:c4");
        await runtime.OnQuitAsync(Pilot, Agent);

        Assert.Equal((9401L, Pilot), Assert.Single(host.TakenBack));
        Assert.Empty(host.Credits);
        Assert.Equal(AgentMissionState.Quit, (await store.ReadAsync(Pilot, Agent))!.State);
    }

    private const string StorylineTrade = """
        {"_key": 9501, "name": {"en": "Trade Step"}, "nameID": 1245, "hasStandingRewards": false,
         "courierMission": {"objectiveTypeID": 30755, "objectiveQuantity": 3, "objectiveSingleton": 0},
         "missionRewards": {"reward": {"rewardTypeID": 29, "rewardQuantity": 100000}}}
        """;

    private const string BasicMining = """
        {"_key": 9511, "name": {"en": "Mining Step"}, "nameID": 1246, "hasStandingRewards": false,
         "killMission": {"dungeonID": 2457, "objectiveTypeID": 30755, "objectiveQuantity": 18000}}
        """;

    private const string MiningLevel4 = """
        {"schema": "eve.rs.mission-level-tags.v1", "tags": [{"missionID": 9511, "level": 4, "provenance": "client", "confidence": "high"}]}
        """;

    [AgentsPgFact]
    public async Task A_storyline_trade_is_a_fetch_at_its_agent_with_no_cargo_no_delivery_and_no_courier_expiry()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "mdr_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var host = new FakeHost { Store = store };
        var catalog = MissionStaticCatalog.FromRows(3405148, [new SdeRow(9501, JsonDocument.Parse(StorylineTrade).RootElement.Clone())], []);
        var runtime = new MissionDungeonRuntime(store, catalog, host, _ => new MissionAgentPlace(System, Corporation, AgentStation), () => Start,
            isTrade: id => id == 9501);
        await store.OfferAsync(Pilot, Agent, new AgentMissionContent(9501, 1245, "Trade Step", "", false), Start, "offer:t1");
        await store.AcceptAsync(Pilot, Agent, Start, "accept:t1");

        // no cargo at the acceptance, no delivery at a station, and no courier expiry
        await runtime.OnAcceptedAsync(Pilot, Agent);
        Assert.Empty(host.Cargo);
        Assert.Equal(0, await runtime.ExpireCouriersAsync(Start + TimeSpan.FromDays(30)));
        await runtime.OnDockedAsync(Pilot, DestinationStation);
        Assert.Equal(AgentMissionState.Accepted, (await store.ReadAsync(Pilot, Agent))!.State);

        host.Held[Datacore] = 2;
        await runtime.OnConversationAsync(Pilot, Agent, together: true);
        Assert.Equal(AgentMissionState.Accepted, (await store.ReadAsync(Pilot, Agent))!.State);
        Assert.Empty(host.Consumed);
        host.Held[Datacore] = 3;
        await runtime.OnConversationAsync(Pilot, Agent, together: true);
        Assert.Equal(AgentMissionState.Completed, (await store.ReadAsync(Pilot, Agent))!.State);
        Assert.Equal((Pilot, Datacore, 3L), Assert.Single(host.Consumed));
        Assert.Equal(100000L, Assert.Single(host.Credits).Amount);
    }

    [AgentsPgFact]
    public async Task A_mining_step_is_not_granted_by_its_dungeon_and_completes_when_the_pilot_hands_in_the_full_ore_quantity()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "mdr_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var host = new FakeHost { Store = store };
        var catalog = MissionStaticCatalog.FromRows(3405148, [new SdeRow(9511, JsonDocument.Parse(BasicMining).RootElement.Clone())], []);
        var runtime = new MissionDungeonRuntime(store, catalog, host, _ => new MissionAgentPlace(System, Corporation, AgentStation), () => Start,
            isMining: id => id == 9511);
        await store.OfferAsync(Pilot, Agent, new AgentMissionContent(9511, 1246, "Mining Step", "", false), Start, "offer:m1");
        await store.AcceptAsync(Pilot, Agent, Start, "accept:m1");

        // the site grants no ore: the dungeon's completion changes nothing
        await runtime.OnDungeonOutcomeAsync("completed", Pilot, "dungeon.2457", 91, default);
        Assert.Empty(host.Grants);
        Assert.Equal(AgentMissionState.Accepted, (await store.ReadAsync(Pilot, Agent))!.State);

        host.Held[Datacore] = 17999;
        await runtime.OnConversationAsync(Pilot, Agent, together: true);
        Assert.Equal(AgentMissionState.Accepted, (await store.ReadAsync(Pilot, Agent))!.State);
        Assert.Empty(host.Consumed);
        host.Held[Datacore] = 18000;
        await runtime.OnConversationAsync(Pilot, Agent, together: true);
        Assert.Equal(AgentMissionState.Completed, (await store.ReadAsync(Pilot, Agent))!.State);
        Assert.Equal((Pilot, Datacore, 18000L), Assert.Single(host.Consumed));
    }

    [Fact]
    public void A_basic_mining_agent_offers_the_lowest_mining_mission_of_its_level_that_the_dungeon_can_start()
    {
        var catalog = MissionStaticCatalog.FromRows(3405148, [new SdeRow(9511, JsonDocument.Parse(BasicMining).RootElement.Clone())], [],
            levels: MissionLevelTags.FromJson(MiningLevel4, "test"));
        var none = new Dictionary<long, DateTimeOffset>();

        Assert.Equal<long?>(9511L, BasicKillOffers.PickMining(catalog, BasicAgent("mining"), none, Start, _ => true, id => id == 9511));
        Assert.Null(BasicKillOffers.PickMining(catalog, BasicAgent("mining"), none, Start, _ => false, id => id == 9511));
        Assert.Null(BasicKillOffers.PickMining(catalog, BasicAgent("mining"), none, Start, _ => true, id => id != 9511));
        Assert.Null(BasicKillOffers.PickMining(catalog, BasicAgent("courier"), none, Start, _ => true, id => id == 9511));
    }

    [Fact]
    public void The_courier_destination_is_a_station_in_the_level_jump_range_reached_by_stargates_and_is_seeded()
    {
        // systems 1-2-3-4 in a chain; system 9 has no gate. Origin: station 10 in system 1.
        var gates = new StargateGraph([(1, 2), (2, 1), (2, 3), (3, 2), (3, 4), (4, 3)]);
        var stations = new[] { Dest(10, 1), Dest(11, 1), Dest(20, 2), Dest(30, 4), Dest(40, 9) };

        Assert.Equal((1, 12), MissionCourierRules.JumpRange(4)!.Value);
        Assert.Equal((0, 5), MissionCourierRules.JumpRange(2)!.Value);
        Assert.Null(MissionCourierRules.JumpRange(6));

        // level 4 (1 to 12 jumps): the origin system's other station (0 jumps) and the unreachable station are out
        var level4 = MissionCourierRules.Destination(9401, Pilot, 10, 1, 4, gates, stations);
        Assert.Contains(level4!.Id, new long[] { 20, 30 });
        Assert.Equal(level4.Id, MissionCourierRules.Destination(9401, Pilot, 10, 1, 4, gates, stations)!.Id);

        // level 1 (0 to 5 jumps): the other station of the origin system counts, the unreachable one does not
        var level1 = MissionCourierRules.Destination(9401, Pilot, 10, 1, 1, gates, stations);
        Assert.Contains(level1!.Id, new long[] { 11, 20, 30 });
    }

    private const string CourierArc = """
        {"_key": 9001, "name": {"en": "Courier Arc"}, "arcRestartInterval": 129600,
         "missions": [{"_key": 9401, "agentID": 3008500, "nextMissions": []}]}
        """;

    [AgentsPgFact]
    public async Task An_epic_arc_courier_takes_its_agents_level_for_the_jump_range()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "mdr_" + Guid.NewGuid().ToString("N")[..12];
        await using var cleanup = new Cleanup(source, schema);
        await AgentMissionStore.MigrateAsync(source, schema);
        var store = new AgentMissionStore(source, schema);
        var host = new FakeHost { Store = store };
        var catalog = MissionStaticCatalog.FromRows(3405148, [new SdeRow(9401, JsonDocument.Parse(CourierMission).RootElement.Clone())],
            [new SdeRow(9001, JsonDocument.Parse(CourierArc).RootElement.Clone())]);
        long? jumpLevel = null;
        var runtime = new MissionDungeonRuntime(store, catalog, host, _ => new MissionAgentPlace(System, Corporation, AgentStation, 2), () => Start,
            (missionId, characterId, place, level) =>
            {
                jumpLevel = level;
                return Dest(DestinationStation, (int)System);
            });
        await store.OfferAsync(Pilot, Agent, new AgentMissionContent(9401, 1239, "Courier Step", "", false), Start, "offer:c3");
        await store.AcceptAsync(Pilot, Agent, Start, "accept:c3");
        await runtime.OnAcceptedAsync(Pilot, Agent);

        await runtime.OnDockedAsync(Pilot, DestinationStation);

        Assert.Equal(2L, jumpLevel);
        Assert.Equal(AgentMissionState.Completed, (await store.ReadAsync(Pilot, Agent))!.State);
    }

    [Fact]
    public void The_courier_level_of_an_epic_arc_step_is_its_agents_and_any_other_courier_keeps_its_tag()
    {
        Assert.Equal(2L, MissionCourierRules.CourierLevel(true, 2, null));
        Assert.Equal(2L, MissionCourierRules.CourierLevel(true, 2, 4));
        Assert.Equal(4L, MissionCourierRules.CourierLevel(false, 2, 4));
        Assert.Null(MissionCourierRules.CourierLevel(false, 2, null));
    }

    // A basic courier (level 4, Distribution) and a basic item kill (level 4, a dungeon whose objective is an item), both untagged by arcs.
    private const string BasicCourierStep = """
        {"_key": 9411, "name": {"en": "Basic Courier"}, "nameID": 1240, "hasStandingRewards": false,
         "courierMission": {"objectiveTypeID": 30755, "objectiveQuantity": 1, "objectiveSingleton": 0}}
        """;

    private const string BasicItemKill = """
        {"_key": 9412, "name": {"en": "Basic Item Kill"}, "nameID": 1241, "hasStandingRewards": false,
         "killMission": {"dungeonID": 3509, "objectiveTypeID": 30755, "objectiveQuantity": 1}}
        """;

    private const string BasicLevel4 = """
        {"schema": "eve.rs.mission-level-tags.v1", "tags": [
          {"missionID": 9411, "level": 4, "provenance": "client", "confidence": "high"},
          {"missionID": 9412, "level": 4, "provenance": "client", "confidence": "high"}]}
        """;

    private static AgentMissionAgentData BasicAgent(string kind, long level = 4) => new(
        AgentId: Agent, AgentTypeId: 2, DivisionId: 22, Level: level, StationId: 60012607, CorporationId: Corporation,
        IsLocatorAgent: false, IsInSpace: false, CareerId: null, ImportantMission: false, MissionKind: kind,
        MissionTypeLabel: "", SpecialityId: null, StationTypeId: 1531, BloodlineId: null, SolarSystemId: System,
        DungeonId: null, SpawnPointId: null, TypeId: null, BaseLocationId: 60015249);

    [Fact]
    public void A_basic_distribution_agent_offers_the_lowest_courier_mission_of_its_level_and_only_while_not_replayed()
    {
        var catalog = MissionStaticCatalog.FromRows(3405148,
            [new SdeRow(9411, JsonDocument.Parse(BasicCourierStep).RootElement.Clone()), new SdeRow(9412, JsonDocument.Parse(BasicItemKill).RootElement.Clone())],
            [], levels: MissionLevelTags.FromJson(BasicLevel4, "test"));
        var none = new Dictionary<long, DateTimeOffset>();

        Assert.Equal<long?>(9411L, BasicKillOffers.PickCourier(catalog, BasicAgent("courier"), none, Start));
        // another level, another kind, and a replay inside the delay are not offered
        Assert.Null(BasicKillOffers.PickCourier(catalog, BasicAgent("courier", level: 3), none, Start));
        Assert.Null(BasicKillOffers.PickCourier(catalog, BasicAgent("encounter"), none, Start));
        Assert.Null(BasicKillOffers.PickCourier(catalog, BasicAgent("courier"), new Dictionary<long, DateTimeOffset> { [9411] = Start }, Start + TimeSpan.FromHours(1)));
        Assert.Equal<long?>(9411L, BasicKillOffers.PickCourier(catalog, BasicAgent("courier"), new Dictionary<long, DateTimeOffset> { [9411] = Start }, Start + BasicKillOffers.ReplayDelay + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void A_basic_encounter_agent_offers_the_item_kill_mission_of_its_level_through_the_dungeon_offer()
    {
        var catalog = MissionStaticCatalog.FromRows(3405148,
            [new SdeRow(9412, JsonDocument.Parse(BasicItemKill).RootElement.Clone())], [], levels: MissionLevelTags.FromJson(BasicLevel4, "test"));

        Assert.Equal<long?>(9412L, BasicKillOffers.Pick(catalog, BasicAgent("encounter"), new Dictionary<long, DateTimeOffset>(), Start, _ => true));
        Assert.Null(BasicKillOffers.Pick(catalog, BasicAgent("encounter"), new Dictionary<long, DateTimeOffset>(), Start, _ => false));
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
