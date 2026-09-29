namespace Eve.Marshal;

/// <summary>
/// EVE MachoNet marshal codec. Byte-compatible with the JS reference
/// (<c>server/src/network/tcp/utils/marshal.js</c>) and the Rust codec; compatibility is enforced by
/// the golden fixtures under <c>oracle/fixtures/marshal</c>, which the tests replay.
/// </summary>
public static class MarshalCodec
{
    /// <summary>
    /// Deepest nesting the codec accepts in either direction. The JS reference only stops at the
    /// engine's stack limit; a native decoder fed by a network peer needs an explicit bound.
    /// </summary>
    public const int MaxDepth = 256;

    /// <summary>
    /// Decodes a marshal stream, tolerating bytes after the root value: the same leniency as the JS
    /// reference's <c>marshalDecode</c>. A string or buffer whose declared length runs past the end
    /// of the input yields the bytes that are there instead of an error, and PyObjectEx lists that
    /// never reach their terminator end at the end of the input. Strings and buffers in the result
    /// are slices of <paramref name="input"/>: keep it alive and unchanged while using them.
    /// </summary>
    public static PyValue Decode(ReadOnlyMemory<byte> input) => MarshalDecoder.DecodeStream(input, false, 0);

    /// <summary>Decodes a marshal stream and requires that it is consumed exactly.</summary>
    public static PyValue DecodeExact(ReadOnlyMemory<byte> input) => MarshalDecoder.DecodeStream(input, true, 0);

    /// <summary>
    /// Encodes <paramref name="value"/> as a marshal stream: the 0x7E magic, a zero save count and the
    /// value. This is the payload of a packet, without the length prefix.
    /// </summary>
    public static byte[] Encode(PyValue value)
    {
        var buffer = new ByteBuffer();
        EncodeInto(value, buffer);
        return buffer.ToArray();
    }

    /// <summary>Encodes <paramref name="value"/> and prefixes the 4-byte little-endian payload length.</summary>
    public static byte[] EncodePacket(PyValue value)
    {
        var buffer = new ByteBuffer();
        buffer.WriteUInt32(0);
        EncodeInto(value, buffer);
        var length = buffer.Length - 4;
        var bytes = buffer.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)length);
        return bytes;
    }

    private static void EncodeInto(PyValue value, ByteBuffer buffer)
    {
        buffer.Write(Wire.Header);
        buffer.WriteUInt32(0);
        new MarshalEncoder(buffer).Value(value);
    }

    /// <summary>
    /// Splits a <c>[u32 length][payload]</c> frame off the front of <paramref name="input"/>.
    /// Returns false when <paramref name="input"/> does not yet hold a whole frame.
    /// </summary>
    public static bool TrySplitPacket(ReadOnlyMemory<byte> input, out ReadOnlyMemory<byte> payload, out ReadOnlyMemory<byte> rest)
    {
        payload = default;
        rest = input;
        if (input.Length < 4)
        {
            return false;
        }
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(input.Span);
        if (input.Length - 4L < length)
        {
            return false;
        }
        payload = input.Slice(4, (int)length);
        rest = input[(4 + (int)length)..];
        return true;
    }

    /// <summary>Compresses a packed row's fixed-width block (see <see cref="PyPackedRow"/>).</summary>
    public static byte[] CompressRle(ReadOnlySpan<byte> input) => PackedRowCodec.CompressRle(input);

    /// <summary>The inverse of <see cref="CompressRle"/>, producing exactly <paramref name="expected"/> bytes.</summary>
    public static byte[] DecompressRle(ReadOnlySpan<byte> input, int expected) => PackedRowCodec.DecompressRle(input, expected);
}
