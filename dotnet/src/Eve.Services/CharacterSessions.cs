using System.Collections.Concurrent;

namespace Eve.Services;

/// <summary>
/// The sessions that have a character in the game, so something the server does on its own (a skill that finishes training) can reach the
/// client that plays the character. A session is forgotten when it closes or plays another character.
/// </summary>
public sealed class CharacterSessions
{
    private readonly ConcurrentDictionary<long, ClientSession> _byCharacter = new();
    private readonly object _presenceGate = new();
    private readonly Action<long, bool, IReadOnlyList<(long CharacterId, ClientSession Session)>>? _presenceChanged;

    public CharacterSessions(Action<long, bool, IReadOnlyList<(long CharacterId, ClientSession Session)>>? presenceChanged = null) =>
        _presenceChanged = presenceChanged;

    /// <summary>Records that <paramref name="session"/> plays <paramref name="characterId"/>.</summary>
    public void Enter(long characterId, ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_presenceGate)
        {
            if (session.IsClosed)
                return;

            foreach (var (previousCharacterId, previousSession) in _byCharacter)
                if (previousCharacterId != characterId && ReferenceEquals(previousSession, session) &&
                    _byCharacter.TryRemove(new KeyValuePair<long, ClientSession>(previousCharacterId, session)))
                    _presenceChanged?.Invoke(previousCharacterId, false, Snapshot());

            var wasPresent = _byCharacter.TryGetValue(characterId, out var current) && current.CharacterId == characterId;
            var alreadyRegistered = wasPresent && ReferenceEquals(current, session);
            _byCharacter[characterId] = session;
            if (!alreadyRegistered)
                session.Closed += () => OnClosed(characterId, session);

            if (session.IsClosed)
            {
                if (_byCharacter.TryRemove(new KeyValuePair<long, ClientSession>(characterId, session)) && wasPresent)
                    _presenceChanged?.Invoke(characterId, false, Snapshot());
                return;
            }

            if (!wasPresent)
                _presenceChanged?.Invoke(characterId, true, Snapshot());
        }
    }

    public void Leave(long characterId, ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_presenceGate)
            if (_byCharacter.TryRemove(new KeyValuePair<long, ClientSession>(characterId, session)))
                _presenceChanged?.Invoke(characterId, false, Snapshot());
    }

    private void OnClosed(long characterId, ClientSession session)
    {
        lock (_presenceGate)
            if (_byCharacter.TryRemove(new KeyValuePair<long, ClientSession>(characterId, session)))
                _presenceChanged?.Invoke(characterId, false, Snapshot());
    }

    private IReadOnlyList<(long CharacterId, ClientSession Session)> Snapshot() =>
        _byCharacter
            .Where(pair => !pair.Value.IsClosed && pair.Value.CharacterId == pair.Key)
            .Select(pair => (pair.Key, pair.Value))
            .ToArray();

    /// <summary>The session that plays the character now, or null.</summary>
    public ClientSession? Of(long characterId) =>
        _byCharacter.TryGetValue(characterId, out var session) && !session.IsClosed && session.CharacterId == characterId ? session : null;

    /// <summary>The sessions of an account that have a character in the game.</summary>
    public IReadOnlyList<ClientSession> OfUser(long userId) =>
        [.. _byCharacter.Values.Where(s => s.UserId == userId).Distinct()];

    /// <summary>The sessions with a character that are in space (not docked) in a solar system.</summary>
    public IReadOnlyList<ClientSession> InSpace(long solarSystemId) =>
        [.. _byCharacter.Values.Where(s => s.CharacterId is not null && s.DockedLocationId is null && s.GetInt("solarsystemid") == solarSystemId).Distinct()];

    /// <summary>Current character sessions in a solar system, including docked pilots.</summary>
    public IReadOnlyList<ClientSession> InSolarSystem(long solarSystemId) =>
        [.. _byCharacter.Values.Where(session =>
            session.CharacterId is not null &&
            (session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid")) == solarSystemId).Distinct()];

    /// <summary>Current character sessions docked at the specified station.</summary>
    public IReadOnlyList<ClientSession> InStation(long stationId) =>
        stationId > 0
            ? [.. _byCharacter.Values.Where(session =>
                session.CharacterId is not null &&
                (session.GetInt("stationid") ?? session.GetInt("stationID")) == stationId).Distinct()]
            : [];

    /// <summary>Current character sessions docked in the specified structure.</summary>
    public IReadOnlyList<ClientSession> InStructure(long structureId) =>
        structureId > 0
            ? [.. _byCharacter.Values.Where(session =>
                session.CharacterId is not null &&
                session.GetInt("structureid") == structureId).Distinct()]
            : [];

    public IReadOnlyList<ClientSession> OfCorporation(long corporationId) =>
        [.. _byCharacter.Values.Where(s => s.CharacterId is not null && s.GetInt("corpid") == corporationId).Distinct()];

public IReadOnlyList<ClientSession> All() => _byCharacter.Values.Distinct().ToArray();
}
