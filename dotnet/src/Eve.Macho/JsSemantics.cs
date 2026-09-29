using System.Text;
using Eve.Marshal;

namespace Eve.Macho;

/// <summary>
/// How the reference server reads a decoded value. Its decoder turns every string opcode into a
/// <c>Buffer</c> and only string-table items into JS strings, and its packet code then tests
/// values with <c>||</c>, <c>typeof</c> and <c>Array.isArray</c>. These helpers give the same
/// answers for <see cref="PyValue"/>s so ported packet code behaves identically:
/// <see cref="PyStr"/> is a JS string, <see cref="PyBytes"/>/<see cref="PyBuffer"/> a Buffer,
/// <see cref="PyTuple"/> the only array, <see cref="PyLong"/> a BigInt.
/// </summary>
public static class JsSemantics
{
    /// <summary>JS truthiness (<c>value || fallback</c>).</summary>
    public static bool Truthy(PyValue? value) => value switch
    {
        null or PyNone => false,
        PyBool flag => flag.Value,
        PyInt number => number.Value != 0,
        PyLong number => number.Value != 0,
        PyReal number => number.Value != 0 && !double.IsNaN(number.Value),
        PyStr text => text.Value.Length > 0,
        _ => true, // Buffers, objects and arrays are always truthy, even when empty
    };

    /// <summary><c>value || null</c>.</summary>
    public static PyValue OrNull(PyValue? value) => Truthy(value) ? value! : PyNone.Instance;

    /// <summary><c>typeof value === "number"</c> (a JS number, not a BigInt).</summary>
    public static bool IsNumber(PyValue? value) => value is PyInt or PyReal;

    public static double NumberValue(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyReal number => number.Value,
        _ => 0,
    };

    /// <summary>
    /// <c>strVal</c>: a string from a string, a Buffer, or a <c>{type: "wstring" | "token"}</c> wrapper;
    /// null for anything else.
    /// </summary>
    public static string? StringValue(PyValue? value) => value switch
    {
        PyStr text => text.Value,
        PyBytes bytes => Encoding.UTF8.GetString(bytes.Value.Span),
        PyBuffer buffer => Encoding.UTF8.GetString(buffer.Value.Span),
        PyWString text => text.Value,
        PyToken text => text.Value,
        _ => null,
    };

    /// <summary>The text of a JS string or Buffer, null otherwise (no wrapper objects).</summary>
    public static string? PlainText(PyValue? value) => value switch
    {
        PyStr text => text.Value,
        PyBytes bytes => Encoding.UTF8.GetString(bytes.Value.Span),
        PyBuffer buffer => Encoding.UTF8.GetString(buffer.Value.Span),
        _ => null,
    };

    /// <summary><c>String(value)</c> for the values that occur as dictionary keys.</summary>
    public static string ToJsString(PyValue? value) => value switch
    {
        null or PyNone => "null",
        PyStr text => text.Value,
        PyBytes bytes => Encoding.UTF8.GetString(bytes.Value.Span),
        PyBuffer buffer => Encoding.UTF8.GetString(buffer.Value.Span),
        PyInt number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PyLong number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PyBool flag => flag.Value ? "true" : "false",
        PyReal number => number.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        PyTuple tuple => string.Join(",", tuple.Items.Select(ToJsString)),
        _ => "[object Object]",
    };

    /// <summary>
    /// What the reference server sends when it echoes a value it received: it decodes every Python
    /// <c>str</c> to a Buffer (written back as a <c>buffer</c>), every integral real to a number (written
    /// back as an integer) and every 64-bit or larger integer to a BigInt (written back as a long). A
    /// service that returns received values must do the same to stay byte-compatible.
    /// </summary>
    public static PyValue AsReceived(PyValue value)
    {
        switch (value)
        {
            case PyBytes bytes:
                return new PyBuffer(bytes.Value);
            case PyReal real when double.IsFinite(real.Value) && Math.Truncate(real.Value) == real.Value:
                return real.Value >= -9_223_372_036_854_775_808.0 && real.Value < 9_223_372_036_854_775_808.0
                    ? new PyInt((long)real.Value)
                    : throw new MarshalException(MarshalErrorKind.OutOfRange, "value out of range: integer beyond int64");
            case PyBigUnsigned big:
                var magnitude = new System.Numerics.BigInteger(big.Bytes, isUnsigned: true);
                return magnitude <= long.MaxValue ? new PyLong((long)magnitude) : throw new MarshalException(MarshalErrorKind.OutOfRange, "value out of range: BigInt beyond int64");
            case PyTuple tuple:
                return new PyTuple([.. tuple.Items.Select(AsReceived)]);
            case PyList list:
                return new PyList([.. list.Items.Select(AsReceived)]);
            case PyDict dict:
                return new PyDict([.. dict.Entries.Select(entry => new KeyValuePair<PyValue, PyValue>(AsReceived(entry.Key), AsReceived(entry.Value)))]);
            case PyObject obj:
                return new PyObject(AsReceived(obj.Name), AsReceived(obj.Args));
            case PyObjectEx ex:
                return new PyObjectEx(
                    ex.Kind,
                    AsReceived(ex.Header),
                    [.. ex.List.Select(AsReceived)],
                    [.. ex.Dict.Select(entry => new KeyValuePair<PyValue, PyValue>(AsReceived(entry.Key), AsReceived(entry.Value)))]);
            case PySubStruct inner:
                return new PySubStruct(AsReceived(inner.Value));
            case PySubStream { Decoded: { } decoded }:
                return new PySubStream(AsReceived(decoded));
            default:
                return value;
        }
    }

    /// <summary>The entries of a dict value (<c>extractDictEntries</c>); empty for anything else.</summary>
    public static IReadOnlyList<KeyValuePair<PyValue, PyValue>> DictEntries(PyValue? value) =>
        value is PyDict dict ? dict.Entries : [];

    /// <summary>The tuple's items when <paramref name="value"/> is a JS array (a tuple).</summary>
    public static bool TryArray(PyValue? value, out IReadOnlyList<PyValue> items)
    {
        if (value is PyTuple tuple)
        {
            items = tuple.Items;
            return true;
        }
        items = [];
        return false;
    }
}
