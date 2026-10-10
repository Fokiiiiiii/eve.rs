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

    public void Add(ClientSession session)
    {
        lock (_gate)
        {
            _sessions.Add(session);
        }
    }

    public void Remove(ClientSession session)
    {
        lock (_gate)
        {
            _sessions.Remove(session);
        }
    }

    public IReadOnlyList<ClientSession> Snapshot()
    {
        lock (_gate)
        {
            return [.. _sessions];
        }
    }
}
