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
    private readonly Dictionary<long, Action<PyValue?>> _clientCalls = new();
    private readonly AsyncLocal<NotificationBatch?> _notificationBatch = new();

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

    public IReadOnlyDictionary<string, PyValue> SnapshotAttributes()
    {
        lock (_gate)
            return new Dictionary<string, PyValue>(_attributes, StringComparer.Ordinal);
    }

    public IReadOnlyList<SessionChange> RestoreAttributes(IReadOnlyDictionary<string, PyValue> previous)
    {
        lock (_gate)
        {
            var names = _attributes.Keys.Union(previous.Keys, StringComparer.Ordinal).ToArray();
            try
            {
                return Change([.. names.Select(name => (name, previous.GetValueOrDefault(name) ?? PyNone.Instance))]);
            }
            finally
            {
                foreach (var name in names)
                    if (!previous.ContainsKey(name))
                        _attributes.Remove(name);
            }
        }
    }

    public long? GetInt(string name) => Get(name) switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };

    /// <summary>The station or Upwell structure this client is docked in, or null while in space.</summary>
    public long? DockedLocationId =>
        GetInt("stationid") ?? GetInt("stationID") ?? GetInt("structureid") ?? GetInt("structureID");

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

    /// <summary>Defers this session's notifications and session changes until the caller commits the batch.</summary>
    public NotificationBatch DeferNotifications()
    {
        lock (_gate)
        {
            if (_notificationBatch.Value is not null)
                throw new InvalidOperationException("A notification batch is already active for this session call.");
            var batch = new NotificationBatch(this);
            _notificationBatch.Value = batch;
            return batch;
        }
    }

    private void SendNotificationPacket(PyValue packet)
    {
        if (_notificationBatch.Value is { } batch && batch.TryDefer(packet))
            return;
        Sink?.Send(packet);
    }

    private void CommitNotifications(NotificationBatch batch)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_notificationBatch.Value, batch))
                throw new InvalidOperationException("The notification batch is not active in this session call.");
            foreach (var packet in batch.CommitPackets())
                Sink?.Send(packet);
        }
    }

    private void EndNotifications(NotificationBatch batch)
    {
        lock (_gate)
        {
            batch.Discard();
            if (ReferenceEquals(_notificationBatch.Value, batch))
                _notificationBatch.Value = null;
        }
    }

    /// <summary>Notifications collected during a character entry; disposal discards any uncommitted packets.</summary>
    public sealed class NotificationBatch(ClientSession session) : IDisposable
    {
        private readonly List<PyValue> _packets = [];
        private int _state;

        internal bool TryDefer(PyValue packet)
        {
            if (_state != 0)
                return false;
            _packets.Add(packet);
            return true;
        }

        internal IReadOnlyList<PyValue> CommitPackets()
        {
            if (_state != 0)
                throw new InvalidOperationException("The notification batch has already ended.");
            _state = 1;
            var packets = _packets.ToArray();
            _packets.Clear();
            return packets;
        }

        internal void Discard()
        {
            if (_state == 0)
            {
                _state = 2;
                _packets.Clear();
            }
        }

        public void Commit() => session.CommitNotifications(this);

        public void Dispose() => session.EndNotifications(this);
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

    /// <summary>Runs the queued post-response actions independently after the call answer has been sent.</summary>
    public void RunAfterResponse(Action<Exception>? onFailure = null)
    {
        Action[] pending;
        lock (_gate)
        {
            pending = [.. _afterResponse];
            _afterResponse.Clear();
        }
        foreach (var action in pending)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                try
                {
                    onFailure?.Invoke(error);
                }
                catch
                {
                    // Reporting a post-response failure must not change the already-sent RPC answer either.
                }
            }
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
    /// <summary>
    /// Calls a client service and runs <paramref name="onResponse"/> with the client's answer (null when the client
    /// </summary>
    public bool SendClientCall(string service, string method, IReadOnlyList<PyValue> args, Action<PyValue?> onResponse, PyDict? kwargs = null)
    {
        if (Sink is null) return false;
        var callId = ClientCallRequest.NextCallId();
        lock (_gate) _clientCalls[callId] = onResponse;
        Send(ClientCallRequest.Build(ClientId, UserId ?? 0, service, method, args, callId, kwargs));
        return true;
    }

    /// <summary>The client's CallRsp to a call of <see cref="SendClientCall"/>: its answer, unwrapped, goes to the callback once.</summary>
    public void CompleteClientCall(long callId, PyValue? answer)
    {
        Action<PyValue?>? onResponse;
        lock (_gate)
        {
            if (!_clientCalls.Remove(callId, out onResponse)) return;
        }
        onResponse(answer);
    }

    public void SendNotification(string name, string idType, params PyValue[] args)
    {
        lock (_gate)
            SendNotificationPacket(ClientNotification.Build(UserId ?? 0, NodeId, name, idType, args, ++_notificationSequence));
    }

    public void SendServiceNotification(string service, string method, IReadOnlyList<PyValue> args)
    {
        lock (_gate)
            SendNotificationPacket(ClientNotification.BuildService(UserId ?? 0, NodeId, service, method, args, ++_notificationSequence));
    }

    public event Action<ClientSession, IReadOnlyList<string>>? AttributesChanged;

    /// <summary>Raised once, when the connection of this session has ended: modules release what the session held.</summary>
    public event Action? Closed;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Ends the session. Later calls do nothing.</summary>
private Action<string>? _disconnectClient;

    /// <summary>Sets the front link action used to close this account's previous connection.</summary>
    public void AttachDisconnectClient(Action<string> disconnect)
    {
        ArgumentNullException.ThrowIfNull(disconnect);
        lock (_gate)
        {
            _disconnectClient = disconnect;
        }
    }

    /// <summary>Requests a clean front-link disconnect with the supplied client-visible reason.</summary>
    public void RequestDisconnect(string reason)
    {
        Action<string> disconnect;
        lock (_gate)
        {
            disconnect = _disconnectClient ?? throw new InvalidOperationException("The session has no front-link disconnect action.");
        }
        disconnect(reason);
    }

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
        List<string> changed = [];
        lock (_gate)
        {
            foreach (var (name, value) in values)
            {
                var old = _attributes.GetValueOrDefault(name) ?? PyNone.Instance;
                _attributes[name] = value;
                if (!Equals(old, value))
                {
                    changed.Add(name);
                    if (SessionAttributes.ClientVisible.Contains(name))
                        announced.Add(new SessionChange(name, old, value));
                }
            }
            // Keep the announced order identical to the committed attribute order.
            if (announced.Count > 0)
                SendNotificationPacket(SessionChangeNotification.Build(SessionId, ClientId, UserId ?? 0, NodeId, announced));
        }
        if (changed.Count > 0)
            AttributesChanged?.Invoke(this, changed);
        return announced;
    }
}
