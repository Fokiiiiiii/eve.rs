namespace Eve.Characters;

/// <summary>Which session has which character in the game. A character is played by one session at a time.</summary>
public sealed class OnlineCharacters
{
    private readonly object _gate = new();
    private readonly Dictionary<long, long> _bySession = [];
    private readonly Dictionary<long, long> _byCharacter = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byCharacter.Count;
            }
        }
    }

    /// <summary>
    /// Puts <paramref name="characterId"/> in the game for <paramref name="sessionId"/>, which stops holding any character
    /// it had. False when another session already has it (nothing changes then).
    /// </summary>
    public bool TryEnter(long characterId, long sessionId)
    {
        lock (_gate)
        {
            if (_byCharacter.TryGetValue(characterId, out var holder))
            {
                return holder == sessionId;
            }
            if (_bySession.Remove(sessionId, out var previous))
            {
                _byCharacter.Remove(previous);
            }
            _bySession[sessionId] = characterId;
            _byCharacter[characterId] = sessionId;
            return true;
        }
    }

    /// <summary>The session's character leaves the game (its connection closed).</summary>
    public void Leave(long sessionId)
    {
        lock (_gate)
        {
            if (_bySession.Remove(sessionId, out var characterId))
            {
                _byCharacter.Remove(characterId);
            }
        }
    }

    public bool IsOnline(long characterId)
    {
        lock (_gate)
        {
            return _byCharacter.ContainsKey(characterId);
        }
    }
}
