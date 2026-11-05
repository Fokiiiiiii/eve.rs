using Eve.Marshal;
using Eve.Npc;
using Eve.Scheduler;

namespace Eve.Services.Space;

/// <summary>The durable daily DBS update and its post-commit client notification.</summary>
public static class DynamicBountyJobs
{
    private const string ProcessKind = "space.dynamic-bounty.process";
    private const string NotifyKind = "space.dynamic-bounty.notify";

    public static void Register(
        DurableScheduler scheduler,
        PgDynamicBountyStore store,
        Func<int, IEnumerable<ClientSession>> sessionsInSystem,
        Action<string> log)
    {
        scheduler.Register(ProcessKind, async (context, token) =>
        {
            var systemId = context.Payload<int>();
            var update = await store.AdvanceAsync(context.Connection, context.Transaction, systemId, context.NowMs, token);
            if (update is not null)
            {
                await context.ScheduleAsync(new JobSpec(NotifyKind, context.NowMs)
                {
                    Key = $"{systemId}:{update.PeriodIndex}",
                    Payload = new DynamicBountyMapEntry(systemId, update.Output),
                }, token);
            }
        });
        scheduler.Register(NotifyKind, (context, _) =>
        {
            var update = context.Payload<DynamicBountyMapEntry>()
                ?? throw new InvalidDataException("A DBS notification job has no update.");
            foreach (var session in sessionsInSystem(update.SolarSystemId))
            {
                try
                {
                    session.SendNotification(
                        "OnSystemDynamicBountyStateChanged", "clientID",
                        new PyInt(update.SolarSystemId), new PyReal(update.Output));
                }
                catch (Exception error)
                {
                    log($"DBS notification for system {update.SolarSystemId} failed: {error.Message}");
                }
            }
            return Task.CompletedTask;
        });
    }

    public static async Task ScheduleProcessAsync(
        DurableScheduler scheduler,
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        int systemId,
        long nextAtMs,
        CancellationToken cancellationToken) =>
        _ = await scheduler.ScheduleAsync(connection, transaction, new JobSpec(ProcessKind, nextAtMs)
        {
            Key = $"system:{systemId}",
            Payload = systemId,
            EveryMs = DynamicBountyRules.TimeIntervalMs,
        }, cancellationToken);
}
