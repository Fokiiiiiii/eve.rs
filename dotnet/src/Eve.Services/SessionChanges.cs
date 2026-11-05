using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services;

/// <summary>An attribute of a session changing from one value to another.</summary>
public sealed record SessionChange(string Name, PyValue Old, PyValue New);

public static class SessionAttributes
{
    /// <summary>
    /// The session attributes the client keeps a copy of and is told about when they change. Anything else on a
    /// session is the server's own bookkeeping and is never sent.
    /// </summary>
    public static readonly IReadOnlySet<string> ClientVisible = new HashSet<string>(StringComparer.Ordinal)
    {
        "role", "countryCode", "charid", "corpid", "corpAccountKey", "hqID", "baseID", "allianceid", "factionid", "warfactionid",
        "genderID", "bloodlineID", "raceID", "schoolID", "stationid", "stationid2", "structureid", "solarsystemid",
        "solarsystemid2", "locationid", "worldspaceid", "constellationid", "regionid", "shipid", "fleetid", "fleetrole",
        "wingid", "squadid", "corprole", "rolesAtAll", "rolesAtBase", "rolesAtHQ", "rolesAtOther",
    };
}

/// <summary>The packet that tells a client which of its session attributes changed.</summary>
public static class SessionChangeNotification
{
    public const string ClassName = "carbon.common.script.net.machoNetPacket.SessionChangeNotification";

    /// <summary>
    /// <c>(SESSIONCHANGENOTIFICATION, node, client, userID, (sessionID, (0, {name: (old, new)}), [node]), {}, ...)</c>,
    /// padded to the fourteen elements of every packet.
    /// </summary>
    public static PyValue Build(long sessionId, long clientId, long userId, int nodeId, IReadOnlyList<SessionChange> changes)
    {
        var entries = changes.Select(c => new KeyValuePair<PyValue, PyValue>(new PyStr(c.Name), new PyTuple([c.Old, c.New]))).ToList();
        var source = new MachoAddress(AddressKind.Node, NodeId: new PyInt(nodeId), CallId: new PyInt(0));
        var destination = new MachoAddress(AddressKind.Client, ClientId: new PyLong(clientId), CallId: new PyInt(0));
        PyValue[] packet =
        [
            new PyInt(MachoMessage.SessionChangeNotification),
            source.Encode(),
            destination.Encode(),
            userId != 0 ? new PyInt(userId) : PyNone.Instance,
            new PyTuple([new PyLong(sessionId), new PyTuple([new PyInt(0), new PyDict(entries)]), new PyList([new PyInt(nodeId)])]),
            new PyDict([]),
            .. Enumerable.Repeat<PyValue>(PyNone.Instance, 8),
        ];
        return new PyObject(new PyStr(ClassName), new PyTuple(packet));
    }
}

/// <summary>The packet that delivers a named broadcast event or calls a client-side service.</summary>
public static class ClientNotification
{
    public const string ClassName = "carbon.common.script.net.machoNetPacket.Notification";

    /// <summary>
    /// <c>(NOTIFICATION, node, broadcast(name, idType), userID, ((0, marshal((0, (1, args)))),), {sn: sequence}, ...)</c>:
    /// the arguments travel as a marshalled stream of their own inside the packet, and <c>sn</c> counts the session's
    /// notifications from 0.
    /// </summary>
    public static PyValue Build(long userId, int nodeId, string name, string idType, IReadOnlyList<PyValue> args, int sequence)
    {
        var inner = MarshalCodec.Encode(new PyTuple([new PyInt(0), new PyTuple([new PyInt(1), new PyTuple(args)])]));
        var destination = new MachoAddress(AddressKind.Broadcast, BroadcastId: name, IdType: idType);
        return BuildPacket(userId, nodeId, destination, inner, sequence);
    }

    /// <summary>Builds a notification addressed to a client-side service method.</summary>
    public static PyValue BuildService(long userId, int nodeId, string service, string method, IReadOnlyList<PyValue> args, int sequence)
    {
        var inner = MarshalCodec.Encode(new PyTuple([new PyInt(1), new PyStr(method), new PyTuple(args)]));
        return BuildPacket(userId, nodeId, MachoAddress.Any(service), inner, sequence);
    }

    private static PyValue BuildPacket(long userId, int nodeId, MachoAddress destination, byte[] inner, int sequence)
    {
        var source = new MachoAddress(AddressKind.Node, NodeId: new PyInt(nodeId));
        PyValue[] packet =
        [
            new PyInt(MachoMessage.Notification),
            source.Encode(),
            destination.Encode(),
            userId != 0 ? new PyInt(userId) : PyNone.Instance,
            new PyTuple([new PyTuple([new PyInt(0), new PyBuffer(inner)])]),
            Py.Dict(("sn", new PyInt(sequence))),
            .. Enumerable.Repeat<PyValue>(PyNone.Instance, 8),
        ];
        return new PyObject(new PyStr(ClassName), new PyTuple(packet));
    }
}
