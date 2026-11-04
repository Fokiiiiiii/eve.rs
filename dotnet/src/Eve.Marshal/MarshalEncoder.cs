using System.Text;

namespace Eve.Marshal;

internal sealed class MarshalEncoder
{
    private static readonly Dictionary<string, byte> TableIndex = BuildTableIndex();

    private int _depth;

    public MarshalEncoder(ByteBuffer output) => Out = output;

    public ByteBuffer Out { get; }

    private static Dictionary<string, byte> BuildTableIndex()
    {
        var map = new Dictionary<string, byte>(StringTable.Entries.Length, StringComparer.Ordinal);
        for (var position = 0; position < StringTable.Entries.Length; position++)
        {
            // First occurrence wins, as in the JS reference.
            map.TryAdd(StringTable.Entries[position], (byte)(position + 1));
        }
        return map;
    }

    /// <summary>The <c>PutSizeEx</c> length: one byte below 255, else 0xFF and a u32.</summary>
    public void Size(long size)
    {
        if (size < 0xff)
        {
            Out.Write((byte)size);
        }
        else
        {
            if (size > uint.MaxValue)
            {
                throw MarshalException.OutOfRange("length larger than 4 GiB");
            }
            Out.Write(0xff);
            Out.WriteUInt32((uint)size);
        }
    }

    private void Integer(long value)
    {
        switch (value)
        {
            case -1:
                Out.Write(Op.MinusOne);
                break;
            case 0:
                Out.Write(Op.ZeroInteger);
                break;
            case 1:
                Out.Write(Op.OneInteger);
                break;
            case >= -128 and <= 127:
                Out.Write(Op.Byte, unchecked((byte)(sbyte)value));
                break;
            case >= -32768 and <= 32767:
                Out.Write(Op.SignedShort);
                Out.WriteInt16((short)value);
                break;
            case >= int.MinValue and <= int.MaxValue:
                Out.Write(Op.Long);
                Out.WriteInt32((int)value);
                break;
            default:
                Out.Write(Op.LongLong);
                Out.WriteInt64(value);
                break;
        }
    }

    private void WriteString(string text)
    {
        var length = Encoding.UTF8.GetByteCount(text);
        switch (length)
        {
            case 0:
                Out.Write(Op.EmptyString);
                break;
            case 1:
                Out.Write(Op.CharString, (byte)text[0]);
                break;
            default:
                if (TableIndex.TryGetValue(text, out var index))
                {
                    Out.Write(Op.StringTableItem, index);
                }
                else
                {
                    Out.Write(Op.LongString);
                    Size(length);
                    WriteUtf8(text, length);
                }
                break;
        }
    }

    private void WriteUtf8(string text, int length)
    {
        if (length <= 512)
        {
            Span<byte> scratch = stackalloc byte[length];
            Encoding.UTF8.GetBytes(text, scratch);
            Out.Write(scratch);
        }
        else
        {
            Out.Write(Encoding.UTF8.GetBytes(text));
        }
    }

    private void ByteString(ReadOnlySpan<byte> bytes)
    {
        switch (bytes.Length)
        {
            case 0:
                Out.Write(Op.EmptyString);
                break;
            case 1:
                Out.Write(Op.CharString, bytes[0]);
                break;
            case <= 255:
                Out.Write(Op.ShortString, (byte)bytes.Length);
                Out.Write(bytes);
                break;
            default:
                Out.Write(Op.LongString);
                Size(bytes.Length);
                Out.Write(bytes);
                break;
        }
    }

    private void Sized(byte opcode, ReadOnlySpan<byte> bytes)
    {
        Out.Write(opcode);
        Size(bytes.Length);
        Out.Write(bytes);
    }

    private void SizedText(byte opcode, string text)
    {
        Out.Write(opcode);
        var length = Encoding.UTF8.GetByteCount(text);
        Size(length);
        WriteUtf8(text, length);
    }

    private void Pairs(IReadOnlyList<KeyValuePair<PyValue, PyValue>> pairs, bool valueFirst)
    {
        for (var index = 0; index < pairs.Count; index++)
        {
            var pair = pairs[index];
            if (valueFirst)
            {
                Value(pair.Value);
                Value(pair.Key);
            }
            else
            {
                Value(pair.Key);
                Value(pair.Value);
            }
        }
    }

    public void Value(PyValue value)
    {
        if (_depth >= MarshalCodec.MaxDepth)
        {
            throw MarshalException.TooDeep();
        }
        _depth++;
        try
        {
            ValueInner(value);
        }
        finally
        {
            _depth--;
        }
    }

