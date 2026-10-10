using Eve.Dogma;

namespace Eve.Services.Space;

/// <summary>
/// The timed command bursts' buffs on the ships of space (track C6): the host's buff registry, behind a lock, because a burst's settlement and the sweep that ends
/// its buffs run on different threads. In memory only: a restart drops every buff, as EVE.js drops its burst history.
/// </summary>
public sealed class ShipBuffs
{
    private readonly object _gate = new();
    private readonly BuffRegistry _registry = new();

    /// <summary>Applies a buff of <paramref name="collection"/> with <paramref name="value"/> to a ship, from <paramref name="nowMs"/> for <paramref name="durationMs"/>.</summary>
    public void Apply(long shipId, BuffCollection collection, double value, double nowMs, double durationMs)
    {
        lock (_gate)
        {
            _registry.Apply(shipId, collection, value, nowMs, durationMs);
        }
    }

    /// <summary>Makes the ship's buffs of <paramref name="nowMs"/> the world's, so the ship's numbers are the buffed ones.</summary>
    public void Sync(DogmaWorld world, long shipId, double nowMs)
    {
        lock (_gate)
        {
            _registry.Sync(world, shipId, nowMs);
        }
    }

    /// <summary>The buffs the ship carries at <paramref name="nowMs"/>, as the client is told them.</summary>
    public IReadOnlyList<BuffState> StateOf(long shipId, double nowMs)
    {
        lock (_gate)
        {
            return _registry.StateOf(shipId, nowMs);
        }
    }

    /// <summary>
    /// The ships a grant holds still until a time (the wiki: the protected ships cannot move, so the simulation's stop is kept by the host's movement
    /// commands, <see cref="SpaceSessions.Command"/>). In memory, as the buffs are.
    /// </summary>
    private readonly Dictionary<long, double> _immobileUntil = [];

    /// <summary>Holds a ship still until <paramref name="untilMs"/> (the later time is kept when one is held already).</summary>
    public void Immobilize(long shipId, double untilMs)
    {
        lock (_gate)
        {
            _immobileUntil[shipId] = _immobileUntil.TryGetValue(shipId, out var held) ? Math.Max(held, untilMs) : untilMs;
        }
    }

    /// <summary>Whether a ship is held still at <paramref name="nowMs"/>.</summary>
    public bool IsImmobile(long shipId, double nowMs)
    {
        lock (_gate)
        {
            return _immobileUntil.TryGetValue(shipId, out var until) && nowMs <= until;
        }
    }

    /// <summary>Drops the buffs that ran out before <paramref name="nowMs"/>; returns the ships that lost one.</summary>
    public IReadOnlyList<long> Prune(double nowMs)
    {
        lock (_gate)
        {
            return _registry.Prune(nowMs);
        }
    }
}
