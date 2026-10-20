namespace Eve.Services;

/// <summary>The durable ship and character identity attached to a pending self-destruct deadline.</summary>
public sealed record ShipSelfDestructCountdown(long ShipId, long CharacterId, int SolarSystemId, long InstanceId, long ExpiresAtMs);

/// <summary>One of the two things <c>/suicide</c> tells about: what was left in space or what the pilot is in now (a label, a name and an item id).</summary>
public sealed record SelfDestructPart(string Label, string? Name, long? Id);

/// <summary>
/// What <c>/suicide</c> did: the ship that was destroyed, what it left (a wreck, or the pilot's corpse when it was a capsule) and what the pilot is in now (a capsule,
/// or a rookie ship at the home station).
/// </summary>
public sealed record SelfDestruct(long ShipId, SelfDestructPart Left, SelfDestructPart Now);

/// <summary>What the services ask of space to destroy a pilot's own ship (<c>/suicide</c>).</summary>
public interface ISpaceDeath
{
    /// <summary>
    /// Destroys the ship the session flies in space and answers when the pilot is in a capsule, or, when the ship was the capsule, awake at the home station
    /// (null when the session has no ship in space or the simulation did not do it in time).
    /// </summary>
    Task<SelfDestruct?> SelfDestructAsync(ClientSession session, CancellationToken cancellationToken);
}
