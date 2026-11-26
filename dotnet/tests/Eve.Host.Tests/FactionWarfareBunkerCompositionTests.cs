using System.Reflection;
using System.Runtime.CompilerServices;
using Eve.Data;
using Eve.Dogma;
using Eve.Events;
using Eve.FactionWarfare;
using Eve.Host;
using Eve.Host.Persistence.FactionWarfare;
using Eve.Scheduler;
using Eve.Sde;
using Eve.Services.Space;
using Eve.Space;
using Eve.Structures;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>
/// The Control Bunker ID the client's facWarMgr.GetControlBunkerIDInSystem reads, through the production composition's own
/// <c>ReadControlBunkerIdAsync</c> (CoreRpc wires it), with the bunker placed by the real <see cref="FactionWarfareControlBunkers"/>.
/// </summary>
public sealed class FactionWarfareBunkerCompositionTests
{
    private const long Now = 1_699_956_000_000;

    [FactionWarfareProducerPgFact]
    public async Task Production_composition_exposes_the_stored_bunker_id_only_while_its_live_group_925_ball_exists()
    {
        var connectionString = PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!);
        await using var source = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var fwSchema = "fw_comp_" + suffix;
        var schedulerSchema = "sch_comp_" + suffix;
        var eventSchema = "evt_comp_" + suffix;
        try
        {
            await FwStore.MigrateAsync(source, fwSchema);
            await DurableScheduler.MigrateAsync(source, schedulerSchema);
            await EventBus.MigrateAsync(source, eventSchema);
            var store = new FwStore(source, fwSchema);
            var systemId = checked((int)FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar)[0]);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await store.SeedSystemsAsync(connection, transaction,
                    [new FwSystem(systemId, FwWarzones.AmarrMinmatar, FwFactions.AmarrEmpire, 6, 0, Now)]);
                await transaction.CommitAsync();
            }

            await using var scheduler = new DurableScheduler(source, () => Now, new SchedulerOptions { Concurrency = 1 }, schedulerSchema);
            var events = new EventBus(source, scheduler, schema: eventSchema);
            var sde = new SdeReader(source);
            Assert.Equal(3_396_210L, await sde.BuildAsync());
            var statics = await DogmaStatic.LoadAsync(sde);
            var simContract = DispatchProxy.Create<ISimulation, FactionWarfareProducerHostTests.RecordingSimulation>();
            var sim = (FactionWarfareProducerHostTests.RecordingSimulation)(object)simContract;
            using var space = new SpaceSessions(simContract, null!, statics, null!, null!, () => Now);
            var bunkers = new FactionWarfareControlBunkers(source, store, scheduler, events, simContract, space, statics, sde);

            var host = (CharacterServicesComposition)RuntimeHelpers.GetUninitializedObject(typeof(CharacterServicesComposition));
            void Field(string name, object? value) =>
                typeof(CharacterServicesComposition).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, value);
            var readMethod = typeof(CharacterServicesComposition).GetMethod("ReadControlBunkerIdAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            async Task<long?> ProductionRead(long solarSystemId) =>
                await (ValueTask<long?>)readMethod.Invoke(host, [solarSystemId, CancellationToken.None])!;

            Field("factionWarfareStore", store);
            Field("fwControlBunkers", bunkers);

            // no live group-925 ball in the scene: the client gets no ID (never a stored or invented one)
            Assert.Null(await ProductionRead(systemId));
            Assert.Empty(sim.Placed);

            // the scene-entry restore places the bunker; the composition now exposes exactly the stored, placed ID
            await bunkers.RestoreAsync(systemId);
            var placed = Assert.Single(sim.Placed);
            Assert.Equal(925, placed.GroupId);
            var stored = await ReadSystemAsync(source, store, systemId);
            Assert.Equal(placed.StructureId, stored.ControlBunkerId);
            Assert.Equal(stored.ControlBunkerId, await ProductionRead(systemId));

            // the ball leaves the scene: the ID is withdrawn again
            simContract.RemoveStructure(placed.StructureId);
            Assert.Null(await ProductionRead(systemId));

            // the space runtime is absent in the composition (EVE_SIM off): the production branch stays fail-closed even with a live ball
            await bunkers.RestoreAsync(systemId);
            Assert.Equal(2, sim.Placed.Count);
            Field("fwControlBunkers", null);
            Assert.Null(await ProductionRead(systemId));
        }
        finally
        {
            await using var cleanup = await source.OpenConnectionAsync();
            foreach (var schema in new[] { fwSchema, schedulerSchema, eventSchema })
                await Sql.ExecuteAsync(cleanup, null, $"DROP SCHEMA IF EXISTS {schema} CASCADE", CancellationToken.None);
        }
    }

    private static async Task<FwSystem> ReadSystemAsync(NpgsqlDataSource source, FwStore store, long systemId)
    {
        await using var connection = await source.OpenConnectionAsync();
        return (await store.SystemAsync(connection, null, systemId, forUpdate: false))!;
    }
}
