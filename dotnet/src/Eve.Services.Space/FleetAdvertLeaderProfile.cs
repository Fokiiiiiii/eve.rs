using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

public sealed record FleetAdvertLeaderProfile(long? CorporationId, long? AllianceId, long? WarFactionId, double SecurityStatus);
