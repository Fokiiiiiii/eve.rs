namespace Eve.Host.PublicGateway;

/// <summary>
/// The protobuf wire format the public gateway needs (varint, length-delimited, fixed32/64), written by hand: the
/// gateway's messages are a handful of small envelopes and plan and title messages, and no generated code is needed.
/// </summary>
internal sealed class ProtoWriter
{
    private readonly MemoryStream _stream = new();

    public byte[] ToArray() => _stream.ToArray();

    public void Varint(ulong value)
    {
        while (value >= 0x80)
        {
            _stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        _stream.WriteByte((byte)value);
    }

    public void Tag(int field, int wireType) => Varint((ulong)((field << 3) | wireType));

    /// <summary>A uint64, int64, uint32, int32, bool or enum field (zero is omitted, as proto3 does).</summary>
    public void Uint(int field, ulong value)
    {
        if (value == 0) return;
        Tag(field, 0);
        Varint(value);
    }

    public void Bool(int field, bool value)
    {
        if (!value) return;
        Tag(field, 0);
        Varint(1);
    }

    /// <summary>A string or bytes field; an empty value is omitted (proto3).</summary>
    public void Bytes(int field, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return;
        Tag(field, 2);
        Varint((ulong)value.Length);
        _stream.Write(value);
    }

    public void String(int field, string value)
    {
        if (value.Length == 0) return;
        Bytes(field, System.Text.Encoding.UTF8.GetBytes(value));
    }

    /// <summary>A message field, always written (a present empty message is still present).</summary>
    public void Message(int field, byte[] message)
    {
        Tag(field, 2);
        Varint((ulong)message.Length);
        _stream.Write(message);
    }
}

/// <summary>Reads one message: the fields in order, each as its number, wire type and value.</summary>
internal readonly struct ProtoField(int number, int wireType, ulong scalar, ReadOnlyMemory<byte> bytes)
{
    public int Number { get; } = number;
    public int WireType { get; } = wireType;
    public ulong Scalar { get; } = scalar;
    public ReadOnlyMemory<byte> Bytes { get; } = bytes;
}

internal static class ProtoReader
{
    public static List<ProtoField> Fields(ReadOnlyMemory<byte> message)
    {
        var fields = new List<ProtoField>();
        var span = message.Span;
        var offset = 0;
        while (offset < span.Length)
        {
            var tag = ReadVarint(span, ref offset);
            var number = (int)(tag >> 3);
            var wireType = (int)(tag & 7);
            switch (wireType)
            {
                case 0:
                    fields.Add(new ProtoField(number, 0, ReadVarint(span, ref offset), default));
                    break;
                case 1:
                    fields.Add(new ProtoField(number, 1, BitConverter.ToUInt64(span.Slice(offset, 8)), default));
                    offset += 8;
                    break;
                case 2:
                {
                    var length = checked((int)ReadVarint(span, ref offset));
                    if (length < 0 || span.Length - offset < length) throw new FormatException("a truncated length-delimited field");
                    fields.Add(new ProtoField(number, 2, 0, message.Slice(offset, length)));
                    offset += length;
                    break;
                }
                case 5:
                    fields.Add(new ProtoField(number, 5, BitConverter.ToUInt32(span.Slice(offset, 4)), default));
                    offset += 4;
                    break;
                default:
                    throw new FormatException($"the wire type {wireType} is not supported");
            }
        }
        return fields;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> span, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (offset >= span.Length) throw new FormatException("a truncated varint");
            var b = span[offset++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new FormatException("a varint longer than 64 bits");
    }
}
