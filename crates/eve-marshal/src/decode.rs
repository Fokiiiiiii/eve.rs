use std::borrow::Cow;
use std::sync::Arc;

use bytes::Bytes;

use crate::MAX_DEPTH;
use crate::error::{MarshalError, Result};
use crate::packed_row::{self, Descriptor};
use crate::string_table::STRING_TABLE;
use crate::value::{ObjectExKind, SubStream, Value};
use crate::wire::{MARSHAL_HEADER, OPCODE_MASK, SAVE_MASK, op};

/// Decode a marshal stream, tolerating bytes after the root value.
///
/// This is the same leniency as the JS reference's `marshalDecode`: a string
/// or buffer whose declared length runs past the end of the input yields the
/// bytes that are there instead of an error, and PyObjectEx lists that never
/// reach their terminator end at the end of the input.
///
/// This copies `bytes` once so the decoded strings and buffers can be views
/// into it; use [`decode_bytes`] when the input is already a [`Bytes`] (a
/// socket read buffer, say) to avoid even that copy.
pub fn decode(bytes: &[u8]) -> Result<Value> {
    decode_bytes(&Bytes::copy_from_slice(bytes))
}

/// [`decode`] without copying: strings and buffers in the result are
/// reference-counted slices of `input`.
pub fn decode_bytes(input: &Bytes) -> Result<Value> {
    decode_stream(input, false, 0)
}

/// Decode a marshal stream and require that it is consumed exactly.
pub fn decode_exact(bytes: &[u8]) -> Result<Value> {
    decode_exact_bytes(&Bytes::copy_from_slice(bytes))
}

/// [`decode_exact`] without copying.
pub fn decode_exact_bytes(input: &Bytes) -> Result<Value> {
    decode_stream(input, true, 0)
}

/// Split a `[u32 length][payload]` frame off the front of `input`.
///
/// Returns `None` when `input` does not yet hold a whole frame.
pub fn split_packet(input: &[u8]) -> Option<(&[u8], &[u8])> {
    let length = u32::from_le_bytes(input.get(..4)?.try_into().ok()?) as usize;
    let end = 4usize.checked_add(length)?;
    let payload = input.get(4..end)?;
    Some((payload, &input[end..]))
}

/// `depth` is the nesting already used by enclosing streams: a nested
/// PySubStream continues the count, so a chain of substreams cannot get
/// around [`MAX_DEPTH`] and exhaust the stack.
fn decode_stream(input: &Bytes, require_exact_end: bool, depth: usize) -> Result<Value> {
    let bytes: &[u8] = input;
    let mut decoder = Decoder {
        input,
        buf: bytes,
        pos: 0,
        stored: Vec::new(),
        save_pos: None,
        save_end: None,
        depth,
        saved_refs: 0,
        descriptor: None,
        scratch: Vec::new(),
    };

    let header = decoder.u8()?;
    if header != MARSHAL_HEADER {
        return Err(MarshalError::InvalidHeader(header));
    }

    let save_count = decoder.u32()? as usize;
    let mut value_end = bytes.len();
    if save_count > 0 {
        let table_bytes = save_count
            .checked_mul(4)
            .filter(|needed| bytes.len().saturating_sub(decoder.pos) >= *needed)
            .ok_or(MarshalError::SaveTable("save table larger than the stream"))?;
        decoder.stored = vec![Value::None; save_count];
        decoder.save_pos = Some(bytes.len() - table_bytes);
        decoder.save_end = Some(bytes.len());
        value_end = bytes.len() - table_bytes;
    }

    let value = decoder.value()?;
    if require_exact_end {
        if decoder.pos != value_end {
            return Err(MarshalError::TrailingData("unconsumed payload bytes"));
        }
        if decoder.save_pos != decoder.save_end {
            return Err(MarshalError::TrailingData("unconsumed save-table entries"));
        }
    }
    Ok(value)
}

/// The row descriptor of the most recent packed row, and where its bytes were.
struct CachedDescriptor {
    span: (usize, usize),
    descriptor: Arc<Descriptor>,
}

pub(crate) struct Decoder<'a> {
    input: &'a Bytes,
    buf: &'a [u8],
    pos: usize,
    /// Values remembered by a save flag, addressed 1-based by PySavedStreamElement.
    stored: Vec<Value>,
    save_pos: Option<usize>,
    save_end: Option<usize>,
    depth: usize,
    /// How many PySavedStreamElement references have been resolved.
    saved_refs: usize,
    descriptor: Option<CachedDescriptor>,
    /// Working space for a packed row's fixed-width block, reused across rows.
    pub(crate) scratch: Vec<u8>,
}

