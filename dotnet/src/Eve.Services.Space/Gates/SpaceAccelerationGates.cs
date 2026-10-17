using Eve.Dungeons;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Space.Waves;
using Eve.Space;

namespace Eve.Services.Space.Gates;

/// <summary>The group and the race of a ship's type (the static data the gate's rules read).</summary>
public sealed record ShipClass(int GroupId, int RaceId);

/// <summary>
/// <c>keeper.ActivateAccelerationGate</c> in space (docs/project/ACCELERATION_GATES.md; EveJS <c>keeperService.js</c> <c>activateAccelerationGateForSession</c>): a
/// pilot in a dungeon site walks the acceleration gate of one of its rooms and comes out in the room the gate leads to. The checks are the reference's, in its
/// order: the gate is one of the pilot's running dungeon, the ship may use it (the race list of the gate, else the blacklisted ship groups), and the simulation
/// checks the reach and the warp.
/// </summary>
public sealed class SpaceAccelerationGates(
    Func<CancellationToken, Task<IReadOnlyList<DungeonPocket>>> activePockets,
    Func<ClientSession, CancellationToken, Task<long?>> currentInstanceOf,
    Func<long, CancellationToken, Task<IReadOnlySet<string>>> clearedRooms,
    Func<ClientSession, CancellationToken, Task<ShipClass?>> shipClassOf,
    Func<ClientSession, long, double[], CancellationToken, Task<CommandReply>> transit,
    Func<long, string, long, CancellationToken, Task> recordRoom)
{
    /// <summary>The ship groups a gate bars when it names no race (EveJS <c>ACCELERATION_GATE_BLACKLISTED_GROUP_IDS</c>: capsules and the capital hulls).</summary>
    public static readonly IReadOnlySet<int> BlacklistedGroups = new HashSet<int> { 29, 30, 485, 513, 547, 659, 883, 902, 1538, 4594, 5120 };

    /// <summary>The instance and the gate's place in its dungeon's list, for a gate id of the host (null when the id is not one of a gate).</summary>
    public static (long InstanceId, int GateIndex)? GateOf(long gateId)
    {
        var slot = gateId - SpaceContainers.AccelerationGateDeployableId(0, 0);
        return slot >= 0 ? (slot / SpaceContainers.IdSlotsPerInstance, (int)(slot % SpaceContainers.IdSlotsPerInstance)) : null;
    }

    /// <summary>Walks the pilot through the gate; the answer is the client's <c>None</c> (the pilot is warping to the other room).</summary>
    public async Task<PyValue> ActivateAsync(ClientSession session, long gateId, CancellationToken cancellationToken)
    {
        if (GateOf(gateId) is not { } where)
        {
            throw MachoErrors.ThrowUserError("DeniedTargetAttemptFailed");
        }
        if (await currentInstanceOf(session, cancellationToken) != where.InstanceId)
        {
            throw MachoErrors.ThrowUserError("DeniedTargetAttemptFailed");
        }
        var pocket = (await activePockets(cancellationToken)).FirstOrDefault(p => p.InstanceId == where.InstanceId);
        if (pocket?.Layout is not { } layout || layout.Gates.FirstOrDefault(g => g.GateIndex == where.GateIndex) is not { } gate)
        {
            throw MachoErrors.ThrowUserError("DeniedTargetAttemptFailed");
        }
        // a gate out of a room is locked until the room's waves are done (EveJS: "The acceleration gate is locked.")
        if (!(await clearedRooms(where.InstanceId, cancellationToken)).Contains(gate.SourceRoomKey))
        {
            throw MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr("The acceleration gate is locked.")));
        }
        if (await shipClassOf(session, cancellationToken) is not { } ship)
        {
            throw MachoErrors.ThrowUserError("DeniedShipChanged");
        }
        if (gate.AllowedRaces.Count > 0)
        {
            if (!gate.AllowedRaces.Contains(ship.RaceId))
            {
                throw MachoErrors.ThrowUserError("DunShipCannotWarp");
            }
        }
        else if (BlacklistedGroups.Contains(ship.GroupId))
        {
            throw MachoErrors.ThrowUserError("DunBlacklistCannotWarp");
        }
        if (layout.Room(gate.DestinationRoomKey) is not { } room)
        {
            throw MachoErrors.ThrowUserError("DeniedTargetAttemptFailed");
        }
        var reply = await transit(session, gateId, room.EntryPoint, cancellationToken);
        if (!reply.Accepted)
        {
            throw Refused(reply.Reason);
        }
        // the pilot is in the room it leads to: the site's game state keeps it (the keeper's current dungeon and the presence queries read it)
        await recordRoom(where.InstanceId, room.Key, room.RoomId ?? 0, cancellationToken);
        return PyNone.Instance;
    }

    /// <summary>The client's message for a walk that cannot be done (the reference's mapping of the simulation's reasons).</summary>
    private static MachoWrappedException Refused(string? reason) => reason switch
    {
        "TOO_FAR_FROM_GATE" => MachoErrors.ThrowUserError("TargetTooFar"),
        "IN_WARP" => MachoErrors.ThrowUserError("ShipInWarp"),
        "NO_SHIP_IN_SPACE" or "NOT_IN_SPACE" => MachoErrors.ThrowUserError("DeniedShipChanged"),
        _ => MachoErrors.ThrowUserError("DeniedTargetAttemptFailed"),
    };
}
