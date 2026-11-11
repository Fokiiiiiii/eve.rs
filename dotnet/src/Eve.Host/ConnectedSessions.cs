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

private readonly Dictionary<long, ClientSession> _byAccount = new();

    public void Add(ClientSession session)
    {
        bool added;
        lock (_gate)
            added = _sessions.Add(session);
        if (added) Added?.Invoke(session);
    }

    /// <summary>Atomically makes a newly opened account session the account's sole connected client.</summary>
    public ClientSession? AddExclusive(ClientSession session)
    {
        var accountId = session.UserId ?? throw new InvalidOperationException("An opened client session must have an account id.");
        ClientSession? previous;
        lock (_gate)
        {
            _byAccount.TryGetValue(accountId, out previous);
            if (ReferenceEquals(previous, session))
                return null;
            if (previous is not null)
                _sessions.Remove(previous);
            _sessions.Add(session);
            _byAccount[accountId] = session;
        }
        if (previous is not null)
            Removed?.Invoke(previous);
        Added?.Invoke(session);
        return previous;
    }

    public void Remove(ClientSession session)
    {
        bool removed;
        lock (_gate)
        {
            removed = _sessions.Remove(session);
            if (session.UserId is { } accountId
                && _byAccount.TryGetValue(accountId, out var current)
                && ReferenceEquals(current, session))
                _byAccount.Remove(accountId);
        }
        if (removed) Removed?.Invoke(session);
    }

    public IReadOnlyList<ClientSession> Snapshot()
    {
        lock (_gate)
        {
            return [.. _sessions];
        }
    }

    /// <summary>Asks every connected session to close with the reason. A session that cannot be reached is skipped, so it never blocks the caller. Returns how many were asked.</summary>
    public int DisconnectAll(string reason)
    {
        var asked = 0;
        foreach (var session in Snapshot())
        {
            try
            {
                session.RequestDisconnect(reason);
                asked++;
            }
            catch (Exception)
            {
                // A session without a front link cannot be told; the shutdown goes on.
            }
        }
        return asked;
    }
}
