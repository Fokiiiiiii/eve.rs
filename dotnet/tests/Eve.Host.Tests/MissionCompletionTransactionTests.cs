using Eve.Agents;
using Eve.Data;
using Eve.Events;
using Eve.Host;
using Eve.Relations;
using Eve.Scheduler;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>The completion of a mission and its standing writes are one transaction: the composition's own path, not a copy of it.</summary>
public class MissionCompletionTransactionTests
{
    private const long Pilot = 90000101;
    private const long Agent = 3008416;
    private const long Corporation = 1000002;
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
            MissingRelations = new RelationStore(source, otherScheduler, otherBus, missingRelationsSchema);
            _schemas = [.. schemas, "mo_" + missingRelationsSchema[4..], missingRelationsSchema];
        }

        public NpgsqlDataSource Source { get; }
        public AgentMissionStore Store { get; }
        public RelationStore Relations { get; }
        /// <summary>A relation store on a schema that does not exist: its first standing read fails inside the completion's transaction.</summary>
        public RelationStore MissingRelations { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            var id = Guid.NewGuid().ToString("N")[..12];
            string[] schemas = ["msch_" + id, "mevt_" + id, "mrel_" + id, "magt_" + id];
            await DurableScheduler.MigrateAsync(source, schemas[0]);
            await EventBus.MigrateAsync(source, schemas[1]);
            await RelationStore.MigrateAsync(source, schemas[2]);
            await AgentMissionStore.MigrateAsync(source, schemas[3]);
            var scheduler = new DurableScheduler(source, () => Start.ToUnixTimeMilliseconds(), new SchedulerOptions { Concurrency = 1 }, schemas[0]);
            var bus = new EventBus(source, scheduler, null, schemas[1]);
            // a relation store registers its jobs on its scheduler, so the store on the missing schema has a scheduler and a bus of its own
            var otherScheduler = new DurableScheduler(source, () => Start.ToUnixTimeMilliseconds(), new SchedulerOptions { Concurrency = 1 }, "mo_" + id);
            var otherBus = new EventBus(source, otherScheduler, null, "mob_" + id);
            var fixture = new Fixture(source, schemas, scheduler, bus, otherScheduler, otherBus, "mis_" + id);
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

        var done = await CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store, fixture.Relations,
            Pilot, Agent, Start + TimeSpan.FromHours(1), "complete:x6", Gains, CancellationToken.None);

        Assert.True(done);
        Assert.Equal(AgentMissionState.Completed, (await fixture.Store.ReadAsync(Pilot, Agent))!.State);
        Assert.Equal(0.28, await fixture.Relations.ValueAsync(RelationKinds.Standing.Name, Agent, Pilot, 0), 10);
        Assert.Equal(0.55, await fixture.Relations.ValueAsync(RelationKinds.Standing.Name, Corporation, Pilot, 0), 10);
    }

    [PgFact]
    public async Task A_standing_write_that_fails_rolls_the_completion_back_and_the_mission_stays_accepted()
    {
        await using var fixture = await Fixture.CreateAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => CharacterServicesComposition.CompleteInTransactionAsync(fixture.Source, fixture.Store,
            fixture.MissingRelations, Pilot, Agent, Start + TimeSpan.FromHours(1), "complete:x6-fail", Gains, CancellationToken.None));

        Assert.Equal(AgentMissionState.Accepted, (await fixture.Store.ReadAsync(Pilot, Agent))!.State);
        Assert.Empty(await fixture.Store.ReadCompletedAsync(Pilot));
    }
}
