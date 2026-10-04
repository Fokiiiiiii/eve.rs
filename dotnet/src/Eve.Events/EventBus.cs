using System.Text.Json;
using Eve.Data;
using Eve.Scheduler;
using Npgsql;

namespace Eve.Events;

/// <summary>
/// The server's domain events, with a transactional outbox. <see cref="PublishAsync(NpgsqlConnection, NpgsqlTransaction, string, object?, string?, CancellationToken)"/> writes the event
/// into the caller's transaction, so the event exists exactly when the change it announces does. The bus's own pump loop (started by <see cref="StartAsync"/>) delivers each event, in order, to every subscriber whose topics match; each subscriber has a cursor, and a handler's writes are committed together with its cursor, so a
/// PostgreSQL-only handler sees each event exactly once and any other handler at least once. A failing handler is retried with a back-off and, after its attempts, the event
/// is set aside as a dead letter and the subscriber goes on. Features subscribe instead of calling each other, and publish instead of keeping their own event lists.
/// See <c>docs/FOUNDATIONS.md</c>.
/// </summary>
/// <remarks>
/// Visibility: row ids are handed out in the order publishers ask for them but become visible in the order they commit, so a cursor over ids alone could skip a row that commits
/// late. Publishing therefore takes a shared advisory lock (held until the publisher commits); a pump takes it exclusively for a moment (up to 100 ms), reads the highest id, and
/// delivers only up to that horizon: at that moment no publisher was open, so everything up to it is final. A pump that cannot get the lock in time tries again shortly. Only
/// publishers of this bus are waited for (unlike a database-wide snapshot horizon, a long transaction elsewhere delays nothing); keep a publishing transaction short.
/// </remarks>
public sealed class EventBus
{
    public const string DefaultSchema = "events";
    public const string PurgeKind = "events.purge";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Subscription(string Name, string[] Exact, string[] Prefixes, DomainEventHandler Handler, StartAt StartAt);

    private readonly NpgsqlDataSource _source;
    private readonly DurableScheduler _scheduler;
    private readonly EventBusOptions _options;
    private readonly string _schema;
    private readonly Action<string>? _log;
    private readonly Dictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public EventBus(NpgsqlDataSource source, DurableScheduler scheduler, EventBusOptions? options = null, string schema = DefaultSchema, Action<string>? log = null)
    {
        _source = source;
        _scheduler = scheduler;
        _options = options ?? new EventBusOptions();
        _schema = Sql.CheckSchema(schema);
        _log = log;
        scheduler.Register(PurgeKind, async (_, token) => await PurgeAsync(token));
    }

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(EventBus).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    private string Q(string text) => Sql.In(_schema, text);

    /// <summary>The advisory lock of this bus: publishers share it until they commit, a pump takes it exclusively to find the horizon.</summary>
    private string LockName => "eve.events." + _schema;
    private long Now => _scheduler.Now;

