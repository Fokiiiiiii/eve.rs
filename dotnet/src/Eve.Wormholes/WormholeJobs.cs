using Eve.Scheduler;
using Eve.Universe;
using Npgsql;

namespace Eve.Wormholes;

/// <summary>A connection that opened (<see cref="Opened"/> set) or collapsed (<see cref="Opened"/> null): the host places or removes its two ends.</summary>
public sealed record WormholeChange(long ConnectionId, StoredConnection? Opened);

/// <summary>The payload of a connection's expiry job: the connection and its seed (the seed decides the replacement of a static).</summary>
public sealed record ExpireJob(long ConnectionId, ulong Seed);

/// <summary>
/// The durable lifecycle of the connections: one expiry job per active connection, due at its expiry. When the job runs the
/// connection collapses; a collapsed static opens one replacement of the same type from the same source, to a random eligible
/// system (seeded by the job's seed). A non-static connection collapses for good. A transit that empties a connection collapses
/// it the same way and cancels its job.
/// </summary>
public sealed class WormholeJobs
{
    public const string ExpireKind = "wormhole.expire";

    private readonly DurableScheduler _scheduler;
    private readonly WormholeStore _store;
    private readonly IReadOnlyDictionary<int, WormholeType> _types;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<int>> _byClass;

    public WormholeJobs(DurableScheduler scheduler, WormholeStore store, IReadOnlyDictionary<int, WormholeType> types, IReadOnlyDictionary<int, int> systemClasses)
    {
        _scheduler = scheduler;
        _store = store;
        _types = types;
        _byClass = WormholePlanner.ByClass(systemClasses);
    }

    /// <summary>Raised for every connection that opens or collapses, unless the caller captures the change (<c>sink</c>).</summary>
    public event Action<WormholeChange>? Changed;

    private void Raise(WormholeChange change, Action<WormholeChange>? sink)
    {
        if (sink is not null)
            sink(change);
        else
            Changed?.Invoke(change);
    }

    /// <summary>Registers the expiry handler with the scheduler (call once at start-up).</summary>
    public void Register() => _scheduler.Register(ExpireKind, ExpireAsync);

    /// <summary>Stores one planned connection (created now) and schedules its expiry job.</summary>
    public async Task<long> OpenAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PlannedConnection planned, bool isStatic, long nowMs,
        CancellationToken cancellationToken = default, Action<WormholeChange>? sink = null)
    {
        var type = _types[planned.TypeId];
        var stored = new StoredConnection(0, planned.SourceSystemId, planned.DestinationSystemId, planned.TypeId, planned.Code, isStatic, planned.MassKg,
            type.MaxStableMassKg, nowMs, WormholeLifecycle.ExpiresAt(nowMs, type), true, planned.Seed);
        var id = await _store.InsertAsync(connection, transaction, stored, cancellationToken);
        await _scheduler.ScheduleAsync(connection, transaction,
            new JobSpec(ExpireKind, stored.ExpiresMs) { Key = id.ToString(), Payload = new ExpireJob(id, planned.Seed) }, cancellationToken);
        Raise(new WormholeChange(id, stored with { Id = id }), sink);
        return id;
    }

    /// <summary>
    /// One ship's transit through a connection: refused over the individual limit, or its mass comes off the wormhole. A connection
    /// that empties collapses (its replacement is opened if it is a static). The caller commits the transaction.
    /// </summary>
    public async Task<TransitResult> TransitAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long connectionId, double shipMassKg, long nowMs,
        CancellationToken cancellationToken = default, Action<WormholeChange>? sink = null)
    {
        var stored = await _store.GetAsync(connection, transaction, connectionId, forUpdate: true, cancellationToken)
            ?? throw new InvalidOperationException($"wormhole connection {connectionId} does not exist");
        if (!stored.Active)
        {
            return new TransitResult(TransitOutcome.Refused, 0, WormholeMassState.Collapsed);
        }
        var result = WormholeLifecycle.Transit(_types[stored.TypeId], stored.MassKg, shipMassKg);
        if (result.Outcome == TransitOutcome.Refused)
        {
            return result;
        }
        if (result.Outcome == TransitOutcome.Passed)
        {
            await _store.UpdateAsync(connection, transaction, stored with { MassKg = result.MassKg }, cancellationToken);
            return result;
        }
        await CollapseAsync(connection, transaction, stored with { MassKg = 0 }, stored.Seed, nowMs, cancellationToken, sink);
        await _scheduler.CancelAsync(connection, transaction, ExpireKind, connectionId.ToString(), cancellationToken);
        return result;
    }

    private async Task ExpireAsync(JobContext context, CancellationToken cancellationToken)
    {
        var job = context.Payload<ExpireJob>() ?? throw new InvalidDataException("wormhole expiry job has no payload");
        var stored = await _store.GetAsync(context.Connection, context.Transaction, job.ConnectionId, forUpdate: true, cancellationToken);
        if (stored is null || !stored.Active || context.NowMs < stored.ExpiresMs)
        {
            return;
        }
        await CollapseAsync(context.Connection, context.Transaction, stored, job.Seed, context.NowMs, cancellationToken, null);
    }

    private async Task CollapseAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, StoredConnection stored, ulong seed, long nowMs,
        CancellationToken cancellationToken, Action<WormholeChange>? sink)
    {
        await _store.UpdateAsync(connection, transaction, stored with { Active = false }, cancellationToken);
        Raise(new WormholeChange(stored.Id, null), sink);
        if (!stored.IsStatic)
        {
            return;
        }
        var type = _types[stored.TypeId];
        var existing = await _store.ActiveFromAsync(connection, transaction, stored.SourceSystemId, cancellationToken);
        var pairs = new HashSet<(int Source, int Destination)>(existing.Select(c => (c.SourceSystemId, c.DestinationSystemId)));
        var random = new SeededRandom(WormholeLifecycle.ReplacementSeed(seed));
        var destination = WormholePlanner.PickDestination(stored.SourceSystemId, type.TargetSystemClass, pairs, _byClass, random);
        if (destination is null)
        {
            return;
        }
        var mass = WormholePlanner.RollMass(type, random);
        var replacementSeed = random.NextUInt64();
        await OpenAsync(connection, transaction,
            new PlannedConnection(stored.SourceSystemId, destination.Value, stored.Code, stored.TypeId, mass, replacementSeed), isStatic: true, nowMs, cancellationToken, sink);
    }
}
