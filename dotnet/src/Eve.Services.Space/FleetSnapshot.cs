using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

public sealed record FleetSnapshot(long FleetId, long? AutoJoinSquadId, IReadOnlyList<FleetMemberSnapshot> Members,
    IReadOnlyList<FleetWingSnapshot> Wings, IReadOnlyList<FleetJoinRequest> JoinRequests,
    int MaxMemberCount = FleetStore.MaxMembers, bool IsFreeMove = false, bool IsRegistered = false, string Motd = "");
