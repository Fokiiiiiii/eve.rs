using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

public sealed record FleetJoinRequest(
    long CharacterId,
    long? CorporationId = null,
    long? AllianceId = null,
    long? WarFactionId = null,
    double SecurityStatus = 0);
