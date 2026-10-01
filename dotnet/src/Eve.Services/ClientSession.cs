using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services;

/// <summary>Where a session's outgoing packets go (the connection).</summary>
public interface IPacketSink
{
    void Send(PyValue packet);
}

/// <summary>State a service keeps per bound object on the session.</summary>
public sealed record BoundObjectState(string ObjectId);

/// <summary>
/// One client connection's session: the attributes the client sees (<c>userid</c>, <c>charid</c>,
/// <c>corpid</c>, ...), and the server's bookkeeping about the bound objects it handed out.
/// Attribute values are marshal values so they reach the wire exactly as set.
/// </summary>
public sealed class ClientSession
{
    private readonly Dictionary<string, PyValue> _attributes = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ClientSession(IPacketSink? sink = null) => Sink = sink;

    public IPacketSink? Sink { get; set; }

    public long SessionId { get; init; }

    /// <summary>The client's id on the network (the address session notifications are sent to).</summary>
    public long ClientId { get; init; }

    /// <summary>The proxy node the client is connected to.</summary>
    public int NodeId { get; init; } = MachoMessage.ProxyNodeId;

    /// <summary>The bound object a call in progress was addressed to (null for an unbound call).</summary>
    public string? CurrentBoundObjectId { get; set; }

    public string? LastBoundObjectId { get; set; }

    /// <summary>Per service, the OID of the bound object this session holds.</summary>
    public Dictionary<string, string> BoundObjectIds { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, BoundObjectState> BoundObjectStates { get; } = new(StringComparer.Ordinal);

    private readonly List<string> _boundOids = [];

    /// <summary>Notes that a bound object was handed to this session (the dispatcher does it when a call answers with one).</summary>
    public void RememberBoundObject(string oid)
    {
        lock (_gate)
        {
            if (!_boundOids.Contains(oid))
            {
                _boundOids.Add(oid);
            }
        }
    }

    /// <summary>Notes that a bound object is gone (released by the client, or dropped by the server).</summary>
    public void ForgetBoundObject(string oid)
    {
        lock (_gate)
        {
            _boundOids.Remove(oid);
        }
    }

    /// <summary>The bound objects handed to this session that are still there, oldest first.</summary>
    public IReadOnlyList<string> BoundOids
    {
        get
        {
            lock (_gate)
            {
                return [.. _boundOids];
            }
        }
    }

    public PyValue? Get(string name)
    {
        lock (_gate)
        {
            return _attributes.GetValueOrDefault(name);
        }
    }

    public void Set(string name, PyValue value)
    {
        lock (_gate)
        {
            _attributes[name] = value;
        }
    }

    public long? GetInt(string name) => Get(name) switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };

    /// <summary>The account id (<c>userid</c>).</summary>
    public long? UserId
    {
        get => GetInt("userid");
        set => Set("userid", value is { } id ? new PyInt(id) : PyNone.Instance);
    }

    public long? CharacterId
    {
        get => GetInt("charid");
        set => Set("charid", value is { } id ? new PyInt(id) : PyNone.Instance);
    }

    public void Send(PyValue packet)
    {
        lock (_gate) Sink?.Send(packet);
    }

    private readonly List<Action> _afterResponse = [];

    /// <summary>
    /// Runs <paramref name="action"/> once the answer to the call in progress has been sent (the reference's <c>afterCallResponse</c>): the events
    /// a call causes that the client expects after its answer. Dropped if the call fails.
    /// </summary>
    public void AfterResponse(Action action)
    {
        lock (_gate)
        {
            _afterResponse.Add(action);
        }
    }

    /// <summary>Runs what the call in progress queued with <see cref="AfterResponse"/>; the dispatcher does this once the answer is sent.</summary>
    public void RunAfterResponse()
    {
        Action[] pending;
        lock (_gate)
        {
            pending = [.. _afterResponse];
            _afterResponse.Clear();
        }
        foreach (var action in pending)
        {
            action();
        }
    }

    internal void DropAfterResponse()
    {
        lock (_gate)
        {
            _afterResponse.Clear();
        }
    }

    private int _closed;
    private int _notificationSequence = -1;

    /// <summary>
    /// Sends the client a named event (<c>OnServerSkillsChanged</c>, ...) addressed by <paramref name="idType"/> (<c>charid</c>,
    /// <c>clientID</c>, ...). The session numbers its notifications from 0.
    /// </summary>
    public void SendNotification(string name, string idType, params PyValue[] args)
    {
        lock (_gate)
            Send(ClientNotification.Build(UserId ?? 0, NodeId, name, idType, args, ++_notificationSequence));
    }

    /// <summary>Raised once, when the connection of this session has ended: modules release what the session held.</summary>
    public event Action? Closed;

    /// <summary>Ends the session. Later calls do nothing.</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            Closed?.Invoke();
        }
    }

    /// <summary>
    /// Sets session attributes. The ones the client tracks (<see cref="SessionAttributes.ClientVisible"/>) that
    /// actually change are announced to it in one <c>SessionChangeNotification</c>, in the order given; the others
    /// are the server's own. Returns what was announced.
    /// </summary>
    public IReadOnlyList<SessionChange> Change(params (string Name, PyValue Value)[] values)
    {
        List<SessionChange> announced = [];
        lock (_gate)
        {
            foreach (var (name, value) in values)
            {
                var old = _attributes.GetValueOrDefault(name) ?? PyNone.Instance;
                _attributes[name] = value;
                if (SessionAttributes.ClientVisible.Contains(name) && !Equals(old, value))
                    announced.Add(new SessionChange(name, old, value));
            }
            // Keep the announced order identical to the committed attribute order.
            if (announced.Count > 0)
                Send(SessionChangeNotification.Build(SessionId, ClientId, UserId ?? 0, NodeId, announced));
        }
        return announced;
    }
}