impl<'a> Decoder<'a> {
    fn u8(&mut self) -> Result<u8> {
        let byte = *self.buf.get(self.pos).ok_or(MarshalError::UnexpectedEnd)?;
        self.pos += 1;
        Ok(byte)
    }

    /// A fixed-width read that fails the way Node's `Buffer.readXxx` does:
    /// `ERR_BUFFER_OUT_OF_BOUNDS` when the whole buffer is shorter than the
    /// value, `ERR_OUT_OF_RANGE` when only the offset is bad.
    fn take<const N: usize>(&mut self) -> Result<[u8; N]> {
        let end = self.pos.checked_add(N);
        match end.and_then(|end| self.buf.get(self.pos..end)) {
            Some(slice) => {
                self.pos += N;
                Ok(slice.try_into().expect("slice has N bytes"))
            }
            None if self.buf.len() < N => Err(MarshalError::BufferOutOfBounds),
            None => Err(MarshalError::OutOfRange("read past end of stream")),
        }
    }

    fn u32(&mut self) -> Result<u32> {
        self.take::<4>().map(u32::from_le_bytes)
    }

    fn size(&mut self) -> Result<usize> {
        match self.u8()? {
            0xff => Ok(self.u32()? as usize),
            size => Ok(usize::from(size)),
        }
    }

    /// The JS reference's `readBytes`: clamps to the input but always advances
    /// by the declared length. Returns the clamped range.
    fn range(&mut self, length: usize) -> (usize, usize) {
        let start = self.pos.min(self.buf.len());
        let end = self.pos.saturating_add(length).min(self.buf.len());
        self.pos = self.pos.saturating_add(length);
        (start, end)
    }

