using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Eve.Marshal;

/// <summary>
/// One pass over a marshal stream. Strings and buffers in the result are slices of
/// the input (no copy); every read is restricted to the value area before the save table.
/// </summary>
internal sealed class MarshalDecoder
{
    private static readonly byte[] SingleBytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

    private readonly byte[] _buf;
    private readonly int _start;
    private readonly int _len;
    private readonly ReadOnlyMemory<byte> _input;

    private long _pos;
    private long _valueEnd;
    private PyValue?[] _stored = [];
    private long? _savePos;
    private long? _saveEnd;
    private int _depth;
    private int _savedRefs;
    private CachedDescriptor? _descriptor;
    private readonly AllocationBudget _budget;

    private sealed record CachedDescriptor(int Start, int End, Descriptor Descriptor);

    private sealed class AllocationBudget
    {
        private int _remaining = MarshalCodec.MaxDecodedAllocationUnits;

        public void Charge(long amount)
        {
            if (amount < 0 || amount > _remaining)
            {
                throw MarshalException.AllocationBudgetExceeded();
            }
            _remaining -= (int)amount;
        }
    }

    private MarshalDecoder(ReadOnlyMemory<byte> input, int depth, AllocationBudget budget)
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
        _budget = budget;
        _valueEnd = _len;
    }

    /// <summary>
    /// <paramref name="depth"/> is the nesting already used by enclosing streams: a nested
    /// PySubStream continues the count, so a chain of substreams cannot get around
    /// <see cref="MarshalCodec.MaxDepth"/> and exhaust the stack.
    /// </summary>
    public static PyValue DecodeStream(ReadOnlyMemory<byte> input, bool requireExactEnd, int depth) =>
        DecodeStream(input, requireExactEnd, depth, new AllocationBudget());

    private static PyValue DecodeStream(ReadOnlyMemory<byte> input, bool requireExactEnd, int depth, AllocationBudget budget)
    {
        var decoder = new MarshalDecoder(input, depth, budget);
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
            decoder._budget.Charge(saveCount);
            decoder._stored = new PyValue[saveCount];
            decoder._savePos = decoder._len - tableBytes;
            decoder._saveEnd = decoder._len;
            valueEnd = decoder._len - tableBytes;
            for (var position = valueEnd; position < decoder._len; position += 4)
            {
                var index = BinaryPrimitives.ReadUInt32LittleEndian(
                    decoder._buf.AsSpan(decoder._start + (int)position, 4));
                if (index == 0 || index > saveCount)
                {
                    throw MarshalException.SaveTable("save index outside the declared table");
                }
            }
        }
        decoder._valueEnd = valueEnd;

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
        if (_pos >= _valueEnd)
        {
            throw MarshalException.UnexpectedEnd();
        }
        return At(_pos++);
    }

    /// <summary>
    /// Reads a fixed-width value wholly within the payload area.
    /// </summary>
    private ReadOnlySpan<byte> Take(int count)
    {
        if (count >= 0 && count <= _valueEnd - _pos)
        {
            var span = _buf.AsSpan(_start + (int)_pos, count);
            _pos += count;
            return span;
        }
        throw MarshalException.UnexpectedEnd();
    }

    private uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    private long Size() => U8() switch
    {
        0xff => U32(),
        var size => size,
    };

    /// <summary>Reads bytes wholly inside this stream's value area, excluding any save table.</summary>
    private (int Start, int End) Range(long length)
    {
        if (length < 0 || length > _valueEnd - _pos)
        {
            throw MarshalException.UnexpectedEnd();
        }
        var start = (int)_pos;
        var end = checked((int)(_pos + length));
        _pos = end;
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
        if (index == 0 || index > _stored.Length)
        {
            throw MarshalException.SaveTable("save index outside the declared table");
        }
        return index;
    }

    private void Store(long index, PyValue value)
    {
        if (index == 0)
        {
            return;
        }
        if (index < 1 || index > _stored.Length)
        {
            throw MarshalException.SaveTable("save index outside the declared table");
        }
        _stored[index - 1] = value;
    }

    /// <summary>A count read from the wire, checked against the minimum encoded bytes before allocation.</summary>
    private int Capacity(long count, int valuesPerEntry = 1)
    {
        if (count < 0 || count > (_valueEnd - _pos) / valuesPerEntry)
        {
            throw MarshalException.UnexpectedEnd();
        }
        _budget.Charge(count * (valuesPerEntry + 1));
        return (int)count;
    }

    internal void ChargeAllocation(long count) => _budget.Charge(count);

    public PyValue Value(bool precharged = false)
    {
        if (!precharged)
        {
            _budget.Charge(1);
        }
        if (_depth >= MarshalCodec.MaxDepth)
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
        if (_pos >= _valueEnd)
        {
            throw MarshalException.UnexpectedEnd();
        }

        var raw = U8();
        var save = (raw & Wire.SaveMask) != 0;
        var opcode = (byte)(raw & Wire.OpcodeMask);

        if (opcode is Op.OneTuple or Op.TwoTuple or Op.Tuple or Op.OneList or Op.List)
        {
            return Sequence(opcode, save);
        }
        if (opcode is Op.Dict or Op.Object or Op.ObjectEx1 or Op.ObjectEx2 or Op.SubStruct or Op.ChecksummedStream or Op.SubStream or Op.PackedRow)
        {
            return Compound(opcode, save);
        }

        var value = Scalar(opcode, raw);
        if (save && IsSavedAfterValue(opcode))
        {
            Store(SaveIndex(), value);
        }
        return value;
    }

    private static bool IsSavedAfterValue(byte opcode) =>
        opcode is Op.Token or Op.Buffer or Op.LongString or Op.CPickled or Op.VarInteger;

    private PyValue Sequence(byte opcode, bool save)
    {
        switch (opcode)
        {
            case Op.OneTuple:
                return ReadTuple(1, save);
            case Op.TwoTuple:
                return ReadTuple(2, save);
            case Op.Tuple:
                return ReadTuple(Size(), save);
            case Op.OneList:
                return ReadList(1, save);
            default:
                return ReadList(Size(), save);
        }
    }

    private PyTuple ReadTuple(long count, bool save)
    {
        var items = new List<PyValue>(Capacity(count));
        var tuple = new PyTuple(items);
        if (save)
        {
            Store(SaveIndex(), tuple);
        }
        for (long i = 0; i < count; i++)
        {
            var item = Value(precharged: true);
            if (ReferenceEquals(item, tuple))
            {
                throw MarshalException.SaveTable("self referencing tuple");
            }
            items.Add(item);
        }
        return tuple;
    }

    private PyList ReadList(long count, bool save)
    {
        var items = new List<PyValue>(Capacity(count));
        var list = new PyList(items);
        if (save)
        {
            Store(SaveIndex(), list);
        }
        for (long i = 0; i < count; i++)
        {
            items.Add(Value(precharged: true));
        }
        return list;
    }

    private PyValue Compound(byte opcode, bool save)
    {
        switch (opcode)
        {
            case Op.Dict:
                return ReadDict(save);
            case Op.Object:
                var storageIndex = save ? SaveIndex() : 0;
                var name = Value();
                var obj = new PyObject(name, PyNone.Instance);
                Store(storageIndex, obj);
                obj.Complete(Value());
                return obj;
            case Op.ObjectEx1 or Op.ObjectEx2:
                return ObjectEx(opcode, save);
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

    private PyValue ReadDict(bool save)
    {
        var count = Size();
        var entries = new List<KeyValuePair<PyValue, PyValue>>(Capacity(count, valuesPerEntry: 2));
        var dict = new PyDict(entries);
        if (save)
        {
            Store(SaveIndex(), dict);
        }
        for (long i = 0; i < count; i++)
        {
            // The wire stores each pair value first, then key.
            var value = Value(precharged: true);
            var key = Value(precharged: true);
            entries.Add(new(key, value));
        }
        return dict;
    }

    private PyValue SubStream()
    {
        var length = Size();
        var data = Chunk(length);
        // A nested stream that does not decode is kept as raw bytes.
        try
        {
            return new PySubStream(DecodeStream(data, false, _depth, _budget));
        }
        catch (MarshalException error) when (error.Kind != MarshalErrorKind.AllocationBudgetExceeded)
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
            if (_pos + length <= _valueEnd
                && _buf.AsSpan(_start + (int)_pos, length).SequenceEqual(_buf.AsSpan(_start + cached.Start, length)))
            {
                _pos += length;
                return cached.Descriptor;
            }
        }

        var start = (int)_pos;
        var (saveBefore, refsBefore) = (_savePos, _savedRefs);
        var header = Value();
        if (Descriptor.DescriptorColumns(header) is { } columns)
        {
            _budget.Charge(columns.Count * 3L);
        }
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
                var pickleEnd = PickleBoundary.FindEnd(_buf.AsSpan(_start, (int)_valueEnd), (int)_pos);
                return new PyCPickled(Chunk(pickleEnd - _pos));
            case Op.SavedStreamElement:
                _savedRefs++;
                var slot = Size();
                if (slot >= 1 && slot <= _stored.Length && _stored[slot - 1] is { } stored)
                {
                    return stored;
                }
                throw MarshalException.SaveTable("TY_REFERENCE points to an unpublished save slot");
            default:
                throw MarshalException.UnknownOpcode(raw, (int)_pos - 1);
        }
    }

    private PyValue VarInteger()
    {
        var size = Size();
        if (size == 0)
        {
            return new PyInt(0);
        }
        if (size > _valueEnd - _pos)
        {
            throw MarshalException.TruncatedInteger();
        }
        var bytes = Bytes(size);
        var value = new BigInteger(bytes, isUnsigned: false, isBigEndian: false);
        if (value >= -9_007_199_254_740_991L && value <= 9_007_199_254_740_991L)
        {
            return new PyInt((long)value);
        }
        if (value.Sign >= 0)
        {
            return new PyBigUnsigned(value.ToByteArray(isUnsigned: true, isBigEndian: false));
        }
        return new PyBigInteger(value);
    }

    private bool Terminated() => _pos < _valueEnd && At(_pos) == Op.PackedTerminator;

    private PyValue ObjectEx(byte opcode, bool save)
    {
        var storageIndex = save ? SaveIndex() : 0;
        var header = Value();
        var list = new List<PyValue>();
        var dict = new List<KeyValuePair<PyValue, PyValue>>();
        var result = new PyObjectEx(opcode == Op.ObjectEx1 ? ObjectExKind.Ex1 : ObjectExKind.Ex2, header, list, dict);
        Store(storageIndex, result);
        var listTerminated = false;
        while (_pos < _valueEnd)
        {
            if (Terminated())
            {
                _pos++;
                listTerminated = true;
                break;
            }
            _budget.Charge(1);
            list.Add(Value());
        }
        if (!listTerminated)
        {
            throw MarshalException.UnexpectedEnd();
        }

        var dictTerminated = false;
        while (_pos < _valueEnd)
        {
            if (Terminated())
            {
                _pos++;
                dictTerminated = true;
                break;
            }
            var key = Value();
            var value = Value();
            _budget.Charge(1);
            dict.Add(new(key, value));
        }
        if (!dictTerminated)
        {
            throw MarshalException.UnexpectedEnd();
        }
        return result;
    }

    /// <summary>Node's <c>toString("utf16le")</c>: whole code units only, invalid surrogates replaced.</summary>
    private static string Utf16Lossy(ReadOnlySpan<byte> bytes) => Encoding.Unicode.GetString(bytes[..(bytes.Length & ~1)]);
}
