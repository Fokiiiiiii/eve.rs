using System.Buffers.Binary;

namespace Eve.Marshal;

/// <summary>Finds the end of the Python 2 protocol-0-to-2 pickle embedded by TY_PICKLER.</summary>
internal static class PickleBoundary
{
    private static readonly HashSet<byte> NoArgumentOpcodes =
    [
        0x28, 0x29, 0x30, 0x31, 0x32, 0x4e, 0x51, 0x52, 0x5d, 0x61, 0x62, 0x64, 0x65,
        0x6c, 0x6f, 0x73, 0x74, 0x75, 0x7d, 0x81, 0x85, 0x86, 0x87, 0x88, 0x89,
    ];
    private static readonly Dictionary<byte, int> FixedArgumentBytes = new()
    {
        [0x47] = 8, [0x4a] = 4, [0x4b] = 1, [0x4d] = 2, [0x68] = 1, [0x6a] = 4,
        [0x71] = 1, [0x72] = 4, [0x80] = 1, [0x82] = 1, [0x83] = 2, [0x84] = 4,
    };
    private static readonly Dictionary<byte, int> LineArgumentCount = new()
    {
        [0x46] = 1, [0x49] = 1, [0x4c] = 1, [0x50] = 1, [0x53] = 1, [0x56] = 1,
        [0x67] = 1, [0x70] = 1, [0x63] = 2, [0x69] = 2,
    };
    private static readonly Dictionary<byte, int> CountedLengthBytes = new()
    {
        [0x54] = 4, [0x55] = 1, [0x58] = 4, [0x8a] = 1, [0x8b] = 4,
    };

    /// <summary>Returns the position just after STOP, or fails for an invalid or truncated pickle.</summary>
    public static int FindEnd(ReadOnlySpan<byte> buffer, int start)
    {
        var position = start;
        while (position < buffer.Length)
        {
            var opcode = buffer[position++];
            if (opcode == 0x2e)
            {
                return position;
            }
            if (NoArgumentOpcodes.Contains(opcode))
            {
                continue;
            }
            if (FixedArgumentBytes.TryGetValue(opcode, out var fixedBytes))
            {
                position = Advance(buffer, position, fixedBytes);
                continue;
            }
            if (LineArgumentCount.TryGetValue(opcode, out var lines))
            {
                for (var line = 0; line < lines; line++)
                {
                    var newline = buffer[position..].IndexOf((byte)0x0a);
                    if (newline < 0)
                    {
                        throw MarshalException.UnexpectedEnd();
                    }
                    position += newline + 1;
                }
                continue;
            }
            if (CountedLengthBytes.TryGetValue(opcode, out var lengthBytes))
            {
                if (lengthBytes > buffer.Length - position)
                {
                    throw MarshalException.UnexpectedEnd();
                }
                var length = lengthBytes == 1
                    ? buffer[position]
                    : BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(position, lengthBytes));
                var contentStart = position + lengthBytes;
                if (length > buffer.Length - contentStart)
                {
                    throw MarshalException.UnexpectedEnd();
                }
                position = contentStart + (int)length;
                continue;
            }
            throw MarshalException.UnknownOpcode(opcode, position - 1);
        }
        throw MarshalException.UnexpectedEnd();
    }

    private static int Advance(ReadOnlySpan<byte> buffer, int position, int count)
    {
        if (count > buffer.Length - position)
        {
            throw MarshalException.UnexpectedEnd();
        }
        return position + count;
    }
}
