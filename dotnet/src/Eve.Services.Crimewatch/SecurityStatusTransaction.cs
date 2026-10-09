using System.Collections.Concurrent;
using Eve.Crimewatch;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Universe;

namespace Eve.Services.Crimewatch;

/// <summary>An attack on the ship of another pilot, and what it made of the attacker (see <see cref="Aggression"/>).</summary>
/// <summary>A canonical (FirstCharacterID &lt; SecondCharacterID) crimewatch pair row.</summary>
/// <summary>One persisted security-status history row projected to the client's exact field names.</summary>
public sealed record SecurityStatusTransaction(
    int? EventTypeID,
    long EventDateUnixMs,
    long? OtherOwnerID,
    int? OtherTypeID,
    long? LocationID,
    double? Modification,
    double? NewValue);
