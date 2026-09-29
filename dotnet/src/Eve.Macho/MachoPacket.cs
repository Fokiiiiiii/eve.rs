using Eve.Marshal;

namespace Eve.Macho;

/// <summary>Packet types (<c>MACHONETMSG_TYPE</c>).</summary>
public static class MachoMessage
{
    public const int AuthenticationReq = 0;
    public const int AuthenticationRsp = 1;
    public const int IdentificationReq = 2;
    public const int IdentificationRsp = 3;
    public const int CallReq = 6;
    public const int CallRsp = 7;
    public const int TransportClosed = 8;
    public const int ResolveReq = 10;
    public const int ResolveRsp = 11;
    public const int Notification = 12;
    public const int ErrorResponse = 15;
    public const int SessionChangeNotification = 16;
    public const int SessionInitialStateNotification = 18;
    public const int PingReq = 20;
    public const int PingRsp = 21;
    public const int MovementNotification = 100;

    /// <summary>The node id the proxy answers as.</summary>
    public const int ProxyNodeId = 0xffaa;

    public static string TypeName(int code) => code switch
    {
        AuthenticationReq => "AUTHENTICATION_REQ",
        AuthenticationRsp => "AUTHENTICATION_RSP",
        IdentificationReq => "IDENTIFICATION_REQ",
        IdentificationRsp => "IDENTIFICATION_RSP",
        4 => "__Fake_Invalid_Type",
        CallReq => "CALL_REQ",
        CallRsp => "CALL_RSP",
        TransportClosed => "TRANSPORTCLOSED",
        ResolveReq => "RESOLVE_REQ",
        ResolveRsp => "RESOLVE_RSP",
        Notification => "NOTIFICATION",
        ErrorResponse => "ERRORRESPONSE",
        SessionChangeNotification => "SESSIONCHANGENOTIFICATION",
        SessionInitialStateNotification => "SESSIONINITIALSTATENOTIFICATION",
        PingReq => "PING_REQ",
        PingRsp => "PING_RSP",
        MovementNotification => "MOVEMENTNOTIFICATION",
        _ => $"UNKNOWN({code})",
    };
}

/// <summary>Error codes of an ERRORRESPONSE (<c>MACHONETERR_TYPE</c>).</summary>
public static class MachoErrorType
{
    public const int UnMachoDestination = 0;
    public const int UnMachoChannel = 1;
    public const int WrappedException = 2;
}

/// <summary>
/// A decoded MachoNet packet: the fields of the 14-element state tuple. Values the server only
/// passes through (user id, oob, trace ids) stay raw so they re-encode as received.
/// </summary>
public sealed record MachoPacket(
    int Type,
    MachoAddress Source,
    MachoAddress Dest,
    PyValue UserId,
    PyValue Payload,
    PyValue NamedPayload,
    PyValue Oob,
    PyValue Bssid,
    PyValue SpanId,
    PyValue Extra9,
    PyValue Extra10,
    PyValue Extra11,
    PyValue Extra12,
    PyValue Extra13)
{
    public string TypeName => MachoMessage.TypeName(Type);

    /// <summary>The service the packet is addressed to.</summary>
    public string? Service => Dest.Service is { Length: > 0 } service ? service : null;

    /// <summary>The call id: the request's source address carries it, else the destination's, else 0.</summary>
    public PyValue CallId => JsSemantics.Truthy(Source.CallId) ? Source.CallId! : JsSemantics.Truthy(Dest.CallId) ? Dest.CallId! : new PyInt(0);

    /// <summary>Decodes a packet (<c>decodePacket</c>); null when it is not one.</summary>
    public static MachoPacket? Decode(PyValue? decoded)
    {
        var tuple = decoded is PyObject { Args: not null } obj ? obj.Args : decoded;
        if (tuple is not PyTuple { Items.Count: >= 6 } state)
        {
            return null;
        }
        var items = state.Items;
        // Fields the server only passes back go out as the reference would send them.
        PyValue At(int index) => items.Count > index ? JsSemantics.AsReceived(items[index]) : PyNone.Instance;

        return new MachoPacket(
            JsSemantics.IsNumber(items[0]) ? (int)JsSemantics.NumberValue(items[0]) : 0,
            MachoAddress.Decode(items[1]),
            MachoAddress.Decode(items[2]),
            At(3),
            items[4],
            JsSemantics.OrNull(items[5]),
            At(6),
            At(7),
            At(8),
            At(9),
            At(10),
            At(11),
            At(12),
            At(13));
    }
}
