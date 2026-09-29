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

    /// <summary>The bound object a call in progress was addressed to (null for an unbound call).</summary>
    public string? CurrentBoundObjectId { get; set; }

    public string? LastBoundObjectId { get; set; }

    /// <summary>Per service, the OID of the bound object this session holds.</summary>
    public Dictionary<string, string> BoundObjectIds { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, BoundObjectState> BoundObjectStates { get; } = new(StringComparer.Ordinal);

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

    public void Send(PyValue packet) => Sink?.Send(packet);
}
