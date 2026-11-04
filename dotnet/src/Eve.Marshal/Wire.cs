namespace Eve.Marshal;

/// <summary>Wire constants shared by the encoder and the decoder.</summary>
internal static class Wire
{
    public const byte Header = 0x7e;
    public const byte SaveMask = 0x40;
    public const byte OpcodeMask = 0x3f;
}

/// <summary>Opcodes, named after the EVEmu opcode table the JS reference follows.</summary>
internal static class Op
{
    public const byte None = 0x01;
    public const byte Token = 0x02;
    public const byte LongLong = 0x03;
    public const byte Long = 0x04;
    public const byte SignedShort = 0x05;
    public const byte Byte = 0x06;
    public const byte MinusOne = 0x07;
    public const byte ZeroInteger = 0x08;
    public const byte OneInteger = 0x09;
    public const byte Real = 0x0a;
    public const byte ZeroReal = 0x0b;
    public const byte Buffer = 0x0d;
    public const byte EmptyString = 0x0e;
    public const byte CharString = 0x0f;
    public const byte ShortString = 0x10;
    public const byte StringTableItem = 0x11;
    public const byte WStringUcs2 = 0x12;
    public const byte LongString = 0x13;
    public const byte Tuple = 0x14;
    public const byte List = 0x15;
    public const byte Dict = 0x16;
    public const byte Object = 0x17;
    public const byte SubStruct = 0x19;
    public const byte SavedStreamElement = 0x1b;
    public const byte ChecksummedStream = 0x1c;
    public const byte True = 0x1f;
    public const byte False = 0x20;
    public const byte CPickled = 0x21;
    public const byte ObjectEx1 = 0x22;
    public const byte ObjectEx2 = 0x23;
    public const byte EmptyTuple = 0x24;
    public const byte OneTuple = 0x25;
    public const byte EmptyList = 0x26;
    public const byte OneList = 0x27;
    public const byte EmptyWString = 0x28;
    public const byte WStringUcs2Char = 0x29;
    public const byte PackedRow = 0x2a;
    public const byte SubStream = 0x2b;
    public const byte TwoTuple = 0x2c;
    public const byte PackedTerminator = 0x2d;
    public const byte WStringUtf8 = 0x2e;
    public const byte VarInteger = 0x2f;
}

/// <summary>Column type codes of a packed row's <c>DBRowDescriptor</c>.</summary>
public static class DbType
{
    public const long Empty = 0x00;
    public const long I2 = 0x02;
    public const long I4 = 0x03;
    public const long R4 = 0x04;
    public const long R8 = 0x05;
    public const long Cy = 0x06;
    public const long Error = 0x0a;
    public const long Bool = 0x0b;
    public const long I1 = 0x10;
    public const long UI1 = 0x11;
    public const long UI2 = 0x12;
    public const long UI4 = 0x13;
    public const long I8 = 0x14;
    public const long UI8 = 0x15;
    public const long FileTime = 0x40;
    public const long Bytes = 0x80;
    public const long Str = 0x81;
    public const long WStr = 0x82;

    /// <summary>
    /// Width in bits of a column's fixed-width slot: 64/32/16/8, 1 for BOOL,
    /// 0 for anything stored as a marshal value after the row data.
    /// </summary>
    public static int SizeBits(long dbType) => dbType switch
    {
        Cy or I8 or UI8 or FileTime or R8 => 64,
        I4 or UI4 or R4 => 32,
        I2 or UI2 => 16,
        I1 or UI1 => 8,
        Bool => 1,
        _ => 0,
    };
}
