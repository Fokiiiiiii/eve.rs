using System.Text.Json;
using Npgsql;

namespace Eve.Scheduler;

/// <summary>
/// What a job handler gets. <see cref="Connection"/> and <see cref="Transaction"/> are the job's own transaction: what the handler writes through them is committed together
/// with the job's completion, so a handler that only writes to PostgreSQL runs exactly once however often the process dies. A handler that also touches something outside
/// PostgreSQL (the simulation, a socket) runs at least once, so it must be safe to run twice.
/// </summary>
public sealed class JobContext
{
    private readonly DurableScheduler _scheduler;

    internal JobContext(DurableScheduler scheduler, ClaimedJob job, NpgsqlConnection connection, NpgsqlTransaction transaction, long nowMs)
    {
        _scheduler = scheduler;
        Id = job.Id;
        Kind = job.Kind;
        Key = job.Key;
        PayloadJson = job.PayloadJson;
        ScheduledForMs = job.RunAtMs;
        Attempt = job.Attempts;
        MaxAttempts = job.MaxAttempts;
        NowMs = nowMs;
        Connection = connection;
        Transaction = transaction;
    }

    public long Id { get; }
    public string Kind { get; }
    public string? Key { get; }
    public string PayloadJson { get; }

    /// <summary>When the job was due (it may run later than that when the server was busy or down).</summary>
    public long ScheduledForMs { get; }

    /// <summary>1 for the first try.</summary>
    public int Attempt { get; }
    public int MaxAttempts { get; }

    /// <summary>The scheduler's clock when the job started.</summary>
    public long NowMs { get; }

    public NpgsqlConnection Connection { get; }
    public NpgsqlTransaction Transaction { get; }

    internal bool RunAgain { get; private set; }
    internal long RunAgainAtMs { get; private set; }
    internal string? RunAgainPayloadJson { get; private set; }

    public T? Payload<T>() => JsonSerializer.Deserialize<T>(PayloadJson, DurableScheduler.Json);

    /// <summary>
    /// Do not finish: run this job again at <paramref name="atMs"/>, with a new payload when one is given (a state machine whose next step is a time, such as "the
    /// protection ends in 5 minutes"). The attempt count starts again.
    /// </summary>
    public void RunAgainAt(long atMs, object? payload = null)
    {
        RunAgain = true;
        RunAgainAtMs = atMs;
        RunAgainPayloadJson = payload is null ? null : JsonSerializer.Serialize(payload, DurableScheduler.Json);
    }

    /// <summary>Schedules another job in this job's transaction.</summary>
    public Task<long> ScheduleAsync(JobSpec spec, CancellationToken cancellationToken = default) =>
        _scheduler.ScheduleAsync(Connection, Transaction, spec, cancellationToken);
}

internal sealed record ClaimedJob(long Id, string Kind, string? Key, string PayloadJson, long RunAtMs, int Attempts, int MaxAttempts, long? EveryMs);
