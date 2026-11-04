using System.Collections.Concurrent;
using Eve.Crimewatch;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Universe;

namespace Eve.Services.Crimewatch;

public sealed record KillRightCreated(
    long KillRightId,
    long OwnerCharacterId,
    long TargetCharacterId,
    long SolarSystemId,
    long CreatedAtUnixMs);
