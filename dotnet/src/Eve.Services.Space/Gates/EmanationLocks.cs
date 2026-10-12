using System.Collections.Concurrent;

namespace Eve.Services.Space.Gates;

/// <summary>
/// The emanation lock of Zarzakh (EVE University wiki, "Zarzakh": "players are locked to that specific gate for six hours"; entering through
/// another gate resets the timer and locks the player to that gate; leaving through any other gate is refused while the lock runs; exits by the
/// Deathless Shipcaster, clone jump or pod are not refused; entry from outside is not affected). The lock is held in memory: a host restart drops it.
/// </summary>
public sealed class EmanationLocks
{
    /// <summary>The lock's length from entry: six hours (the wiki).</summary>
    public const long DurationMs = 6L * 60 * 60 * 1000;

    /// <summary>The systems that have the lock (Zarzakh, 30100000).</summary>
    public static readonly IReadOnlySet<int> RestrictedSystems = new HashSet<int> { 30_100_000 };

    public sealed record Lock(int SolarSystemId, long GateId, long ExpiresMs);

    private readonly ConcurrentDictionary<long, Lock> _locks = new();

    /// <summary>Records an entry into a restricted system through <paramref name="gateId"/> (the arrival gate). Other systems have no lock.</summary>
    public void Entered(long characterId, int solarSystemId, long gateId, long nowMs)
    {
        if (RestrictedSystems.Contains(solarSystemId))
        {
            _locks[characterId] = new Lock(solarSystemId, gateId, nowMs + DurationMs);
        }
    }

    /// <summary>The character's lock in this system while it runs, or null.</summary>
    public Lock? Active(long characterId, int solarSystemId, long nowMs) =>
        _locks.TryGetValue(characterId, out var locked) && locked.SolarSystemId == solarSystemId && locked.ExpiresMs > nowMs ? locked : null;

    /// <summary>Whether the character may leave the system through this gate: no lock runs there, or the lock names this gate.</summary>
    public bool MayLeave(long characterId, int solarSystemId, long gateId, long nowMs) =>
        Active(characterId, solarSystemId, nowMs) is not { } locked || locked.GateId == gateId;
}
