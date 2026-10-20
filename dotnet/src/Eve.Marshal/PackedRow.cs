using System.Buffers.Binary;
using System.Text;

namespace Eve.Marshal;

/// <summary>One column of a packed row's <c>DBRowDescriptor</c>.</summary>
/// <param name="Name">The column name.</param>
/// <param name="DbType">
/// The DBTYPE code (see <see cref="Eve.Marshal.DbType"/>). Codes outside the known set are carried
/// as-is and laid out like a variable-width value.
/// </param>
public readonly record struct PackedColumn(string Name, long DbType);

/// <summary>
/// A row descriptor: the header as it appears on the wire, the columns it
/// declares, and the row layout derived from them.
///
/// Every row of a rowset repeats the same descriptor, so rows share one. The
/// decoder recognises a repeated descriptor by its bytes and reuses it; the
/// encoder writes the header bytes it produced the first time.
/// </summary>
public sealed class Descriptor
{
    private const long UnknownDbType = long.MinValue;

    private byte[]? _encodedHeader;

    /// <summary>A descriptor with explicit columns (the header is written verbatim and need not describe them).</summary>
    public Descriptor(PyValue header, IReadOnlyList<PackedColumn> columns)
    {
        Header = header;
        Columns = columns;
        Layout = new RowLayout(columns);
    }

    public PyValue Header { get; }

    public IReadOnlyList<PackedColumn> Columns { get; }

    internal RowLayout Layout { get; }

    /// <summary>A descriptor whose columns are read from its header.</summary>
    public static Descriptor FromHeader(PyValue header) => new(header, ColumnsFromHeader(header));

    internal byte[]? EncodedHeader => Volatile.Read(ref _encodedHeader);

    internal void SetEncodedHeader(byte[] bytes) => Interlocked.CompareExchange(ref _encodedHeader, bytes, null);

    public override bool Equals(object? obj) =>
        obj is Descriptor other && Equals(Header, other.Header) && Columns.SequenceEqual(other.Columns);

    public override int GetHashCode() => HashCode.Combine(Header, Columns.Count);

    /// <summary>
    /// The raw column entries (<c>(name, type)</c> tuples) of a <c>DBRowDescriptor</c> header:
    /// <c>header.header[1][0]</c> of a PyObjectEx.
    /// </summary>
    public static IReadOnlyList<PyValue>? DescriptorColumns(PyValue header)
    {
        if (header is not PyObjectEx { Header: PyTuple items })
        {
            return null;
        }
        if (items.Items.Count < 2 || items.Items[1] is not PyTuple second || second.Items.Count == 0 || second.Items[0] is not PyTuple columns)
        {
            return null;
        }
        return columns.Items;
    }

    internal static string? ColumnName(PyValue? value) => value switch
    {
        PyStr text => text.Value,
        PyWString text => text.Value,
        PyToken text => text.Value,
        PyBytes bytes => Encoding.UTF8.GetString(bytes.Value.Span),
        PyBuffer bytes => Encoding.UTF8.GetString(bytes.Value.Span),
        _ => null,
    };

    private static long ColumnType(PyValue? value) => value switch
    {
        PyInt code => code.Value,
        PyReal code when double.IsFinite(code.Value) && Math.Truncate(code.Value) == code.Value => (long)code.Value,
        _ => UnknownDbType,
    };

    /// <summary>Resolves the columns from a row descriptor header.</summary>
    public static IReadOnlyList<PackedColumn> ColumnsFromHeader(PyValue header)
    {
        var entries = DescriptorColumns(header) ?? throw MarshalException.InvalidPackedRow("missing DBRowDescriptor columns");
        var columns = new List<PackedColumn>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index] is not PyTuple parts)
            {
                throw MarshalException.InvalidPackedRow("column entry is not a (name, type) tuple");
            }
            columns.Add(new PackedColumn(
                ColumnName(parts.Items.Count > 0 ? parts.Items[0] : null) ?? $"col_{index}",
                ColumnType(parts.Items.Count > 1 ? parts.Items[1] : null)));
        }
        return columns;
    }
}

/// <summary>A PyPackedRow: a row descriptor plus one value per column.</summary>
public sealed record PyPackedRow : PyValue
{
    /// <summary>A row over an existing descriptor. Nothing is checked until the row is encoded, as in the JS reference.</summary>
    public PyPackedRow(Descriptor descriptor, IReadOnlyList<PyValue> values, ReadOnlyMemory<byte>? rle = null)
    {
        Descriptor = descriptor;
        Values = values;
        Rle = rle;
    }

    public Descriptor Descriptor { get; }

