using System.Text.Json;
using Eve.Agents;
using Eve.Data;
using Eve.Sde;
using Npgsql;

namespace Eve.Services.Core.Tests;

/// <summary>
/// the pending count, and the acceptance marks it made.
/// </summary>
public class StorylineOffersTests
{
    private const long PilotDeclines = 90000301, PilotAccepts = 90000302;
    private const long StorylineAgent = 3008600, Corporation = 1000002, Faction = 500003, System = 30000001;
    private const int Level = 4;
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private const string TradeStep = """
        {"_key": 9601, "name": {"en": "Storyline Trade"}, "nameID": 1250, "hasStandingRewards": false,
         "courierMission": {"objectiveTypeID": 30755, "objectiveQuantity": 1, "objectiveSingleton": 0},
         "missionRewards": {"reward": {"rewardTypeID": 29, "rewardQuantity": 150000}}}
        """;

    private static AgentMissionAgentData StorylineAgentData() => new(
        AgentId: StorylineAgent, AgentTypeId: AgentMissionKindRules.TypeStorylineMissionAgent, DivisionId: 22, Level: 1, StationId: 60012607,
        CorporationId: Corporation, IsLocatorAgent: false, IsInSpace: false, CareerId: null, ImportantMission: true, MissionKind: "storyline",
        MissionTypeLabel: "", SpecialityId: null, StationTypeId: 1531, BloodlineId: null, SolarSystemId: System, DungeonId: null,
        SpawnPointId: null, TypeId: null, BaseLocationId: 60015249);

    private sealed class Rig : IAsyncDisposable
    {
        public NpgsqlDataSource Source { get; }
        public string Schema { get; }
        public AgentMissionStore Store { get; }
        public StorylineCounters Counters { get; }
        public StorylineOffers Offers { get; }

        private Rig(NpgsqlDataSource source, string schema, AgentMissionStore store, StorylineCounters counters, StorylineOffers offers)
        {
            Source = source;
            Schema = schema;
            Store = store;
            Counters = counters;
            Offers = offers;
        }

        public static async Task<Rig> CreateAsync(string pgUrl)
        {
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(pgUrl));
            var schema = "sto_" + Guid.NewGuid().ToString("N")[..12];
            await AgentMissionStore.MigrateAsync(source, schema);
            var store = new AgentMissionStore(source, schema);
            var counters = new StorylineCounters(source, schema);
            var catalog = MissionStaticCatalog.FromRows(3405148, [new SdeRow(9601, JsonDocument.Parse(TradeStep).RootElement.Clone())], []);
            var offers = new StorylineOffers(store, counters, catalog, id => id == 9601, new Dictionary<long, long> { [Corporation] = Faction });
            return new Rig(source, schema, store, counters, offers);
        }

        /// <summary>One completed regular mission of the faction and level, counted as the completion does (in its transaction).</summary>
        public async Task CompleteRegularAsync(long characterId)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await Counters.IncrementAsync(connection, transaction, characterId, Faction, Level);
            await transaction.CommitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await using (var drop = Source.CreateCommand($"DROP SCHEMA IF EXISTS {Schema} CASCADE"))
            {
                await drop.ExecuteNonQueryAsync();
            }
            await Source.DisposeAsync();
        }
    }

    [AgentsPgFact]
    public async Task The_sixteenth_completion_offers_the_storyline_trade_a_decline_keeps_it_due_and_the_acceptance_consumes_it()
    {
        await using var rig = await Rig.CreateAsync(Environment.GetEnvironmentVariable("EVE_PG_URL")!);
        var agent = StorylineAgentData();

        // fifteen completions: nothing is due
        for (var i = 0; i < 15; i++) await rig.CompleteRegularAsync(PilotDeclines);
        Assert.Null(await rig.Offers.PickAsync(agent, PilotDeclines));

        // the sixteenth: the storyline trade is offered; picking it changes no count
        await rig.CompleteRegularAsync(PilotDeclines);
        var picked = Assert.IsType<AgentMissionContent>(await rig.Offers.PickAsync(agent, PilotDeclines));
        Assert.Equal(9601, picked.MissionId);
        Assert.Equal((16L, 0L), await rig.Counters.ReadAsync(PilotDeclines, Faction, Level));

        // a decline keeps the pending count: the offer is still due after it
        await rig.Store.OfferAsync(PilotDeclines, StorylineAgent, picked, Start, "offer:decline");
        await rig.Store.DeclineAsync(PilotDeclines, StorylineAgent, Start.AddMinutes(1), "decline:storyline");
        await rig.Offers.MarkAcceptedAsync(agent, PilotDeclines);
        Assert.Equal((16L, 0L), await rig.Counters.ReadAsync(PilotDeclines, Faction, Level));
        Assert.NotNull(await rig.Offers.PickAsync(agent, PilotDeclines));

        // the acceptance marks the offer made: the pending count is consumed and no offer is due
        for (var i = 0; i < 16; i++) await rig.CompleteRegularAsync(PilotAccepts);
        await rig.Store.OfferAsync(PilotAccepts, StorylineAgent, picked, Start, "offer:accept");
        await rig.Store.AcceptAsync(PilotAccepts, StorylineAgent, Start.AddMinutes(1), "accept:storyline");
        await rig.Offers.MarkAcceptedAsync(agent, PilotAccepts);
        Assert.Equal((16L, 1L), await rig.Counters.ReadAsync(PilotAccepts, Faction, Level));
        Assert.Null(await rig.Offers.PickAsync(agent, PilotAccepts));
    }

    [AgentsPgFact]
    public async Task Replaying_the_same_accepted_mission_event_consumes_one_storyline_offer()
    {
        await using var rig = await Rig.CreateAsync(Environment.GetEnvironmentVariable("EVE_PG_URL")!);
        var agent = StorylineAgentData();
        for (var i = 0; i < 16; i++) await rig.CompleteRegularAsync(PilotAccepts);
        await rig.Store.OfferAsync(PilotAccepts, StorylineAgent,
            new AgentMissionContent(9601, 1250, "Storyline Trade", "storyline", true), Start, "offer:receipt");
        var accepted = Assert.IsType<AgentMissionChange>(await rig.Store.AcceptAsync(
            PilotAccepts, StorylineAgent, Start.AddMinutes(1), "accept:receipt"));

        await rig.Offers.MarkAcceptedAsync(agent, accepted);
        await rig.Offers.MarkAcceptedAsync(agent, accepted);

        Assert.Equal((16L, 1L), await rig.Counters.ReadAsync(PilotAccepts, Faction, Level));
        await using var receipt = rig.Source.CreateCommand(
            $"SELECT count(*) FROM {rig.Schema}.storyline_offer_receipts WHERE character_id = $1 AND acceptance_reference = $2");
        receipt.Parameters.AddWithValue(PilotAccepts);
        receipt.Parameters.AddWithValue(accepted.Reference);
        Assert.Equal(1L, (long)(await receipt.ExecuteScalarAsync())!);
    }
}
