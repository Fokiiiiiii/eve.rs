using Eve.Marshal;

namespace Eve.Services;

/// <summary>One call to a service: what the reference passes as <c>(args, session, kwargs, callContext)</c>.</summary>
public sealed class ServiceCall
{
    public required string Method { get; init; }

    public required IReadOnlyList<PyValue> Args { get; init; }

    /// <summary>The keyword-argument dict as sent, or None.</summary>
    public PyValue Kwargs { get; init; } = PyNone.Instance;

    public required ClientSession Session { get; init; }

    /// <summary>The call id the response will carry.</summary>
    public PyValue CallId { get; init; } = new PyInt(0);

    /// <summary>The bound object the call was addressed to (its OID), or null.</summary>
    public string? BoundObjectId { get; init; }

    /// <summary>The bind parameters that created the bound object, or null for an unbound call.</summary>
    public PyValue? BoundObjectParameters { get; init; }

    public CancellationToken Cancellation { get; init; }

    public PyValue Arg(int index) => index < Args.Count ? Args[index] : PyNone.Instance;

    /// <summary>A keyword argument by name, or null when absent (<c>getKwarg</c>).</summary>
    public PyValue? Kwarg(string name)
    {
        if (Kwargs is not PyDict dict)
        {
            return null;
        }
        foreach (var (key, value) in dict.Entries)
        {
            if (Eve.Macho.JsSemantics.StringValue(key) == name)
            {
                return value;
            }
        }
        return null;
    }
}