    /// <summary>One value per column, in column order.</summary>
    public IReadOnlyList<PyValue> Values { get; }

    /// <summary>The RLE-compressed fixed-width block exactly as received. Only set by the decoder; the encoder always recompresses.</summary>
    public ReadOnlyMemory<byte>? Rle { get; }

    /// <summary>A row whose columns are read from its descriptor header.</summary>
    public static PyPackedRow FromHeader(PyValue header, IReadOnlyList<PyValue> values) => new(Descriptor.FromHeader(header), values);

    public PyValue Header => Descriptor.Header;

    public IReadOnlyList<PackedColumn> Columns => Descriptor.Columns;

    public bool Equals(PyPackedRow? other) =>
        other is not null && Descriptor.Equals(other.Descriptor) && Seq.Equal(Values, other.Values);

    public override int GetHashCode() => HashCode.Combine(Descriptor, Seq.Hash(Values));

    /// <summary>
    /// The row as a name → value map, including the virtual columns the header declares
    /// (<c>StackSize</c>, <c>Singleton</c>). A repeated column name keeps the position of
    /// its first occurrence and the value of its last.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, PyValue>> Fields()
    {
        var fields = new List<KeyValuePair<string, PyValue>>(Descriptor.Columns.Count);
        for (var i = 0; i < Descriptor.Columns.Count && i < Values.Count; i++)
        {
            SetField(fields, Descriptor.Columns[i].Name, Values[i]);
        }
        foreach (var (name, resolver) in VirtualColumns(Descriptor.Header))
        {
            var derived = resolver switch
            {
                "eve.common.script.sys.eveCfg.StackSize" => DeriveStackSize(fields),
                "eve.common.script.sys.eveCfg.Singleton" => DeriveSingleton(fields),
                _ => null,
            };
            if (derived is not null)
            {
                SetField(fields, name, derived);
            }
        }
        return fields;
    }

    private static void SetField(List<KeyValuePair<string, PyValue>> fields, string name, PyValue value)
    {
        var index = fields.FindIndex(field => field.Key == name);
        if (index >= 0)
        {
            fields[index] = new(name, value);
        }
        else
        {
            fields.Add(new(name, value));
        }
    }

    private static double FieldNumber(List<KeyValuePair<string, PyValue>> fields, string name)
    {
        var index = fields.FindIndex(field => field.Key == name);
        return index < 0 ? double.NaN : JsNumber.ValueToNumber(fields[index].Value);
    }

    private static PyValue NumberValue(double number) =>
        JsNumber.IsSafeInteger(number) ? new PyInt((long)number) : new PyReal(number);

    private static PyValue DeriveStackSize(List<KeyValuePair<string, PyValue>> fields)
    {
        var quantity = FieldNumber(fields, "quantity");
        if (!double.IsFinite(quantity))
        {
            return new PyInt(0);
        }
        return NumberValue(quantity < 0 ? 1.0 : quantity);
    }

    private static PyValue DeriveSingleton(List<KeyValuePair<string, PyValue>> fields)
    {
        var quantity = FieldNumber(fields, "quantity");
        if (double.IsFinite(quantity) && quantity < 0)
        {
            return NumberValue(-quantity);
        }
        var location = FieldNumber(fields, "locationID");
        var inStationRange = double.IsFinite(location) && location >= 30_000_000.0 && location < 40_000_000.0;
        return new PyInt(inStationRange ? 1 : 0);
    }

    /// <summary><c>(column name, resolver name)</c> pairs from the header's virtual-column list.</summary>
    private static List<(string Name, string Resolver)> VirtualColumns(PyValue header)
    {
        var result = new List<(string, string)>();
        if (header is not PyObjectEx { Header: PyTuple items } || items.Items.Count < 3)
        {
            return result;
        }
        IReadOnlyList<PyValue> entries = items.Items[2] switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            _ => [],
        };
        foreach (var entry in entries)
        {
            if (entry is not PyTuple parts)
            {
                continue;
            }
            var name = Descriptor.ColumnName(parts.Items.Count > 0 ? parts.Items[0] : null);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }
            result.Add((name, Descriptor.ColumnName(parts.Items.Count > 1 ? parts.Items[1] : null) ?? ""));
        }
        return result;
    }
}

/// <summary>The order and geometry of a row's slots.</summary>
internal sealed class RowLayout
{
    /// <summary>Column indices sorted widest first, ties by column index.</summary>
    public readonly (int Index, int Size)[] Order;

    /// <summary>For each BOOL column, its position in the boolean bitmap (or -1).</summary>
    public readonly int[] BoolSlot;

    private readonly int _booleans;

    /// <summary>Bytes taken by the fixed-width columns.</summary>
    public readonly int DataBytes;

