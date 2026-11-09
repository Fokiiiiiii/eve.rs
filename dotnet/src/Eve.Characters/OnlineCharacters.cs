namespace Eve.Characters;

/// <summary>Which session has which character in the game. A character is played by one session at a time.</summary>
public sealed class OnlineCharacters
{
    private readonly object _gate = new();
    private readonly Dictionary<long, long> _bySession = [];
    private readonly Dictionary<long, long> _byCharacter = [];
    private readonly Dictionary<long, long?> _previousBySession = [];
    private readonly Dictionary<long, LifecycleGate> _lifecycleGates = [];

    /// <summary>Serializes character entry with changes to that character's offline deletion state.</summary>
    public async ValueTask<IAsyncDisposable> AcquireCharacterLifecycleLockAsync(
        long characterId,
        CancellationToken cancellationToken = default)
    {
        LifecycleGate gate;
        lock (_gate)
        {
            if (_lifecycleGates.TryGetValue(characterId, out var existing))
                gate = existing;
            else
                _lifecycleGates.Add(characterId, gate = new LifecycleGate());
            gate.References++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new LifecycleLease(this, characterId, gate);
        }
        catch
        {
            DropLifecycleReference(characterId, gate);
            throw;
        }
    }

    private void ReleaseLifecycleLock(long characterId, LifecycleGate gate)
    {
        gate.Semaphore.Release();
        DropLifecycleReference(characterId, gate);
    }

    private void DropLifecycleReference(long characterId, LifecycleGate gate)
    {
        lock (_gate)
        {
            gate.References--;
            if (gate.References == 0 && _lifecycleGates.TryGetValue(characterId, out var current) && ReferenceEquals(current, gate))
            {
                _lifecycleGates.Remove(characterId);
                gate.Semaphore.Dispose();
            }
        }
    }

    private sealed class LifecycleGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }

    private sealed class LifecycleLease(OnlineCharacters owner, long characterId, LifecycleGate gate) : IAsyncDisposable
    {
        private OnlineCharacters? _owner = owner;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseLifecycleLock(characterId, gate);
            return ValueTask.CompletedTask;
        }
    }

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

    public bool CompleteEnter(long sessionId)
    {
        lock (_gate)
            return CompleteEnterCore(sessionId);
    }

    private bool CompleteEnterCore(long sessionId)
    {
        if (!_previousBySession.Remove(sessionId, out var previous))
            return false;
        if (previous is { } characterId && _bySession.GetValueOrDefault(sessionId) != characterId)
            _byCharacter.Remove(characterId);
        return true;
    }

    public bool CancelEnter(long sessionId)
    {
        lock (_gate)
        {
            if (!_previousBySession.Remove(sessionId, out var previous))
                return false;
            if (_bySession.Remove(sessionId, out var enteredCharacterId) && enteredCharacterId != previous)
                _byCharacter.Remove(enteredCharacterId);
            if (previous is { } characterId)
            {
                _bySession[sessionId] = characterId;
                _byCharacter[characterId] = sessionId;
            }
            return true;
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
