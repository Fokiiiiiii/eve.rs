namespace Eve.Services.Space;

/// <summary>Enforces the per-character fleet broadcast interval across all fleet RPC entry points.</summary>
public sealed class FleetBroadcastRateLimiter
{
    public const long SameBroadcastIntervalMs = 2_000;
    // The client truncates 2/3 second to 6,666,666 100 ns ticks; the first
    // elapsed whole millisecond at or beyond that deadline is 667 ms.
    public const long DifferentBroadcastIntervalMs = 667;

    private readonly object _gate = new();
    private readonly Dictionary<(long FleetId, long CharacterId), (string Name, long SentAtMs)> _last = [];
    private long _nextPruneAtMs = long.MinValue;

    /// <summary>Returns true and records the send time when this character may send the broadcast.</summary>
    public bool TryAccept(long fleetId, long characterId, string name, long nowUnixMs)
    {
        if (fleetId <= 0 || characterId <= 0 || string.IsNullOrEmpty(name))
            return false;

        lock (_gate)
        {
            Prune(nowUnixMs);
            var key = (fleetId, characterId);
            if (_last.TryGetValue(key, out var previous))
            {
                if (nowUnixMs < previous.SentAtMs)
                    return false;

                var elapsedMs = nowUnixMs - previous.SentAtMs;
                var requiredMs = previous.Name == name ? SameBroadcastIntervalMs : DifferentBroadcastIntervalMs;
                if (elapsedMs < requiredMs)
                    return false;
            }

            _last[key] = (name, nowUnixMs);
            return true;
        }
    }

    private void Prune(long nowUnixMs)
    {
        if (nowUnixMs < _nextPruneAtMs)
            return;

        var expired = _last
            .Where(entry => nowUnixMs >= entry.Value.SentAtMs && nowUnixMs - entry.Value.SentAtMs >= SameBroadcastIntervalMs)
            .Select(entry => entry.Key)
            .ToArray();
        foreach (var key in expired)
            _last.Remove(key);

        _nextPruneAtMs = nowUnixMs > long.MaxValue - SameBroadcastIntervalMs
            ? long.MaxValue
            : nowUnixMs + SameBroadcastIntervalMs;
    }
}
