using System.Collections.Concurrent;

namespace Eve.Services;

/// <summary>
/// The sessions that have a character in the game, so something the server does on its own (a skill that finishes training) can reach the
/// client that plays the character. A session is forgotten when it closes or plays another character.
/// </summary>
public sealed class CharacterSessions
{
    private readonly ConcurrentDictionary<long, ClientSession> _byCharacter = new();

    /// <summary>Records that <paramref name="session"/> plays <paramref name="characterId"/>.</summary>
    public void Enter(long characterId, ClientSession session)
    {
        _byCharacter[characterId] = session;
        session.Closed += () => _byCharacter.TryRemove(new KeyValuePair<long, ClientSession>(characterId, session));
    }

    /// <summary>The session that plays the character now, or null.</summary>
    public ClientSession? Of(long characterId) =>
        _byCharacter.TryGetValue(characterId, out var session) && session.CharacterId == characterId ? session : null;

    /// <summary>The sessions of an account that have a character in the game.</summary>
    public IReadOnlyList<ClientSession> OfUser(long userId) =>
        [.. _byCharacter.Values.Where(s => s.UserId == userId).Distinct()];

    /// <summary>The sessions with a character that are in space (not docked) in a solar system.</summary>
    public IReadOnlyList<ClientSession> InSpace(long solarSystemId) =>
        [.. _byCharacter.Values.Where(s => s.CharacterId is not null && s.GetInt("stationid") is null && s.GetInt("solarsystemid") == solarSystemId).Distinct()];

    /// <summary>Current character sessions docked in the specified structure.</summary>
    public IReadOnlyList<ClientSession> InStructure(long structureId) =>
        structureId > 0
            ? [.. _byCharacter.Values.Where(session =>
                session.CharacterId is not null &&
                session.GetInt("structureid") == structureId).Distinct()]
            : [];

    public IReadOnlyList<ClientSession> OfCorporation(long corporationId) =>
        [.. _byCharacter.Values.Where(s => s.CharacterId is not null && s.GetInt("corpid") == corporationId).Distinct()];
}