    private void ValueInner(PyValue value)
    {
        switch (value)
        {
            case PyNone:
                Out.Write(Op.None);
                break;
            case PyBool flag:
                Out.Write(flag.Value ? Op.True : Op.False);
                break;
            case PyInt number:
                Integer(number.Value);
                break;
            case PyLong number:
                Out.Write(Op.LongLong);
                Out.WriteInt64(number.Value);
                break;
            case PyReal number:
                if (number.Value == 0.0)
                {
                    Out.Write(Op.ZeroReal);
                }
                else
                {
                    Out.Write(Op.Real);
                    Out.WriteDouble(number.Value);
                }
                break;
            case PyBigUnsigned big:
                Sized(Op.VarInteger, big.Bytes);
                break;
            case PyStr text:
                WriteString(text.Value);
                break;
            case PyBytes bytes:
                ByteString(bytes.Value.Span);
                break;
            case PyBuffer bytes:
                Sized(Op.Buffer, bytes.Value.Span);
                break;
            case PyWString text:
                if (text.Value.Length == 0)
                {
                    Out.Write(Op.EmptyWString);
                }
                else
                {
                    SizedText(Op.WStringUtf8, text.Value);
                }
                break;
            case PyToken text:
                SizedText(Op.Token, text.Value);
                break;
            case PyTuple tuple:
                switch (tuple.Items.Count)
                {
                    case 0:
                        Out.Write(Op.EmptyTuple);
                        break;
                    case 1:
                        Out.Write(Op.OneTuple);
                        break;
                    case 2:
                        Out.Write(Op.TwoTuple);
                        break;
                    default:
                        Out.Write(Op.Tuple);
                        Size(tuple.Items.Count);
                        break;
                }
                for (var index = 0; index < tuple.Items.Count; index++)
                {
                    Value(tuple.Items[index]);
                }
                break;
            case PyList list:
                switch (list.Items.Count)
                {
                    case 0:
                        Out.Write(Op.EmptyList);
                        break;
                    case 1:
                        Out.Write(Op.OneList);
                        Value(list.Items[0]);
                        break;
                    default:
                        Out.Write(Op.List);
                        Size(list.Items.Count);
                        for (var index = 0; index < list.Items.Count; index++)
                        {
                            Value(list.Items[index]);
                        }
                        break;
                }
                break;
            case PyDict dict:
                Out.Write(Op.Dict);
                Size(dict.Entries.Count);
                // The wire stores each pair value first, then key.
                Pairs(dict.Entries, valueFirst: true);
                break;
            case PyObject obj:
                Out.Write(Op.Object);
                Value(obj.Name);
                Value(obj.Args);
                break;
            case PyObjectEx ex:
                Out.Write(ex.Kind == ObjectExKind.Ex1 ? Op.ObjectEx1 : Op.ObjectEx2);
                Value(ex.Header);
                for (var index = 0; index < ex.List.Count; index++)
                {
                    Value(ex.List[index]);
                }
                Out.Write(Op.PackedTerminator);
                Pairs(ex.Dict, valueFirst: false);
                Out.Write(Op.PackedTerminator);
                break;
            case PySubStruct inner:
                Out.Write(Op.SubStruct);
                Value(inner.Value);
                break;
            case PySubStream { Decoded: { } decoded }:
                Out.Write(Op.SubStream);
                LengthPrefixed(() =>
                {
                    Out.Write(Wire.Header);
                    Out.WriteUInt32(0);
                    Value(decoded);
                });
                break;
            case PySubStream stream:
                Sized(Op.SubStream, stream.Raw.Span);
                break;
            case PyChecksummed checksummed:
                Out.Write(Op.ChecksummedStream);
                Out.WriteUInt32(checksummed.Checksum);
                Value(checksummed.Value);
                break;
            case PyCPickled pickled:
                Sized(Op.CPickled, pickled.Value.Span);
                break;
            case PyPackedRow row:
                PackedRowCodec.Encode(this, row);
                break;
            default:
                throw MarshalException.Unencodable(value.GetType().Name);
        }
    }

    /// <summary>
    /// Writes a size field followed by whatever <paramref name="body"/> appends, without building
    /// the body in a scratch buffer: the body is encoded in place behind a one-byte size slot,
    /// and widened afterwards if it turns out to need the five-byte form.
    /// </summary>
    private void LengthPrefixed(Action body)
    {
        var slot = Out.Length;
        Out.Write(0);
        var start = Out.Length;
        body();
        var length = Out.Length - start;
        if (length < 0xff)
        {
            Out.Set(slot, (byte)length);
        }
        else
        {
            Out.Set(slot, 0xff);
            Span<byte> width = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(width, (uint)length);
            Out.Insert(start, width);
        }
    }
}
