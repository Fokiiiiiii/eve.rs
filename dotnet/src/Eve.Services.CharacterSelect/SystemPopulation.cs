using Eve.Services;
using Eve.Universe;

namespace Eve.Services.CharacterSelect;

/// <summary>
/// How many characters are in the game in each solar system, against a cap per system. The count is live state: a
/// session that plays a character is counted in the system its session says it is in, docked or in space.
/// </summary>
public sealed class SystemPopulation
{
    /// <summary>The cap of a system when none is configured. The reference's cap is not published; this one is eve.rs's own.</summary>
    public const int DefaultCapacity = 300;

    /// <summary>How many alternative systems the selection screen is offered at most.</summary>
    public const int MaxAlternatives = 8;

    /// <summary>The Pochven region: no character is loaded into it as an alternative.</summary>
    public const int PochvenRegionId = 10000070;

    /// <summary>The first wormhole system id; wormhole space is never offered as an alternative.</summary>
    public const int FirstWormholeSystemId = 31000000;

    private readonly CharacterSessions _sessions;
    private readonly SolarSystemCatalog _systems;

    private readonly object _gate = new();
    private readonly Dictionary<Seat, (long System, long Character, long? Session)> _seats = [];

    public SystemPopulation(CharacterSessions sessions, SolarSystemCatalog systems, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(systems);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _sessions = sessions;
        _systems = systems;
        Capacity = capacity;
    }

    /// <summary>The most characters a system takes before the selection screen looks for another one.</summary>
    public int Capacity { get; }

    /// <summary>The static data of a system, or null when it is unknown.</summary>
    public SolarSystemInfo? Get(long solarSystemId) => solarSystemId is > 0 and <= int.MaxValue ? _systems.Get((int)solarSystemId) : null;

    /// <summary>The characters in the game in a system, not counting <paramref name="characterId"/> or the replaced session.</summary>
    public int LoadOf(long solarSystemId, long characterId, long? replacingSessionId = null) =>
        Loads(characterId, replacingSessionId).GetValueOrDefault(solarSystemId);

    /// <summary>True when the system takes one more character, excluding the selected character and a replaced session.</summary>
    public bool HasRoom(long solarSystemId, long characterId, long? replacingSessionId = null) =>
        LoadOf(solarSystemId, characterId, replacingSessionId) < Capacity;

    /// <summary>
    /// The nearest systems that have room for the character, nearest first, never the home system, wormhole space or Pochven.
    /// At most <see cref="MaxAlternatives"/>.
    /// </summary>
    public IReadOnlyList<int> Alternatives(int homeSystemId, long characterId, long? replacingSessionId = null)
    {
        var loads = Loads(characterId, replacingSessionId);
        return [.. _systems.Systems
            .Where(system => system.Id != homeSystemId && IsOfferable(system) && loads.GetValueOrDefault(system.Id) < Capacity)
            .Select(system => (system.Id, Distance: _systems.LightYearsBetween(homeSystemId, system.Id) ?? double.MaxValue))
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Id)
            .Take(MaxAlternatives)
            .Select(candidate => candidate.Id)];
    }

    private static bool IsOfferable(SolarSystemInfo system) =>
        system.Id < FirstWormholeSystemId && system.RegionId != PochvenRegionId;

    /// <summary>
    /// Takes a seat in a system for a character entering it, or answers null when the system has no room. The room is checked and the seat
    /// taken under one lock, so two entries into the last room admit one. A seat counts as a character in its system until it is disposed:
    /// the entry disposes it when it ends (the session counts from then on, so a refused entry leaves the count as it was).
    /// </summary>
    public Seat? TryReserve(long solarSystemId, long characterId, long? replacingSessionId = null)
    {
        lock (_gate)
        {
            if (LoadOf(solarSystemId, characterId, replacingSessionId) >= Capacity)
                return null;
            var seat = new Seat(this);
            _seats[seat] = (solarSystemId, characterId, replacingSessionId);
            return seat;
        }
    }

    private void Release(Seat seat)
    {
        lock (_gate) _seats.Remove(seat);
    }

    /// <summary>A place taken in a system by <see cref="TryReserve"/>; disposing it gives the place back.</summary>
    public sealed class Seat : IDisposable
    {
        private SystemPopulation? _owner;

        internal Seat(SystemPopulation owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(this);
    }

    /// <summary>Characters in the game per system, leaving out <paramref name="characterId"/> and a session being replaced.</summary>
    private Dictionary<long, int> Loads(long characterId, long? replacingSessionId = null)
    {
        var loads = new Dictionary<long, int>();
        KeyValuePair<Seat, (long System, long Character, long? Session)>[] reservations;
        lock (_gate)
            reservations = [.. _seats];
        var reservedSessions = reservations
            .Where(reservation => reservation.Value.Session is not null)
            .Select(reservation => reservation.Value.Session!.Value)
            .ToHashSet();
        foreach (var session in _sessions.All())
        {
            if (session.SessionId == replacingSessionId || reservedSessions.Contains(session.SessionId) ||
                session.CharacterId is not { } playing || playing == characterId)
                continue;
            if ((session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid")) is { } system)
                loads[system] = loads.GetValueOrDefault(system) + 1;
        }
        foreach (var reservation in reservations)
        {
            var (system, playing, session) = reservation.Value;
            if (playing != characterId && (replacingSessionId is null || session != replacingSessionId))
                loads[system] = loads.GetValueOrDefault(system) + 1;
        }
        return loads;
    }
}