    fn bytes(&mut self, length: usize) -> &'a [u8] {
        let (start, end) = self.range(length);
        &self.buf[start..end]
    }

    /// [`Decoder::range`] as a zero-copy view of the input.
    fn chunk(&mut self, length: usize) -> Bytes {
        let (start, end) = self.range(length);
        if start == end { Bytes::new() } else { self.input.slice(start..end) }
    }

    fn save_index(&mut self) -> Result<usize> {
        let (Some(position), Some(end)) = (self.save_pos, self.save_end) else {
            return Err(MarshalError::SaveTable("no save index table"));
        };
        if position + 4 > end {
            return Err(MarshalError::SaveTable("save index table exhausted"));
        }
        let index = u32::from_le_bytes(self.buf[position..position + 4].try_into().expect("4 bytes"));
        self.save_pos = Some(position + 4);
        Ok(index as usize)
    }

    /// A count read from the wire, bounded before it is used to size an
    /// allocation: every element takes at least one byte.
    fn capacity(&self, count: usize) -> usize {
        count.min(self.buf.len().saturating_sub(self.pos))
    }

    pub(crate) fn value(&mut self) -> Result<Value> {
        if self.depth >= MAX_DEPTH {
            return Err(MarshalError::TooDeep);
        }
        self.depth += 1;
        let result = self.value_inner();
        self.depth -= 1;
        result
    }

    fn value_inner(&mut self) -> Result<Value> {
        if self.pos >= self.buf.len() {
            return Err(MarshalError::UnexpectedEnd);
        }

        let raw = self.u8()?;
        let save = raw & SAVE_MASK != 0;
        let opcode = raw & OPCODE_MASK;
        let storage_index = if save { self.save_index()? } else { 0 };

        // The recursion runs through this function, so it is kept a thin
        // dispatcher: every arm with real locals lives in its own function
        // and a deeply nested value costs one small frame per level, not one
        // frame sized for the largest arm.
        let value = match opcode {
            op::ONE_TUPLE | op::TWO_TUPLE | op::TUPLE | op::ONE_LIST | op::LIST => self.sequence(opcode)?,
            op::DICT
            | op::OBJECT
            | op::OBJECT_EX1
            | op::OBJECT_EX2
            | op::SUB_STRUCT
            | op::CHECKSUMMED_STREAM
            | op::SUB_STREAM
            | op::PACKED_ROW => self.compound(opcode)?,
            _ => self.scalar(opcode, raw)?,
        };

        if save && storage_index > 0 && storage_index <= self.stored.len() {
            self.stored[storage_index - 1] = value.clone();
        }
        Ok(value)
    }

    /// Tuples and lists.
    #[inline(never)]
    fn sequence(&mut self, opcode: u8) -> Result<Value> {
        Ok(match opcode {
            op::ONE_TUPLE => Value::Tuple(vec![self.value()?]),
            op::TWO_TUPLE => {
                let first = self.value()?;
                Value::Tuple(vec![first, self.value()?])
            }
            op::TUPLE => {
                let count = self.size()?;
                Value::Tuple(self.items(count)?)
            }
            op::ONE_LIST => Value::List(vec![self.value()?]),
            _ => {
                let count = self.size()?;
                Value::List(self.items(count)?)
            }
        })
    }

    /// The other opcodes that contain values.
    #[inline(never)]
    fn compound(&mut self, opcode: u8) -> Result<Value> {
        match opcode {
            op::DICT => self.dict(),
            op::OBJECT => {
                let name = self.value()?;
                let args = self.value()?;
                Ok(Value::Object { name: Box::new(name), args: Box::new(args) })
            }
            op::OBJECT_EX1 | op::OBJECT_EX2 => self.object_ex(opcode),
            op::SUB_STRUCT => Ok(Value::SubStruct(Box::new(self.value()?))),
            op::CHECKSUMMED_STREAM => {
                let checksum = self.u32()?;
                Ok(Value::Checksummed { checksum, value: Box::new(self.value()?) })
            }
            op::SUB_STREAM => self.sub_stream(),
            _ => self.packed_row(),
        }
    }

    #[inline(never)]
    fn dict(&mut self) -> Result<Value> {
        let count = self.size()?;
        let mut entries = Vec::with_capacity(self.capacity(count));
        for _ in 0..count {
            // The wire stores each pair value first, then key.
            let value = self.value()?;
            let key = self.value()?;
            entries.push((key, value));
        }
        Ok(Value::Dict(entries))
    }

    #[inline(never)]
    fn sub_stream(&mut self) -> Result<Value> {
        let length = self.size()?;
        let data = self.chunk(length);
        // A nested stream that does not decode is kept as raw bytes.
        Ok(match decode_stream(&data, false, self.depth) {
            Ok(inner) => Value::SubStream(SubStream::Decoded(Box::new(inner))),
            Err(_) => Value::SubStream(SubStream::Raw(data)),
        })
    }

    #[inline(never)]
    fn packed_row(&mut self) -> Result<Value> {
        let descriptor = self.descriptor()?;
        let length = self.size()?;
        let rle = self.chunk(length);
        packed_row::decode(self, descriptor, rle)
    }

    /// Read a packed row's `DBRowDescriptor`. Every row of a rowset repeats
    /// the same one, so when the bytes at the cursor equal the previous
    /// descriptor's they are skipped and the parsed descriptor is shared.
    ///
    /// Reuse is only sound when decoding those bytes depends on nothing but
    /// the bytes, so a descriptor that consumed a save index or resolved a
    /// saved-element reference is never cached.
    fn descriptor(&mut self) -> Result<Arc<Descriptor>> {
        if let Some(cached) = &self.descriptor {
            let (start, end) = cached.span;
            let length = end - start;
            let upcoming = self.pos.checked_add(length).and_then(|stop| self.buf.get(self.pos..stop));
            if upcoming == Some(&self.buf[start..end]) {
                self.pos += length;
                return Ok(Arc::clone(&cached.descriptor));
            }
        }

        let start = self.pos;
        let (save_before, refs_before) = (self.save_pos, self.saved_refs);
        let header = self.value()?;
        let descriptor = Descriptor::from_header(header)?;
        if self.save_pos == save_before && self.saved_refs == refs_before {
            self.descriptor =
                Some(CachedDescriptor { span: (start, self.pos), descriptor: Arc::clone(&descriptor) });
        }
        Ok(descriptor)
    }

    /// Opcodes that hold no nested value.
    #[inline(never)]
    fn scalar(&mut self, opcode: u8, raw: u8) -> Result<Value> {
        Ok(match opcode {
            op::NONE => Value::None,
            op::TRUE => Value::Bool(true),
            op::FALSE => Value::Bool(false),
            op::ZERO_INTEGER => Value::Int(0),
            op::ONE_INTEGER => Value::Int(1),
            op::MINUS_ONE => Value::Int(-1),
            op::BYTE => Value::Int(i64::from(self.u8()? as i8)),
            op::SIGNED_SHORT => Value::Int(i64::from(i16::from_le_bytes(self.take()?))),
            op::LONG => Value::Int(i64::from(i32::from_le_bytes(self.take()?))),
            op::LONG_LONG => Value::Long(i64::from_le_bytes(self.take()?)),
            op::VAR_INTEGER => self.var_integer()?,
            op::ZERO_REAL => Value::Real(0.0),
            op::REAL => Value::Real(f64::from_le_bytes(self.take()?)),
            op::EMPTY_STRING => Value::Bytes(Bytes::new()),
            op::CHAR_STRING => {
                let byte = usize::from(self.u8()?);
                Value::Bytes(Bytes::from_static(&SINGLE_BYTES[byte..=byte]))
            }
            op::SHORT_STRING => {
                let length = usize::from(self.u8()?);
                Value::Bytes(self.chunk(length))
            }
            op::LONG_STRING => {
                let length = self.size()?;
                Value::Bytes(self.chunk(length))
            }
            op::STRING_TABLE_ITEM => {
                let index = usize::from(self.u8()?);
                match index.checked_sub(1).and_then(|slot| STRING_TABLE.get(slot)) {
                    Some(text) => Value::Str(Cow::Borrowed(text)),
                    None => Value::Str(Cow::Owned(format!("<stringtable:{index}>"))),
                }
            }
            op::EMPTY_WSTRING => Value::WString(Cow::Borrowed("")),
            op::WSTRING_UCS2 => {
                let units = self.size()?;
                Value::WString(Cow::Owned(utf16_lossy(self.bytes(units.saturating_mul(2)))))
            }
            op::WSTRING_UCS2_CHAR => Value::WString(Cow::Owned(utf16_lossy(self.bytes(2)))),
            op::WSTRING_UTF8 => {
                let length = self.size()?;
                Value::WString(Cow::Owned(String::from_utf8_lossy(self.bytes(length)).into_owned()))
            }
            op::TOKEN => {
                let length = self.size()?;
                Value::Token(Cow::Owned(String::from_utf8_lossy(self.bytes(length)).into_owned()))
            }
            op::EMPTY_TUPLE => Value::Tuple(Vec::new()),
            op::EMPTY_LIST => Value::List(Vec::new()),
            op::BUFFER => {
                let length = self.size()?;
                Value::Buffer(self.chunk(length))
            }
            op::CPICKLED => {
                let length = self.size()?;
                Value::CPickled(self.chunk(length))
            }
            op::SAVED_STREAM_ELEMENT => {
                self.saved_refs += 1;
                let index = self.size()?;
                match index.checked_sub(1).and_then(|slot| self.stored.get(slot)) {
                    Some(stored) => stored.clone(),
                    None => Value::None,
                }
            }
            _ => {
                return Err(MarshalError::UnknownOpcode { opcode: raw, position: self.pos - 1 });
            }
        })
    }

    #[inline(never)]
    fn items(&mut self, count: usize) -> Result<Vec<Value>> {
        let mut items = Vec::with_capacity(self.capacity(count));
        for _ in 0..count {
            items.push(self.value()?);
        }
        Ok(items)
    }

    fn var_integer(&mut self) -> Result<Value> {
        let size = self.size()?;
        if size == 0 {
            return Ok(Value::Int(0));
        }
        let bytes = self.bytes(size);
        // JS reads each declared byte; a truncated integer reads `undefined`.
        if bytes.len() < size {
            return Err(MarshalError::TruncatedInteger);
        }
        let mut significant = bytes.len();
        while significant > 0 && bytes[significant - 1] == 0 {
            significant -= 1;
        }
        let bytes = &bytes[..significant];
        if bytes.len() <= 8 {
            let mut buffer = [0u8; 8];
            buffer[..bytes.len()].copy_from_slice(bytes);
            let magnitude = u64::from_le_bytes(buffer);
            if magnitude <= 9_007_199_254_740_991 {
                return Ok(Value::Int(magnitude as i64));
            }
        }
        Ok(Value::BigUnsigned(bytes.to_vec()))
    }

    #[inline(never)]
    fn object_ex(&mut self, opcode: u8) -> Result<Value> {
        let header = self.value()?;
        let terminated = |decoder: &Decoder<'_>| {
            decoder.buf.get(decoder.pos).is_some_and(|byte| byte & OPCODE_MASK == op::PACKED_TERMINATOR)
        };

        let mut list = Vec::new();
        while self.pos < self.buf.len() {
            if terminated(self) {
                self.pos += 1;
                break;
            }
            list.push(self.value()?);
        }

        let mut dict = Vec::new();
        while self.pos < self.buf.len() {
            if terminated(self) {
                self.pos += 1;
                break;
            }
            let key = self.value()?;
            let value = self.value()?;
            dict.push((key, value));
        }

        Ok(Value::ObjectEx {
            kind: if opcode == op::OBJECT_EX1 { ObjectExKind::Ex1 } else { ObjectExKind::Ex2 },
            header: Box::new(header),
            list,
            dict,
        })
    }
}

/// One-byte strings without an allocation.
static SINGLE_BYTES: [u8; 256] = {
    let mut table = [0u8; 256];
    let mut index = 0;
    while index < 256 {
        table[index] = index as u8;
        index += 1;
    }
    table
};

/// Node's `toString("utf16le")`: whole code units only, invalid surrogates
/// replaced.
fn utf16_lossy(bytes: &[u8]) -> String {
    let units: Vec<u16> = bytes.as_chunks::<2>().0.iter().map(|pair| u16::from_le_bytes(*pair)).collect();
    String::from_utf16_lossy(&units)
}