    /// <summary>Bytes taken by the booleans + null flags bitmap.</summary>
    public readonly int BitmapBytes;

    public RowLayout(IReadOnlyList<PackedColumn> columns)
    {
        Order = new (int, int)[columns.Count];
        BoolSlot = new int[columns.Count];
        var booleans = 0;
        long dataBits = 0;
        for (var index = 0; index < columns.Count; index++)
        {
            var size = DbType.SizeBits(columns[index].DbType);
            BoolSlot[index] = -1;
            if (columns[index].DbType == DbType.Bool)
            {
                BoolSlot[index] = booleans;
                booleans++;
            }
            if (size >= 8)
            {
                dataBits += size;
            }
            Order[index] = (index, size);
        }
        Array.Sort(Order, (left, right) => right.Size != left.Size ? right.Size.CompareTo(left.Size) : left.Index.CompareTo(right.Index));
        _booleans = booleans;
        DataBytes = checked((int)(dataBits >> 3));
        BitmapBytes = ((booleans + columns.Count) >> 3) + 1;
    }

    public int NullBit(int index) => index + _booleans;
}

internal static class PackedRowCodec
{
    private static bool BitIsSet(ReadOnlySpan<byte> bitmap, int bit) =>
        (bit >> 3) < bitmap.Length && (bitmap[bit >> 3] & (1 << (bit & 7))) != 0;

    public static PyValue Decode(MarshalDecoder decoder, Descriptor descriptor, ReadOnlyMemory<byte> rle)
    {
        var columns = descriptor.Columns;
        var layout = descriptor.Layout;

        var packed = new byte[layout.DataBytes + layout.BitmapBytes];
        DecompressRleInto(rle.Span, packed);
        var bitmap = packed.AsSpan(layout.DataBytes);
        var values = new PyValue[columns.Count];
        Array.Fill(values, PyNone.Instance);

        var offset = 0;
        foreach (var (index, size) in layout.Order)
        {
            if (size <= 1)
            {
                continue;
            }
            values[index] = BitIsSet(bitmap, layout.NullBit(index))
                ? PyNone.Instance
                : FixedValue(columns[index].DbType, packed, offset);
            offset += size >> 3;
        }

        foreach (var (index, size) in layout.Order)
        {
            if (size != 1)
            {
                continue;
            }
            values[index] = BitIsSet(bitmap, layout.NullBit(index))
                ? PyNone.Instance
                : PyValue.Of(BitIsSet(bitmap, layout.BoolSlot[index]));
        }

        foreach (var (index, size) in layout.Order)
        {
            if (size == 0)
            {
                values[index] = decoder.Value();
            }
        }
        return new PyPackedRow(descriptor, values, rle);
    }

    private static ReadOnlySpan<byte> Read(byte[] packed, int offset, int count)
    {
        if (offset < 0 || offset + count > packed.Length)
        {
            throw MarshalException.OutOfRange("packed row data");
        }
        return packed.AsSpan(offset, count);
    }

    private static ulong UnsignedAbs(long value) => value < 0 ? (ulong)(-(value + 1)) + 1UL : (ulong)value;

    private static PyValue FixedValue(long dbType, byte[] packed, int offset)
    {
        switch (dbType)
        {
            case DbType.Cy or DbType.I8 or DbType.UI8 or DbType.FileTime:
                var number = BinaryPrimitives.ReadInt64LittleEndian(Read(packed, offset, 8));
                // The JS reference hands back a number when it is a safe integer.
                return UnsignedAbs(number) <= 9_007_199_254_740_991UL ? new PyInt(number) : new PyLong(number);
            case DbType.I4:
                return new PyInt(BinaryPrimitives.ReadInt32LittleEndian(Read(packed, offset, 4)));
            case DbType.UI4:
                return new PyInt(BinaryPrimitives.ReadUInt32LittleEndian(Read(packed, offset, 4)));
            case DbType.R4:
                return new PyReal(BinaryPrimitives.ReadSingleLittleEndian(Read(packed, offset, 4)));
            case DbType.R8:
                return new PyReal(BinaryPrimitives.ReadDoubleLittleEndian(Read(packed, offset, 8)));
            case DbType.I2:
                return new PyInt(BinaryPrimitives.ReadInt16LittleEndian(Read(packed, offset, 2)));
            case DbType.UI2:
                return new PyInt(BinaryPrimitives.ReadUInt16LittleEndian(Read(packed, offset, 2)));
            case DbType.I1:
                return new PyInt((sbyte)Read(packed, offset, 1)[0]);
            case DbType.UI1:
                return new PyInt(Read(packed, offset, 1)[0]);
            default:
                return PyNone.Instance;
        }
    }

