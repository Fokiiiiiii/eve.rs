using System.Collections.Concurrent;
using Eve.Crimewatch;
using Eve.Events;
using Eve.Inventory;
using Eve.Scheduler;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Macho;
using Eve.Marshal;
using Eve.Npc;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Space;
using Eve.Universe;
using ModuleAttributes = Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space;

public sealed class TransitionRuntime
{
    private static readonly TimeSpan SessionChangeDuration = TimeSpan.FromSeconds(10);
    private readonly LocalTimers _timers;
    private readonly ConcurrentDictionary<ulong, byte> _sessionChanges = new();

    public TransitionRuntime(LocalTimers timers, EventBus? events = null)
    {
        _timers = timers;
        Events = events;
    }

    public EventBus? Events { get; set; }

    public bool SessionChangeActive(ulong sessionId) => _sessionChanges.ContainsKey(sessionId);

    public void StartSessionChange(ulong sessionId)
    {
        _sessionChanges[sessionId] = 0;
        _timers.Set($"space.transition.session-change:{sessionId}", SessionChangeDuration,
            () => _sessionChanges.TryRemove(sessionId, out _));
    }

    public void EndSessionChange(ulong sessionId)
    {
        _timers.Cancel($"space.transition.session-change:{sessionId}");
        _sessionChanges.TryRemove(sessionId, out _);
    }

    public async Task WriteEventAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        Eve.Ships.ShipTransitionEvent transition,
        CancellationToken cancellationToken)
    {
        if (Events is { } events)
        {
            await events.PublishAsync(
                connection,
                transaction,
                "space.transition",
                transition,
                $"{transition.Kind}:{transition.CharacterId}:{transition.ShipId}:{transition.RelatedShipId}",
                cancellationToken);
        }
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> persist,
        Func<T, bool> committed,
        Func<T, CancellationToken, Task>? applySimulation,
        Func<T, CancellationToken, Task<bool>>? applySession,
        Func<T, CancellationToken, Task>? notify,
        CancellationToken cancellationToken,
        ulong? sessionId = null)
    {
        var result = await persist(cancellationToken);
        if (!committed(result))
            return result;

        try
        {
            if (applySimulation is not null)
                await applySimulation(result, cancellationToken);
            if (applySession is not null && !await applySession(result, cancellationToken))
                return result;
            if (sessionId is { } id)
                StartSessionChange(id);
            if (notify is not null)
                await notify(result, cancellationToken);
            return result;
        }
        finally
        {
            Events?.Wake();
        }
    }
}
