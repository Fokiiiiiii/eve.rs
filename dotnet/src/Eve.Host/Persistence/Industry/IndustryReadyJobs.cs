using System.Globalization;
using Eve.Events;
using Eve.Scheduler;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// The readiness of installed industry jobs (EVE.js industryRuntimeState.js: a durable job per installed job, due at its end
/// date, that moves it to ready and publishes the change). The job is scheduled in the install transaction; its handler is
/// idempotent (a conditional update), so a retry or a restart changes nothing twice. A recurring sweep readies every installed
/// job whose end has passed and re-registers the ones still waiting, which covers a job whose durable entry was lost.
/// Client status 3 is READY (industry/const.py STATUS_READY); the client's OnIndustryJob is sent by the change subscriber.
/// </summary>
public sealed class IndustryReadyJobs(NpgsqlDataSource source, string schema, EventBus events, DurableScheduler scheduler, Func<long> nowMs)
{
    public const string ReadyKind = "industry.job.ready";
    public const string SweepKind = "industry.job.sweep";
    public const int StatusInstalled = 1;
    public const int StatusReady = 3;
    private const long FileTimeEpochOffset = 116444736000000000L;

    public sealed record ReadyPayload(long JobId);

    public static string KeyFor(long jobId) => "job:" + jobId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The unix milliseconds of a FILETIME (the jobs' end dates are FILETIMEs).</summary>
    public static long UnixMsOfFileTime(long fileTime) => (fileTime - FileTimeEpochOffset) / 10_000;

    private static long FileTimeOf(long unixMs) => FileTimeEpochOffset + unixMs * 10_000L;

    /// <summary>Registers the two handlers with the scheduler; call once before the scheduler runs.</summary>
    public void Register()
    {
        scheduler.Register(ReadyKind, async (context, token) =>
        {
            var payload = context.Payload<ReadyPayload>() ?? throw new InvalidOperationException("the industry readiness job has no payload");
            await MarkReadyAsync(payload.JobId, token);
        });
        scheduler.Register(SweepKind, async (_, token) => await SweepAsync(token));
    }

    /// <summary>Starts the recurring sweep (the first run is due at once, so a restart readies overdue jobs).</summary>
    public async Task StartAsync() =>
        await scheduler.EnsureRecurringAsync(SweepKind, "industry-jobs", TimeSpan.FromMinutes(1));

    /// <summary>Within the install transaction: the job becomes ready at its end date. Replaces an earlier entry for the same job.</summary>
    public async Task ScheduleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long jobId, long endFileTime, CancellationToken token) =>
        await scheduler.ScheduleAsync(connection, transaction, new JobSpec(ReadyKind, UnixMsOfFileTime(endFileTime))
        {
            Key = KeyFor(jobId),
            Payload = new ReadyPayload(jobId),
        }, token);

    /// <summary>
    /// Moves one installed job whose end has passed to ready and publishes the change in the same transaction. Returns false
    /// when the job is not installed, its end has not passed, or it is unknown (a repeat or a late handler changes nothing).
    /// </summary>
    public async Task<bool> MarkReadyAsync(long jobId, CancellationToken token)
    {
        var now = FileTimeOf(nowMs());
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        long ownerId = 0, blueprintId = 0, installerId = 0, successfulRuns = 0;
        bool found;
        await using (var update = new NpgsqlCommand($"""
            UPDATE {schema}.jobs SET status = {StatusReady}
             WHERE job_id = $1 AND status = {StatusInstalled} AND end_date_filetime <= $2
            RETURNING owner_id, blueprint_item_id, installer_id, successful_runs
            """, connection, transaction))
        {
            update.Parameters.Add(new NpgsqlParameter { Value = jobId });
            update.Parameters.Add(new NpgsqlParameter { Value = now });
            await using var reader = await update.ExecuteReaderAsync(token);
            found = await reader.ReadAsync(token);
            if (found)
            {
                ownerId = reader.GetInt64(0);
                blueprintId = reader.GetInt64(1);
                installerId = reader.GetInt64(2);
                successfulRuns = reader.GetInt64(3);
            }
        }
        if (!found)
        {
            await transaction.CommitAsync(token);
            return false;
        }
        await events.PublishAsync(connection, transaction, IndustryJobEvents.Changed,
            new IndustryJobChangedEvent(jobId, ownerId, blueprintId, installerId, StatusReady, successfulRuns, "industry-ready:" + jobId),
            "industry-ready:" + jobId, token);
        await transaction.CommitAsync(token);
        return true;
    }

    /// <summary>
    /// The startup sweep: every installed job whose end has passed is readied; the others keep their durable entry (registered
    /// again when missing). Returns the number readied.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken token)
    {
        var now = FileTimeOf(nowMs());
        var installed = new List<(long JobId, long EndFileTime)>();
        await using (var connection = await source.OpenConnectionAsync(token))
        await using (var command = new NpgsqlCommand($"SELECT job_id, end_date_filetime FROM {schema}.jobs WHERE status = {StatusInstalled}", connection))
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                installed.Add((reader.GetInt64(0), reader.GetInt64(1)));

        var readied = 0;
        foreach (var (jobId, endFileTime) in installed)
        {
            if (endFileTime <= now)
            {
                if (await MarkReadyAsync(jobId, token))
                    readied++;
                continue;
            }
            await scheduler.ScheduleAsync(new JobSpec(ReadyKind, UnixMsOfFileTime(endFileTime))
            {
                Key = KeyFor(jobId),
                Payload = new ReadyPayload(jobId),
                OnDuplicate = OnDuplicate.Keep,
            }, token);
        }
        return readied;
    }
}
