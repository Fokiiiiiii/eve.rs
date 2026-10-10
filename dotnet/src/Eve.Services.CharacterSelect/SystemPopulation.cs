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

    /// <summary>The characters in the game in a system, not counting <paramref name="characterId"/>.</summary>
    public int LoadOf(long solarSystemId, long characterId) => Loads(characterId).GetValueOrDefault(solarSystemId);

    /// <summary>True when the system takes one more character (not counting <paramref name="characterId"/>).</summary>
    public bool HasRoom(long solarSystemId, long characterId) => LoadOf(solarSystemId, characterId) < Capacity;

    /// <summary>
    /// The nearest systems that have room for the character, nearest first, never the home system, wormhole space or Pochven.
    /// At most <see cref="MaxAlternatives"/>.
    /// </summary>
    public IReadOnlyList<int> Alternatives(int homeSystemId, long characterId)
    {
        var loads = Loads(characterId);
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

    /// <summary>Characters in the game per system, leaving out <paramref name="characterId"/> (the one being placed).</summary>
    private Dictionary<long, int> Loads(long characterId)
    {
        var loads = new Dictionary<long, int>();
        foreach (var session in _sessions.All())
        {
            if (session.CharacterId is not { } playing || playing == characterId)
                continue;
            if ((session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid")) is { } system)
                loads[system] = loads.GetValueOrDefault(system) + 1;
        }
        return loads;
    }
}
