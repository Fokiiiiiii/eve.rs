using Eve.Marshal;

namespace Eve.Macho;

/// <summary>
/// The call carried by a CALL_REQ (or a notification about a bound object). The packet body is
/// <c>((isBound, SubStream((remoteObject, method, args, kwargs))),)</c>.
/// </summary>
/// <param name="Method">The method name; <c>"unknown"</c> when the body cannot be read.</param>
/// <param name="Args">Positional arguments (a call that sends a non-tuple gets it wrapped in one).</param>
/// <param name="Kwargs">The keyword dict as sent, or None.</param>
/// <param name="RemoteObject">The bound-object reference, or the constant 1 for an unbound service call.</param>
public sealed record CallRequest(string Method, IReadOnlyList<PyValue> Args, PyValue Kwargs, PyValue? RemoteObject)
{
    private static CallRequest Unreadable(PyValue? _) => new("unknown", [], PyNone.Instance, null);

    /// <summary>Decodes a call request from a packet payload (<c>decodeCallRequest</c>); null when there is no payload.</summary>
    public static CallRequest? Decode(PyValue? payload)
    {
        if (!JsSemantics.Truthy(payload))
        {
            return null;
        }
        // payload = [ wrapper ]
        if (payload is not PyTuple { Items.Count: > 0 } outer)
        {
            return null;
        }
        var wrapper = outer.Items[0];
        if (wrapper is PyObject { Args: not null } wrappedObject)
        {
            wrapper = wrappedObject.Args;
        }

        PyValue? callBody = null;
        if (wrapper is PyTuple wrapperItems)
        {
            // wrapper = [isbound, substream_or_data, ...]: the substream carries the body
            foreach (var element in wrapperItems.Items)
            {
                if (element is PySubStream stream)
                {
                    callBody = StreamValue(stream);
                    break;
                }
            }
            // No substream: the wrapper itself might be the call body.
            if (!JsSemantics.Truthy(callBody))
            {
                callBody = wrapper;
            }
        }
        else if (wrapper is PySubStream single)
        {
            callBody = StreamValue(single);
        }

        if (callBody is PyObject { Args: not null } bodyObject)
        {
            callBody = bodyObject.Args;
        }
        if (callBody is not PyTuple { Items.Count: >= 2 } body)
        {
            return Unreadable(payload);
        }

        var remoteObject = body.Items[0];
        var rawMethod = body.Items.Count > 1 ? body.Items[1] : null;
        var method = MethodName(rawMethod);

        PyValue? args = body.Items.Count > 2 ? body.Items[2] : new PyTuple([]);
        var kwargs = body.Items.Count > 3 ? body.Items[3] : PyNone.Instance;
        // The argument tuple may itself be a substream.
        if (args is PySubStream argsStream)
        {
            args = StreamValue(argsStream);
        }
        IReadOnlyList<PyValue> argList = args switch
        {
            PyTuple tuple => tuple.Items,
            null or PyNone => [],
            var other => [other],
        };
        return new CallRequest(method, argList, kwargs, remoteObject);
    }

    /// <summary>A substream's value; a stream that did not decode reads as null.</summary>
    private static PyValue? StreamValue(PySubStream stream) => stream.Decoded;

    private static string MethodName(PyValue? rawMethod) => rawMethod switch
    {
        null or PyNone => "unknown",
        PyStr or PyBytes or PyBuffer or PyWString or PyToken => JsSemantics.StringValue(rawMethod)!,
        var other => JsSemantics.ToJsString(other),
    };
}
