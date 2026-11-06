using Eve.Agents;
using Eve.Data;
using Eve.Events;
using Eve.Host;
using Eve.Inventory;
using Eve.Marshal;
using Eve.ReadSide;
using Eve.Relations;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Corporations;
using Eve.Services.Core;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>The completion of a mission and its standing writes are one transaction: the composition's own path, not a copy of it.</summary>
public class MissionCompletionTransactionTests
{
    private const long Pilot = 90000101;
    private const long Agent = 3008416;
    private const long Corporation = 1000002;
    private const int Datacore = 30755;
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly AgentMissionContent Kill = new(MissionId: 5101, NameId: 9100, Title: "", MissionTypeLabel: "kill", Important: false);
    private static readonly MissionStandingGain[] Gains = [new(Agent, 0.028, true), new(Corporation, 0.055, true)];

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string[] _schemas;

        private Fixture(NpgsqlDataSource source, string[] schemas, DurableScheduler scheduler, EventBus bus, DurableScheduler otherScheduler,
            EventBus otherBus, string missingRelationsSchema)
        {
            Source = source;
            Store = new AgentMissionStore(source, schemas[3]);
            Relations = new RelationStore(source, scheduler, bus, schemas[2]);
            Loyalty = new LoyaltyPointStore(source, schemas[5]);
            MissingRelations = new RelationStore(source, otherScheduler, otherBus, missingRelationsSchema);
            _schemas = [.. schemas, "mo_" + missingRelationsSchema[4..], missingRelationsSchema];
        }

        public NpgsqlDataSource Source { get; }
        public AgentMissionStore Store { get; }
        public PgItemStore Items { get; set; } = default!;
        public Eve.Agents.StorylineCounters Counters { get; set; } = default!;

