using System.Globalization;
using System.Numerics;
using System.Text;

namespace Eve.Marshal.Tests;

/// <summary>
/// Maps the JS value graph to and from <see cref="PyValue"/>. Both directions follow marshal.js
/// exactly, quirks included, because the adapter is what lets golden fixtures recorded from the JS
/// codec drive the typed C# codec:
/// <list type="bullet">
/// <item><see cref="ToValue"/> mirrors <c>encodeValue</c>: how a plain JS value or a
/// <c>{type: "..."}</c> object becomes a wire type, and the packed-row input coercions.</item>
/// <item><see cref="ToJs"/> mirrors <c>decodeValue</c>: what JS hands back for each opcode.</item>
/// </list>
/// </summary>
internal static class JsAdapter
{
    private const double I64Limit = 9_223_372_036_854_775_808.0;
    private const long UnknownDbType = long.MinValue;

    // ─── JS -> PyValue (the encoder's input rules) ──────────────────────────

    public static PyValue ToValue(Js js) => js switch
    {
        JsNull => PyNone.Instance,
        JsBool flag => PyValue.Of(flag.Value),
        JsNum number => PlainNumber(number.Value),
        JsBig big => BigToLong(big.Digits),
        JsStr text => new PyStr(text.Value),
        JsBuf buf => new PyBuffer(buf.Value),
        JsArr array => new PyTuple(Values(array.Items)),
        JsObj => Typed(js),
        _ => throw new InvalidOperationException(),
    };

    private static List<PyValue> Values(IEnumerable<Js> items) => [.. items.Select(ToValue)];

    /// <summary>A JS number: an integer (<c>Number.isInteger</c>, so also -0 and 2^60) becomes the smallest integer opcode, anything else a PyReal.</summary>
    private static PyValue PlainNumber(double number)
    {
        if (double.IsFinite(number) && Math.Truncate(number) == number)
        {
            if (number >= -I64Limit && number < I64Limit)
            {
                return new PyInt((long)number);
            }
            // BigInt(number) then writeBigInt64LE: a RangeError.
            throw MarshalException.OutOfRange("integer beyond int64");
        }
        return new PyReal(number);
    }

    private static PyValue BigToLong(string digits)
    {
        if (!BigInteger.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            || number < Int128.MinValue || number > Int128.MaxValue)
        {
            throw MarshalException.OutOfRange("BigInt beyond i128");
        }
        return number >= long.MinValue && number <= long.MaxValue
            ? new PyLong((long)number)
            : throw MarshalException.OutOfRange("BigInt beyond int64");
    }

    private static List<KeyValuePair<PyValue, PyValue>> Pairs(Js? node)
    {
        if (node is not JsArr entries)
        {
            throw MarshalException.Unencodable("dict entries missing");
        }
        return [.. entries.Items.Select(entry => entry is JsArr { Items.Count: >= 2 } pair
            ? new KeyValuePair<PyValue, PyValue>(ToValue(pair.Items[0]), ToValue(pair.Items[1]))
            : throw MarshalException.Unencodable("dict entry is not a pair"))];
    }

    private static List<KeyValuePair<PyValue, PyValue>> OptionalPairs(Js? node) => node is null or JsNull ? [] : Pairs(node);

    private static List<PyValue> ItemsOf(Js? node) => node switch
    {
        null or JsNull => [],
        JsArr array => Values(array.Items),
        _ => throw MarshalException.Unencodable("items is not an array"),
    };

    private static PyValue Field(Js js, string name) => js.Get(name) is { } value ? ToValue(value) : PyNone.Instance;

    /// <summary><c>Buffer.from(x)</c> for a Buffer or an array of numbers.</summary>
    private static byte[] BufferSource(Js? node) => node switch
    {
        null or JsNull => [],
        JsBuf buf => buf.Value,
        JsArr array => [.. array.Items.Select(item => item is JsNum number
            ? (byte)((long)number.Value & 0xff)
            : throw MarshalException.Unencodable("byte source element is not a number"))],
        _ => throw MarshalException.Unencodable("byte source"),
    };

