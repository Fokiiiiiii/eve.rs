using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services;

/// <summary>An error the client receives as an ERRORRESPONSE.</summary>
/// <param name="ErrorCode">A <see cref="MachoErrorType"/> code.</param>
/// <param name="Payload">The wrapped exception object.</param>
public sealed record MachoError(int ErrorCode, PyValue Payload)
{
    /// <summary>The debug tuple the server sends after the exception object (<c>(1, stack, variables, id, key, node, mode)</c>), or null.</summary>
    public PyValue? Detail { get; init; }

    /// <summary>The exception class token of the payload ("eveexceptions.UserError", ...), or "".</summary>
    public string ClassName =>
        Payload is PyObjectEx { Header: PyTuple { Items.Count: > 0 } header } && header.Items[0] is PyToken token ? token.Value : "";

    public bool IsUserError => ClassName == MachoErrors.UserErrorClass;

    /// <summary>The message of a user error, else "".</summary>
    public string UserErrorMessage =>
        IsUserError
        && Payload is PyObjectEx { Header: PyTuple { Items.Count: > 1 } header }
        && header.Items[1] is PyTuple { Items.Count: > 0 } args
            ? JsSemantics.PlainText(args.Items[0]) ?? ""
            : "";
}

/// <summary>An exception a service throws to answer the call with an error response.</summary>
public sealed class MachoWrappedException(MachoError error, string message = "Wrapped remote exception") : Exception(message)
{
    public MachoError Error { get; } = error;
}

/// <summary>The wrapped-exception payloads the client understands.</summary>
public static class MachoErrors
{
    public const string UserErrorClass = "eveexceptions.UserError";
    public const string WrongMachoNodeClass = "carbon.common.script.net.machoNetExceptions.WrongMachoNode";
    public const string CacheOkClass = "carbon.common.script.net.objectCaching.CacheOK";
    public const string RuntimeErrorClass = "exceptions.RuntimeError";

    /// <summary>A wrapped exception object: <c>ObjectEx1((Token(className), args[, state]), [], {})</c>.</summary>
    public static PyValue WrappedObject(string className, IReadOnlyList<PyValue> args, IReadOnlyList<KeyValuePair<PyValue, PyValue>>? state = null)
    {
        var header = new List<PyValue> { new PyToken(className), new PyTuple(args) };
        if (state is not null)
        {
            header.Add(new PyDict(state));
        }
        return new PyObjectEx(ObjectExKind.Ex1, new PyTuple(header), [], []);
    }

    private static KeyValuePair<PyValue, PyValue> Entry(string key, PyValue value) => new(new PyStr(key), value);

    /// <summary>A <c>UserError</c> the client shows as the message <paramref name="message"/>, with substitution values.</summary>
    public static PyValue UserErrorPayload(string message, IReadOnlyList<KeyValuePair<PyValue, PyValue>>? values = null)
    {
        var entries = values ?? [];
        return new PyObjectEx(
            ObjectExKind.Ex1,
            new PyTuple([
                new PyToken(UserErrorClass),
                new PyTuple([new PyStr(message), new PyDict(entries)]),
                new PyDict([Entry("msg", new PyStr(message)), Entry("dict", new PyDict(entries))]),
            ]),
            [],
            []);
    }

    public static MachoError UserError(string message, params (string Name, PyValue Value)[] values) =>
        new(MachoErrorType.WrappedException, UserErrorPayload(message, [.. values.Select(v => Entry(v.Name, v.Value))]));

    public static MachoError WrongMachoNode(long payload = 0) =>
        new(MachoErrorType.WrappedException, WrappedObject(WrongMachoNodeClass, [], [Entry("payload", new PyInt(payload))]));

    public static MachoError CacheOk() =>
        new(MachoErrorType.WrappedException, WrappedObject(CacheOkClass, [new PyStr("CacheOK")]));

    public static MachoError Wrapped(string className, IReadOnlyList<PyValue> args, IReadOnlyList<KeyValuePair<PyValue, PyValue>>? state = null) =>
        new(MachoErrorType.WrappedException, WrappedObject(className, args, state));

    /// <summary>
    /// The answer to a handler that threw something other than a user error: the V24.01 server's wrapped exception in
    /// its release form (<c>ExceptionWrapper.CallUp</c>, carbon/common/script/net/ExceptionWrapperGPCS.py:46-93). The
    /// client re-raises a <c>RuntimeError</c> at the call site; its text names only <paramref name="errorId"/>, and the
    /// debug tuple carries that ID and a one-line stack key, as the release server's does. The exception's message and
    /// stack stay on the host, which logs them under the same ID. <paramref name="errorId"/> is 8 hex digits.
    /// </summary>
    public static MachoError UnexpectedFailure(string errorId, string target)
    {
        var key = Convert.ToInt64(errorId, 16);
        var stackLine = $"//server/host=eve.rs/nodeID={MachoMessage.ProxyNodeId}/errorHashKey={key}\n";
        var detail = new PyTuple([
            new PyInt(1), // an error, not a warning: the client logs it at error level
            new PyList([new PyStr(stackLine)]),
            new PyList([]), // context variables: none outside a development node
            new PyStr(errorId),
            new PyTuple([new PyInt(key), new PyStr(stackLine)]),
            new PyInt(MachoMessage.ProxyNodeId),
            new PyStr("server"),
        ]);
        var payload = WrappedObject(RuntimeErrorClass, [new PyStr($"Unhandled error {errorId} in {target}")]);
        return new MachoError(MachoErrorType.WrappedException, payload) { Detail = detail };
    }

    /// <summary>Throws a user error: the client shows <paramref name="message"/>.</summary>
    public static MachoWrappedException ThrowUserError(string message, params (string Name, PyValue Value)[] values) =>
        new(UserError(message, values));
}
