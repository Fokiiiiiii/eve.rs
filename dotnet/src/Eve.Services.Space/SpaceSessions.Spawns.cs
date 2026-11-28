using System.Collections.Concurrent;
using Eve.Services;
using Eve.Space;
using Eve.Ships;

namespace Eve.Services.Space;

/// <summary>
/// The host's transient spawned hulls and NPC ships (<see cref="ISpaceSpawns"/>), tracked by item ID and removed from their solar system.
/// </summary>
public sealed partial class SpaceSessions : ISpaceSpawns
{
    private readonly ConcurrentDictionary<long, SpawnedNpc> _spawned = new();

    private sealed record SpawnedNpc(int SolarSystemId, int TypeId);

    public async Task<SpawnResult> SpawnHullsAsync(ClientSession session, int typeId, int count, double deviationMeters, CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {
            return new SpawnResult([], SpawnResult.NotInSpace);
        }
        if (!_sim.IsConnected)
        {
            return new SpawnResult([], SpawnResult.SpaceUnavailable);
        }
        await _statics.EnsureTypesAsync([typeId], cancellationToken);
        if (_statics.Type(typeId) is not { } type)
        {
            return new SpawnResult([], SpawnResult.SpawnFailed);
        }
        var ids = new List<long>();
        foreach (var place in HullPlaces(count, Math.Max(0, deviationMeters)))
        {
            var shipId = HullIdBase + Interlocked.Increment(ref _hulls);
            var spawn = new SimSpawn(
                NextSpawnRequest(), SimSceneKey.SolarSystem(pilot.SolarSystemId), SimShips.OfType(shipId, type), pilot.ShipId, place, [0, 0, 0], RandomDirection(), "",
                SimNpcIdentity.Default with { Hull = true });
            if (await SpawnAsync(spawn, "spawn", cancellationToken) is null)
            {
                break;
            }
            _spawned[shipId] = new SpawnedNpc(pilot.SolarSystemId, typeId);
            ids.Add(shipId);
        }
        return new SpawnResult(ids, ids.Count == 0 ? SpawnResult.SpawnFailed : null);
    }

    public async Task<SpawnedView?> SpawnedAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot))
        {
            return null;
        }
        var own = await ScenePositionOfAsync(session, pilot.ShipId, cancellationToken);
        var ships = new List<SpawnedShip>();
        foreach (var (shipId, npc) in _spawned.Where(entry => entry.Value.SolarSystemId == pilot.SolarSystemId).OrderBy(entry => entry.Key))
        {
            ships.Add(new SpawnedShip(shipId, npc.TypeId, await PlaceAsync(session, shipId, cancellationToken)));
        }
        return new SpawnedView(own is { } mine ? [mine.X, mine.Y, mine.Z] : null, ships);
    }

    private async Task<IReadOnlyList<double>?> PlaceAsync(ClientSession session, long shipId, CancellationToken cancellationToken) =>
        await ScenePositionOfAsync(session, shipId, cancellationToken) is { } at ? [at.X, at.Y, at.Z] : null;

    public async Task<int> RemoveAsync(IReadOnlyList<long> shipIds, CancellationToken cancellationToken)
    {
        var removed = 0;
        foreach (var shipId in shipIds)
        {
            if (!_spawned.TryGetValue(shipId, out var npc))
            {
                continue;
            }
            SimRemoveSpawnedShipReply reply;
            try
            {
                reply = await _sim.RemoveSpawnedShipAsync(
                    new SimRemoveSpawnedShip(NextSpawnRequest(), npc.SolarSystemId, shipId), cancellationToken);
            }
            catch (SimUnavailableException error)
            {
                _log?.Invoke($"spawned ship {shipId} could not be removed: {error.Message}");
                throw Notify("Space is not available right now. Try again in a moment.");
            }
            _spawned.TryRemove(shipId, out _);
            _npcShips.TryRemove(shipId, out _);
            if (reply.Removed) removed++;
        }
        return removed;
    }
}
