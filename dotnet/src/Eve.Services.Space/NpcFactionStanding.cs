using Eve.Relations;

namespace Eve.Services.Space;

/// <summary>
/// </summary>
public sealed class NpcFactionStanding(RelationStore relations, Func<long> nowMs)
{

    public const double LossPerKillRaw = -0.00006875;
    private const int CombatAggressionEventTypeId = 76;

    private static readonly long DebounceMs = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;
    private readonly Dictionary<(long Character, int Faction, int System), long> _lastApplied = [];
    private readonly object _gate = new();

    /// <summary>Applies the loss for one kill. Answers the new standing, or null when nothing changed (no faction, debounced, or at the floor).</summary>
    public async Task<double?> ApplyAsync(long characterId, int factionId, int systemId, CancellationToken cancellationToken = default, int npcTypeId = 0)
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

        var standing = await relations.ValueAsync(RelationKinds.Standing.Name, factionId, characterId, 0, cancellationToken: cancellationToken);
        var next = UndockContraband.StandingAfter(standing, LossPerKillRaw);
        if (next == standing)
            return null;
        var reason = System.Text.Json.JsonSerializer.Serialize(new
        {
            eventTypeID = CombatAggressionEventTypeId,
            modification = LossPerKillRaw,
            msg = "Combat aggression",
            int_1 = npcTypeId > 0 ? npcTypeId : (int?)null,
        });
        await relations.SetAsync(RelationKinds.Standing.Name, factionId, characterId, next, reason, cancellationToken);
        lock (_gate)
        {
            _lastApplied[key] = now;
        }
        return next;
    }
}
