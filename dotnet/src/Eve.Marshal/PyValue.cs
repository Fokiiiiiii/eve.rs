namespace Eve.Marshal;

/// <summary>
/// A marshal value. One type serves both directions. The decoder never produces
/// a value the encoder cannot write back.
///
/// The JS reference collapses several wire types into one JS type (every string
/// opcode decodes to a <c>Buffer</c>, every small integer to a <c>number</c>).
/// This model keeps them apart because the distinction is meaningful on the wire:
/// <see cref="PyBytes"/> is a Python <c>str</c>, <see cref="PyBuffer"/> a
/// <c>buffer</c>, <see cref="PyStr"/> a string-table or UTF-8 string produced by
/// the server, <see cref="PyWString"/> a unicode string.
///
/// Values compare structurally (<c>==</c> on two <see cref="PyTuple"/>s compares
/// their items), so tests can assert on whole values.
/// </summary>
public abstract record PyValue
{
    public static readonly PyValue None = PyNone.Instance;

    public static PyValue Of(bool value) => value ? PyBool.True : PyBool.False;

    public static PyValue Of(long value) => new PyInt(value);

    public static PyValue Of(double value) => new PyReal(value);

    /// <summary>A server string (string-table item or UTF-8 <c>str</c>).</summary>
    public static PyValue Of(string? value) => value is null ? PyNone.Instance : new PyStr(value);

    /// <summary>Deep structural equality (also available as <c>==</c> / <c>Equals</c>).</summary>
    public static bool DeepEquals(PyValue? left, PyValue? right) => Equals(left, right);

    /// <summary>
    /// A Python <c>set</c>. The protocol has no set opcode: <c>set.__reduce__</c> gives
    /// <c>(set, (list(items),))</c>, so the wire is a PyObjectEx1 whose header is the token
    /// <c>__builtin__.set</c> and a tuple holding the list.
    /// </summary>
    public static PyObjectEx PythonSet(IEnumerable<PyValue> items) => new(
        ObjectExKind.Ex1,
        new PyTuple([new PyToken("__builtin__.set"), new PyTuple([new PyList([.. items])])]),
        [],
        []);
}

internal static class Seq
{
    public static bool Equal(IReadOnlyList<PyValue> left, IReadOnlyList<PyValue> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left.Count != right.Count)
        {
            return false;
        }
        for (var i = 0; i < left.Count; i++)
        {
            if (!Equals(left[i], right[i]))
            {
                return false;
            }
        }
        return true;
    }

    public static bool Equal(IReadOnlyList<KeyValuePair<PyValue, PyValue>> left, IReadOnlyList<KeyValuePair<PyValue, PyValue>> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left.Count != right.Count)
        {
            return false;
        }
        for (var i = 0; i < left.Count; i++)
        {
            if (!Equals(left[i].Key, right[i].Key) || !Equals(left[i].Value, right[i].Value))
            {
                return false;
            }
        }
        return true;
    }

    public static int Hash(IReadOnlyList<PyValue> items)
    {
        var hash = new HashCode();
        foreach (var item in items)
        {
            hash.Add(item);
        }
        return hash.ToHashCode();
    }

    public static int Hash(IReadOnlyList<KeyValuePair<PyValue, PyValue>> entries)
    {
        var hash = new HashCode();
        foreach (var (key, value) in entries)
        {
            hash.Add(key);
            hash.Add(value);
        }
        return hash.ToHashCode();
    }

    public static int Hash(ReadOnlyMemory<byte> bytes)
    {
        var hash = new HashCode();
        hash.AddBytes(bytes.Span);
        return hash.ToHashCode();
    }
}

public sealed record PyNone : PyValue
{
    public static readonly PyNone Instance = new();

    private PyNone()
    {
    }
}

public sealed record PyBool : PyValue
{
    public static readonly PyBool True = new(true);
    public static readonly PyBool False = new(false);

    private PyBool(bool value) => Value = value;

    public bool Value { get; }
}

/// <summary>
/// An integer written with the smallest of PyByte / PySignedShort / PyLong /
/// PyLongLong (and PyMinusOne / PyZeroInteger / PyOneInteger). Beyond
/// <see cref="int"/> it goes out as PyLongLong and decodes as <see cref="PyLong"/>.
/// </summary>
public sealed record PyInt(long Value) : PyValue;

/// <summary>Always written as PyLongLong, however small.</summary>
public sealed record PyLong(long Value) : PyValue;

/// <summary>PyReal; a zero is written as the one-byte PyZeroReal.</summary>
public sealed record PyReal(double Value) : PyValue;

