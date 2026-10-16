using Eve.Ships;
using Eve.Services.Space.Waves;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// The deadspace pockets of the dungeons (ST-23; EveJS <c>deadspaceWarpPolicy.js</c>): a dungeon that runs (a mission pocket or an incursion site) is a deadspace
/// pocket in the scene at its origin, with the EveJS radius (<see cref="DeadspaceSiteRadiusMeters"/>). The pocket is a host record of kind <see cref="DeadspacePocketKind"/>
/// and a simulation body with no client ball. A job keeps the two in step with the running dungeons: a pocket is placed when its dungeon is active, and taken out
/// when it is not. An observatory is refused inside a pocket by its record.
/// </summary>
public sealed partial class SpaceContainers
{
    /// <summary>The deployed record kind of a dungeon's pocket (migration 0017).</summary>
    public const string DeadspacePocketKind = "dungeon_pocket";

    /// <summary>The deployable id of a pocket is this base plus its dungeon instance's id (instance ids are small, item ids are not in this range).</summary>
    private const long PocketIdBase = 4_500_000_000_000_000_000L;

    private const string PocketName = "Deadspace Pocket";

    /// <summary>The deployable id of a dungeon's pocket.</summary>
    public static long PocketDeployableId(long instanceId) => PocketIdBase + instanceId;

    /// <summary>
    /// Places a dungeon's pocket: its record (kept once) and its body in the scene at its origin. The body is given to the simulation again on each call, which
    /// the simulation takes as given once, so a restart puts it back in the scene by the next call.
    /// </summary>
    public async Task PlaceDeadspacePocketAsync(int solarSystemId, long instanceId, double[] origin, CancellationToken cancellationToken = default)
    {
        if (_deployed is null)
        {
            return;
        }
        var id = PocketDeployableId(instanceId);
        if (await _deployed.GetAsync(id, cancellationToken) is null)
        {
            await _deployed.SaveAsync(new DeployedRecord(
                id, DeadspacePocketKind, solarSystemId, PocketName, 0, 0, origin, DeadspaceSiteRadiusMeters, 0, 0, 0, 0, NeverExpires), cancellationToken);
        }
        _sim.PlaceDeadspacePocket(new SimPlaceDeadspacePocket(id, solarSystemId, PocketName, origin, DeadspaceSiteRadiusMeters));
    }

    /// <summary>
    /// Keeps the pockets in step with the running dungeons: each active one is placed (see <see cref="PlaceDeadspacePocketAsync"/>), and each pocket whose
    /// dungeon is not active any more is taken out of the scene and forgotten.
    /// </summary>
    public async Task ReconcileDeadspacePocketsAsync(IReadOnlyList<DungeonPocket> active, CancellationToken cancellationToken = default)
    {
        if (_deployed is null)
        {
            return;
        }
        var running = new HashSet<long>();
        foreach (var pocket in active)
        {
            running.Add(PocketDeployableId(pocket.InstanceId));
            await PlaceDeadspacePocketAsync(pocket.SolarSystemId, pocket.InstanceId, pocket.Origin, cancellationToken);
        }
        foreach (var record in await _deployed.ByKindAsync(DeadspacePocketKind, cancellationToken))
        {
            if (!running.Contains(record.DeployableId))
            {
                _sim.RemoveDeployed(record.DeployableId);
                await _deployed.RemoveAsync(record.DeployableId, cancellationToken);
            }
        }
    }
}
