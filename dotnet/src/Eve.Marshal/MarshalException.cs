namespace Eve.Marshal;

public enum MarshalErrorKind
{
    /// <summary>The stream ended where a value or a length byte was expected.</summary>
    UnexpectedEnd,

    /// <summary>The first byte is not the 0x7E marshal magic.</summary>
    InvalidHeader,

    /// <summary>An opcode the codec does not know.</summary>
    UnknownOpcode,

    /// <summary>The save-index table at the end of the stream is malformed or missing.</summary>
    SaveTable,

    /// <summary><see cref="MarshalCodec.DecodeExact(ReadOnlyMemory{byte})"/> found bytes left over after the root value.</summary>
    TrailingData,

    /// <summary>A fixed-width read ran past the end of a buffer shorter than the read.</summary>
    BufferOutOfBounds,

    /// <summary>A fixed-width read ran past the end of a longer buffer, or an integer does not fit its wire width.</summary>
    OutOfRange,

    /// <summary>A variable-width integer was truncated.</summary>
    TruncatedInteger,

    /// <summary>A packed row whose header, columns, or values do not agree.</summary>
    InvalidPackedRow,

    /// <summary>A value the encoder cannot represent.</summary>
    Unencodable,

    /// <summary>Nesting deeper than <see cref="MarshalCodec.MaxDepth"/>.</summary>
    TooDeep,
}

/// <summary>
/// A codec failure. <see cref="Kind"/> lines up with the failure classes of the
/// JS reference so golden tests can assert not just "it failed" but "it failed
/// the same way": see <see cref="ReferenceClass"/>.
/// </summary>
public sealed class MarshalException(MarshalErrorKind kind, string message) : Exception(message)
{
    public MarshalErrorKind Kind { get; } = kind;

    /// <summary>
    /// The class of failure as the JS reference reports it: <c>Error</c>,
    /// <c>TypeError</c>, <c>RangeError:ERR_OUT_OF_RANGE</c> or
    /// <c>RangeError:ERR_BUFFER_OUT_OF_BOUNDS</c>.
    /// </summary>
    public string ReferenceClass => Kind switch
    {
        MarshalErrorKind.BufferOutOfBounds => "RangeError:ERR_BUFFER_OUT_OF_BOUNDS",
        MarshalErrorKind.OutOfRange => "RangeError:ERR_OUT_OF_RANGE",
        MarshalErrorKind.TruncatedInteger => "TypeError",
        _ => "Error",
    };

    internal static MarshalException UnexpectedEnd() => new(MarshalErrorKind.UnexpectedEnd, "unexpected end of marshal data");

    internal static MarshalException InvalidHeader(byte b) => new(MarshalErrorKind.InvalidHeader, $"invalid marshal header 0x{b:x2} (expected 0x7e)");

    internal static MarshalException UnknownOpcode(byte opcode, int position) => new(MarshalErrorKind.UnknownOpcode, $"unknown marshal opcode 0x{opcode:x2} at position {position}");

    internal static MarshalException SaveTable(string what) => new(MarshalErrorKind.SaveTable, $"invalid marshal save table: {what}");

    internal static MarshalException TrailingData(string what) => new(MarshalErrorKind.TrailingData, $"marshal stream has {what}");

    internal static MarshalException BufferOutOfBounds() => new(MarshalErrorKind.BufferOutOfBounds, "attempt to access memory outside buffer bounds");

    internal static MarshalException OutOfRange(string what) => new(MarshalErrorKind.OutOfRange, $"value out of range: {what}");

    internal static MarshalException TruncatedInteger() => new(MarshalErrorKind.TruncatedInteger, "truncated variable-width integer");

    internal static MarshalException InvalidPackedRow(string what) => new(MarshalErrorKind.InvalidPackedRow, $"invalid packed row: {what}");

    internal static MarshalException Unencodable(string what) => new(MarshalErrorKind.Unencodable, $"cannot marshal value: {what}");

    internal static MarshalException TooDeep() => new(MarshalErrorKind.TooDeep, "marshal value nested too deeply");
}
