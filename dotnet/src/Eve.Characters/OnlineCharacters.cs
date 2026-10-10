namespace Eve.Characters;

/// <summary>Which session has which character in the game. A character is played by one session at a time.</summary>
public sealed class OnlineCharacters
{
    private readonly object _gate = new();
    private readonly Dictionary<long, long> _bySession = [];
    private readonly Dictionary<long, long> _byCharacter = [];
    private readonly Dictionary<long, long?> _previousBySession = [];

    public int Count
    {
        get
        {
            lock (_gate)
                return _bySession.Count;
        }
    }

    /// <summary>Enters immediately, releasing the session's previous character on success.</summary>
    public bool TryEnter(long characterId, long sessionId)
    {
        lock (_gate)
        {
            if (!TryBeginEnterCore(characterId, sessionId))
                return false;
            CompleteEnterCore(sessionId);
            return true;
        }
    }

    /// <summary>Holds the previous character until entry completes so rollback cannot lose it.</summary>
    public bool TryBeginEnter(long characterId, long sessionId)
    {
        lock (_gate)
            return TryBeginEnterCore(characterId, sessionId);
    }

    private bool TryBeginEnterCore(long characterId, long sessionId)
    {
        if (_previousBySession.ContainsKey(sessionId) ||
            (_byCharacter.TryGetValue(characterId, out var holder) && holder != sessionId))
            return false;

        _previousBySession[sessionId] = _bySession.TryGetValue(sessionId, out var previous) ? previous : null;
        _bySession[sessionId] = characterId;
        _byCharacter[characterId] = sessionId;
        return true;
    }

    public void CompleteEnter(long sessionId)
    {
        lock (_gate)
            CompleteEnterCore(sessionId);
    }

    private void CompleteEnterCore(long sessionId)
    {
        if (_previousBySession.Remove(sessionId, out var previous) && previous is { } characterId &&
            _bySession.GetValueOrDefault(sessionId) != characterId)
            _byCharacter.Remove(characterId);
    }

    public void CancelEnter(long sessionId)
    {
        lock (_gate)
        {
            if (!_previousBySession.Remove(sessionId, out var previous))
                return;
            if (_bySession.Remove(sessionId, out var enteredCharacterId) && enteredCharacterId != previous)
                _byCharacter.Remove(enteredCharacterId);
            if (previous is { } characterId)
            {
                _bySession[sessionId] = characterId;
                _byCharacter[characterId] = sessionId;
            }
        }
    }

    /// <summary>The session's character and any pending entry reservation leave the game.</summary>
    public void Leave(long sessionId)
    {
        lock (_gate)
        {
            if (_bySession.Remove(sessionId, out var characterId))
                _byCharacter.Remove(characterId);
            if (_previousBySession.Remove(sessionId, out var previous) && previous is { } previousCharacterId)
                _byCharacter.Remove(previousCharacterId);
        }
    }

    /// <summary>The characters in the game now, excluding reservations held only for rollback.</summary>
    public IReadOnlyList<long> Snapshot()
    {
        lock (_gate)
            return [.. _bySession.Values];
    }

    public bool IsOnline(long characterId)
    {
        lock (_gate)
            return _byCharacter.ContainsKey(characterId);
    }
}
