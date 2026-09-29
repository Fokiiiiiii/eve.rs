use std::collections::HashMap;
use std::hash::{BuildHasherDefault, Hasher};
use std::sync::OnceLock;

use crate::MAX_DEPTH;
use crate::error::{MarshalError, Result};
use crate::packed_row;
use crate::string_table::STRING_TABLE;
use crate::value::{ObjectExKind, SubStream, Value};
use crate::wire::{MARSHAL_HEADER, op};

/// Encode `value` as a marshal stream: the 0x7E magic, a zero save count and
/// the value. This is the payload of a packet, without the length prefix.
pub fn encode(value: &Value) -> Result<Vec<u8>> {
    let mut out = Vec::with_capacity(256);
    encode_into(value, &mut out)?;
    Ok(out)
}

/// Like [`encode`], appending to `out` (useful to reuse a buffer).
pub fn encode_into(value: &Value, out: &mut Vec<u8>) -> Result<()> {
    out.push(MARSHAL_HEADER);
    out.extend_from_slice(&[0, 0, 0, 0]);
    Encoder { out, depth: 0 }.value(value)
}

/// Encode `value` and prefix the 4-byte little-endian payload length.
pub fn encode_packet(value: &Value) -> Result<Vec<u8>> {
    let mut out = vec![0; 4];
    encode_into(value, &mut out)?;
    let length =
        u32::try_from(out.len() - 4).map_err(|_| MarshalError::OutOfRange("packet larger than 4 GiB"))?;
    out[..4].copy_from_slice(&length.to_le_bytes());
    Ok(out)
}

/// FNV-1a: the table is tiny and its keys short, so SipHash's flood
/// resistance buys nothing here and costs more than the lookup itself.
struct Fnv(u64);

impl Default for Fnv {
    fn default() -> Self {
        Self(0xcbf2_9ce4_8422_2325)
    }
}

impl Hasher for Fnv {
    fn write(&mut self, bytes: &[u8]) {
        for &byte in bytes {
            self.0 ^= u64::from(byte);
            self.0 = self.0.wrapping_mul(0x0000_0100_0000_01b3);
        }
    }

    fn finish(&self) -> u64 {
        self.0
    }
}

type TableIndex = HashMap<&'static str, u8, BuildHasherDefault<Fnv>>;

fn string_table_index(text: &str) -> Option<u8> {
    static INDEX: OnceLock<TableIndex> = OnceLock::new();
    INDEX
        .get_or_init(|| {
            let mut map = TableIndex::with_capacity_and_hasher(STRING_TABLE.len(), Default::default());
            for (position, entry) in STRING_TABLE.iter().enumerate() {
                // First occurrence wins, as in the JS reference.
                map.entry(*entry).or_insert((position + 1) as u8);
            }
            map
        })
        .get(text)
        .copied()
}

pub(crate) struct Encoder<'a> {
    pub(crate) out: &'a mut Vec<u8>,
    depth: usize,
}

impl<'a> Encoder<'a> {
    /// The `PutSizeEx` length: one byte below 255, else 0xFF and a u32.
    pub(crate) fn size(&mut self, size: usize) -> Result<()> {
        if size < 0xff {
            self.out.push(size as u8);
        } else {
            let size =
                u32::try_from(size).map_err(|_| MarshalError::OutOfRange("length larger than 4 GiB"))?;
            self.out.push(0xff);
            self.out.extend_from_slice(&size.to_le_bytes());
        }
        Ok(())
    }

    fn integer(&mut self, value: i64) {
        match value {
            -1 => self.out.push(op::MINUS_ONE),
            0 => self.out.push(op::ZERO_INTEGER),
            1 => self.out.push(op::ONE_INTEGER),
            -128..=127 => self.out.extend_from_slice(&[op::BYTE, value as i8 as u8]),
            -32768..=32767 => {
                self.out.push(op::SIGNED_SHORT);
                self.out.extend_from_slice(&(value as i16).to_le_bytes());
            }
            -2_147_483_648..=2_147_483_647 => {
                self.out.push(op::LONG);
                self.out.extend_from_slice(&(value as i32).to_le_bytes());
            }
            _ => {
                self.out.push(op::LONG_LONG);
                self.out.extend_from_slice(&value.to_le_bytes());
            }
        }
    }

    fn string(&mut self, text: &str) -> Result<()> {
        match text.len() {
            0 => self.out.push(op::EMPTY_STRING),
            1 => self.out.extend_from_slice(&[op::CHAR_STRING, text.as_bytes()[0]]),
            length => {
                if let Some(index) = string_table_index(text) {
                    self.out.extend_from_slice(&[op::STRING_TABLE_ITEM, index]);
                } else {
                    self.out.push(op::LONG_STRING);
                    self.size(length)?;
                    self.out.extend_from_slice(text.as_bytes());
                }
            }
        }
        Ok(())
    }

    fn byte_string(&mut self, bytes: &[u8]) -> Result<()> {
        match bytes.len() {
            0 => self.out.push(op::EMPTY_STRING),
            1 => self.out.extend_from_slice(&[op::CHAR_STRING, bytes[0]]),
            length @ 2..=255 => {
                self.out.extend_from_slice(&[op::SHORT_STRING, length as u8]);
                self.out.extend_from_slice(bytes);
            }
            length => {
                self.out.push(op::LONG_STRING);
                self.size(length)?;
                self.out.extend_from_slice(bytes);
            }
        }
        Ok(())
    }

