use std::fmt;

/// Errors the codec can produce.
///
/// The variants line up with the failure classes of the JS reference so the
/// golden tests can assert not just "it failed" but "it failed the same way":
/// see [`MarshalError::kind`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum MarshalError {
    /// The stream ended where a value or a length byte was expected.
    UnexpectedEnd,
    /// The first byte is not the 0x7E marshal magic.
    InvalidHeader(u8),
    /// An opcode the codec does not know.
    UnknownOpcode { opcode: u8, position: usize },
    /// The save-index table at the end of the stream is malformed or missing.
    SaveTable(&'static str),
    /// `decode_exact` found bytes left over after the root value.
    TrailingData(&'static str),
    /// A fixed-width read ran past the end of a buffer shorter than the read.
    BufferOutOfBounds,
    /// A fixed-width read ran past the end of a longer buffer, or an integer
    /// does not fit its wire width.
    OutOfRange(&'static str),
    /// A variable-width integer was truncated.
    TruncatedInteger,
    /// A packed row whose header, columns, or values do not agree.
    InvalidPackedRow(&'static str),
    /// A value the encoder cannot represent.
    Unencodable(&'static str),
    /// Nesting deeper than [`crate::MAX_DEPTH`] (a hardening limit the JS
    /// reference does not have).
    TooDeep,
}

impl MarshalError {
    /// The class of failure as the JS reference reports it: `Error`,
    /// `TypeError`, `RangeError:ERR_OUT_OF_RANGE` or
    /// `RangeError:ERR_BUFFER_OUT_OF_BOUNDS`.
    pub fn kind(&self) -> &'static str {
        match self {
            Self::BufferOutOfBounds => "RangeError:ERR_BUFFER_OUT_OF_BOUNDS",
            Self::OutOfRange(_) => "RangeError:ERR_OUT_OF_RANGE",
            Self::TruncatedInteger => "TypeError",
            _ => "Error",
        }
    }
}

impl fmt::Display for MarshalError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::UnexpectedEnd => write!(f, "unexpected end of marshal data"),
            Self::InvalidHeader(byte) => {
                write!(f, "invalid marshal header 0x{byte:02x} (expected 0x7e)")
            }
            Self::UnknownOpcode { opcode, position } => {
                write!(f, "unknown marshal opcode 0x{opcode:02x} at position {position}")
            }
            Self::SaveTable(what) => write!(f, "invalid marshal save table: {what}"),
            Self::TrailingData(what) => write!(f, "marshal stream has {what}"),
            Self::BufferOutOfBounds => write!(f, "attempt to access memory outside buffer bounds"),
            Self::OutOfRange(what) => write!(f, "value out of range: {what}"),
            Self::TruncatedInteger => write!(f, "truncated variable-width integer"),
            Self::InvalidPackedRow(what) => write!(f, "invalid packed row: {what}"),
            Self::Unencodable(what) => write!(f, "cannot marshal value: {what}"),
            Self::TooDeep => write!(f, "marshal value nested too deeply"),
        }
    }
}

impl std::error::Error for MarshalError {}

pub type Result<T> = std::result::Result<T, MarshalError>;
