namespace Eve.Services;

/// <summary>What <c>/spawn</c> and <c>/spawnn</c> made: the item IDs of the ships that came into space, or why they did not (<see cref="Error"/>).</summary>
public sealed record SpawnResult(IReadOnlyList<long> ShipIds, string? Error = null)
{
    public const string NotInSpace = "NOT_IN_SPACE";
    public const string SpawnFailed = "SPAWN_FAILED";
    public const string SpaceUnavailable = "SPACE_UNAVAILABLE";
}

/// <summary>A ship the host made in space and still has: its item ID, its type, and its place (null when the simulation does not give one).</summary>
public sealed record SpawnedShip(long ShipId, int TypeId, IReadOnlyList<double>? Position);

/// <summary>The ships the host made in the pilot's solar system, with the pilot's own place (for a range).</summary>
public sealed record SpawnedView(IReadOnlyList<double>? Own, IReadOnlyList<SpawnedShip> Ships);

/// <summary>
/// The game master's spawns on this host: <c>/spawn</c>, <c>/spawnn</c>, <c>/unspawn</c> and <c>/nuke</c>. The ships are the bare hulls of <c>/deathtest</c> (nobody
/// flies them) and the NPC ships the host makes; what is removed leaves the scene as a ship leaves it, with no wreck.
/// </summary>
public interface ISpaceSpawns
{
    /// <summary>Makes <paramref name="count"/> ships of a type in space around the pilot's ship, between 3 km and <paramref name="deviationMeters"/> off it.</summary>
    Task<SpawnResult> SpawnHullsAsync(ClientSession session, int typeId, int count, double deviationMeters, CancellationToken cancellationToken);

    /// <summary>The ships the host made in the pilot's solar system (null when the pilot is not in space).</summary>
    Task<SpawnedView?> SpawnedAsync(ClientSession session, CancellationToken cancellationToken);

    /// <summary>Takes the ships out of space; the number of them that were the host's and went.</summary>
    Task<int> RemoveAsync(IReadOnlyList<long> shipIds, CancellationToken cancellationToken);
}