    /// <summary><c>Number(x)</c> for the values <c>{type: "real"}</c> accepts.</summary>
    private static double JsNumberOf(Js? node) => node switch
    {
        null => double.NaN,
        JsNull => 0.0,
        JsBool flag => flag.Value ? 1.0 : 0.0,
        JsNum number => number.Value,
        JsBig big => double.TryParse(big.Digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN,
        JsStr text => double.TryParse(text.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN,
        _ => double.NaN,
    };

    private static string TextOf(Js? node) => node is JsStr text ? text.Value : throw MarshalException.Unencodable("expected a string");

    private static PyValue Typed(Js js)
    {
        var kind = js.Get("type")?.AsString();
        if (string.IsNullOrEmpty(kind))
        {
            throw MarshalException.Unencodable("object without a type");
        }

        switch (kind)
        {
            case "dict":
                return new PyDict(Pairs(js.Get("entries")));
            case "wstring":
                return new PyWString(js.Get("value") is null or JsNull ? "" : TextOf(js.Get("value")));
            case "long":
                switch (js.Get("value"))
                {
                    case JsNum number when double.IsFinite(number.Value) && Math.Truncate(number.Value) == number.Value:
                        return number.Value >= -I64Limit && number.Value < I64Limit
                            ? new PyLong((long)number.Value)
                            : throw MarshalException.OutOfRange("integer beyond int64");
                    case JsBig big:
                        return BigToLong(big.Digits);
                    default:
                        throw MarshalException.OutOfRange("BigInt() of a non-integer");
                }
            case "real":
                var real = JsNumberOf(js.Get("value"));
                return new PyReal(double.IsFinite(real) ? real : 0.0);
            case "tuple":
                return new PyTuple(ItemsOf(js.Get("items")));
            case "list":
                return new PyList(ItemsOf(js.Get("items")));
            case "bytes":
                return new PyBytes(BufferSource(js.Get("value")));
            case "rawstr":
                return js.Get("value") switch
                {
                    null or JsNull => new PyBytes(ReadOnlyMemory<byte>.Empty),
                    JsStr text => new PyBytes(Encoding.UTF8.GetBytes(text.Value)),
                    _ => throw MarshalException.Unencodable("rawstr of a non-string"),
                };
            case "object":
                return new PyObject(Field(js, "name"), Field(js, "args"));
            case "packedrow":
                return PackedRow(js);
            case "substruct":
                return new PySubStruct(Field(js, "value"));
            case "substream":
                return new PySubStream(Field(js, "value"));
            case "token":
                return new PyToken(TextOf(js.Get("value")));
            case "objectex1" or "objectex2":
                return new PyObjectEx(
                    kind == "objectex1" ? ObjectExKind.Ex1 : ObjectExKind.Ex2,
                    Field(js, "header"),
                    ItemsOf(js.Get("list")),
                    OptionalPairs(js.Get("dict")));
            case "cpicked":
                return new PyCPickled(BufferSource(js.Get("data")));
            default:
                throw MarshalException.Unencodable("unknown marshal type");
        }
    }

    /// <summary><c>unwrapPackedScalarValue</c>: typed scalar wrappers give up their value.</summary>
    private static Js UnwrapScalar(Js js)
    {
        if (js is JsObj && js.Get("type")?.AsString() is "int" or "long" or "float" or "double" or "bool" or "wstring" or "token" or "rawstr")
        {
            return js.Get("value") is { } inner ? UnwrapScalar(inner) : JsNull.Instance;
        }
        return js;
    }

    /// <summary>
    /// A packed-row cell. Variable-width columns are ordinary marshal values; fixed-width and BOOL
    /// columns are coerced numerically, so typed wrappers are unwrapped and numbers stay plain doubles.
    /// </summary>
    private static PyValue PackedValue(Js js, long dbType)
    {
        if (DbType.SizeBits(dbType) == 0)
        {
            return ToValue(js);
        }
        switch (UnwrapScalar(js))
        {
            case JsNull:
                return PyNone.Instance;
            case JsBool flag:
                return PyValue.Of(flag.Value);
            case JsNum number:
                return new PyReal(number.Value);
            case JsBig big:
                if (BigInteger.TryParse(big.Digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
                    && parsed >= Int128.MinValue && parsed <= Int128.MaxValue
                    && parsed >= long.MinValue && parsed <= long.MaxValue)
                {
                    return new PyLong((long)parsed);
                }
                return new PyReal(double.TryParse(big.Digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.PositiveInfinity);
            case JsStr text:
                return new PyStr(text.Value);
            case JsBuf buf:
                return new PyBuffer(buf.Value);
            default:
                // Anything else coerces to the fallback (zero / false).
                return new PyList([]);
        }
    }

    private static PackedColumn PackedColumnOf(Js entry)
    {
        if (entry is not JsArr parts)
        {
            throw MarshalException.InvalidPackedRow("column entry is not an array");
        }
        var dbType = parts.Items.Count > 1 && parts.Items[1] is JsNum number && double.IsFinite(number.Value) && Math.Truncate(number.Value) == number.Value
            ? (long)number.Value
            : UnknownDbType;
        return new PackedColumn("", dbType);
    }

    private static PyValue PackedRow(Js js)
    {
        var header = Field(js, "header");
        var columns = js.Get("columns") is JsArr declared
            ? declared.Items.Select(PackedColumnOf).ToList()
            : [.. Descriptor.ColumnsFromHeader(header)];
        if (js.Get("values") is not JsArr cells)
        {
            throw MarshalException.InvalidPackedRow("missing values");
        }
        if (cells.Items.Count != columns.Count)
        {
            throw MarshalException.InvalidPackedRow("values length does not match the column count");
        }
        var values = cells.Items.Select((cell, index) => PackedValue(cell, columns[index].DbType)).ToList();
        return new PyPackedRow(new Descriptor(header, columns), values);
    }

    // ─── PyValue -> JS (the decoder's output shapes) ────────────────────────

    private static JsObj Obj(params (string Name, Js Value)[] fields) => new([.. fields.Select(f => new KeyValuePair<string, Js>(f.Name, f.Value))]);

    public static Js ToJs(PyValue value)
    {
        switch (value)
        {
            case PyNone:
                return JsNull.Instance;
            case PyBool flag:
                return new JsBool(flag.Value);
            case PyInt number:
                return new JsNum(number.Value);
            case PyLong number:
                return new JsBig(number.Value.ToString(CultureInfo.InvariantCulture));
            case PyReal number:
                return new JsNum(number.Value);
            case PyBigUnsigned big:
                return new JsBig(new BigInteger(big.Bytes, isUnsigned: true).ToString(CultureInfo.InvariantCulture));
            case PyStr text:
                return new JsStr(text.Value);
            // Every string opcode and PyBuffer decode to a JS Buffer.
            case PyBytes bytes:
                return new JsBuf(bytes.Value.ToArray());
            case PyBuffer bytes:
                return new JsBuf(bytes.Value.ToArray());
            case PyWString text:
                return Obj(("type", new JsStr("wstring")), ("value", new JsStr(text.Value)));
            case PyToken text:
                return Obj(("type", new JsStr("token")), ("value", new JsStr(text.Value)));
            case PyTuple tuple:
                return new JsArr([.. tuple.Items.Select(ToJs)]);
            case PyList list:
                return Obj(("type", new JsStr("list")), ("items", new JsArr([.. list.Items.Select(ToJs)])));
            case PyDict dict:
                return Obj(("type", new JsStr("dict")), ("entries", PairsToJs(dict.Entries)));
            case PyObject obj:
                return Obj(("type", new JsStr("object")), ("name", ToJs(obj.Name)), ("args", ToJs(obj.Args)));
            case PyObjectEx ex:
                return Obj(
                    ("type", new JsStr(ex.Kind == ObjectExKind.Ex1 ? "objectex1" : "objectex2")),
                    ("header", ToJs(ex.Header)),
                    ("list", new JsArr([.. ex.List.Select(ToJs)])),
                    ("dict", PairsToJs(ex.Dict)));
            case PySubStruct inner:
                return Obj(("type", new JsStr("substruct")), ("value", ToJs(inner.Value)));
            case PySubStream { Decoded: { } decoded }:
                return Obj(("type", new JsStr("substream")), ("value", ToJs(decoded)));
            case PySubStream stream:
                return Obj(("type", new JsStr("substream")), ("raw", new JsBuf(stream.Raw.ToArray())));
            case PyChecksummed checksummed:
                return Obj(("type", new JsStr("checksummed")), ("checksum", new JsNum(checksummed.Checksum)), ("value", ToJs(checksummed.Value)));
            case PyCPickled pickled:
                return Obj(("type", new JsStr("cpicked")), ("data", new JsBuf(pickled.Value.ToArray())));
            case PyPackedRow row:
                return PackedRowToJs(row);
            default:
                throw new InvalidOperationException(value.GetType().Name);
        }
    }

    private static JsArr PairsToJs(IReadOnlyList<KeyValuePair<PyValue, PyValue>> entries) =>
        new([.. entries.Select(pair => (Js)new JsArr([ToJs(pair.Key), ToJs(pair.Value)]))]);

    private static Js PackedRowToJs(PyPackedRow row)
    {
        var columns = Descriptor.DescriptorColumns(row.Header) is { } entries
            ? entries.Select(ToJs).ToList()
            : [];
        return Obj(
            ("type", new JsStr("packedrow")),
            ("header", ToJs(row.Header)),
            ("columns", new JsArr(columns)),
            ("values", new JsArr([.. row.Values.Select(ToJs)])),
            ("fields", new JsObj([.. row.Fields().Select(f => new KeyValuePair<string, Js>(f.Key, ToJs(f.Value)))])),
            ("rleData", new JsBuf(row.Rle?.ToArray() ?? [])));
    }
}
