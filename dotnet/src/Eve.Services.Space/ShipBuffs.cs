using Eve.Dogma;

namespace Eve.Services.Space;

/// <summary>
/// The timed command bursts' buffs on the ships of space (track C6): the host's buff registry, behind a lock, because a burst's settlement and the sweep that ends
/// its buffs run on different threads. In memory, and kept in the host's record (<c>ship_buffs</c>) so a restart restores them.
/// </summary>
public sealed class ShipBuffs
{
    private readonly object _gate = new();
    private BuffRegistry _registry = new();
    private HashSet<long> _restored = [];

    /// <summary>Applies a buff of <paramref name="collection"/> with <paramref name="value"/> to a ship, from <paramref name="nowMs"/> for <paramref name="durationMs"/>.</summary>
    public void Apply(long shipId, BuffCollection collection, double value, double nowMs, double durationMs)
    {
        lock (_gate)
        {
            _registry.Apply(shipId, collection, value, nowMs, durationMs);
        }
    }

    /// <summary>The inputs the ship carries at <paramref name="nowMs"/>, as the host keeps them (<see cref="Restore"/>).</summary>
    public IReadOnlyList<(int CollectionId, double Value, double ExpiresAtMs)> InputsOf(long shipId, double nowMs)
    {
        lock (_gate)
        {
            return _registry.InputsOf(shipId, nowMs);
        }
    }

    /// <summary>A restart: the registry starts empty, and the buffs are what the host's record restores (tests simulate a restart with it).</summary>
    internal void DropAll()
    {
        lock (_gate)
        {
            _registry = new BuffRegistry();
            _restored = [];
        }
    }

    /// <summary>Marks a ship as restored from the record. Returns false when it was marked already in this run (the record is read once per ship).</summary>
    public bool MarkRestored(long shipId)
    {
        lock (_gate)
        {
            return _restored.Add(shipId);
        }
    }

    /// <summary>Clears the mark of a ship whose record could not be read, so the next sync reads it again.</summary>
    public void UnmarkRestored(long shipId)
    {
        lock (_gate)
        {
            _restored.Remove(shipId);
        }
    }

    /// <summary>Restores a buff input the host kept for a ship (a restart drops the registry; the record brings the buffs back).</summary>
    public void Restore(long shipId, BuffCollection collection, double value, double expiresAtMs)
    {
        lock (_gate)
        {
            _registry.Restore(shipId, collection, value, expiresAtMs);
        }
    }

    /// <summary>Makes the ship's buffs of <paramref name="nowMs"/> the world's, so the ship's numbers are the buffed ones.</summary>
    /// <summary>The buff effects the ship carries at the time (the registry's), as the dogma world takes them.</summary>
    public IReadOnlyList<Eve.Dogma.EffectDef> EffectsOf(long shipId, double nowMs) => _registry.EffectsOf(shipId, nowMs);

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