    public static void Encode(MarshalEncoder encoder, PyPackedRow row)
    {
        var descriptor = row.Descriptor;
        var layout = descriptor.Layout;
        if (row.Values.Count != descriptor.Columns.Count)
        {
            throw MarshalException.InvalidPackedRow("values length does not match the column count");
        }

        encoder.Out.Write(Op.PackedRow);
        var cached = descriptor.EncodedHeader;
        if (cached is not null)
        {
            encoder.Out.Write(cached);
        }
        else
        {
            var start = encoder.Out.Length;
            encoder.Value(descriptor.Header);
            descriptor.SetEncodedHeader(encoder.Out.CopyFrom(start));
        }

        var bitmap = new byte[layout.BitmapBytes];
        using var fixedBlock = new ByteBuffer(layout.DataBytes + layout.BitmapBytes);

        foreach (var (index, size) in layout.Order)
        {
            if (size <= 1)
            {
                continue;
            }
            var value = row.Values[index];
            if (value is PyNone)
            {
                var bit = layout.NullBit(index);
                bitmap[bit >> 3] |= (byte)(1 << (bit & 7));
            }
            FixedBytes(descriptor.Columns[index].DbType, value, fixedBlock);
        }

        foreach (var (index, size) in layout.Order)
        {
            if (size == 1 && PackBool(row.Values[index]))
            {
                var slot = layout.BoolSlot[index];
                bitmap[slot >> 3] |= (byte)(1 << (slot & 7));
            }
        }

        fixedBlock.Write(bitmap);
        var compressed = CompressRle(fixedBlock.AsSpan());
        encoder.Size(compressed.Length);
        encoder.Out.Write(compressed);

        foreach (var (index, size) in layout.Order)
        {
            if (size == 0)
            {
                encoder.Value(row.Values[index]);
            }
        }
    }

    private static void FixedBytes(long dbType, PyValue value, ByteBuffer output)
    {
        switch (dbType)
        {
            case DbType.Cy or DbType.I8 or DbType.UI8 or DbType.FileTime:
                output.WriteInt64(PackInt64(value));
                break;
            case DbType.I4:
                output.WriteInt32((int)CheckedInt(value, int.MinValue, int.MaxValue, "I4"));
                break;
            case DbType.UI4:
                output.WriteUInt32(JsNumber.ToUint32(Math.Truncate(PackNumber(value))));
                break;
            case DbType.R4:
                output.WriteSingle((float)PackNumber(value));
                break;
            case DbType.R8:
                output.WriteDouble(PackNumber(value));
                break;
            case DbType.I2:
                output.WriteInt16((short)CheckedInt(value, short.MinValue, short.MaxValue, "I2"));
                break;
            case DbType.UI2:
                output.WriteInt16(unchecked((short)(ushort)(JsNumber.ToUint32(Math.Truncate(PackNumber(value))) & 0xffff)));
                break;
            case DbType.I1:
                output.Write(unchecked((byte)(sbyte)CheckedInt(value, sbyte.MinValue, sbyte.MaxValue, "I1")));
                break;
            case DbType.UI1:
                output.Write((byte)(JsNumber.ToUint32(Math.Truncate(PackNumber(value))) & 0xff));
                break;
        }
    }

    /// <summary>A signed column value, failing (like Node's <c>writeIntXxLE</c>) outside its width.</summary>
    private static long CheckedInt(PyValue value, long min, long max, string column)
    {
        var number = Math.Truncate(PackNumber(value));
        if (number < min || number > max)
        {
            throw MarshalException.OutOfRange(column);
        }
        return (long)number;
    }

    /// <summary><c>normalizePackedNumber</c>: any loosely typed value as a finite number, else 0.</summary>
    private static double PackNumber(PyValue value)
    {
        var number = value switch
        {
            PyNone => 0.0,
            PyBool flag => flag.Value ? 1.0 : 0.0,
            PyInt or PyLong or PyReal or PyBigUnsigned or PyStr or PyWString or PyToken or PyBytes or PyBuffer => JsNumber.ValueToNumber(value),
            _ => 0.0,
        };
        return double.IsFinite(number) ? number : 0.0;
    }