        /// <summary>Puts items of the type into the pilot's hangar (a station of the agent).</summary>
        public async Task GrantAsync(int typeId, long quantity, string? customInfo = null)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await Items.CreateAsync(connection, transaction, new NewItem(typeId, Pilot, 60015249, ItemFlags.Hangar, quantity, CustomInfo: customInfo));
            await transaction.CommitAsync();
        }
        public RelationStore Relations { get; }
        public string RelationSchema => _schemas[2];
        public LoyaltyPointStore Loyalty { get; }
        public string LoyaltySchema => _schemas[5];
        /// <summary>A relation store on a schema that does not exist: its first standing read fails inside the completion's transaction.</summary>
        public RelationStore MissingRelations { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            var id = Guid.NewGuid().ToString("N")[..12];
            string[] schemas = ["msch_" + id, "mevt_" + id, "mrel_" + id, "magt_" + id, "mitm_" + id, "mwal_" + id];
            await DurableScheduler.MigrateAsync(source, schemas[0]);
            await EventBus.MigrateAsync(source, schemas[1]);
            await RelationStore.MigrateAsync(source, schemas[2]);
            await AgentMissionStore.MigrateAsync(source, schemas[3]);
            await PgItemStore.MigrateAsync(source, schemas[4]);
            await PgWalletStore.MigrateAsync(source, schemas[5]);
            var scheduler = new DurableScheduler(source, () => Start.ToUnixTimeMilliseconds(), new SchedulerOptions { Concurrency = 1 }, schemas[0]);
            var bus = new EventBus(source, scheduler, null, schemas[1]);
            // a relation store registers its jobs on its scheduler, so the store on the missing schema has a scheduler and a bus of its own
            var otherScheduler = new DurableScheduler(source, () => Start.ToUnixTimeMilliseconds(), new SchedulerOptions { Concurrency = 1 }, "mo_" + id);
            var otherBus = new EventBus(source, otherScheduler, null, "mob_" + id);
            var fixture = new Fixture(source, schemas, scheduler, bus, otherScheduler, otherBus, "mis_" + id) { Items = new PgItemStore(source, schemas[4]), Counters = new Eve.Agents.StorylineCounters(source, schemas[3]) };
            await fixture.Store.OfferAsync(Pilot, Agent, Kill, Start, "offer:x6");
            await fixture.Store.AcceptAsync(Pilot, Agent, Start, "accept:x6");
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var schema in _schemas)
            {
                await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
            await Source.DisposeAsync();
        }
    }

    [PgFact]
    public async Task A_completion_and_its_standing_writes_are_committed_together()
    {
        await using var fixture = await Fixture.CreateAsync();

        var done = await CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store, fixture.Relations, fixture.Items,
            Pilot, Agent, Start + TimeSpan.FromHours(1), "complete:x6", Gains, [], CancellationToken.None);

        Assert.True(done);
        Assert.Equal(AgentMissionState.Completed, (await fixture.Store.ReadAsync(Pilot, Agent))!.State);
        Assert.Equal(0.28, await fixture.Relations.ValueAsync(RelationKinds.Standing.Name, Agent, Pilot, 0), 10);
        Assert.Equal(0.55, await fixture.Relations.ValueAsync(RelationKinds.Standing.Name, Corporation, Pilot, 0), 10);
    }

    [PgFact]
    public async Task Concurrent_mission_completions_apply_diminishing_gain_to_the_latest_standing()
    {
        await using var fixture = await Fixture.CreateAsync();
        const long otherAgent = 3008417;
        await fixture.Store.OfferAsync(Pilot, otherAgent, Kill, Start, "offer:standing-race");
        await fixture.Store.AcceptAsync(Pilot, otherAgent, Start, "accept:standing-race");
        await fixture.Relations.SetAsync(RelationKinds.Standing.Name, Agent, Pilot, 5.0, "seed");

        var completions = await Task.WhenAll(
            CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store, fixture.Relations, fixture.Items,
                Pilot, Agent, Start + TimeSpan.FromHours(1), "complete:standing-race-a", [new(Agent, 0.028, true)], [], CancellationToken.None),
            CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store, fixture.Relations, fixture.Items,
                Pilot, otherAgent, Start + TimeSpan.FromHours(1), "complete:standing-race-b", [new(Agent, 0.028, true)], [], CancellationToken.None));

        Assert.All(completions, done => Assert.True(done));
        Assert.Equal(5.276, await fixture.Relations.ValueAsync(RelationKinds.Standing.Name, Agent, Pilot, 0), 10);
    }

    [PgFact]
    public async Task Standing_cap_records_the_mission_transaction_and_zero_gain_writes_nothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        const long otherAgent = 3008417;
        await fixture.Store.OfferAsync(Pilot, otherAgent, Kill, Start, "offer:standing-zero");
        await fixture.Store.AcceptAsync(Pilot, otherAgent, Start, "accept:standing-zero");
        await fixture.Relations.SetAsync(RelationKinds.Standing.Name, Agent, Pilot, 10, "seed");
        var notifications = 0;

        Assert.True(await CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store, fixture.Relations, fixture.Items,
            Pilot, Agent, Start + TimeSpan.FromHours(1), "complete:standing-cap", [new(Agent, 0.028, true, "Mission at the cap")], [], CancellationToken.None,
            afterStandingCommit: _ => { notifications++; return Task.CompletedTask; }));
        Assert.True(await CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store, fixture.Relations, fixture.Items,
            Pilot, otherAgent, Start + TimeSpan.FromHours(1), "complete:standing-zero", [new(Agent, 0, true)], [], CancellationToken.None,
            afterStandingCommit: _ => { notifications++; return Task.CompletedTask; }));

        var transaction = Assert.Single((await StandingTransactionsAsync(fixture)).Items);
        Assert.Equal(10, await fixture.Relations.ValueAsync(RelationKinds.Standing.Name, Agent, Pilot, 0), 10);
        Assert.Equal(1, notifications);
        Assert.Equal("Mission at the cap", Assert.IsType<PyWString>(KeyValField(transaction, "msg")).Value);
        Assert.Equal(new PyReal(0.028), KeyValField(transaction, "modification"));
    }

    [PgFact]
    public async Task Mission_standing_history_is_returned_after_reopening_the_read_side()
    {
        await using var fixture = await Fixture.CreateAsync();
        var eventTime = Start + TimeSpan.FromHours(1);
        const string message = "The completed mission";

        Assert.True(await CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store, fixture.Relations, fixture.Items,
            Pilot, Agent, eventTime, "complete:standing-history", [new(Agent, 0.028, true, message)], [], CancellationToken.None));

        var transaction = Assert.Single((await StandingTransactionsAsync(fixture)).Items);
        Assert.Equal(new PyInt(73), KeyValField(transaction, "eventTypeID"));
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(eventTime.ToUnixTimeMilliseconds())), KeyValField(transaction, "eventDateTime"));
        Assert.Equal(new PyReal(0.028), KeyValField(transaction, "modification"));
        Assert.Equal(new PyInt(Agent), KeyValField(transaction, "fromID"));
        Assert.Equal(new PyInt(Pilot), KeyValField(transaction, "toID"));
        Assert.Equal(message, Assert.IsType<PyWString>(KeyValField(transaction, "msg")).Value);
        Assert.Same(PyNone.Instance, KeyValField(transaction, "int_1"));
        Assert.Same(PyNone.Instance, KeyValField(transaction, "int_2"));
        Assert.Same(PyNone.Instance, KeyValField(transaction, "int_3"));
    }

    [PgFact]
    public async Task Standing_notification_is_sent_after_commit_once_with_the_client_row_shape()
    {
        await using var fixture = await Fixture.CreateAsync();
        var order = new List<string>();
        PyValue? payload = null;
        fixture.Store.Committed += _ => order.Add("mission");
        async Task StandingNotification(PyValue modifications)
        {
            Assert.Equal(0.28, await fixture.Relations.ValueAsync(RelationKinds.Standing.Name, Agent, Pilot, 0), 10);
            payload = modifications;
            order.Add("standing");
        }

        Assert.True(await CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store, fixture.Relations, fixture.Items,
            Pilot, Agent, Start + TimeSpan.FromHours(1), "complete:standing-notify", [new(Agent, 0.028, true)], [], CancellationToken.None,
            afterStandingCommit: StandingNotification));
        Assert.False(await CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store, fixture.Relations, fixture.Items,
            Pilot, Agent, Start + TimeSpan.FromHours(1), "complete:standing-notify-retry", [new(Agent, 0.028, true)], [], CancellationToken.None,
            afterStandingCommit: StandingNotification));

        Assert.Equal(["standing", "mission"], order);
        var row = Assert.IsType<PyList>(Assert.Single(Assert.IsType<PyList>(payload).Items));
        Assert.Equal(5, row.Items.Count);
        Assert.Equal(new PyInt(Agent), row.Items[0]);
        Assert.Equal(new PyInt(Pilot), row.Items[1]);
        Assert.Equal(new PyReal(0.028), row.Items[2]);
        Assert.Equal(new PyInt(-10), row.Items[3]);
        Assert.Equal(new PyInt(10), row.Items[4]);
        Assert.Single((await StandingTransactionsAsync(fixture)).Items);
    }

    [PgFact]
    public async Task Mission_loyalty_reward_and_corporation_tax_commit_with_completion()
    {
        await using var fixture = await Fixture.CreateAsync();

        var done = await CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store, fixture.Relations, fixture.Items,
            Pilot, Agent, Start + TimeSpan.FromHours(1), "complete:lp", Gains, [], CancellationToken.None,
            loyalty: fixture.Loyalty, loyaltyAmount: 100, issuerCorporationId: Corporation, taxCorporationId: 90000202, loyaltyTaxRate: 0.2m);

        Assert.True(done);
        Assert.Equal([new LoyaltyPointBalance(Corporation, 80)], await fixture.Loyalty.ReadAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, Pilot)));
        Assert.Equal([new LoyaltyPointBalance(Corporation, 20)], await fixture.Loyalty.ReadAsync(new LoyaltyHolder(LoyaltyHolderKind.Corporation, 90000202)));
        Assert.Equal(AgentMissionState.Completed, (await fixture.Store.ReadAsync(Pilot, Agent))!.State);
        Assert.False(await CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store, fixture.Relations, fixture.Items,
            Pilot, Agent, Start + TimeSpan.FromHours(1), "complete:lp", Gains, [], CancellationToken.None,
            loyalty: fixture.Loyalty, loyaltyAmount: 100, issuerCorporationId: Corporation, taxCorporationId: 90000202, loyaltyTaxRate: 0.2m));
        var reopenedLoyalty = new LoyaltyPointStore(fixture.Source, fixture.LoyaltySchema);
        Assert.Equal([new LoyaltyPointBalance(Corporation, 80)], await reopenedLoyalty.ReadAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, Pilot)));
    }

    [PgFact]
    public async Task Concurrent_mission_completions_serialize_credits_to_the_same_lp_balance()
    {
        await using var fixture = await Fixture.CreateAsync();
        const long otherAgent = 3008417;
        await fixture.Store.OfferAsync(Pilot, otherAgent, Kill, Start, "offer:lp-race");
        await fixture.Store.AcceptAsync(Pilot, otherAgent, Start, "accept:lp-race");

        var completions = await Task.WhenAll(
            CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store, fixture.Relations, fixture.Items,
                Pilot, Agent, Start + TimeSpan.FromHours(1), "complete:lp-race-a", [], [], CancellationToken.None,
                loyalty: fixture.Loyalty, loyaltyAmount: 125, issuerCorporationId: Corporation),
            CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store, fixture.Relations, fixture.Items,
                Pilot, otherAgent, Start + TimeSpan.FromHours(1), "complete:lp-race-b", [], [], CancellationToken.None,
                loyalty: fixture.Loyalty, loyaltyAmount: 125, issuerCorporationId: Corporation));

        Assert.All(completions, done => Assert.True(done));
        Assert.Equal([new LoyaltyPointBalance(Corporation, 250)], await fixture.Loyalty.ReadAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, Pilot)));
    }

    [PgFact]
    public async Task Mission_loyalty_reward_rolls_back_when_a_later_completion_write_fails()
    {
        await using var fixture = await Fixture.CreateAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store,
            fixture.MissingRelations, fixture.Items, Pilot, Agent, Start + TimeSpan.FromHours(1), "complete:lp-rollback", Gains, [], CancellationToken.None,
            loyalty: fixture.Loyalty, loyaltyAmount: 100, issuerCorporationId: Corporation));

        Assert.Empty(await fixture.Loyalty.ReadAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, Pilot)));
        Assert.Equal(AgentMissionState.Accepted, (await fixture.Store.ReadAsync(Pilot, Agent))!.State);
    }

    [PgFact]
    public async Task A_standing_write_that_fails_rolls_the_completion_back_and_the_mission_stays_accepted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var notifications = 0;

        await Assert.ThrowsAnyAsync<Exception>(() => CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store,
            fixture.MissingRelations, fixture.Items, Pilot, Agent, Start + TimeSpan.FromHours(1), "complete:x6-fail", Gains, [], CancellationToken.None,
            afterStandingCommit: _ => { notifications++; return Task.CompletedTask; }));

        Assert.Equal(AgentMissionState.Accepted, (await fixture.Store.ReadAsync(Pilot, Agent))!.State);
        Assert.Empty(await fixture.Store.ReadCompletedAsync(Pilot));
        Assert.Equal(0, notifications);
        Assert.Equal(0.0, await fixture.Relations.ValueAsync(RelationKinds.Standing.Name, Agent, Pilot, 0), 10);
    }

    [PgFact]
    public async Task A_completion_that_consumes_the_objective_item_takes_it_with_the_completion()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.GrantAsync(Datacore, 1);

        var done = await CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store, fixture.Relations, fixture.Items,
            Pilot, Agent, Start + TimeSpan.FromHours(1), "complete:x8", Gains, [(Datacore, 1L, (string?)null)], CancellationToken.None);

        Assert.True(done);
        Assert.Equal(AgentMissionState.Completed, (await fixture.Store.ReadAsync(Pilot, Agent))!.State);
        Assert.Empty(await fixture.Items.OwnedByAsync(Pilot));
    }

    [PgFact]
    public async Task Two_turn_ins_racing_for_one_item_consume_it_once()
    {
        await using var fixture = await Fixture.CreateAsync();
        const long OtherAgent = 3008417;
        await fixture.Store.OfferAsync(Pilot, OtherAgent, Kill, Start, "offer:x9-race");
        await fixture.Store.AcceptAsync(Pilot, OtherAgent, Start, "accept:x9-race");
        await fixture.GrantAsync(Datacore, 1);

        var results = await Task.WhenAll(
            CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store, fixture.Relations, fixture.Items,
                Pilot, Agent, Start + TimeSpan.FromHours(1), "complete:x9-a", Gains, [(Datacore, 1L, (string?)null)], CancellationToken.None),
            CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store, fixture.Relations, fixture.Items,
                Pilot, OtherAgent, Start + TimeSpan.FromHours(1), "complete:x9-b", Gains, [(Datacore, 1L, (string?)null)], CancellationToken.None));

        Assert.Equal(1, results.Count(done => done));
        Assert.Empty(await fixture.Items.OwnedByAsync(Pilot));
    }

    [PgFact]
    public async Task A_counted_completion_adds_one_to_its_faction_and_level_and_an_uncounted_one_adds_nothing()
    {
        await using var fixture = await Fixture.CreateAsync();

        var done = await CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store, fixture.Relations, fixture.Items,
            Pilot, Agent, Start + TimeSpan.FromHours(1), "complete:x15", Gains, [], CancellationToken.None, fixture.Counters, (500003L, 4L));

        Assert.True(done);
        Assert.Equal((1L, 0L), await fixture.Counters.ReadAsync(Pilot, 500003L, 4L));
        Assert.Equal((0L, 0L), await fixture.Counters.ReadAsync(Pilot, 500003L, 3L));
    }

    [PgFact]
    public async Task A_courier_consumption_takes_only_the_tagged_stack_and_leaves_the_pilots_own_stacks()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.GrantAsync(Datacore, 1, "courier:9401");
        await fixture.GrantAsync(Datacore, 1);

        var done = await CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store, fixture.Relations, fixture.Items,
            Pilot, Agent, Start + TimeSpan.FromHours(1), "complete:x12", Gains, [(Datacore, 1L, "courier:9401")], CancellationToken.None);

        Assert.True(done);
        var left = Assert.Single(await fixture.Items.OwnedByAsync(Pilot));
        Assert.Null(left.CustomInfo);
    }

    [PgFact]
    public async Task A_courier_consumption_with_only_the_pilots_own_stacks_is_refused_and_writes_nothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.GrantAsync(Datacore, 1);

        var done = await CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store, fixture.Relations, fixture.Items,
            Pilot, Agent, Start + TimeSpan.FromHours(1), "complete:x12-own", Gains, [(Datacore, 1L, "courier:9401")], CancellationToken.None);

        Assert.False(done);
        Assert.Equal(AgentMissionState.Accepted, (await fixture.Store.ReadAsync(Pilot, Agent))!.State);
        Assert.Single(await fixture.Items.OwnedByAsync(Pilot));
    }

    [PgFact]
    public async Task A_completion_with_the_objective_item_short_is_refused_and_writes_nothing()
    {
        await using var fixture = await Fixture.CreateAsync();

        var done = await CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store, fixture.Relations, fixture.Items,
            Pilot, Agent, Start + TimeSpan.FromHours(1), "complete:x8-short", Gains, [(Datacore, 1L, (string?)null)], CancellationToken.None);

        Assert.False(done);
        Assert.Equal(AgentMissionState.Accepted, (await fixture.Store.ReadAsync(Pilot, Agent))!.State);
        Assert.Equal(0.0, await fixture.Relations.ValueAsync(RelationKinds.Standing.Name, Agent, Pilot, 0), 10);
    }

    private static async Task<PyList> StandingTransactionsAsync(Fixture fixture)
    {
        var readSide = new Eve.ReadSide.ReadSide(fixture.Source, new Dictionary<string, string> { ["relations"] = fixture.RelationSchema });
        var service = new StandingMgrService(readSide, null!, () => Start.ToUnixTimeMilliseconds());
        return Assert.IsType<PyList>(await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "GetStandingTransactions",
            Args = [new PyLong(Agent), new PyLong(Pilot)],
            Session = new ClientSession { CharacterId = Pilot },
        }));
    }

    private static PyValue KeyValField(PyValue value, string name)
    {
        var dictionary = Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args);
        return dictionary.Entries.Single(entry => Assert.IsType<PyStr>(entry.Key).Value == name).Value;
    }
}
