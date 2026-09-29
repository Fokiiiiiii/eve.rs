using Eve.Marshal;

namespace Eve.Macho;

/// <summary>An object a call result registered for later calls: its OID string and the refID of the bind.</summary>
public readonly record struct BoundObjectRegistration(string Oid, PyValue? RefId);

/// <summary>Finding bound-object names in the values that mention them.</summary>
public static class BoundObjects
{
    public const string OidPrefix = "N=";

    /// <summary>The first <c>N=...</c> name inside a value (<c>extractBoundObjectName</c>), searching nested values.</summary>
    public static string? ExtractName(PyValue? value, int depth = 0)
    {
        if (depth > 6 || value is null or PyNone)
        {
            return null;
        }
        switch (value)
        {
            case PyStr or PyBytes or PyBuffer:
                var text = JsSemantics.PlainText(value)!;
                return text.StartsWith(OidPrefix, StringComparison.Ordinal) ? text : null;
            case PyTuple tuple:
                foreach (var entry in tuple.Items)
                {
                    var resolved = ExtractName(entry, depth + 1);
                    if (resolved is not null)
                    {
                        return resolved;
                    }
                }
                return null;
        }

        // An object-like value: its own text, then the fields the reference looks at.
        var own = ValueOf(value);
        if (own is PyStr or PyBytes or PyBuffer or PyWString or PyToken)
        {
            var ownText = JsSemantics.StringValue(own)!;
            if (own is not PyWString and not PyToken && ownText.StartsWith(OidPrefix, StringComparison.Ordinal))
            {
                return ownText;
            }
            if (own is PyWString or PyToken && ownText.StartsWith(OidPrefix, StringComparison.Ordinal))
            {
                return ownText;
            }
        }
        foreach (var field in new[] { ValueOf(value), ArgsOf(value) })
        {
            var resolved = ExtractName(field, depth + 1);
            if (resolved is not null)
            {
                return resolved;
            }
        }
        return null;
    }

    /// <summary>The <c>.value</c> of a wrapper object (wstring, token, substream, substruct, checksummed), else null.</summary>
    private static PyValue? ValueOf(PyValue value) => value switch
    {
        PyWString text => new PyStr(text.Value),
        PyToken text => new PyStr(text.Value),
        PySubStream stream => stream.Decoded,
        PySubStruct inner => inner.Value,
        PyChecksummed sum => sum.Value,
        _ => null,
    };

    private static PyValue? ArgsOf(PyValue value) => value is PyObject obj ? obj.Args : null;

    /// <summary>Every <c>N=...</c> name a value mentions (<c>collectBoundObjectIDs</c>): strings, and the keys of dicts.</summary>
    public static void CollectIds(PyValue? value, Action<string> add, int depth = 0)
    {
        if (depth > 8 || value is null or PyNone)
        {
            return;
        }
        switch (value)
        {
            case PyStr or PyBytes or PyBuffer:
                var text = JsSemantics.PlainText(value)!;
                if (text.StartsWith(OidPrefix, StringComparison.Ordinal))
                {
                    add(text);
                }
                return;
            case PyTuple tuple:
                foreach (var entry in tuple.Items)
                {
                    CollectIds(entry, add, depth + 1);
                }
                return;
            case PyDict dict:
                foreach (var (key, _) in dict.Entries)
                {
                    CollectIds(key, add, depth + 1);
                }
                return;
            case PyWString wide:
                CollectIds(new PyStr(wide.Value), add, depth + 1);
                return;
            case PyToken token:
                CollectIds(new PyStr(token.Value), add, depth + 1);
                return;
            case PySubStream stream:
                CollectIds(stream.Decoded, add, depth + 1);
                return;
            case PySubStruct inner:
                CollectIds(inner.Value, add, depth + 1);
                return;
            case PyChecksummed sum:
                CollectIds(sum.Value, add, depth + 1);
                return;
        }
        // Other objects contribute only their field names, which are never OIDs.
    }

    /// <summary>
    /// The registrations in a call result (<c>collectBoundObjectRegistrations</c>): a
    /// <c>substruct(substream((oid, refID)))</c> anywhere in a tuple of results.
    /// </summary>
    public static IReadOnlyList<BoundObjectRegistration> Registrations(PyValue? result)
    {
        var found = new List<BoundObjectRegistration>();
        Collect(result, found);
        // A repeated oid keeps its first position and its last refID, as Map.set does.
        var order = new List<string>();
        var latest = new Dictionary<string, PyValue?>(StringComparer.Ordinal);
        foreach (var registration in found)
        {
            if (!latest.ContainsKey(registration.Oid))
            {
                order.Add(registration.Oid);
            }
            latest[registration.Oid] = registration.RefId;
        }
        return [.. order.Select(oid => new BoundObjectRegistration(oid, latest[oid]))];
    }

    private static void Collect(PyValue? value, List<BoundObjectRegistration> registrations)
    {
        switch (value)
        {
            case PyTuple tuple:
                foreach (var entry in tuple.Items)
                {
                    Collect(entry, registrations);
                }
                return;
            case PySubStruct inner:
                if (TryRegistration(inner, out var registration))
                {
                    registrations.Add(registration);
                }
                return;
        }
    }

    /// <summary>The registration inside a substruct, if it holds one.</summary>
    public static bool TryRegistration(PySubStruct substruct, out BoundObjectRegistration registration)
    {
        registration = default;
        var inner = substruct.Value is PySubStream stream ? stream.Decoded : substruct.Value;
        if (inner is PyTuple { Items.Count: >= 1 } tuple && tuple.Items[0] is PyStr oid && oid.Value.StartsWith(OidPrefix, StringComparison.Ordinal))
        {
            var refId = tuple.Items.Count > 1 ? tuple.Items[1] : null;
            registration = new BoundObjectRegistration(oid.Value, refId is null or PyNone ? null : refId);
            return true;
        }
        return false;
    }
}