    /// <summary><c>normalizePackedBigInt</c>, then the range check of <c>writeBigInt64LE</c>.</summary>
    private static long PackInt64(PyValue value)
    {
        const double limit = 9_223_372_036_854_775_808.0;
        switch (value)
        {
            case PyNone:
                return 0;
            case PyInt number:
                return number.Value;
            case PyLong number:
                return number.Value;
            case PyBool flag:
                return flag.Value ? 1 : 0;
            case PyReal number when double.IsFinite(number.Value):
                var truncated = Math.Truncate(number.Value);
                if (!(truncated >= -limit && truncated < limit))
                {
                    throw MarshalException.OutOfRange("64-bit column");
                }
                return (long)truncated;
            case PyBigUnsigned big:
                if (big.Bytes.Length > 8)
                {
                    throw MarshalException.OutOfRange("64-bit column");
                }
                Span<byte> buffer = stackalloc byte[8];
                big.Bytes.CopyTo(buffer);
                var unsigned = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
                return unsigned <= long.MaxValue ? (long)unsigned : throw MarshalException.OutOfRange("64-bit column");
            case PyStr text:
                return TextToInt64(text.Value);
            case PyWString text:
                return TextToInt64(text.Value);
            case PyToken text:
                return TextToInt64(text.Value);
            case PyBytes bytes:
                return TextToInt64(Encoding.UTF8.GetString(bytes.Value.Span));
            case PyBuffer bytes:
                return TextToInt64(Encoding.UTF8.GetString(bytes.Value.Span));
            default:
                return 0;
        }
    }

    private static long TextToInt64(string text)
    {
        if (JsNumber.TrimJs(text).Length == 0)
        {
            return 0;
        }
        // Text JS cannot parse as a BigInt falls back to zero.
        var number = JsNumber.BigIntFromString(text);
        if (number is null)
        {
            return 0;
        }
        return number.Value >= long.MinValue && number.Value <= long.MaxValue
            ? (long)number.Value
            : throw MarshalException.OutOfRange("64-bit column");
    }

    /// <summary><c>normalizePackedBoolean</c>.</summary>
    private static bool PackBool(PyValue value) => value switch
    {
        PyBool flag => flag.Value,
        PyNone => false,
        _ => PackNumber(value) != 0.0,
    };

    /// <summary>
    /// The row block's RLE: a control byte carries two nibbles; a nibble below 8 announces
    /// <c>8 - n</c> literal bytes, a nibble of 8 or more a run of <c>n - 7</c> zero bytes.
    /// Trailing all-zero control bytes are dropped.
    /// </summary>
    public static byte[] CompressRle(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length + (input.Length / 8) + 2);
        var secondNibble = false;
        var controlIndex = 0;
        var position = 0;
        uint zeroChains = 0;

        while (position < input.Length)
        {
            if (!secondNibble)
            {
                controlIndex = output.Count;
                output.Add(0);
            }

            var start = position;
            var end = Math.Min(position + 8, input.Length);
            int count;
            if (input[position] != 0)
            {
                zeroChains = 0;
                do
                {
                    output.Add(input[position]);
                    position++;
                }
                while (position < end && input[position] != 0);
                count = 8 - (position - start);
            }
            else
            {
                zeroChains++;
                while (position < end && input[position] == 0)
                {
                    position++;
                }
                count = (position - start) + 7;
            }

            if (secondNibble)
            {
                output[controlIndex] |= (byte)(count << 4);
            }
            else
            {
                output[controlIndex] = (byte)count;
            }
            secondNibble = !secondNibble;
        }

        if (secondNibble && zeroChains > 0)
        {
            zeroChains++;
        }
        while (zeroChains > 1)
        {
            zeroChains -= 2;
            output.RemoveAt(output.Count - 1);
        }
        return [.. output];
    }

    /// <summary>The inverse of <see cref="CompressRle"/>, producing exactly <paramref name="expected"/> bytes (missing input reads as zeros, surplus input is ignored).</summary>
    public static byte[] DecompressRle(ReadOnlySpan<byte> input, int expected)
    {
        var output = new byte[expected];
        DecompressRleInto(input, output);
        return output;
    }

    /// <summary>Decompresses into a zero-filled span whose length is the expected size.</summary>
    private static void DecompressRleInto(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var expected = output.Length;
        var read = 0;
        var written = 0;

        while (read < input.Length && written < expected)
        {
            var control = input[read];
            read++;
            for (var half = 0; half < 2; half++)
            {
                if (written >= expected)
                {
                    break;
                }
                int nibble = half == 0 ? control & 0x0f : control >> 4;
                if (nibble < 8)
                {
                    var literals = Math.Min(Math.Min(8 - nibble, input.Length - read), expected - written);
                    input.Slice(read, literals).CopyTo(output[written..]);
                    read += literals;
                    written += literals;
                }
                else
                {
                    written += Math.Min(nibble - 7, expected - written);
                }
                if (read >= input.Length && written >= expected)
                {
                    break;
                }
            }
        }
    }
}
