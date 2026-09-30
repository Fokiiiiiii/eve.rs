namespace Eve.Services;

/// <summary>What <c>/suicide</c> did: the ship that was destroyed, the wreck it left (if any) and the capsule its pilot is in.</summary>
public sealed record SelfDestruct(long ShipId, string? WreckName, long? WreckId, string CapsuleName, long CapsuleId);

/// <summary>What the services ask of space to destroy a pilot's own ship (<c>/suicide</c>).</summary>
public interface ISpaceDeath
{
    /// <summary>
    /// Destroys the ship the session flies in space and answers when the pilot is in a capsule (or null when the session has no ship in space, the ship is a
    /// capsule, or the simulation did not do it in time).
    /// </summary>
    Task<SelfDestruct?> SelfDestructAsync(ClientSession session, CancellationToken cancellationToken);
}
