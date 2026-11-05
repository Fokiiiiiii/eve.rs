namespace Eve.Scheduler;

/// <summary>What to do when a job with the same kind and key is already waiting.</summary>
public enum OnDuplicate
{
    /// <summary>The new time and payload replace the waiting job's (a timer that is restarted). The default.</summary>
    Replace,
    /// <summary>The waiting job stays as it is (idempotent registration: "make sure this is scheduled").</summary>
    Keep,
    /// <summary>The earlier of the two times wins, together with its payload (a timer that may only be pulled in).</summary>
    Earlier,
    /// <summary>The later of the two times wins, together with its payload (a timer that may only be pushed out, a debounce).</summary>
    Later,
}

/// <summary>A job to schedule: the handler's kind, when it is due, and optionally a key, a payload, a recurrence and a retry limit.</summary>
public sealed record JobSpec(string Kind, long RunAtMs)
{
    /// <summary>What the job is about; at most one waiting job exists per (kind, key). Leave it out for a job that is never replaced or cancelled.</summary>
    public string? Key { get; init; }

    /// <summary>Anything System.Text.Json can write; the handler reads it back with <see cref="JobContext.Payload{T}"/>.</summary>
    public object? Payload { get; init; }

    /// <summary>Runs again this many milliseconds after the time it was due, for ever (until cancelled).</summary>
    public long? EveryMs { get; init; }

    /// <summary>How many times a failing handler is tried before the job is parked as failed.</summary>
    public int MaxAttempts { get; init; } = 5;

    public OnDuplicate OnDuplicate { get; init; } = OnDuplicate.Replace;
}

/// <summary>A job as it is stored; for tests, tools and the admin views.</summary>
public sealed record JobInfo(
    long Id, string Kind, string? Key, string PayloadJson, long RunAtMs, string State, int Attempts, int MaxAttempts, long? EveryMs, string? LastError);
