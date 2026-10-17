using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services;

/// <summary>
/// A call the server makes to a client's service (EveJS <c>ClientSession.sendClientCallRequest</c>, clientSession.js:1012-1090): the
/// <c>CallReq</c> packet from the proxy node to the client's address with the service name, the body <c>(1, method, args, kwargs)</c> as a substream,
/// and the timeout as the named payload. The client answers with a call response, which is not waited for here.
/// </summary>
public static class ClientCallRequest
{
    public const string ClassName = "carbon.common.script.net.machoNetPacket.CallReq";
    public const long MachoTimeoutSeconds = 86400;

    private static long _nextCallId = 610_000;

    /// <summary>The next call id the server uses for its own calls (EveJS starts at 610000 per session; one counter here serves all sessions).</summary>
    public static long NextCallId() => Interlocked.Increment(ref _nextCallId);

    public static PyValue Build(long clientId, long userId, string service, string method, IReadOnlyList<PyValue> args, long callId, PyDict? kwargs = null)
    {
        // the engine sends the arguments as a tuple (carbon ServiceCallGPCS.RemoteServiceCall: payload = (1, method, args, kw2) with *args)
        var body = new PyTuple([new PyInt(1), new PyStr(method), new PyTuple([.. args]), kwargs ?? new PyDict([])]);
        var source = new MachoAddress(AddressKind.Node, NodeId: new PyInt(MachoMessage.ProxyNodeId), CallId: new PyInt(callId));
        var destination = new MachoAddress(AddressKind.Client, ClientId: new PyInt(clientId), Service: service);
        PyValue[] packet =
        [
            new PyInt(MachoMessage.CallReq),
            source.Encode(),
            destination.Encode(),
            userId != 0 ? new PyInt(userId) : PyNone.Instance,
            new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]),
            Py.Dict(("machoTimeout", new PyInt(MachoTimeoutSeconds))),
            .. Enumerable.Repeat<PyValue>(PyNone.Instance, 8),
        ];
        return new PyObject(new PyStr(ClassName), new PyTuple(packet));
    }
}
