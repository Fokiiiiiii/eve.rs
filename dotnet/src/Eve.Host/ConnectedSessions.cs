using Eve.Services;

namespace Eve.Host;

/// <summary>
/// The sessions the host serves right now: from the moment one is opened until it ends. A notification to every client
/// (the reference's <c>Broadcast</c>) goes to each of them.
/// </summary>
public sealed class ConnectedSessions
{
    private readonly HashSet<ClientSession> _sessions = new(ReferenceEqualityComparer.Instance);
    private readonly object _gate = new();

    public event Action<ClientSession>? Added;
    public event Action<ClientSession>? Removed;

    public void Add(ClientSession session)
    {
        bool added;
        lock (_gate)
            added = _sessions.Add(session);
        if (added) Added?.Invoke(session);
    }

    public void Remove(ClientSession session)
    {
        bool removed;
        lock (_gate)
            removed = _sessions.Remove(session);
        if (removed) Removed?.Invoke(session);
    }

    public IReadOnlyList<ClientSession> Snapshot()
    {
        lock (_gate)
        {
            return [.. _sessions];
        }
    }
}