    fn sized(&mut self, opcode: u8, bytes: &[u8]) -> Result<()> {
        self.out.push(opcode);
        self.size(bytes.len())?;
        self.out.extend_from_slice(bytes);
        Ok(())
    }

    fn pairs(&mut self, pairs: &[(Value, Value)], value_first: bool) -> Result<()> {
        for (key, value) in pairs {
            if value_first {
                self.value(value)?;
                self.value(key)?;
            } else {
                self.value(key)?;
                self.value(value)?;
            }
        }
        Ok(())
    }

    pub(crate) fn value(&mut self, value: &Value) -> Result<()> {
        if self.depth >= MAX_DEPTH {
            return Err(MarshalError::TooDeep);
        }
        self.depth += 1;
        let result = self.value_inner(value);
        self.depth -= 1;
        result
    }

    fn value_inner(&mut self, value: &Value) -> Result<()> {
        match value {
            Value::None => self.out.push(op::NONE),
            Value::Bool(flag) => self.out.push(if *flag { op::TRUE } else { op::FALSE }),
            Value::Int(number) => self.integer(*number),
            Value::Long(number) => {
                self.out.push(op::LONG_LONG);
                self.out.extend_from_slice(&number.to_le_bytes());
            }
            Value::Real(number) => {
                if *number == 0.0 {
                    self.out.push(op::ZERO_REAL);
                } else {
                    self.out.push(op::REAL);
                    self.out.extend_from_slice(&number.to_le_bytes());
                }
            }
            Value::BigUnsigned(bytes) => self.sized(op::VAR_INTEGER, bytes)?,
            Value::Str(text) => self.string(text)?,
            Value::Bytes(bytes) => self.byte_string(bytes)?,
            Value::Buffer(bytes) => self.sized(op::BUFFER, bytes)?,
            Value::WString(text) => {
                if text.is_empty() {
                    self.out.push(op::EMPTY_WSTRING);
                } else {
                    self.sized(op::WSTRING_UTF8, text.as_bytes())?;
                }
            }
            Value::Token(text) => self.sized(op::TOKEN, text.as_bytes())?,
            Value::Tuple(items) => {
                match items.len() {
                    0 => self.out.push(op::EMPTY_TUPLE),
                    1 => self.out.push(op::ONE_TUPLE),
                    2 => self.out.push(op::TWO_TUPLE),
                    length => {
                        self.out.push(op::TUPLE);
                        self.size(length)?;
                    }
                }
                for item in items {
                    self.value(item)?;
                }
            }
            Value::List(items) => match items.len() {
                0 => self.out.push(op::EMPTY_LIST),
                1 => {
                    self.out.push(op::ONE_LIST);
                    self.value(&items[0])?;
                }
                length => {
                    self.out.push(op::LIST);
                    self.size(length)?;
                    for item in items {
                        self.value(item)?;
                    }
                }
            },
            Value::Dict(entries) => {
                self.out.push(op::DICT);
                self.size(entries.len())?;
                // The wire stores each pair value first, then key.
                self.pairs(entries, true)?;
            }
            Value::Object { name, args } => {
                self.out.push(op::OBJECT);
                self.value(name)?;
                self.value(args)?;
            }
            Value::ObjectEx { kind, header, list, dict } => {
                self.out.push(match kind {
                    ObjectExKind::Ex1 => op::OBJECT_EX1,
                    ObjectExKind::Ex2 => op::OBJECT_EX2,
                });
                self.value(header)?;
                for item in list {
                    self.value(item)?;
                }
                self.out.push(op::PACKED_TERMINATOR);
                self.pairs(dict, false)?;
                self.out.push(op::PACKED_TERMINATOR);
            }
            Value::SubStruct(inner) => {
                self.out.push(op::SUB_STRUCT);
                self.value(inner)?;
            }
            Value::SubStream(SubStream::Decoded(inner)) => {
                self.out.push(op::SUB_STREAM);
                self.length_prefixed(|encoder| {
                    encoder.out.push(MARSHAL_HEADER);
                    encoder.out.extend_from_slice(&[0, 0, 0, 0]);
                    encoder.value(inner)
                })?;
            }
            Value::SubStream(SubStream::Raw(bytes)) => self.sized(op::SUB_STREAM, bytes)?,
            Value::Checksummed { checksum, value } => {
                self.out.push(op::CHECKSUMMED_STREAM);
                self.out.extend_from_slice(&checksum.to_le_bytes());
                self.value(value)?;
            }
            Value::CPickled(bytes) => self.sized(op::CPICKLED, bytes)?,
            Value::PackedRow(row) => packed_row::encode(self, row)?,
        }
        Ok(())
    }

    /// Write a size field followed by whatever `body` appends, without
    /// building the body in a scratch buffer: the body is encoded in place
    /// behind a one-byte size slot, and widened afterwards if it turns out
    /// to need the five-byte form.
    fn length_prefixed(&mut self, body: impl FnOnce(&mut Self) -> Result<()>) -> Result<()> {
        let slot = self.out.len();
        self.out.push(0);
        let start = self.out.len();
        body(self)?;
        let length = self.out.len() - start;
        if length < 0xff {
            self.out[slot] = length as u8;
        } else {
            let length =
                u32::try_from(length).map_err(|_| MarshalError::OutOfRange("length larger than 4 GiB"))?;
            self.out[slot] = 0xff;
            self.out.splice(start..start, length.to_le_bytes());
        }
        Ok(())
    }
}
