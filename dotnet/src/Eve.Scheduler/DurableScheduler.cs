using System.Text.Json;
using Eve.Data;
using Npgsql;

namespace Eve.Scheduler;

/// <summary>A job handler: runs the job inside <see cref="JobContext.Transaction"/>; throwing means "this try failed" and the job is retried with a back-off.</summary>
public delegate Task JobHandler(JobContext context, CancellationToken cancellationToken);

/// <summary>Knobs of the scheduler; the defaults suit the server, tests shrink them.</summary>
public sealed record SchedulerOptions
{
    /// <summary>How long a claim holds before another runner may take the job over (a handler's row lock holds longer while it runs).</summary>
    public long LeaseMs { get; init; } = 60_000;

    /// <summary>How many due jobs are claimed at once.</summary>
    public int BatchSize { get; init; } = 16;

    /// <summary>How many jobs run at the same time in this process.</summary>
    public int Concurrency { get; init; } = 4;

    /// <summary>The longest the runner sleeps when nothing is due (it wakes sooner for anything scheduled in this process).</summary>
    public int MaxPollMs { get; init; } = 1000;

    /// <summary>A failing job is tried again after <c>BackoffBaseMs * 2^(attempt-1)</c>, at most <see cref="BackoffMaxMs"/>.</summary>
    public long BackoffBaseMs { get; init; } = 1_000;

    public long BackoffMaxMs { get; init; } = 300_000;
}

/// <summary>
/// The one place for "do this later" in the server: a PostgreSQL-backed scheduler and job runner. Features register a handler under a kind and schedule jobs of that kind;
/// they do not own timers. A job is a row, so it survives restarts, and it can be scheduled in the same transaction as the state change it belongs to (the job exists exactly
/// when the change does). A runner claims due jobs with <c>FOR UPDATE SKIP LOCKED</c> and a lease, runs the handler in a transaction that also completes the job, retries a
/// failing handler with a back-off and parks a job that ran out of attempts as <c>failed</c>. A job may recur, be replaced or cancelled by its key, and may ask to run again.
/// See <c>docs/design/FOUNDATIONS.md</c>.
/// </summary>
public sealed class DurableScheduler : IAsyncDisposable
{
    public const string DefaultSchema = "scheduler";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _source;
    private readonly Func<long> _now;
    private readonly SchedulerOptions _options;
    private readonly string _schema;
    private readonly Action<string>? _log;
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, JobHandler> _handlers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _stop;
    private Task? _loop;

    public DurableScheduler(NpgsqlDataSource source, Func<long> now, SchedulerOptions? options = null, string schema = DefaultSchema, Action<string>? log = null)
    {
        _source = source;
        _now = now;
        _options = options ?? new SchedulerOptions();
        _schema = Sql.CheckSchema(schema);
        _log = log;
    }

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(DurableScheduler).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    /// <summary>The scheduler's clock, in milliseconds since the Unix epoch.</summary>
    public long Now => _now();

    private string Q(string text) => Sql.In(_schema, text);

    // ---- handlers ----------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Registers the handler of a kind. A kind has one handler; a process only runs the kinds it has a handler for.</summary>
    public void Register(string kind, JobHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        lock (_gate)
        {
            if (!_handlers.TryAdd(kind, handler))
            {
                throw new InvalidOperationException($"a handler for job kind '{kind}' is already registered");
            }
        }
        Wake();
    }

    public IReadOnlyCollection<string> Kinds
    {
        get
        {
            lock (_gate)
            {
                return [.. _handlers.Keys];
            }
        }
    }

