using Eve.Scheduler;
using Eve.Universe;
using Npgsql;

namespace Eve.Wormholes;

/// <summary>A connection that opened (<see cref="Opened"/> set) or collapsed (<see cref="Opened"/> null): the host places or removes its two ends.</summary>
public sealed record WormholeChange(long ConnectionId, StoredConnection? Opened);

/// <summary>The payload of a connection's expiry job: the connection and its seed (the seed decides the replacement of a static).</summary>
public sealed record ExpireJob(long ConnectionId, ulong Seed);

/// <summary>The static connection to replace after its respawn delay has elapsed.</summary>
public sealed record StaticRespawnJob(int SourceSystemId, int DestinationSystemId, int TypeId, string Code, ulong Seed);

/// <summary>A connection whose client-visible age bucket needs to be refreshed.</summary>
public sealed record WormholeSlimStateJob(long ConnectionId);

/// <summary>
/// The durable lifecycle of the connections: one expiry job per active connection, due at its expiry. When the job runs the
/// connection collapses; a collapsed static opens one replacement of the same type from the same source, to a random eligible
/// system (seeded by the job's seed). A non-static connection collapses for good. A transit that empties a connection collapses
/// it the same way and cancels its job.
/// </summary>
public sealed class WormholeJobs
{
    public const string ExpireKind = "wormhole.expire";
    public const string StaticRespawnKind = "wormhole.static_respawn";
    public const string SlimStateKind = "wormhole.slim_state";
    public const long StaticRespawnDelayMs = 60_000;

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

    public event Func<long, CancellationToken, Task>? StateChanged;

    public void Publish(WormholeChange change) => Changed?.Invoke(change);

    public async Task PublishStateChangedAsync(long connectionId, CancellationToken cancellationToken = default)
    {
        if (StateChanged is null)
            return;
        foreach (var callback in StateChanged.GetInvocationList().Cast<Func<long, CancellationToken, Task>>())
            await callback(connectionId, cancellationToken).ConfigureAwait(false);
    }

    private void Raise(WormholeChange change, Action<WormholeChange>? sink)
    {
        if (sink is not null)
            sink(change);
        else
            Changed?.Invoke(change);
    }

