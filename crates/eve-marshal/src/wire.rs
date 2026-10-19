pub const MARSHAL_HEADER: u8 = 0x7e;
pub const SAVE_MASK: u8 = 0x40;
pub const OPCODE_MASK: u8 = 0x3f;

/// Opcodes, named after the EVEmu opcode table the JS reference follows.
pub mod op {
    pub const NONE: u8 = 0x01;
    pub const TOKEN: u8 = 0x02;
    pub const LONG_LONG: u8 = 0x03;
    pub const LONG: u8 = 0x04;
    pub const SIGNED_SHORT: u8 = 0x05;
    pub const BYTE: u8 = 0x06;
    pub const MINUS_ONE: u8 = 0x07;
    pub const ZERO_INTEGER: u8 = 0x08;
    pub const ONE_INTEGER: u8 = 0x09;
    pub const REAL: u8 = 0x0a;
    pub const ZERO_REAL: u8 = 0x0b;
    pub const BUFFER: u8 = 0x0d;
    pub const EMPTY_STRING: u8 = 0x0e;
    pub const CHAR_STRING: u8 = 0x0f;
    pub const SHORT_STRING: u8 = 0x10;
    pub const STRING_TABLE_ITEM: u8 = 0x11;
    pub const WSTRING_UCS2: u8 = 0x12;
    pub const LONG_STRING: u8 = 0x13;
    pub const TUPLE: u8 = 0x14;
    pub const LIST: u8 = 0x15;
    pub const DICT: u8 = 0x16;
    pub const OBJECT: u8 = 0x17;
    pub const SUB_STRUCT: u8 = 0x19;
    pub const SAVED_STREAM_ELEMENT: u8 = 0x1b;
    pub const CHECKSUMMED_STREAM: u8 = 0x1c;
    pub const TRUE: u8 = 0x1f;
    pub const FALSE: u8 = 0x20;
    pub const CPICKLED: u8 = 0x21;
    pub const OBJECT_EX1: u8 = 0x22;
    pub const OBJECT_EX2: u8 = 0x23;
    pub const EMPTY_TUPLE: u8 = 0x24;
    pub const ONE_TUPLE: u8 = 0x25;
    pub const EMPTY_LIST: u8 = 0x26;
    pub const ONE_LIST: u8 = 0x27;
    pub const EMPTY_WSTRING: u8 = 0x28;
    pub const WSTRING_UCS2_CHAR: u8 = 0x29;
    pub const PACKED_ROW: u8 = 0x2a;
    pub const SUB_STREAM: u8 = 0x2b;
    pub const TWO_TUPLE: u8 = 0x2c;
    pub const PACKED_TERMINATOR: u8 = 0x2d;
    pub const WSTRING_UTF8: u8 = 0x2e;
    pub const VAR_INTEGER: u8 = 0x2f;
}

/// Column type codes of a packed row's `DBRowDescriptor`.
pub mod dbtype {
    pub const EMPTY: i64 = 0x00;
    pub const I2: i64 = 0x02;
    pub const I4: i64 = 0x03;
    pub const R4: i64 = 0x04;
    pub const R8: i64 = 0x05;
    pub const CY: i64 = 0x06;
    pub const ERROR: i64 = 0x0a;
    pub const BOOL: i64 = 0x0b;
    pub const I1: i64 = 0x10;
    pub const UI1: i64 = 0x11;
    pub const UI2: i64 = 0x12;
    pub const UI4: i64 = 0x13;
    pub const I8: i64 = 0x14;
    pub const UI8: i64 = 0x15;
    pub const FILETIME: i64 = 0x40;
    pub const BYTES: i64 = 0x80;
    pub const STR: i64 = 0x81;
    pub const WSTR: i64 = 0x82;

    /// Width in bits of a column's fixed-width slot: 64/32/16/8, 1 for BOOL,
    /// 0 for anything stored as a marshal value after the row data.
    pub fn size_bits(db_type: i64) -> u32 {
        match db_type {
            CY | I8 | UI8 | FILETIME | R8 => 64,
            I4 | UI4 | R4 => 32,
            I2 | UI2 => 16,
            I1 | UI1 => 8,
            BOOL => 1,
            _ => 0,
        }
    }
}