    // ---- subscribing -------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Subscribes <paramref name="handler"/> under a name that never changes (the name is the cursor's identity). A topic is an exact name (<c>npc.killed</c>), a prefix ending in a
    /// star (<c>npc.*</c>) or a bare <c>*</c> for everything.
    /// </summary>
    public void Subscribe(string name, IEnumerable<string> topics, DomainEventHandler handler, StartAt startAt = StartAt.Now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var list = topics.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("a subscriber needs at least one topic", nameof(topics));
        }
        var exact = list.Where(t => !t.EndsWith('*')).ToArray();
        var prefixes = list.Where(t => t.EndsWith('*')).Select(t => t[..^1]).ToArray();
        lock (_gate)
        {
            if (!_subscriptions.TryAdd(name, new Subscription(name, exact, prefixes, handler, startAt)))
            {
                throw new InvalidOperationException($"a subscriber named '{name}' is already registered");
            }
        }
        _scheduler.Wake();
    }

    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _stop;
    private Task? _loop;

    /// <summary>
    /// Starts delivering: makes sure the housekeeping job exists and starts the pump loop, which delivers what is waiting, again at once when something was delivered, and otherwise when
    /// woken or after <see cref="EventBusOptions.PollMs"/>. Call it once at start-up, after the subscribers are registered.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _scheduler.EnsureRecurringAsync(PurgeKind, "purge", TimeSpan.FromHours(1), cancellationToken: cancellationToken);
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

    /// <summary>Lets the pump loop look now instead of at its next poll (a publisher that used its own transaction should call it after the commit; <see cref="PublishAsync(string, object?, string?, CancellationToken)"/> does).</summary>
    public void Wake() => _wake.TrySetResult();

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
            await loop;
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var delivered = 0;
            long? retryAt = null;
            try
            {
                var result = await PumpAsync(token);
                delivered = result.Delivered;
                retryAt = result.RetryAtMs;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                _log?.Invoke($"events: the pump failed: {error.Message}");
            }
            if (delivered > 0)
            {
                continue;   // more may be waiting
            }
            var delay = retryAt is { } at ? Math.Clamp(at - Now, 5, _options.PollMs) : _options.PollMs;
            var wake = _wake;
            try
            {
                await Task.WhenAny(wake.Task, Task.Delay((int)delay, token));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (wake.Task.IsCompleted)
            {
                Interlocked.CompareExchange(ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), wake);
            }
        }
    }

    // ---- publishing --------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Publishes in its own transaction. Returns the event's id.</summary>
    public async Task<long> PublishAsync(string topic, object? payload, string? key = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var id = await PublishAsync(connection, transaction, topic, payload, key, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        Wake();
        return id;
    }

    /// <summary>
    /// Publishes in the caller's transaction: the event is written with the change it announces. Call <see cref="Wake"/> after the commit to have it delivered at once (otherwise within
    /// <see cref="EventBusOptions.PollMs"/>).
    /// </summary>
    public async Task<long> PublishAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string topic, object? payload, string? key = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        var json = JsonSerializer.Serialize(payload ?? new { }, Json);
        // until this transaction ends, a pump does not deliver past the rows it is writing
        await Sql.ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock_shared(hashtext($1))", cancellationToken, LockName);
        var id = (long)(await Sql.ScalarAsync(connection, transaction,
            Q("INSERT INTO {schema}.outbox (topic, key, payload, occurred_ms) VALUES ($1, $2, $3::text::jsonb, $4) RETURNING id"), cancellationToken, topic, key, json, Now))!;
        return id;
    }

    // ---- delivering --------------------------------------------------------------------------------------------------------------------------------

    /// <summary>What one pump did: how many events it delivered, and when to look again if some subscriber is waiting out a back-off.</summary>
    public sealed record PumpResult(int Delivered, long? RetryAtMs);

    /// <summary>
    /// Delivers what is waiting to every subscriber. The pump loop runs this; tests call it directly (with the loop not started).
    /// </summary>
    public async Task<PumpResult> PumpAsync(CancellationToken cancellationToken = default)
    {
        Subscription[] subscriptions;
        lock (_gate)
        {
            subscriptions = [.. _subscriptions.Values];
        }
        if (subscriptions.Length == 0)
        {
            return new PumpResult(0, null);
        }
        if (await HorizonAsync(cancellationToken) is not { } horizon)
        {
            return new PumpResult(0, Now + RetryHorizonMs);   // a publisher is still open: look again shortly
        }
        var delivered = 0;
        long? retryAt = null;
        foreach (var subscription in subscriptions)
        {
            var (count, retry) = await PumpSubscriberAsync(subscription, horizon, cancellationToken);
            delivered += count;
            if (retry is { } at)
            {
                retryAt = retryAt is { } current ? Math.Min(current, at) : at;
            }
        }
        return new PumpResult(delivered, retryAt);
    }

    private const long RetryHorizonMs = 50;

    /// <summary>The highest event id such that every event up to it is final, or <c>null</c> when a publisher is still open and the lock could not be had in 100 ms.</summary>
    private async Task<long?> HorizonAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await Sql.ExecuteAsync(connection, transaction, "SET LOCAL lock_timeout = '100ms'", cancellationToken);
        try
        {
            await Sql.ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtext($1))", cancellationToken, LockName);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return null;
        }
        var horizon = (long)(await Sql.ScalarAsync(connection, transaction, Q("SELECT COALESCE(max(id), 0) FROM {schema}.outbox"), cancellationToken))!;
        await transaction.CommitAsync(cancellationToken);
        return horizon;
    }

    private async Task EnsureSubscriberAsync(Subscription subscription, long horizon, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        // a new subscriber starts at the horizon (events published from now on) or at the beginning; every event up to the horizon is final, so none is half-seen
        await Sql.ExecuteAsync(connection, null,
            Q("INSERT INTO {schema}.subscribers (name, cursor_id, updated_ms) VALUES ($1, $2, $3) ON CONFLICT (name) DO NOTHING"), cancellationToken,
            subscription.Name, subscription.StartAt == StartAt.Beginning ? 0L : horizon, Now);
    }

    private async Task<(int Delivered, long? RetryAtMs)> PumpSubscriberAsync(Subscription subscription, long horizon, CancellationToken cancellationToken)
    {
        await EnsureSubscriberAsync(subscription, horizon, cancellationToken);
        var delivered = 0;
        while (delivered < _options.BatchPerPump && !cancellationToken.IsCancellationRequested)
        {
            await using var connection = await _source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            // the cursor row is the subscriber's lock: another runner that has it is already delivering to this subscriber
            long cursorId;
            long? retryAt;
            await using (var command = Sql.Command(connection, transaction,
                Q("SELECT cursor_id, retry_at_ms FROM {schema}.subscribers WHERE name = $1 FOR UPDATE SKIP LOCKED"), subscription.Name))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    return (delivered, null);
                }
                cursorId = reader.GetInt64(0);
                retryAt = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            }
            if (retryAt is { } wait && wait > Now)
            {
                return (delivered, wait);
            }

            DomainEvent? next = null;
            await using (var command = Sql.Command(connection, transaction, Q("""
                SELECT id, topic, key, payload::text, occurred_ms FROM {schema}.outbox
                WHERE id > $1 AND id <= $2
                  AND (topic = ANY($3) OR EXISTS (SELECT 1 FROM unnest($4::text[]) p WHERE starts_with(topic, p)))
                ORDER BY id LIMIT 1
                """), cursorId, horizon, subscription.Exact, subscription.Prefixes))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    next = new DomainEvent(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetInt64(4));
                }
            }
            if (next is null)
            {
                // nothing for this subscriber: move the cursor over what it does not care about, so that it is not looked at again
                await Sql.ExecuteAsync(connection, transaction,
                    Q("UPDATE {schema}.subscribers SET cursor_id = $2, updated_ms = $3 WHERE name = $1 AND cursor_id < $2"), cancellationToken, subscription.Name, horizon, Now);
                await transaction.CommitAsync(cancellationToken);
                return (delivered, null);
            }

            try
            {
                await subscription.Handler(new EventContext(this, next, connection, transaction), cancellationToken);
                await Sql.ExecuteAsync(connection, transaction, Q("""
                    UPDATE {schema}.subscribers SET cursor_id = $2, failures = 0, retry_at_ms = NULL, last_error = NULL, updated_ms = $3 WHERE name = $1
                    """), cancellationToken, subscription.Name, next.Id, Now);
                await transaction.CommitAsync(cancellationToken);
                delivered++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return (delivered, null);
            }
            catch (Exception error)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                var outcome = await RecordFailureAsync(subscription, next, error);
                if (outcome is not null)
                {
                    return (delivered, outcome);
                }
                // dead-lettered: the subscriber goes on with the next event
            }
        }
        return (delivered, null);
    }

    /// <summary>Counts a failed try. Returns when to try again, or <c>null</c> when the event was given up and skipped.</summary>
    private async Task<long?> RecordFailureAsync(Subscription subscription, DomainEvent @event, Exception error)
    {
        var message = error.Message.Length > 2000 ? error.Message[..2000] : error.Message;
        await using var connection = await _source.OpenConnectionAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
        var failures = (int)(await Sql.ScalarAsync(connection, transaction, Q("""
            UPDATE {schema}.subscribers SET failures = failures + 1, last_error = $2, updated_ms = $3 WHERE name = $1 RETURNING failures
            """), CancellationToken.None, subscription.Name, message, Now))!;
        long? retryAt = null;
        if (failures >= _options.MaxAttempts)
        {
            await Sql.ExecuteAsync(connection, transaction, Q("""
                INSERT INTO {schema}.dead_letters (subscriber, event_id, topic, key, payload, occurred_ms, error, failed_ms)
                VALUES ($1, $2, $3, $4, $5::text::jsonb, $6, $7, $8)
                """), CancellationToken.None, subscription.Name, @event.Id, @event.Topic, @event.Key, @event.PayloadJson, @event.OccurredMs, message, Now);
            await Sql.ExecuteAsync(connection, transaction, Q("""
                UPDATE {schema}.subscribers SET cursor_id = $2, failures = 0, retry_at_ms = NULL WHERE name = $1
                """), CancellationToken.None, subscription.Name, @event.Id);
            _log?.Invoke($"events: {subscription.Name} gave up on event {@event.Id} ({@event.Topic}) after {failures} tries: {message}");
        }
        else
        {
            retryAt = Now + Math.Min(_options.BackoffMaxMs, _options.BackoffBaseMs << Math.Min(failures - 1, 20));
            await Sql.ExecuteAsync(connection, transaction, Q("UPDATE {schema}.subscribers SET retry_at_ms = $2 WHERE name = $1"), CancellationToken.None, subscription.Name, retryAt);
            _log?.Invoke($"events: {subscription.Name} failed on event {@event.Id} ({@event.Topic}), try {failures}/{_options.MaxAttempts}: {message}");
        }
        await transaction.CommitAsync(CancellationToken.None);
        return retryAt;
    }

    // ---- housekeeping and looking ----------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Deletes the events every subscriber has read and that are older than the retention, and the events older than the hard retention whoever has read them. Returns how many.
    /// </summary>
    public async Task<int> PurgeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        return await Sql.ExecuteAsync(connection, null, Q("""
            DELETE FROM {schema}.outbox o
            WHERE o.occurred_ms < $2
               OR (o.occurred_ms < $1 AND NOT EXISTS (SELECT 1 FROM {schema}.subscribers s WHERE s.cursor_id < o.id))
            """), cancellationToken, Now - _options.RetentionMs, Now - _options.HardRetentionMs);
    }

    public async Task<IReadOnlyList<DeadLetter>> DeadLettersAsync(string? subscriber = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(connection, null, Q("""
            SELECT id, subscriber, event_id, topic, key, payload::text, error, failed_ms FROM {schema}.dead_letters WHERE $1::text IS NULL OR subscriber = $1 ORDER BY id
            """), subscriber);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<DeadLetter>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new DeadLetter(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt64(7)));
        }
        return list;
    }

    public async Task<IReadOnlyList<SubscriberStatus>> StatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(connection, null, Q("SELECT name, cursor_id, failures, retry_at_ms, last_error FROM {schema}.subscribers ORDER BY name"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<SubscriberStatus>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new SubscriberStatus(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return list;
    }

    /// <summary>How many events are kept (for tests and the admin views).</summary>
    public async Task<long> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        return (long)(await Sql.ScalarAsync(connection, null, Q("SELECT count(*) FROM {schema}.outbox"), cancellationToken))!;
    }
}
