using Eve.Relations;

namespace Eve.Services.Space;

/// <summary>
/// The standing a pilot loses with a faction when it destroys one of the faction's NPCs (EveJS <c>factionKillStandingRuntime.js</c>; the wiki's
/// "Faction standing" page has no text, so the rule is EveJS's, calibrated from its golden mission logs). One raw change per kill, debounced per pilot,
/// faction and system for ten minutes (in memory, as EveJS keeps the window).
/// </summary>
public sealed class NpcFactionStanding(RelationStore relations, Func<long> nowMs)
{
    /// <summary>The raw change of one kill (EveJS <c>FACTION_STANDING_LOSS_PER_KILL_RAW</c>).</summary>
    public const double LossPerKillRaw = -0.00006875;

    private static readonly long DebounceMs = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;
    private const string Reason = "Combat aggression";
    private readonly Dictionary<(long Character, int Faction, int System), long> _lastApplied = [];
    private readonly object _gate = new();

    /// <summary>Applies the loss for one kill. Answers the new standing, or null when nothing changed (no faction, debounced, or at the floor).</summary>
    public async Task<double?> ApplyAsync(long characterId, int factionId, int systemId, CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || factionId <= 0)
            return null;
        var now = nowMs();
        var key = (characterId, factionId, systemId);
        lock (_gate)
        {
            if (_lastApplied.TryGetValue(key, out var last) && now - last < DebounceMs)
                return null;
        }

        var standing = await relations.ValueAsync(RelationKinds.Standing.Name, characterId, factionId, 0, cancellationToken: cancellationToken);
        var next = UndockContraband.StandingAfter(standing, LossPerKillRaw);
        if (next == standing)
            return null;
        await relations.SetAsync(RelationKinds.Standing.Name, characterId, factionId, next, Reason, cancellationToken);
        lock (_gate)
        {
            _lastApplied[key] = now;
        }
        return next;
    }
}
