using System.Buffers;
using System.Buffers.Binary;

namespace Eve.Marshal;

/// <summary>
/// A growable byte sink with the little-endian writers the encoder needs. Storage comes from the
/// shared array pool, so encoding a large value does not allocate a chain of ever larger arrays
/// (which land on the large object heap); only the final <see cref="ToArray"/> allocates.
/// </summary>
internal sealed class ByteBuffer : IDisposable
{
    private byte[] _array;
    private int _length;

    public ByteBuffer(int capacity = 256) => _array = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 256));

    public void Dispose()
    {
        var array = _array;
        _array = [];
        _length = 0;
        if (array.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(array);
        }
    }

    public int Length => _length;

    public ReadOnlySpan<byte> AsSpan() => _array.AsSpan(0, _length);

    private Span<byte> Reserve(int count)
    {
        if (_length + count > _array.Length)
        {
            var larger = ArrayPool<byte>.Shared.Rent(Math.Max(_array.Length * 2, _length + count));
            Buffer.BlockCopy(_array, 0, larger, 0, _length);
            ArrayPool<byte>.Shared.Return(_array);
            _array = larger;
        }
        var span = _array.AsSpan(_length, count);
        _length += count;
        return span;
    }

    public void Write(byte value) => Reserve(1)[0] = value;

    public void Write(byte first, byte second)
    {
        var span = Reserve(2);
        span[0] = first;
        span[1] = second;
    }

    public void Write(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Reserve(bytes.Length));

    public void WriteInt16(short value) => BinaryPrimitives.WriteInt16LittleEndian(Reserve(2), value);

    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);

    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);

    public void WriteInt64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);

    public void WriteSingle(float value) => BinaryPrimitives.WriteSingleLittleEndian(Reserve(4), value);

    public void WriteDouble(double value) => BinaryPrimitives.WriteDoubleLittleEndian(Reserve(8), value);

    /// <summary>A copy of the bytes from <paramref name="start"/> to the end.</summary>
    public byte[] CopyFrom(int start) => _array.AsSpan(start, _length - start).ToArray();

    /// <summary>Overwrites one already-written byte.</summary>
    public void Set(int index, byte value) => _array[index] = value;

    /// <summary>Inserts <paramref name="bytes"/> at <paramref name="index"/>, shifting what follows.</summary>
    public void Insert(int index, ReadOnlySpan<byte> bytes)
    {
        Reserve(bytes.Length);
        Array.Copy(_array, index, _array, index + bytes.Length, _length - bytes.Length - index);
        bytes.CopyTo(_array.AsSpan(index));
    }

    public byte[] ToArray() => _array.AsSpan(0, _length).ToArray();
}
