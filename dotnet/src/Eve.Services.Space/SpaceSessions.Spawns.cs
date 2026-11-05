using System.Collections.Concurrent;
using Eve.Services;
using Eve.Space;
using Eve.Ships;

namespace Eve.Services.Space;

/// <summary>
/// The game master's spawns (<see cref="ISpaceSpawns"/>): the bare hulls of <c>/deathtest</c>'s placement and the NPC ships the host makes, by item ID, each with the
/// simulation session it is known by, so that it can be taken out.
/// </summary>
public sealed partial class SpaceSessions : ISpaceSpawns
{
    private readonly ConcurrentDictionary<long, SpawnedNpc> _spawned = new();

    private sealed record SpawnedNpc(ulong Session, int SolarSystemId, int TypeId);

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
            if (await SpawnAsync(spawn, "spawn", cancellationToken) is not { } npcSession)
            {
                break;
            }
            _spawned[shipId] = new SpawnedNpc(npcSession, pilot.SolarSystemId, typeId);
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

    public Task<int> RemoveAsync(IReadOnlyList<long> shipIds, CancellationToken cancellationToken)
    {
        var removed = 0;
        foreach (var shipId in shipIds)
        {
            if (!_spawned.TryRemove(shipId, out var npc))
            {
                continue;
            }
            // a ship that leaves the scene for good: the simulation drops it with no wreck (the NPC's own departure)
            _sim.Leave(npc.Session, LeaveWhy.Docked);
            _npcShips.TryRemove(shipId, out _);
            removed++;
        }
        return Task.FromResult(removed);
    }
}
