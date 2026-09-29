using Eve.Marshal;

namespace Eve.Macho;

/// <summary>Options for <see cref="ResponseBuilder.CallResponse"/>.</summary>
public sealed record CallResponseOptions
{
    /// <summary>Extra <c>namedPayload</c> entries, ahead of the ones derived from the result.</summary>
    public IReadOnlyList<KeyValuePair<PyValue, PyValue>> NamedPayloadEntries { get; init; } = [];

    /// <summary>The context key; when null the request's oob is echoed.</summary>
    public PyValue? ContextKey { get; init; }

    /// <summary>Send the result directly instead of inside a substream.</summary>
    public bool WrapResultInSubstream { get; init; } = true;
}

/// <summary>Builds the packets a server sends back. Byte-compatible with the reference dispatcher.</summary>
public static class ResponseBuilder
{
    public const string CallRspClass = "carbon.common.script.net.machoNetPacket.CallRsp";
    public const string ErrorResponseClass = "carbon.common.script.net.machoNetPacket.ErrorResponse";
    public const string PingRspClass = "carbon.common.script.net.machoNetPacket.PingRsp";

    private const long FileTimeEpochTicks = 116444736000000000L;
    private const long FileTimeTicksPerMillisecond = 10000L;

    private static PyValue Packet(string className, PyValue[] state) => new PyObject(new PyStr(className), new PyTuple(state));

    private static PyValue[] Tail(MachoPacket request) =>
    [
        JsSemantics.OrNull(request.Bssid),
        JsSemantics.OrNull(request.SpanId),
        JsSemantics.OrNull(request.Extra9),
        JsSemantics.OrNull(request.Extra10),
        JsSemantics.OrNull(request.Extra11),
        JsSemantics.OrNull(request.Extra12),
        JsSemantics.OrNull(request.Extra13),
    ];

    /// <summary><c>session.userid || pkt.userID || null</c>.</summary>
    private static PyValue User(PyValue? sessionUserId, MachoPacket request) =>
        JsSemantics.Truthy(sessionUserId) ? sessionUserId! : JsSemantics.OrNull(request.UserId);

    /// <summary>The source of a response mirrors the request's destination (<c>buildCallResponseSourceAddress</c>).</summary>
    private static MachoAddress ResponseSource(MachoPacket request, string? responseServiceName)
    {
        var destination = request.Dest;
        var service = !string.IsNullOrEmpty(responseServiceName) ? responseServiceName : destination.Service;
        var callId = JsSemantics.Truthy(destination.CallId) ? destination.CallId : null;
        if (destination.Kind == AddressKind.Any)
        {
            return new MachoAddress(AddressKind.Any, service, callId);
        }
        return new MachoAddress(
            AddressKind.Node,
            service,
            callId,
            NodeId: destination.Kind == AddressKind.Node && destination.NodeId is not null and not PyNone
                ? destination.NodeId
                : new PyInt(MachoMessage.ProxyNodeId));
    }

    private static MachoAddress ToClient(MachoPacket request) =>
        request.Source with { CallId = request.CallId, Service = null };

    /// <summary>A CALL_RSP for <paramref name="request"/> carrying <paramref name="result"/>.</summary>
    public static PyValue CallResponse(
        MachoPacket request,
        PyValue result,
        PyValue? sessionUserId,
        string? responseServiceName,
        CallResponseOptions? options = null)
    {
        options ??= new CallResponseOptions();
        var registrations = BoundObjects.Registrations(result);
        var compressedPart = CachedObjects.CompressedPart(result);

        var entries = new List<KeyValuePair<PyValue, PyValue>>(options.NamedPayloadEntries);
        entries = MergeRegistrations(entries, registrations);
        if (compressedPart > 0)
        {
            entries.Add(new(new PyStr("compressedPart"), new PyInt(compressedPart)));
        }

        PyValue[] payload = options.WrapResultInSubstream ? [new PySubStream(result)] : [result];
        var contextKey = options.ContextKey ?? JsSemantics.OrNull(request.Oob);

        return Packet(CallRspClass,
        [
            new PyInt(MachoMessage.CallRsp),
            ResponseSource(request, responseServiceName).Encode(),
            ToClient(request).Encode(),
            User(sessionUserId, request),
            new PyTuple(payload),
            new PyDict(entries),
            contextKey,
            .. Tail(request),
        ]);
    }

    /// <summary>
    /// Adds the <c>OID+</c> entry that tells the client about newly bound objects, merging with any
    /// the caller supplied (<c>mergeBoundObjectRegistrationsIntoNamedPayload</c>).
    /// </summary>
    private static List<KeyValuePair<PyValue, PyValue>> MergeRegistrations(
        List<KeyValuePair<PyValue, PyValue>> existing,
        IReadOnlyList<BoundObjectRegistration> registrations)
    {
        if (registrations.Count == 0)
        {
            return existing;
        }
        var merged = new List<KeyValuePair<PyValue, PyValue>>();
        var oids = new List<KeyValuePair<PyValue, PyValue>>();
        var hasOidEntry = false;

        void Set(string oid, PyValue? refId)
        {
            var value = refId ?? PyNone.Instance;
            var at = oids.FindIndex(pair => pair.Key is PyStr key && key.Value == oid);
            if (at >= 0)
            {
                oids[at] = new(oids[at].Key, value);
            }
            else
            {
                oids.Add(new(new PyStr(oid), value));
            }
        }

        foreach (var entry in existing)
        {
            if (JsSemantics.ToJsString(entry.Key) != "OID+")
            {
                merged.Add(entry);
                continue;
            }
            hasOidEntry = true;
            foreach (var (objectId, refId) in JsSemantics.DictEntries(entry.Value))
            {
                if (objectId is PyStr text && text.Value.StartsWith(BoundObjects.OidPrefix, StringComparison.Ordinal))
                {
                    Set(text.Value, JsSemantics.Truthy(refId) || refId is PyInt or PyLong ? refId : null);
                }
            }
        }
        foreach (var registration in registrations)
        {
            Set(registration.Oid, registration.RefId);
        }
        if (!hasOidEntry || oids.Count > 0)
        {
            merged.Add(new(new PyStr("OID+"), new PyDict(oids)));
        }
        return merged;
    }

