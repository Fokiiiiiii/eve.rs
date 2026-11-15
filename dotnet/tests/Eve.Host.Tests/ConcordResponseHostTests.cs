using Eve.Crimewatch;
using Eve.Data;
using Eve.Host;
using Eve.Macho;
using Eve.Services;
using Eve.Services.Crimewatch;
using Eve.Services.Space;
using Eve.Space;
using Eve.Universe;
using Npgsql;
using System.Reflection;

namespace Eve.Host.Tests;

public sealed class ConcordResponseHostTests
{
    private sealed class ControlledDelay
    {
        private readonly object _gate = new();
        private readonly List<(TimeSpan After, TaskCompletionSource Done)> _waits = [];

        public Task Delay(TimeSpan after, CancellationToken cancellationToken)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
                _waits.Add((after, done));
            return done.Task.WaitAsync(cancellationToken);
        }

        public IReadOnlyList<(TimeSpan After, TaskCompletionSource Done)> Snapshot()
        {
            lock (_gate)
                return [.. _waits];
        }
    }

    [PgFact]
    public async Task Host_wiring_reports_a_rejected_concord_spawn_so_crimewatch_retries_it()
    {
        const long characterId = 90_001_001;
        const long systemId = 30_100_032;
        const long start = 1_900_000_000_000;
        var now = start;
        var schema = "conchost_" + Guid.NewGuid().ToString("N")[..12];
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(
            Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        await PgCrimewatchStore.MigrateAsync(source, schema);
        try
        {
            var store = new PgCrimewatchStore(source, schema);
            await store.UpdateAsync(characterId,
                _ => new CombatState(0, 0, 0, start + CombatState.FlagDurationMs,
                    Flag.Criminal, 0, systemId), start);
            var waits = new ControlledDelay();
            using var runtime = new CrimewatchRuntime(store, new CharacterSessions(), () => now,
                persistence: null!, delay: waits.Delay);
            using var space = new SpaceSessions(
                DispatchProxy.Create<ISimulation, InertWormholeSimulation>(), null!, null!,
                new StationCatalog([]), new SolarSystemCatalog([]), () => now);

            CharacterServicesComposition.WireConcordResponseCallback(runtime, space);
            await runtime.OnSystemEnteredAsync(characterId, systemId, 1.0);

            await UntilAsync(() => waits.Snapshot().Count == 1);
            var first = Assert.Single(waits.Snapshot());
            Assert.Equal(TimeSpan.FromMilliseconds(6_005), first.After);

            now += 6_000;
            first.Done.TrySetResult();
            await UntilAsync(() => waits.Snapshot().Count == 2);

            var retry = Assert.Single(waits.Snapshot(), wait => wait.After == TimeSpan.FromMilliseconds(1_005));
            Assert.True((await store.StateOfAsync(characterId, CancellationToken.None)).IsCriminal(now));
            Assert.Equal(systemId, (await store.StateOfAsync(characterId, CancellationToken.None)).SolarSystemId);
            retry.Done.TrySetCanceled();
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400 && !condition(); attempt++)
            await Task.Delay(5);
        Assert.True(condition(), "condition was not reached before timeout");
    }
}