    /// <summary>Registers the expiry handler with the scheduler (call once at start-up).</summary>
    public void Register()
    {
        _scheduler.Register(ExpireKind, ExpireAsync);
        _scheduler.Register(StaticRespawnKind, RespawnStaticAsync);
        _scheduler.Register(SlimStateKind, UpdateSlimStateAsync);
    }

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
        await ScheduleSlimStateAsync(connection, transaction, id, stored.ExpiresMs, nowMs, cancellationToken);
        Raise(new WormholeChange(id, stored with { Id = id }), sink);
        return id;
    }

    /// <summary>
    /// One ship's transit through a connection: refused over the individual limit, or its mass comes off the wormhole. A connection
    /// that empties collapses (its replacement is opened if it is a static). The caller commits the transaction.
    /// </summary>
    public async Task<TransitResult> TransitAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long connectionId, double shipMassKg, long nowMs,
        CancellationToken cancellationToken = default, Action<WormholeChange>? sink = null, long characterId = 0)
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
        if (characterId > 0)
            await _store.SetPolarizationAsync(connection, transaction, characterId, connectionId,
                nowMs + (long)WormholeRules.Polarization.TotalMilliseconds, cancellationToken);
        if (result.Outcome == TransitOutcome.Passed)
        {
            await _store.UpdateAsync(connection, transaction, stored with { MassKg = result.MassKg }, cancellationToken);
            return result;
        }
        await CollapseAsync(connection, transaction, stored with { MassKg = 0 }, stored.Seed, nowMs, cancellationToken, sink);
        await _scheduler.CancelAsync(connection, transaction, ExpireKind, connectionId.ToString(), cancellationToken);
        return result;
    }

    /// <summary>
    /// Validates and accepts one jump while holding the connection row lock, then commits its mass debit and polarization in the same transaction.
    /// The simulation callback runs only after the authoritative row, expiry and mass checks have passed.
    /// </summary>
    public async Task<WormholeJumpAttempt> AttemptJumpAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long endId, int currentSystemId,
        long characterId, double shipMassKg, long nowMs, Func<CancellationToken, Task<bool>> acceptInSimulation,
        CancellationToken cancellationToken = default, Action<WormholeChange>? sink = null)
    {
        if (endId <= 0 || characterId <= 0)
            return new WormholeJumpAttempt(WormholeJumpRejection.UnknownEnd, null);

        var connectionId = endId / 2;
        var stored = await _store.GetAsync(connection, transaction, connectionId, forUpdate: true, cancellationToken);
        if (stored is null)
            return new WormholeJumpAttempt(WormholeJumpRejection.UnknownEnd, null);
        if (!stored.Active)
            return new WormholeJumpAttempt(WormholeJumpRejection.Collapsed, null);
        if (stored.ExpiresMs <= nowMs)
        {
            await CollapseAsync(connection, transaction, stored, stored.Seed, nowMs, cancellationToken, sink);
            await _scheduler.CancelAsync(connection, transaction, ExpireKind, connectionId.ToString(), cancellationToken);
            return new WormholeJumpAttempt(WormholeJumpRejection.Expired, null);
        }

        var endSystemId = endId % 2 == 0 ? stored.SourceSystemId : stored.DestinationSystemId;
        if (endSystemId != currentSystemId)
            return new WormholeJumpAttempt(WormholeJumpRejection.WrongSystem, null);

        if (await _store.PolarizedUntilAsync(connection, transaction, characterId, connectionId, nowMs, cancellationToken) is not null)
            return new WormholeJumpAttempt(WormholeJumpRejection.Polarized, null);

        var transit = WormholeLifecycle.Transit(_types[stored.TypeId], stored.MassKg, shipMassKg);
        if (transit.Outcome == TransitOutcome.Refused)
            return new WormholeJumpAttempt(WormholeJumpRejection.ShipTooMassive, transit);
        if (!await acceptInSimulation(cancellationToken).ConfigureAwait(false))
            return new WormholeJumpAttempt(WormholeJumpRejection.SimulationRefused, transit);

        await _store.SetPolarizationAsync(connection, transaction, characterId, connectionId,
            nowMs + (long)WormholeRules.Polarization.TotalMilliseconds, cancellationToken);
        if (transit.Outcome == TransitOutcome.Passed)
        {
            await _store.UpdateAsync(connection, transaction, stored with { MassKg = transit.MassKg }, cancellationToken);
        }
        else
        {
            await CollapseAsync(connection, transaction, stored with { MassKg = 0 }, stored.Seed, nowMs, cancellationToken, sink);
            await _scheduler.CancelAsync(connection, transaction, ExpireKind, connectionId.ToString(), cancellationToken);
        }
        return new WormholeJumpAttempt(WormholeJumpRejection.None, transit);
    }

    private async Task ExpireAsync(JobContext context, CancellationToken cancellationToken)
    {
        var job = context.Payload<ExpireJob>() ?? throw new InvalidDataException("wormhole expiry job has no payload");
        var stored = await _store.GetAsync(context.Connection, context.Transaction, job.ConnectionId, forUpdate: true, cancellationToken);
        if (stored is null || !stored.Active || context.NowMs < stored.ExpiresMs)
        {
            return;
        }
        var changes = new List<WormholeChange>();
        await CollapseAsync(context.Connection, context.Transaction, stored, job.Seed, context.NowMs, cancellationToken, changes.Add);
        if (changes.Count > 0)
            context.AfterCommit(_ => PublishAsync(changes));
    }

    private async Task CollapseAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, StoredConnection stored, ulong seed, long nowMs,
        CancellationToken cancellationToken, Action<WormholeChange>? sink)
    {
        await _store.UpdateAsync(connection, transaction, stored with { Active = false }, cancellationToken);
        foreach (var age in new[] { 1, 2, 3 })
            await _scheduler.CancelAsync(connection, transaction, SlimStateKind, SlimStateKey(stored.Id, age), cancellationToken);
        Raise(new WormholeChange(stored.Id, null), sink);
        if (!stored.IsStatic)
        {
            return;
        }
        await _scheduler.ScheduleAsync(connection, transaction,
            new JobSpec(StaticRespawnKind, nowMs + StaticRespawnDelayMs)
            {
                Key = stored.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Payload = new StaticRespawnJob(stored.SourceSystemId, stored.DestinationSystemId, stored.TypeId, stored.Code, seed),
            }, cancellationToken);
    }

    private async Task ScheduleSlimStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long connectionId, long expiresMs, long nowMs,
        CancellationToken cancellationToken)
    {
        foreach (var (age, beforeExpiryMs) in new[]
        {
            (1, 24L * 60 * 60_000),
            (2, 4L * 60 * 60_000),
            (3, 60L * 60_000),
        })
        {
            var dueMs = expiresMs - beforeExpiryMs;
            if (dueMs <= nowMs)
                continue;
            await _scheduler.ScheduleAsync(connection, transaction,
                new JobSpec(SlimStateKind, dueMs) { Key = SlimStateKey(connectionId, age), Payload = new WormholeSlimStateJob(connectionId) },
                cancellationToken);
        }
    }

    private static string SlimStateKey(long connectionId, int age) => $"{connectionId}:{age}";

    private async Task UpdateSlimStateAsync(JobContext context, CancellationToken cancellationToken)
    {
        var job = context.Payload<WormholeSlimStateJob>() ?? throw new InvalidDataException("wormhole slim state job has no payload");
        var stored = await _store.GetAsync(context.Connection, context.Transaction, job.ConnectionId, forUpdate: false, cancellationToken);
        if (stored is null || !stored.Active || stored.ExpiresMs <= context.NowMs)
            return;
        context.AfterCommit(token => PublishStateChangedAsync(job.ConnectionId, token));
    }

    private async Task RespawnStaticAsync(JobContext context, CancellationToken cancellationToken)
    {
        var job = context.Payload<StaticRespawnJob>() ?? throw new InvalidDataException("wormhole static respawn job has no payload");
        var current = await _store.ActiveFromAsync(context.Connection, context.Transaction, job.SourceSystemId, cancellationToken);
        if (current.Any(connection => connection.IsStatic && connection.Code == job.Code))
        {
            return;
        }

        var type = _types[job.TypeId];
        var random = new SeededRandom(WormholeLifecycle.ReplacementSeed(job.Seed));
        PlannedConnection? replacement;
        if (TheraConnections.TypeOf(job.Code) is { } theraType &&
            (job.SourceSystemId == TheraConnections.SystemId || job.DestinationSystemId == TheraConnections.SystemId))
        {
            // a Thera connection: the free end is drawn among the systems of its classes, not connected to Thera yet
            var touching = await _store.ActiveAtAsync(context.Connection, context.Transaction, TheraConnections.SystemId, cancellationToken);
            var connected = new HashSet<int>(touching.Select(c => c.SourceSystemId == TheraConnections.SystemId ? c.DestinationSystemId : c.SourceSystemId));
            var ends = TheraConnections.PickEnds(theraType, _byClass, connected, random);
            if (ends is null)
            {
                context.RunAgainAt(context.NowMs + StaticRespawnDelayMs, job);
                return;
            }
            var mass = WormholePlanner.RollMass(type, random);
            replacement = new PlannedConnection(ends.Value.Source, ends.Value.Destination, job.Code, job.TypeId, mass, random.NextUInt64());
        }
        else
        {
            var pairs = new HashSet<(int Source, int Destination)>(current.Select(c => (c.SourceSystemId, c.DestinationSystemId)));
            var destination = WormholePlanner.PickDestination(job.SourceSystemId, type.TargetSystemClass, pairs, _byClass, random);
            if (destination is null)
            {
                context.RunAgainAt(context.NowMs + StaticRespawnDelayMs, job);
                return;
            }
            var mass = WormholePlanner.RollMass(type, random);
            replacement = new PlannedConnection(job.SourceSystemId, destination.Value, job.Code, job.TypeId, mass, random.NextUInt64());
        }
        var changes = new List<WormholeChange>();
        await OpenAsync(context.Connection, context.Transaction, replacement, isStatic: true, context.NowMs, cancellationToken, changes.Add);
        if (changes.Count > 0)
            context.AfterCommit(_ => PublishAsync(changes));
    }

    private Task PublishAsync(IReadOnlyList<WormholeChange> changes)
    {
        foreach (var change in changes)
            Changed?.Invoke(change);
        return Task.CompletedTask;
    }
}
