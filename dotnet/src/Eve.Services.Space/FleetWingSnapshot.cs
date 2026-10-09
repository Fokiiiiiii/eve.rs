using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

public sealed record FleetWingSnapshot(long WingId, string Name, IReadOnlyList<FleetSquadSnapshot> Squads);