    /// <summary>An ERRORRESPONSE for <paramref name="request"/> (<c>_sendErrorResponse</c>).</summary>
    public static PyValue ErrorResponse(
        MachoPacket request,
        int errorCode,
        PyValue payload,
        PyValue? sessionUserId,
        string? responseServiceName)
    {
        var service = !string.IsNullOrEmpty(responseServiceName) ? responseServiceName : request.Dest.Service;
        return Packet(ErrorResponseClass,
        [
            new PyInt(MachoMessage.ErrorResponse),
            (request.Dest with { Service = service }).Encode(),
            ToClient(request).Encode(),
            User(sessionUserId, request),
            new PyTuple([new PyInt(MachoMessage.CallReq), new PyInt(errorCode), new PyTuple([new PySubStream(payload)])]),
            new PyDict([]),
            JsSemantics.OrNull(request.Oob),
            .. Tail(request),
        ]);
    }

    /// <summary>A PING_RSP with the timing entries the client expects.</summary>
    public static PyValue PingResponse(MachoPacket request, long nowUnixMs)
    {
        var now = (nowUnixMs * FileTimeTicksPerMillisecond) + FileTimeEpochTicks;
        PyValue Entry(string step) => new PyTuple([new PyLong(now - 20), new PyLong(now), new PyStr(step)]);
        var timings = new PyList([
            Entry("proxy::handle_message"),
            Entry("proxy::writing"),
            Entry("server::handle_message"),
            Entry("server::turnaround"),
            Entry("proxy::handle_message"),
            Entry("proxy::writing"),
        ]);
        return Packet(PingRspClass,
        [
            new PyInt(MachoMessage.PingRsp),
            request.Dest.Encode(),
            request.Source.Encode(),
            JsSemantics.OrNull(request.UserId),
            new PyTuple([timings]),
            new PyDict([]),
            JsSemantics.OrNull(request.Oob),
            .. Tail(request),
        ]);
    }
}

/// <summary>Cached-object results tell the client how many bytes of compressed cache they carry.</summary>
public static class CachedObjects
{
    private static readonly string[] Names =
    [
        "objectCaching.CachedObject",
        "carbon.common.script.net.objectCaching.CachedObject",
    ];

    /// <summary>The total compressed pickle bytes of the cached objects in a result (<c>extractCachedObjectCompressedPart</c>).</summary>
    public static long CompressedPart(PyValue? value)
    {
        long total = 0;
        Walk(value, new HashSet<PyValue>(ReferenceEqualityComparer.Instance), ref total);
        return total;
    }

    private static void Walk(PyValue? value, HashSet<PyValue> seen, ref long total)
    {
        if (value is null or PyNone or PyInt or PyLong or PyReal or PyBool or PyStr or PyBytes or PyBuffer || !seen.Add(value))
        {
            return;
        }
        switch (value)
        {
            case PyTuple tuple:
                foreach (var item in tuple.Items)
                {
                    Walk(item, seen, ref total);
                }
                return;
            case PyObject obj:
                var name = ObjectName(obj.Name);
                if (Names.Contains(name))
                {
                    var args = obj.Args is PyTuple tuple ? tuple.Items : [];
                    var pickle = args.Count > 4 ? args[4] : null;
                    var compressed = args.Count > 5 && JsSemantics.IsNumber(args[5]) && JsSemantics.NumberValue(args[5]) == 1;
                    if (compressed && pickle is PyBytes bytes)
                    {
                        total += bytes.Value.Length;
                        return;
                    }
                }
                // Other objects: the reference walks the object's own fields (type, name, args).
                Walk(obj.Args, seen, ref total);
                return;
            case PyList list:
                foreach (var item in list.Items)
                {
                    Walk(item, seen, ref total);
                }
                return;
            case PyDict dict:
                foreach (var (_, entry) in dict.Entries)
                {
                    Walk(entry, seen, ref total);
                }
                return;
            case PySubStream { Decoded: { } inner }:
                Walk(inner, seen, ref total);
                return;
            case PySubStruct sub:
                Walk(sub.Value, seen, ref total);
                return;
        }
    }

    private static string ObjectName(PyValue name) => name switch
    {
        PyStr text => text.Value,
        PyBytes or PyBuffer => JsSemantics.PlainText(name)!,
        PyWString text => text.Value,
        PyToken text => text.Value,
        _ => "",
    };
}
