namespace Eve.Services;

/// <summary>What <c>/npc</c> made: the NPCs that came into space around the pilot (how many of each, and how many of them have no weapon the simulation can fire), or why none did.</summary>
public sealed record NpcSpawnReport(
    string? Error, IReadOnlyList<string> Suggestions, string SelectionName, IReadOnlyList<(string Name, int Count)> Composition, int Requested, int Unarmed = 0)
{
    public const string NotInSpace = "NOT_IN_SPACE";
    public const string ShipNotFound = "SHIP_NOT_FOUND";
    public const string SpawnFailed = "NPC_NATIVE_SPAWN_FAILED";

    public int Spawned => Composition.Sum(part => part.Count);

    public static NpcSpawnReport Failed(string error, IReadOnlyList<string>? suggestions = null) => new(error, suggestions ?? [], "", [], 0);
}

/// <summary>What the services ask of space to make NPCs (<c>/npc</c>).</summary>
public interface ISpaceNpcs
{
    /// <summary>
    /// Spawns <paramref name="amount"/> NPCs of what <paramref name="query"/> names (a profile or a pool of the authored NPCs; nothing named is the default pool)
    /// at random places around the session's ship, armed with what their hulls carry.
    /// </summary>
    /// <param name="stationary">QA: the NPCs are given no top speed, so they hold where they are (<c>/npc ... still</c>).</param>
    Task<NpcSpawnReport> SpawnNpcsAsync(ClientSession session, string query, int amount, CancellationToken cancellationToken, bool stationary = false);
}
