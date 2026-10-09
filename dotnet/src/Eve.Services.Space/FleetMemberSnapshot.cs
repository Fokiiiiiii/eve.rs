using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// The <c>beyonce</c> service the client binds to for the solar system it is in: binding it starts the destiny stream, and its commands steer
/// the ship. The simulation does the work (<see cref="SpaceSessions"/>); this reads the client's calls.
/// </summary>
public sealed record FleetMemberSnapshot(long CharacterId, long WingId, long SquadId, int Role, int Job,
    bool AcceptsFleetWarp = true, bool AcceptsConduitJumps = true, bool AcceptsFleetRegroups = true, long? ShipTypeId = null)
{
    public long FleetId { get; init; }
}
