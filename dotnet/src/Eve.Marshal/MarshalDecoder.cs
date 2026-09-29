using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Eve.Marshal;

/// <summary>
/// One pass over a marshal stream. Strings and buffers in the result are slices of
/// the input (no copy); a string or buffer whose declared length runs past the end
/// of the input yields the bytes that are there, as in the JS reference.
/// </summary>
internal sealed class MarshalDecoder
{
    private static readonly byte[] SingleBytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

    private readonly byte[] _buf;
    private readonly int _start;
    private readonly int _len;
    private readonly ReadOnlyMemory<byte> _input;

    private long _pos;
    private PyValue[] _stored = [];
    private long? _savePos;
    private long? _saveEnd;
    private int _depth;
    private int _savedRefs;
    private CachedDescriptor? _descriptor;

    private sealed record CachedDescriptor(int Start, int End, Descriptor Descriptor);

    private MarshalDecoder(ReadOnlyMemory<byte> input, int depth)
    {
        if (MemoryMarshal.TryGetArray(input, out var segment) && segment.Array is not null)
        {
            _buf = segment.Array;
            _start = segment.Offset;
            _len = segment.Count;
            _input = input;
        }
        else
        {
            _buf = input.ToArray();
            _start = 0;
            _len = _buf.Length;
            _input = _buf;
        }
        _depth = depth;
    }

    /// <summary>
    /// <paramref name="depth"/> is the nesting already used by enclosing streams: a nested
    /// PySubStream continues the count, so a chain of substreams cannot get around
    /// <see cref="Marshal.MaxDepth"/> and exhaust the stack.
    /// </summary>
    public static PyValue DecodeStream(ReadOnlyMemory<byte> input, bool requireExactEnd, int depth)
    {
        var decoder = new MarshalDecoder(input, depth);
        var header = decoder.U8();
        if (header != Wire.Header)
        {
            throw MarshalException.InvalidHeader(header);
        }

        var saveCount = decoder.U32();
        long valueEnd = decoder._len;
        if (saveCount > 0)
        {
            var tableBytes = saveCount * 4L;
            if (decoder._len - decoder._pos < tableBytes)
            {
                throw MarshalException.SaveTable("save table larger than the stream");
            }
            decoder._stored = new PyValue[saveCount];
            Array.Fill(decoder._stored, PyNone.Instance);
            decoder._savePos = decoder._len - tableBytes;
            decoder._saveEnd = decoder._len;
            valueEnd = decoder._len - tableBytes;
        }

        var value = decoder.Value();
        if (requireExactEnd)
        {
            if (decoder._pos != valueEnd)
            {
                throw MarshalException.TrailingData("unconsumed payload bytes");
            }
            if (decoder._savePos != decoder._saveEnd)
            {
                throw MarshalException.TrailingData("unconsumed save-table entries");
            }
        }
        return value;
    }

    private byte At(long index) => _buf[_start + (int)index];

    private byte U8()
    {
        if (_pos >= _len)
        {
            throw MarshalException.UnexpectedEnd();
        }
        return At(_pos++);
    }

    /// <summary>
    /// A fixed-width read that fails the way Node's <c>Buffer.readXxx</c> does:
    /// <c>ERR_BUFFER_OUT_OF_BOUNDS</c> when the whole buffer is shorter than the value,
    /// <c>ERR_OUT_OF_RANGE</c> when only the offset is bad.
    /// </summary>
    private ReadOnlySpan<byte> Take(int count)
    {
        if (_pos + count <= _len)
        {
            var span = _buf.AsSpan(_start + (int)_pos, count);
            _pos += count;
            return span;
        }
        throw _len < count
            ? MarshalException.BufferOutOfBounds()
            : MarshalException.OutOfRange("read past end of stream");
    }

    private uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    private long Size() => U8() switch
    {
        0xff => U32(),
        var size => size,
    };

    /// <summary>The JS reference's <c>readBytes</c>: clamps to the input but always advances by the declared length.</summary>
    private (int Start, int End) Range(long length)
    {
        var start = (int)Math.Min(_pos, _len);
        var end = (int)Math.Min(_pos + length, _len);
        _pos += length;
        return (start, end);
    }

    private ReadOnlySpan<byte> Bytes(long length)
    {
        var (start, end) = Range(length);
        return _buf.AsSpan(_start + start, end - start);
    }

    /// <summary>A zero-copy view of the input.</summary>
    private ReadOnlyMemory<byte> Chunk(long length)
    {
        var (start, end) = Range(length);
        return start == end ? ReadOnlyMemory<byte>.Empty : _input.Slice(start, end - start);
    }

