namespace Eve.Services;

/// <summary>What <c>/deathtest</c> made: the hulls that were spawned around the pilot, and what they are told they will do.</summary>
public sealed record HullField(string ShipName, int Spawned, double RadiusMeters, double DelaySeconds);

/// <summary>What the services ask of space to make ships that nobody flies (<c>/deathtest</c>).</summary>
public interface ISpaceHulls
{
    /// <summary>
    /// Spawns <paramref name="count"/> unpiloted hulls of a ship type (the type of the pilot's ship when null) at random places around the session's ship, and
    /// destroys them after a moment. Null when the session has no ship in space or the type is not known.
    /// </summary>
    Task<HullField?> DeathTestAsync(ClientSession session, int? shipTypeId, int count, CancellationToken cancellationToken);
}
