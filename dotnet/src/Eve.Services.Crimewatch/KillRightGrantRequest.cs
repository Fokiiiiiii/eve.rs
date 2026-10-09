using System.Collections.Concurrent;
using Eve.Crimewatch;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Universe;

namespace Eve.Services.Crimewatch;

/// <summary>
/// What crimewatch does as pilots attack one another: it keeps the timers and flags (<see cref="PgCrimewatchStore"/>, by the rules of <see cref="Aggression"/>) and tells the clients what
/// changed, as the reference does: the pilot its own timers (<c>OnWeaponsTimerUpdate</c>, <c>OnPvpTimerUpdate</c>, <c>OnNpcTimerUpdate</c>, <c>OnCriminalTimerUpdate</c>,
/// <c>OnDisapprovalTimerUpdate</c>, each <c>(state, expiry)</c>, only for the ones that changed) and everyone in space in the system who is flagged now and who is not any more
/// (<c>OnSystemCriminalFlagUpdates</c>: the ones that went idle, the new suspects, the new criminals). A timer that runs out is told when it does: the state is in PostgreSQL and the wait
/// for its end is <see cref="LocalTimers"/> (a process-local wait; a process that starts takes it up again with <see cref="ResumeAsync"/>). One step at a time: crimewatch is told of
/// an attack and of an end by the same door, and what it told last is what it compares with.
/// </summary>
public sealed record KillRightGrantRequest(
    long OwnerCharacterId,
    long TargetCharacterId,
    long SolarSystemId,
    long CreatedAtUnixMs);