/// <summary>
/// A variable-width integer that does not fit in a JS safe integer, as minimal
/// little-endian bytes (no trailing zero byte).
/// </summary>
public sealed record PyBigUnsigned(byte[] Bytes) : PyValue
{
    public bool Equals(PyBigUnsigned? other) => other is not null && Bytes.AsSpan().SequenceEqual(other.Bytes);

    public override int GetHashCode() => Seq.Hash(Bytes);
}

/// <summary>A string-table item or a UTF-8 string chosen by the sender.</summary>
public sealed record PyStr(string Value) : PyValue;

/// <summary>A Python <c>str</c>: PyEmptyString, PyCharString, PyShortString or PyLongString.</summary>
public sealed record PyBytes(ReadOnlyMemory<byte> Value) : PyValue
{
    public bool Equals(PyBytes? other) => other is not null && Value.Span.SequenceEqual(other.Value.Span);

    public override int GetHashCode() => Seq.Hash(Value);
}

/// <summary>A Python <c>buffer</c>: PyBuffer.</summary>
public sealed record PyBuffer(ReadOnlyMemory<byte> Value) : PyValue
{
    public bool Equals(PyBuffer? other) => other is not null && Value.Span.SequenceEqual(other.Value.Span);

    public override int GetHashCode() => Seq.Hash(Value);
}

/// <summary>A unicode string: PyWStringUTF8 (or a UCS-2 form when decoding).</summary>
public sealed record PyWString(string Value) : PyValue;

public sealed record PyToken(string Value) : PyValue;

public sealed record PyTuple(IReadOnlyList<PyValue> Items) : PyValue
{
    public static PyTuple Of(params PyValue[] items) => new(items);

    public bool Equals(PyTuple? other) => other is not null && Seq.Equal(Items, other.Items);

    public override int GetHashCode() => Seq.Hash(Items);
}

public sealed record PyList(IReadOnlyList<PyValue> Items) : PyValue
{
    public static PyList Of(params PyValue[] items) => new(items);

    public bool Equals(PyList? other) => other is not null && Seq.Equal(Items, other.Items);

    public override int GetHashCode() => Seq.Hash(Items);
}

/// <summary>Entries in wire order. The wire stores each pair value first, then key.</summary>
public sealed record PyDict(IReadOnlyList<KeyValuePair<PyValue, PyValue>> Entries) : PyValue
{
    public bool Equals(PyDict? other) => other is not null && Seq.Equal(Entries, other.Entries);

    public override int GetHashCode() => Seq.Hash(Entries);
}

public sealed record PyObject(PyValue Name, PyValue Args) : PyValue;

public enum ObjectExKind
{
    /// <summary>PyObjectEx1 (0x22)</summary>
    Ex1,

    /// <summary>PyObjectEx2 (0x23)</summary>
    Ex2,
}

public sealed record PyObjectEx(
    ObjectExKind Kind,
    PyValue Header,
    IReadOnlyList<PyValue> List,
    IReadOnlyList<KeyValuePair<PyValue, PyValue>> Dict) : PyValue
{
    public bool Equals(PyObjectEx? other) =>
        other is not null && Kind == other.Kind && Equals(Header, other.Header) && Seq.Equal(List, other.List) && Seq.Equal(Dict, other.Dict);

    public override int GetHashCode() => HashCode.Combine(Kind, Header, Seq.Hash(List), Seq.Hash(Dict));
}

public sealed record PySubStruct(PyValue Value) : PyValue;

/// <summary>
/// A nested marshal stream. <see cref="Decoded"/> is the value it decoded to (or is
/// to be encoded from); when it is null the stream failed to decode and
/// <see cref="Raw"/> keeps its bytes.
/// </summary>
public sealed record PySubStream : PyValue
{
    public PySubStream(PyValue decoded) => Decoded = decoded;

    public PySubStream(ReadOnlyMemory<byte> raw) => Raw = raw;

    public PyValue? Decoded { get; }

    public ReadOnlyMemory<byte> Raw { get; }

    public bool Equals(PySubStream? other) =>
        other is not null && Equals(Decoded, other.Decoded) && Raw.Span.SequenceEqual(other.Raw.Span);

    public override int GetHashCode() => HashCode.Combine(Decoded, Seq.Hash(Raw));
}

public sealed record PyChecksummed(uint Checksum, PyValue Value) : PyValue;

/// <summary>Raw Python pickle bytes (cPicked).</summary>
public sealed record PyCPickled(ReadOnlyMemory<byte> Value) : PyValue
{
    public bool Equals(PyCPickled? other) => other is not null && Value.Span.SequenceEqual(other.Value.Span);

    public override int GetHashCode() => Seq.Hash(Value);
}