    private long SaveIndex()
    {
        if (_savePos is not { } position || _saveEnd is not { } end)
        {
            throw MarshalException.SaveTable("no save index table");
        }
        if (position + 4 > end)
        {
            throw MarshalException.SaveTable("save index table exhausted");
        }
        var index = BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(_start + (int)position, 4));
        _savePos = position + 4;
        return index;
    }

    /// <summary>A count read from the wire, bounded before it is used to size an allocation: every element takes at least one byte.</summary>
    private int Capacity(long count) => (int)Math.Min(count, Math.Max(0, _len - _pos));

    public PyValue Value()
    {
        if (_depth >= Marshal.MaxDepth)
        {
            throw MarshalException.TooDeep();
        }
        _depth++;
        try
        {
            return ValueInner();
        }
        finally
        {
            _depth--;
        }
    }

    private PyValue ValueInner()
    {
        if (_pos >= _len)
        {
            throw MarshalException.UnexpectedEnd();
        }

        var raw = U8();
        var save = (raw & Wire.SaveMask) != 0;
        var opcode = (byte)(raw & Wire.OpcodeMask);
        var storageIndex = save ? SaveIndex() : 0;

        var value = opcode switch
        {
            Op.OneTuple or Op.TwoTuple or Op.Tuple or Op.OneList or Op.List => Sequence(opcode),
            Op.Dict or Op.Object or Op.ObjectEx1 or Op.ObjectEx2 or Op.SubStruct or Op.ChecksummedStream or Op.SubStream or Op.PackedRow => Compound(opcode),
            _ => Scalar(opcode, raw),
        };

        if (save && storageIndex > 0 && storageIndex <= _stored.Length)
        {
            _stored[storageIndex - 1] = value;
        }
        return value;
    }

    private PyValue Sequence(byte opcode)
    {
        switch (opcode)
        {
            case Op.OneTuple:
                return new PyTuple([Value()]);
            case Op.TwoTuple:
                var first = Value();
                return new PyTuple([first, Value()]);
            case Op.Tuple:
                return new PyTuple(Items(Size()));
            case Op.OneList:
                return new PyList([Value()]);
            default:
                return new PyList(Items(Size()));
        }
    }

    private PyValue Compound(byte opcode)
    {
        switch (opcode)
        {
            case Op.Dict:
                return ReadDict();
            case Op.Object:
                var name = Value();
                return new PyObject(name, Value());
            case Op.ObjectEx1 or Op.ObjectEx2:
                return ObjectEx(opcode);
            case Op.SubStruct:
                return new PySubStruct(Value());
            case Op.ChecksummedStream:
                var checksum = U32();
                return new PyChecksummed(checksum, Value());
            case Op.SubStream:
                return SubStream();
            default:
                return PackedRow();
        }
    }

    private PyValue ReadDict()
    {
        var count = Size();
        var entries = new List<KeyValuePair<PyValue, PyValue>>(Capacity(count));
        for (long i = 0; i < count; i++)
        {
            // The wire stores each pair value first, then key.
            var value = Value();
            var key = Value();
            entries.Add(new(key, value));
        }
        return new PyDict(entries);
    }

    private PyValue SubStream()
    {
        var length = Size();
        var data = Chunk(length);
        // A nested stream that does not decode is kept as raw bytes.
        try
        {
            return new PySubStream(DecodeStream(data, false, _depth));
        }
        catch (MarshalException)
        {
            return new PySubStream(data);
        }
    }

    private PyValue PackedRow()
    {
        var descriptor = ReadDescriptor();
        var length = Size();
        var rle = Chunk(length);
        return PackedRowCodec.Decode(this, descriptor, rle);
    }

    /// <summary>
    /// Read a packed row's <c>DBRowDescriptor</c>. Every row of a rowset repeats the same
    /// one, so when the bytes at the cursor equal the previous descriptor's they are skipped
    /// and the parsed descriptor is shared.
    ///
    /// Reuse is only sound when decoding those bytes depends on nothing but the bytes, so a
    /// descriptor that consumed a save index or resolved a saved-element reference is never cached.
    /// </summary>
    private Descriptor ReadDescriptor()
    {
        if (_descriptor is { } cached)
        {
            var length = cached.End - cached.Start;
            if (_pos + length <= _len
                && _buf.AsSpan(_start + (int)_pos, length).SequenceEqual(_buf.AsSpan(_start + cached.Start, length)))
            {
                _pos += length;
                return cached.Descriptor;
            }
        }

        var start = (int)_pos;
        var (saveBefore, refsBefore) = (_savePos, _savedRefs);
        var header = Value();
        var descriptor = Descriptor.FromHeader(header);
        if (_savePos == saveBefore && _savedRefs == refsBefore)
        {
            _descriptor = new CachedDescriptor(start, (int)_pos, descriptor);
        }
        return descriptor;
    }

    private PyValue Scalar(byte opcode, byte raw)
    {
        switch (opcode)
        {
            case Op.None:
                return PyNone.Instance;
            case Op.True:
                return PyBool.True;
            case Op.False:
                return PyBool.False;
            case Op.ZeroInteger:
                return new PyInt(0);
            case Op.OneInteger:
                return new PyInt(1);
            case Op.MinusOne:
                return new PyInt(-1);
            case Op.Byte:
                return new PyInt((sbyte)U8());
            case Op.SignedShort:
                return new PyInt(BinaryPrimitives.ReadInt16LittleEndian(Take(2)));
            case Op.Long:
                return new PyInt(BinaryPrimitives.ReadInt32LittleEndian(Take(4)));
            case Op.LongLong:
                return new PyLong(BinaryPrimitives.ReadInt64LittleEndian(Take(8)));
            case Op.VarInteger:
                return VarInteger();
            case Op.ZeroReal:
                return new PyReal(0.0);
            case Op.Real:
                return new PyReal(BinaryPrimitives.ReadDoubleLittleEndian(Take(8)));
            case Op.EmptyString:
                return new PyBytes(ReadOnlyMemory<byte>.Empty);
            case Op.CharString:
                var single = U8();
                return new PyBytes(SingleBytes.AsMemory(single, 1));
            case Op.ShortString:
                return new PyBytes(Chunk(U8()));
            case Op.LongString:
                return new PyBytes(Chunk(Size()));
            case Op.StringTableItem:
                var index = U8();
                return new PyStr(index >= 1 && index <= StringTable.Entries.Length
                    ? StringTable.Entries[index - 1]
                    : $"<stringtable:{index}>");
            case Op.EmptyWString:
                return new PyWString("");
            case Op.WStringUcs2:
                var units = Size();
                return new PyWString(Utf16Lossy(Bytes(units * 2)));
            case Op.WStringUcs2Char:
                return new PyWString(Utf16Lossy(Bytes(2)));
            case Op.WStringUtf8:
                return new PyWString(Encoding.UTF8.GetString(Bytes(Size())));
            case Op.Token:
                return new PyToken(Encoding.UTF8.GetString(Bytes(Size())));
            case Op.EmptyTuple:
                return new PyTuple([]);
            case Op.EmptyList:
                return new PyList([]);
            case Op.Buffer:
                return new PyBuffer(Chunk(Size()));
            case Op.CPickled:
                return new PyCPickled(Chunk(Size()));
            case Op.SavedStreamElement:
                _savedRefs++;
                var slot = Size();
                return slot >= 1 && slot <= _stored.Length ? _stored[slot - 1] : PyNone.Instance;
            default:
                throw MarshalException.UnknownOpcode(raw, (int)_pos - 1);
        }
    }

    private List<PyValue> Items(long count)
    {
        var items = new List<PyValue>(Capacity(count));
        for (long i = 0; i < count; i++)
        {
            items.Add(Value());
        }
        return items;
    }

    private PyValue VarInteger()
    {
        var size = Size();
        if (size == 0)
        {
            return new PyInt(0);
        }
        var bytes = Bytes(size);
        // JS reads each declared byte; a truncated integer reads `undefined`.
        if (bytes.Length < size)
        {
            throw MarshalException.TruncatedInteger();
        }
        var significant = bytes.Length;
        while (significant > 0 && bytes[significant - 1] == 0)
        {
            significant--;
        }
        bytes = bytes[..significant];
        if (bytes.Length <= 8)
        {
            Span<byte> buffer = stackalloc byte[8];
            bytes.CopyTo(buffer);
            var magnitude = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
            if (magnitude <= 9_007_199_254_740_991UL)
            {
                return new PyInt((long)magnitude);
            }
        }
        return new PyBigUnsigned(bytes.ToArray());
    }

    private bool Terminated() => _pos < _len && (At(_pos) & Wire.OpcodeMask) == Op.PackedTerminator;

    private PyValue ObjectEx(byte opcode)
    {
        var header = Value();
        var list = new List<PyValue>();
        while (_pos < _len)
        {
            if (Terminated())
            {
                _pos++;
                break;
            }
            list.Add(Value());
        }

        var dict = new List<KeyValuePair<PyValue, PyValue>>();
        while (_pos < _len)
        {
            if (Terminated())
            {
                _pos++;
                break;
            }
            var key = Value();
            var value = Value();
            dict.Add(new(key, value));
        }
        return new PyObjectEx(opcode == Op.ObjectEx1 ? ObjectExKind.Ex1 : ObjectExKind.Ex2, header, list, dict);
    }

    /// <summary>Node's <c>toString("utf16le")</c>: whole code units only, invalid surrogates replaced.</summary>
    private static string Utf16Lossy(ReadOnlySpan<byte> bytes) => Encoding.Unicode.GetString(bytes[..(bytes.Length & ~1)]);
}
