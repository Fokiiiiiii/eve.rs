using Eve.Dungeons;
using Eve.Ships;
using Eve.Services.Space.Waves;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// The deadspace pockets of the dungeons (ST-23; EveJS <c>deadspaceWarpPolicy.js</c>): a dungeon that runs (a mission pocket or an incursion site) is a deadspace
/// pocket in the scene at its origin, with the EveJS radius (<see cref="DeadspaceSiteRadiusMeters"/>). The pocket is a host record of kind <see cref="DeadspacePocketKind"/>
/// and a simulation body with no client ball. A running dungeon with a room layout also has a pocket for each room after the entry, and an acceleration gate
/// (a client ball, protocol 75) for each gate of its rooms (docs/project/ACCELERATION_GATES.md). A job keeps all of these in step with the running dungeons: they are
/// placed when their dungeon is active, and taken out when it is not. An observatory is refused inside a pocket by its record.
/// </summary>
public sealed partial class SpaceContainers
{
    /// <summary>The deployed record kind of a dungeon's pocket (migration 0017).</summary>
    public const string DeadspacePocketKind = "dungeon_pocket";

    /// <summary>The deployed record kind of an acceleration gate of a dungeon room (migration 0018).</summary>
    public const string AccelerationGateKind = "dungeon_gate";

    /// <summary>The deployable id of a pocket is this base plus its dungeon instance's id (instance ids are small, item ids are not in this range).</summary>
    private const long PocketIdBase = 4_500_000_000_000_000_000L;

    /// <summary>The pockets of the rooms after the entry: this base plus the instance's slot (<see cref="IdSlotsPerInstance"/>).</summary>
    private const long RoomPocketIdBase = 4_490_000_000_000_000_000L;

    /// <summary>
    /// The gates' deployable ids: this base plus the instance's slot. The host sends a gate's id to the simulation as a double in its transit command, so it is
    /// below 2^53 (the simulation takes an id only that small).
    /// </summary>
    private const long AccelerationGateIdBase = 8_000_000_000_000_000L;

    /// <summary>The ids a dungeon instance has for its rooms and its gates (a site has fewer gates than this).</summary>
    public const int IdSlotsPerInstance = 256;

    private const string PocketName = "Deadspace Pocket";
    private const string AccelerationGateName = "Acceleration Gate";

    /// <summary>The group of the acceleration gate (EveJS <c>ACCELERATION_GATE_SLIM_GROUP_ID</c>).</summary>
    private const int AccelerationGateGroupId = 366;

    /// <summary>The deployable id of a pocket.</summary>
    public static long PocketDeployableId(long instanceId) => PocketIdBase + instanceId;

    /// <summary>The deployable id of the pocket of a dungeon's room after the entry (its index is 1 or more).</summary>
    public static long RoomPocketDeployableId(long instanceId, int roomIndex) => RoomPocketIdBase + instanceId * IdSlotsPerInstance + roomIndex;

    /// <summary>The deployable id of an acceleration gate of a dungeon: the instance and the gate's place in its dungeon's gate list.</summary>
    public static long AccelerationGateDeployableId(long instanceId, int gateIndex) => AccelerationGateIdBase + instanceId * IdSlotsPerInstance + gateIndex;

    /// <summary>
    /// Places a dungeon's pocket: its record (kept once) and its body in the scene at its origin. The body is given to the simulation again on each call, which
    /// the simulation takes as given once, so a restart puts it back in the scene by the next call.
    /// </summary>
    public async Task PlaceDeadspacePocketAsync(int solarSystemId, long instanceId, double[] origin, CancellationToken cancellationToken = default) =>
        await PlacePocketAsync(solarSystemId, PocketDeployableId(instanceId), origin, cancellationToken);

    /// <summary>Places an acceleration gate of a dungeon room at its position in the system (the record kept once; the ball is given again on each call).</summary>
    public async Task PlaceAccelerationGateAsync(int solarSystemId, long deployableId, long typeId, double radius, double[] position, CancellationToken cancellationToken = default)
    {
        if (_deployed is null)
        {
            return;
        }
        var type = checked((int)typeId);
        if (await _deployed.GetAsync(deployableId, cancellationToken) is null)
        {
            await _deployed.SaveAsync(new DeployedRecord(
                deployableId, AccelerationGateKind, solarSystemId, AccelerationGateName, type, AccelerationGateGroupId, position, radius, 0, 0, 0, 0, NeverExpires), cancellationToken);
        }
        _sim.PlaceAccelerationGate(new SimPlaceAccelerationGate(deployableId, solarSystemId, AccelerationGateName, type, AccelerationGateGroupId, position, radius));
    }

    /// <summary>
    /// Keeps the pockets and gates in step with the running dungeons: each active one is placed (see <see cref="PlaceDeadspacePocketAsync"/>), with its rooms
    /// and gates when its layout is known, and each pocket or gate whose dungeon is not active any more is taken out of the scene and forgotten.
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
            if (pocket.Layout is { } layout)
            {
                await PlaceLayoutAsync(pocket, layout, running, cancellationToken);
            }
        }
        foreach (var kind in new[] { DeadspacePocketKind, AccelerationGateKind })
        {
            foreach (var record in await _deployed.ByKindAsync(kind, cancellationToken))
            {
                if (!running.Contains(record.DeployableId))
                {
                    _sim.RemoveDeployed(record.DeployableId);
                    await _deployed.RemoveAsync(record.DeployableId, cancellationToken);
                }
            }
        }
    }

    private async Task PlaceLayoutAsync(DungeonPocket pocket, DungeonLayout layout, HashSet<long> running, CancellationToken cancellationToken)
    {
        foreach (var room in layout.Rooms.Where(room => room.Index > 0))
        {
            var id = RoomPocketDeployableId(pocket.InstanceId, room.Index);
            running.Add(id);
            await PlacePocketAsync(pocket.SolarSystemId, id, room.Anchor, cancellationToken);
        }
        foreach (var gate in layout.Gates)
        {
            var id = AccelerationGateDeployableId(pocket.InstanceId, gate.GateIndex);
            running.Add(id);
            await PlaceAccelerationGateAsync(pocket.SolarSystemId, id, gate.TypeId, gate.Radius, gate.Position, cancellationToken);
        }
    }

    private async Task PlacePocketAsync(int solarSystemId, long id, double[] origin, CancellationToken cancellationToken)
    {
        if (_deployed is null)
        {
            return;
        }
        if (await _deployed.GetAsync(id, cancellationToken) is null)
        {
            await _deployed.SaveAsync(new DeployedRecord(
                id, DeadspacePocketKind, solarSystemId, PocketName, 0, 0, origin, DeadspaceSiteRadiusMeters, 0, 0, 0, 0, NeverExpires), cancellationToken);
        }
        _sim.PlaceDeadspacePocket(new SimPlaceDeadspacePocket(id, solarSystemId, PocketName, origin, DeadspaceSiteRadiusMeters));
    }
}
