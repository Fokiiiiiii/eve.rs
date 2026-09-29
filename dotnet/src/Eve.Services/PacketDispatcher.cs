using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services;

/// <summary>
/// Routes decoded MachoNet packets after the handshake: calls to services, pings, and notifications
/// about bound objects. Behaves like the reference server's PacketDispatcher, whose recorded packets
/// the tests replay byte for byte.
/// </summary>
public sealed class PacketDispatcher
{
    private const string ReleaseMethod = "ClientHasReleasedTheseObjects";
    private const string ReleaseKey = "OID-";

    private readonly ServiceRegistry _registry;
    private readonly Func<long> _nowUnixMs;

    public PacketDispatcher(ServiceRegistry registry, Func<long>? nowUnixMs = null)
    {
        _registry = registry;
        _nowUnixMs = nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>Called with a problem worth logging (a failed handler, an unroutable call).</summary>
    public Action<string, Exception?>? OnProblem { get; set; }

    /// <summary>
    /// Handles one decoded packet from <paramref name="session"/>. Returns whether it was handled;
    /// responses go out through the session's sink.
    /// </summary>
    public async Task<bool> DispatchAsync(PyValue raw, ClientSession session, CancellationToken ct = default)
    {
        var packet = MachoPacket.Decode(raw);
        if (packet is null)
        {
            return false;
        }
        switch (packet.Type)
        {
            case MachoMessage.CallReq:
                return await HandleCallRequestAsync(packet, session, ct);
            case MachoMessage.CallRsp:
            case MachoMessage.PingRsp:
            case MachoMessage.SessionChangeNotification:
            case MachoMessage.ErrorResponse:
                return true;
            case MachoMessage.Notification:
                HandleNotification(packet, session);
                return true;
            case MachoMessage.PingReq:
                session.Send(ResponseBuilder.PingResponse(packet, _nowUnixMs()));
                return true;
            default:
                return false;
        }
    }

    // ── calls ───────────────────────────────────────────────────────────────

    private async Task<bool> HandleCallRequestAsync(MachoPacket packet, ClientSession session, CancellationToken ct)
    {
        var serviceName = packet.Service;
        var call = CallRequest.Decode(packet.Payload);
        if (call is null)
        {
            return false;
        }

        // A bound-object call names its object in the call body, not in the address.
        if (string.IsNullOrEmpty(serviceName) && JsSemantics.Truthy(call.RemoteObject))
        {
            serviceName = BoundObjects.ExtractName(call.RemoteObject) ?? serviceName;
        }

        if (string.IsNullOrEmpty(serviceName))
        {
            SendCallResponse(packet, PyNone.Instance, session, null);
            return true;
        }

        var service = _registry.Lookup(serviceName);
        if (service is null)
        {
            if (serviceName.StartsWith(BoundObjects.OidPrefix, StringComparison.Ordinal))
            {
                // A released bound object. Answering None would be read as the call's result (a colony
                // that dies on None.level); UnMachoDestination makes the client unresolve, rebind and
                // retry once.
                SendError(packet, MachoErrors.UserError("UnMachoDestination"), session, serviceName);
                return true;
            }
            SendCallResponse(packet, PyNone.Instance, session, serviceName);
            return true;
        }

        var previousBoundObjectId = session.CurrentBoundObjectId;
        try
        {
            session.CurrentBoundObjectId = serviceName.StartsWith(BoundObjects.OidPrefix, StringComparison.Ordinal) ? serviceName : null;
            var result = await service.CallAsync(new ServiceCall
            {
                Method = call.Method,
                Args = call.Args,
                Kwargs = call.Kwargs,
                Session = session,
                CallId = packet.CallId,
                BoundObjectId = session.CurrentBoundObjectId,
                Cancellation = ct,
            });

            if (result is PyDeferred deferred)
            {
                await deferred.Start(new DeferredContext(
                    packet, session, service.Name, reply => SendCallResponse(packet, reply, session, service.Name)));
                return true;
            }

            RegisterBoundObjects(result, service);
            SendCallResponse(packet, result, session, service.Name);
            return true;
        }
        catch (MachoWrappedException wrapped)
        {
            if (!wrapped.Error.IsUserError)
            {
                OnProblem?.Invoke($"{service.Name}.{call.Method}: wrapped exception {wrapped.Error.ClassName}", wrapped);
            }
            SendError(packet, wrapped.Error, session, service.Name);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The reference answers a failing handler with a None result rather than an error.
            OnProblem?.Invoke($"{service.Name}.{call.Method}: {ex.Message}", ex);
            SendCallResponse(packet, PyNone.Instance, session, service.Name);
            return true;
        }
        finally
        {
            session.CurrentBoundObjectId = previousBoundObjectId;
        }
    }

    private static void SendCallResponse(MachoPacket packet, PyValue result, ClientSession session, string? serviceName) =>
        session.Send(ResponseBuilder.CallResponse(packet, result, session.Get("userid"), serviceName));

    private static void SendError(MachoPacket packet, MachoError error, ClientSession session, string? serviceName) =>
        session.Send(ResponseBuilder.ErrorResponse(packet, error.ErrorCode, error.Payload, session.Get("userid"), serviceName));

    /// <summary>
    /// A result that hands out a bound object (<c>[substruct(substream((oid, refID))), callResult]</c>, or the
    /// substruct itself) registers the object so later calls to its OID reach this service.
    /// </summary>
    private void RegisterBoundObjects(PyValue result, IService service)
    {
        if (!JsSemantics.Truthy(result))
        {
            return;
        }
        try
        {
            if (result is PyTuple tuple)
            {
                foreach (var element in tuple.Items)
                {
                    TryRegister(element, service);
                }
            }
            TryRegister(result, service);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OnProblem?.Invoke($"registering a bound object from a call result failed: {ex.Message}", ex);
        }
    }

    private void TryRegister(PyValue value, IService service)
    {
        if (value is PySubStruct substruct && BoundObjects.TryRegistration(substruct, out var registration))
        {
            _registry.RegisterBoundObject(registration.Oid, service, registration.RefId);
        }
    }

    // ── notifications ───────────────────────────────────────────────────────

    private void HandleNotification(MachoPacket packet, ClientSession session)
    {
        var carriers = new[] { packet.NamedPayload, packet.Oob };
        var hasReleaseMetadata = carriers.Any(carrier =>
            JsSemantics.DictEntries(carrier).Any(entry => JsSemantics.ToJsString(entry.Key) == ReleaseKey));
        var notification = hasReleaseMetadata ? CallRequest.Decode(packet.Payload) : null;
        if (notification is null || notification.Method != ReleaseMethod)
        {
            return;
        }

        var released = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        void Collect(PyValue? value) => BoundObjects.CollectIds(value, oid =>
        {
            if (released.Add(oid))
            {
                ordered.Add(oid);
            }
        });

        Collect(notification.RemoteObject);
        foreach (var carrier in carriers)
        {
            foreach (var (key, value) in JsSemantics.DictEntries(carrier))
            {
                if (JsSemantics.ToJsString(key) == ReleaseKey)
                {
                    Collect(value);
                }
            }
        }

        var refIds = ReleasedRefIds(carriers);
        foreach (var oid in ordered)
        {
            _registry.UnregisterBoundObject(oid, session, refIds.GetValueOrDefault(oid));
        }
    }

    /// <summary>
    /// <c>OID-</c> maps each released object to the refID of the newest bind the client still knew about.
    /// Objects named only by the notification's remote object carry no refID.
    /// </summary>
    private static Dictionary<string, long?> ReleasedRefIds(IEnumerable<PyValue> carriers)
    {
        var refIds = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var carrier in carriers)
        {
            foreach (var (key, value) in JsSemantics.DictEntries(carrier))
            {
                if (JsSemantics.ToJsString(key) != ReleaseKey)
                {
                    continue;
                }
                foreach (var (objectId, refId) in JsSemantics.DictEntries(value))
                {
                    if (JsSemantics.PlainText(objectId) is { } oid && oid.StartsWith(BoundObjects.OidPrefix, StringComparison.Ordinal))
                    {
                        refIds[oid] = NormalizeRefId(refId);
                    }
                }
            }
        }
        return refIds;
    }

    private static long? NormalizeRefId(PyValue value) => value switch
    {
        PyLong number => number.Value,
        PyInt number => number.Value,
        PyReal number when double.IsFinite(number.Value) => (long)Math.Truncate(number.Value),
        _ => null,
    };
}
