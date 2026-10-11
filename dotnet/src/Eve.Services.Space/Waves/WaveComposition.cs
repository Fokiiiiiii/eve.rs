using Eve.Npc;

namespace Eve.Services.Space.Waves;

/// <summary>
/// The NPCs a wave asks for, resolved against the NPC authority: a spawn query names a pool or a profile of the authority exactly (never a partial match), and anything else is
/// refused with the reason, so no NPC type is ever invented for a wave.
/// </summary>
public static class WaveComposition
{
    public static (NpcSelection? Selection, string? Refusal) Resolve(NpcSelector selector, string spawnQuery)
    {
        var authority = selector.Authority;
        var profile = authority.ResolveProfile(spawnQuery).Match;
        var pool = authority.ResolvePool(spawnQuery).Match;
        if (profile is not { Exact: true } && pool is not { Exact: true })
        {
            var nearest = authority.ResolvePool(spawnQuery).Suggestions.Concat(authority.ResolveProfile(spawnQuery).Suggestions).Take(3).ToList();
            return (null, $"no NPC pool or profile is named '{spawnQuery}' exactly" + (nearest.Count > 0 ? $" (nearest: {string.Join(", ", nearest)})" : ""));
        }
        var (selection, error, _) = selector.Select(spawnQuery);
        return selection is null ? (null, $"'{spawnQuery}' does not resolve: {error}") : (selection, null);
    }
}
