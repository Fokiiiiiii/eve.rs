using Eve.Dogma;
using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    /// <summary>Materializes an inventory ship already committed as an unpiloted hull at its persisted pose.</summary>
    public async Task<bool> PlaceLaunchedShipAsync(
        long itemId,
        long ownerId,
        int solarSystemId,
        TypeDef type,
        IReadOnlyList<double> position,
        IReadOnlyList<double> direction,
        CancellationToken cancellationToken)
    {
        if (itemId <= 0 || ownerId <= 0 || solarSystemId <= 0 ||
            position.Count < 3 || direction.Count < 3 ||
            position.Take(3).Any(value => !double.IsFinite(value)) ||
            direction.Take(3).Any(value => !double.IsFinite(value)))
            return false;

        var ship = SimShips.OfType(itemId, type) with
        {
            CharacterId = 0,
            OwnerId = ownerId,
        };
        var spawn = new SimSpawn(
            NextSpawnRequest(),
            SimSceneKey.SolarSystem(solarSystemId),
            ship,
            0,
            position.Take(3).ToArray(),
            [0d, 0d, 0d],
            direction.Take(3).ToArray(),
            "",
            SimNpcIdentity.Default with { Hull = true });
        var reply = await _sim.SpawnAsync(spawn, cancellationToken);
        if (reply.Session is not { } sessionId)
        {
            _log?.Invoke($"launched ship {itemId} was not placed: {reply.Error}");
            return false;
        }
        _spawned[itemId] = new SpawnedNpc(sessionId, solarSystemId, type.TypeId);
        return true;
    }
}