    // ---- scheduling --------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Schedules a job in its own transaction. Returns the id of the job (the existing one when the policy keeps it).</summary>
    public async Task<long> ScheduleAsync(JobSpec spec, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var id = await ScheduleAsync(connection, transaction, spec, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        Wake();
        return id;
    }

    /// <summary>
    /// Schedules a job in the caller's transaction, so that it exists exactly when the caller's change does. Call <see cref="Wake"/> after the commit to have it picked up at
    /// once (otherwise the runner finds it within <see cref="SchedulerOptions.MaxPollMs"/>).
    /// </summary>
    public async Task<long> ScheduleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, JobSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Kind);
        if (spec.MaxAttempts < 1)
        {
            throw new ArgumentException("MaxAttempts must be at least 1", nameof(spec));
        }
        if (spec.EveryMs is <= 0)
        {
            throw new ArgumentException("EveryMs must be positive", nameof(spec));
        }
        var payload = JsonSerializer.Serialize(spec.Payload ?? new { }, Json);
        var now = _now();
        var policy = spec.OnDuplicate.ToString().ToLowerInvariant();
        var id = await Sql.ScalarAsync(connection, transaction, Q("""
            INSERT INTO {schema}.jobs (kind, key, payload, run_at_ms, max_attempts, every_ms, created_ms, updated_ms)
            VALUES ($1, $2, $3::text::jsonb, $4, $5, $6, $7, $7)
            ON CONFLICT (kind, key) WHERE key IS NOT NULL AND state = 'pending' DO UPDATE SET
                run_at_ms    = CASE $8::text WHEN 'replace' THEN EXCLUDED.run_at_ms
                                             WHEN 'earlier' THEN LEAST(jobs.run_at_ms, EXCLUDED.run_at_ms)
                                             WHEN 'later'   THEN GREATEST(jobs.run_at_ms, EXCLUDED.run_at_ms)
                                             ELSE jobs.run_at_ms END,
                payload      = CASE $8::text WHEN 'replace' THEN EXCLUDED.payload
                                             WHEN 'earlier' THEN (CASE WHEN EXCLUDED.run_at_ms < jobs.run_at_ms THEN EXCLUDED.payload ELSE jobs.payload END)
                                             WHEN 'later'   THEN (CASE WHEN EXCLUDED.run_at_ms > jobs.run_at_ms THEN EXCLUDED.payload ELSE jobs.payload END)
                                             ELSE jobs.payload END,
                every_ms     = CASE $8::text WHEN 'keep' THEN jobs.every_ms ELSE EXCLUDED.every_ms END,
                max_attempts = CASE $8::text WHEN 'keep' THEN jobs.max_attempts ELSE EXCLUDED.max_attempts END,
                attempts     = CASE $8::text WHEN 'keep' THEN jobs.attempts ELSE 0 END,
                updated_ms   = EXCLUDED.updated_ms
            RETURNING id
            """), cancellationToken, spec.Kind, spec.Key, payload, spec.RunAtMs, spec.MaxAttempts, spec.EveryMs, now, policy);
        return (long)id!;
    }

    /// <summary>Makes sure a recurring job exists (a no-op when it already does): call it at start-up for every periodic task.</summary>
    public Task<long> EnsureRecurringAsync(string kind, string key, TimeSpan every, object? payload = null, CancellationToken cancellationToken = default) =>
        ScheduleAsync(new JobSpec(kind, _now() + (long)every.TotalMilliseconds)
        {
            Key = key, Payload = payload, EveryMs = (long)every.TotalMilliseconds, OnDuplicate = OnDuplicate.Keep,
        }, cancellationToken);

    /// <summary>Cancels the waiting job of a kind and key. Returns whether there was one. A job that is running right now finishes.</summary>
    public async Task<bool> CancelAsync(string kind, string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        return await CancelAsync(connection, null, kind, key, cancellationToken);
    }

    /// <summary>Cancels in the caller's transaction (the cancel and the change it belongs to stand or fall together).</summary>
    public async Task<bool> CancelAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string kind, string key, CancellationToken cancellationToken = default) =>
        await Sql.ExecuteAsync(connection, transaction, Q("DELETE FROM {schema}.jobs WHERE kind = $1 AND key = $2 AND state = 'pending'"), cancellationToken, kind, key) > 0;

    // ---- looking ------------------------------------------------------------------------------------------------------------------------------------

    private const string Columns = "id, kind, key, payload::text, run_at_ms, state, attempts, max_attempts, every_ms, last_error";

    private static JobInfo Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetString(5), r.GetInt32(6), r.GetInt32(7),
        r.IsDBNull(8) ? null : r.GetInt64(8), r.IsDBNull(9) ? null : r.GetString(9));

    /// <summary>The job of a kind and key that is waiting or running, if there is one.</summary>
    public async Task<JobInfo?> FindAsync(string kind, string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(connection, null, Q($"SELECT {Columns} FROM {{schema}}.jobs WHERE kind = $1 AND key = $2 AND state <> 'failed' ORDER BY state DESC, id LIMIT 1"), kind, key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    /// <summary>Jobs (optionally of one kind and state), oldest due first; for tools and tests.</summary>
    public async Task<IReadOnlyList<JobInfo>> ListAsync(string? kind = null, string? state = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(connection, null,
            Q($"SELECT {Columns} FROM {{schema}}.jobs WHERE ($1::text IS NULL OR kind = $1) AND ($2::text IS NULL OR state = $2) ORDER BY run_at_ms, id LIMIT $3"), kind, state, limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<JobInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }
        return list;
    }

    /// <summary>Puts a failed job back to waiting, due now, with its attempts reset.</summary>
    public async Task<bool> RetryAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var changed = await Sql.ExecuteAsync(connection, null, Q("""
            UPDATE {schema}.jobs SET state = 'pending', attempts = 0, run_at_ms = $2, updated_ms = $2
            WHERE id = $1 AND state = 'failed'
              AND (key IS NULL OR NOT EXISTS (SELECT 1 FROM {schema}.jobs o WHERE o.kind = jobs.kind AND o.key = jobs.key AND o.state = 'pending'))
            """), cancellationToken, id, _now()) > 0;
        Wake();
        return changed;
    }

    // ---- running -----------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs every job that is due now (and every job those jobs make due) until nothing is, at most <paramref name="maxJobs"/>. Returns how many ran. The runner loop calls this;
    /// tests call it directly with a clock they control.
    /// </summary>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken = default, int maxJobs = 1000)
    {
        var total = 0;
        while (total < maxJobs)
        {
            var batch = await ClaimAsync(Math.Min(_options.BatchSize, maxJobs - total), cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }
            await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.Concurrency), CancellationToken = cancellationToken },
                async (job, token) => await ExecuteAsync(job, token));
            total += batch.Count;
        }
        return total;
    }

    private async Task<List<ClaimedJob>> ClaimAsync(int limit, CancellationToken cancellationToken)
    {
        string[] kinds;
        lock (_gate)
        {
            kinds = [.. _handlers.Keys];
        }
        var claimed = new List<ClaimedJob>();
        if (kinds.Length == 0)
        {
            return claimed;
        }
        var now = _now();
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // a claim that lapsed after the last allowed attempt is not tried again: the process that held it died each time
        await Sql.ExecuteAsync(connection, transaction, Q("""
            UPDATE {schema}.jobs SET state = 'failed', last_error = 'the lease lapsed on every attempt', lease_owner = NULL, lease_until_ms = NULL, updated_ms = $1
            WHERE state = 'running' AND lease_until_ms < $1 AND attempts >= max_attempts AND kind = ANY($2)
            """), cancellationToken, now, kinds);
        await using (var command = Sql.Command(connection, transaction, Q("""
            WITH due AS (
                SELECT id FROM {schema}.jobs
                WHERE kind = ANY($5) AND ((state = 'pending' AND run_at_ms <= $1) OR (state = 'running' AND lease_until_ms < $1))
                ORDER BY run_at_ms, id
                LIMIT $2
                FOR UPDATE SKIP LOCKED)
            UPDATE {schema}.jobs j SET state = 'running', attempts = j.attempts + 1, lease_owner = $3, lease_until_ms = $4, updated_ms = $1
            FROM due WHERE j.id = due.id
            RETURNING j.id, j.kind, j.key, j.payload::text, j.run_at_ms, j.attempts, j.max_attempts, j.every_ms
            """), now, limit, _owner, now + _options.LeaseMs, kinds))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                claimed.Add(new ClaimedJob(
                    reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
                    reader.GetInt32(5), reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetInt64(7)));
            }
        }
        await transaction.CommitAsync(cancellationToken);
        claimed.Sort((a, b) => a.RunAtMs != b.RunAtMs ? a.RunAtMs.CompareTo(b.RunAtMs) : a.Id.CompareTo(b.Id));
        return claimed;
    }

    private async Task ExecuteAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        JobHandler? handler;
        lock (_gate)
        {
            _handlers.TryGetValue(job.Kind, out handler);
        }
        await using var connection = await _source.OpenConnectionAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
        // the row lock keeps every other runner away while the handler runs, and tells us if the claim was taken over after a lapse
        var mine = await Sql.ScalarAsync(connection, transaction,
            Q("SELECT 1 FROM {schema}.jobs WHERE id = $1 AND state = 'running' AND lease_owner = $2 FOR UPDATE"), CancellationToken.None, job.Id, _owner);
        if (mine is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return;
        }
        var context = new JobContext(this, job, connection, transaction, _now());
        try
        {
            if (handler is null)
            {
                throw new InvalidOperationException($"no handler for job kind '{job.Kind}'");
            }
            await handler(context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shutting down: give the job back as if it had not been tried
            await transaction.RollbackAsync(CancellationToken.None);
            await ReleaseAsync(job, "pending", job.RunAtMs, null, Math.Max(0, job.Attempts - 1), null);
            return;
        }
        catch (Exception error)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _log?.Invoke($"scheduler: job {job.Id} ({job.Kind}{(job.Key is null ? "" : ":" + job.Key)}) failed on try {job.Attempts}/{job.MaxAttempts}: {error.Message}");
            var message = error.Message.Length > 2000 ? error.Message[..2000] : error.Message;
            var exhausted = job.Attempts >= job.MaxAttempts;
            if (exhausted && job.EveryMs is { } period)
            {
                // a periodic task is never parked: it gives up on this run and tries again at its next time
                await ReleaseAsync(job, "pending", NextOccurrence(job.RunAtMs, period, _now()), null, 0, message);
                return;
            }
            var backoff = Math.Min(_options.BackoffMaxMs, _options.BackoffBaseMs << Math.Min(job.Attempts - 1, 20));
            await ReleaseAsync(job, exhausted ? "failed" : "pending", exhausted ? job.RunAtMs : _now() + backoff, null, job.Attempts, message);
            return;
        }

        if (context.RunAgain)
        {
            await FinishAsync(context, job, "pending", context.RunAgainAtMs, context.RunAgainPayloadJson, 0);
        }
        else if (job.EveryMs is { } every)
        {
            await FinishAsync(context, job, "pending", NextOccurrence(job.RunAtMs, every, _now()), null, 0);
        }
        else
        {
            await Sql.ExecuteAsync(connection, transaction, Q("DELETE FROM {schema}.jobs WHERE id = $1"), CancellationToken.None, job.Id);
        }
        await transaction.CommitAsync(CancellationToken.None);
    }

    /// <summary>The first time after <paramref name="now"/> that a job due at <paramref name="dueMs"/> and recurring every <paramref name="everyMs"/> is due (the runs that were missed are skipped).</summary>
    private static long NextOccurrence(long dueMs, long everyMs, long now) => dueMs + (Math.Max(now - dueMs, 0) / everyMs + 1) * everyMs;

    /// <summary>Writes the job's next state in the handler's own transaction.</summary>
    private async Task FinishAsync(JobContext context, ClaimedJob job, string state, long runAtMs, string? payloadJson, int attempts) =>
        await ReleaseAsync(context.Connection, context.Transaction, job, state, runAtMs, payloadJson, attempts, null);

    private async Task ReleaseAsync(ClaimedJob job, string state, long runAtMs, string? payloadJson, int attempts, string? error)
    {
        await using var connection = await _source.OpenConnectionAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
        await ReleaseAsync(connection, transaction, job, state, runAtMs, payloadJson, attempts, error);
        await transaction.CommitAsync(CancellationToken.None);
    }

    /// <summary>
    /// Ends this claim: the job becomes waiting (or failed) with the given time, payload and count. If another job of the same kind and key has been scheduled meanwhile it is the
    /// one that stays: this one is dropped (a job that is replaced while it runs is superseded by the replacement).
    /// </summary>
    private async Task ReleaseAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ClaimedJob job, string state, long runAtMs, string? payloadJson, int attempts, string? error)
    {
        var changed = await Sql.ExecuteAsync(connection, transaction, Q("""
            UPDATE {schema}.jobs SET state = $3, run_at_ms = $4, payload = COALESCE($5::text::jsonb, payload), attempts = $6, last_error = $7,
                                     lease_owner = NULL, lease_until_ms = NULL, updated_ms = $8
            WHERE id = $1 AND lease_owner = $2 AND state = 'running'
              AND ($3::text = 'failed' OR key IS NULL OR NOT EXISTS (SELECT 1 FROM {schema}.jobs o WHERE o.kind = jobs.kind AND o.key = jobs.key AND o.state = 'pending'))
            """), CancellationToken.None, job.Id, _owner, state, runAtMs, payloadJson, attempts, error, _now());
        if (changed == 0)
        {
            await Sql.ExecuteAsync(connection, transaction, Q("DELETE FROM {schema}.jobs WHERE id = $1 AND lease_owner = $2 AND state = 'running'"), CancellationToken.None, job.Id, _owner);
        }
    }

    // ---- the runner loop ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>Lets the runner look for work now instead of at its next poll. Cheap; safe to call from anywhere.</summary>
    public void Wake() => _wake.TrySetResult();

    /// <summary>Starts the background runner. Register the handlers first (a kind without a handler is left alone).</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return;
            }
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _loop = Task.Run(() => LoopAsync(token));
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            loop = _loop;
            _loop = null;
            _stop?.Cancel();
        }
        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (await RunDueAsync(token) > 0)
                {
                    continue;
                }
                var delay = await NextDelayAsync(token);
                var wake = _wake;
                await Task.WhenAny(wake.Task, Task.Delay(delay, token));
                if (wake.Task.IsCompleted)
                {
                    Interlocked.CompareExchange(ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), wake);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                _log?.Invoke($"scheduler: {error.Message}");
                try
                {
                    await Task.Delay(1000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<int> NextDelayAsync(CancellationToken cancellationToken)
    {
        string[] kinds;
        lock (_gate)
        {
            kinds = [.. _handlers.Keys];
        }
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var next = await Sql.ScalarAsync(connection, null, Q("""
            SELECT LEAST(
                (SELECT min(run_at_ms) FROM {schema}.jobs WHERE state = 'pending' AND kind = ANY($1)),
                (SELECT min(lease_until_ms) + 1 FROM {schema}.jobs WHERE state = 'running' AND kind = ANY($1)))
            """), cancellationToken, (object?)kinds);   // one array argument: without the cast it would be taken for the whole parameter list
        if (next is not long due)
        {
            return _options.MaxPollMs;
        }
        return (int)Math.Clamp(due - _now(), 5, _options.MaxPollMs);
    }
}
