using System.Text.Json;
using Npgsql;

namespace Eve.Events;

/// <summary>An event as a subscriber sees it.</summary>
public sealed record DomainEvent(long Id, string Topic, string? Key, string PayloadJson, long OccurredMs)
{
    /// <summary>The payload as the type it was published as (or any type with the same JSON shape).</summary>
    public T? Payload<T>() => JsonSerializer.Deserialize<T>(PayloadJson, EventBus.Json);

    /// <summary>The payload as a JSON document, for the code that filters on fields it does not know the type of.</summary>
    public JsonElement Json() => JsonDocument.Parse(PayloadJson).RootElement.Clone();
}

/// <summary>
/// What a subscriber's handler gets: the event, and the transaction the event is handled in. What the handler writes through <see cref="Transaction"/> is committed together
/// with the subscriber's cursor, so a handler that only writes to PostgreSQL handles each event exactly once. Events it publishes through <see cref="PublishAsync"/> join the same
/// transaction.
/// </summary>
public sealed class EventContext
{
    private readonly EventBus _bus;

    internal EventContext(EventBus bus, DomainEvent @event, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _bus = bus;
        Event = @event;
        Connection = connection;
        Transaction = transaction;
    }

    public DomainEvent Event { get; }
    public NpgsqlConnection Connection { get; }
    public NpgsqlTransaction Transaction { get; }

    public Task<long> PublishAsync(string topic, object? payload, string? key = null, CancellationToken cancellationToken = default) =>
        _bus.PublishAsync(Connection, Transaction, topic, payload, key, cancellationToken);
}

/// <summary>A subscriber's handler. Throwing means "not handled": the event is tried again later and, after the last attempt, set aside as a dead letter.</summary>
public delegate Task DomainEventHandler(EventContext context, CancellationToken cancellationToken);

/// <summary>Where a subscriber that has never run before starts reading.</summary>
public enum StartAt
{
    /// <summary>With the events published from now on (a feature that is switched on today does not replay history). The default.</summary>
    Now,
    /// <summary>With the oldest event still kept (a read model that is rebuilt from the log).</summary>
    Beginning,
}

/// <summary>A subscriber that gave up on an event.</summary>
public sealed record DeadLetter(long Id, string Subscriber, long EventId, string Topic, string? Key, string PayloadJson, string Error, long FailedMs);

/// <summary>How a subscriber is doing; for tools and tests.</summary>
public sealed record SubscriberStatus(string Name, long CursorTx, long CursorId, int Failures, long? RetryAtMs, string? LastError);

/// <summary>Knobs of the bus.</summary>
public sealed record EventBusOptions
{
    /// <summary>A subscriber gives an event up after this many failed tries.</summary>
    public int MaxAttempts { get; init; } = 8;

    public long BackoffBaseMs { get; init; } = 1_000;
    public long BackoffMaxMs { get; init; } = 300_000;

    /// <summary>The most events one pump delivers to one subscriber before it lets the others have a turn.</summary>
    public int BatchPerPump { get; init; } = 500;

    /// <summary>Events that every subscriber has read are deleted after this long.</summary>
    public long RetentionMs { get; init; } = 24L * 3600 * 1000;

    /// <summary>Events are deleted after this long even if a subscriber (one that no longer exists, say) has not read them.</summary>
    public long HardRetentionMs { get; init; } = 7L * 24 * 3600 * 1000;
}
