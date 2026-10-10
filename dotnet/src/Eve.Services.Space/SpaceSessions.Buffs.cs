using Eve.Dogma;
using Eve.Ships;
using Eve.Services.Space.Effects;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// The buffs of the ships in space, kept by the host (track C8): a ship's buff inputs are written to the record (<c>ship_buffs</c>) when they change, a restart
/// restores them when the ship is next synced, and a ship that enters a grid (undock, login in space, a gate jump) is told the state it carries.
/// </summary>
public sealed partial class SpaceSessions
{
    /// <summary>The host's record of the ships' buff inputs. Null keeps the buffs in memory only.</summary>
    public PgShipBuffStore? BuffStore { get; set; }

    /// <summary>The ship's world carries its buffs: the ones a restart dropped are restored from the record first, then the world is synced with them.</summary>
    internal async Task SyncBuffsAsync(Eve.Dogma.DogmaWorld world, long shipId, CancellationToken cancellationToken)
    {
        var now = _nowUnixMs();
        await RestoreBuffsAsync(shipId, now, cancellationToken);
        Buffs.Sync(world, shipId, now);
    }

    /// <summary>
    /// A ship that the registry holds no buffs for has its record's inputs restored (a restart dropped them). A record that cannot be read is logged, and the ship
    /// goes on without its buffs.
    /// </summary>
    internal async Task RestoreBuffsAsync(long shipId, double nowMs, CancellationToken cancellationToken)
    {
        if (BuffStore is null || Buffs.StateOf(shipId, nowMs).Count > 0 || !Buffs.MarkRestored(shipId))
        {
            return;
        }
        IReadOnlyList<ShipBuffRecord> records;
        try
        {
            records = await BuffStore.LoadAsync(shipId, nowMs, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Buffs.UnmarkRestored(shipId);
            _log?.Invoke($"the buffs of ship {shipId} were not restored: {error.Message}");
            return;
        }
        foreach (var record in records)
        {
            if (PanicBuffs.CollectionOf(_statics, record.CollectionId) is { } collection)
            {
                Buffs.Restore(shipId, collection, record.Value, record.ExpiresAtMs);
            }
        }
    }

    /// <summary>Keeps the ship's buff inputs of <paramref name="nowMs"/> in the record (nothing is kept without a store).</summary>
    internal async Task KeepBuffsAsync(long shipId, double nowMs)
    {
        if (BuffStore is null)
        {
            return;
        }
        try
        {
            await BuffStore.SaveAsync(shipId, [.. Buffs.InputsOf(shipId, nowMs).Select(input => new ShipBuffRecord(input.CollectionId, input.Value, input.ExpiresAtMs))]);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"the buffs of ship {shipId} were not kept: {error.Message}");
        }
    }

    /// <summary>Takes the buff inputs whose time is up out of the record (the containers' sweep).</summary>
    internal async Task PruneBuffRecordAsync(double nowMs)
    {
        if (BuffStore is null)
        {
            return;
        }
        try
        {
            await BuffStore.PruneAsync(nowMs);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"the expired buffs were not taken out of the record: {error.Message}");
        }
    }

    /// <summary>A ship that enters a grid is told the buffs it carries (the simulation tells its pilot the state, OnDbuffUpdated). Nothing is sent without a buff.</summary>
    internal void TellEnteredBuffs(ulong sessionId, long shipId, double nowMs)
    {
        var entries = Buffs.StateOf(shipId, nowMs)
            .Select(state => new SimBuffEntry(state.CollectionId, state.Value, Math.Max(0d, state.ExpiresAtMs - nowMs)))
            .ToArray();
        if (entries.Length > 0)
        {
            _sim.BuffState(sessionId, shipId, entries);
        }
    }
}
