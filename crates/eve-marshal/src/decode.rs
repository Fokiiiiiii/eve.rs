use std::borrow::Cow;
use std::cell::RefCell;
use std::mem::size_of;
use std::rc::Rc;
use std::sync::Arc;

use bytes::Bytes;

use crate::error::{MarshalError, Result};
use crate::packed_row::{self, Descriptor};
use crate::string_table::STRING_TABLE;
use crate::value::{ObjectExKind, SaveSlot, SavedGraph, SavedReference, SubStream, Value};
use crate::wire::{MARSHAL_HEADER, OPCODE_MASK, SAVE_MASK, op};
use crate::{MAX_DECODE_ALLOCATION, MAX_DEPTH, MAX_ELEMENTS, MAX_INPUT_BYTES, MAX_STRING_BYTES};

/// Decode a marshal stream, tolerating bytes after the root value.
///
/// This is the same root-value leniency as the JS reference's `marshalDecode`:
/// bytes after the root value are tolerated. Every nested read stays inside
/// the payload (before any save table), and truncated values or missing
/// PyObjectEx terminators fail.
///
/// This copies `bytes` once so the decoded strings and buffers can be views
/// into it; use [`decode_bytes`] when the input is already a [`Bytes`] (a
/// socket read buffer, say) to avoid even that copy.
pub fn decode(bytes: &[u8]) -> Result<Value> {
    if bytes.len() > MAX_INPUT_BYTES {
        return Err(MarshalError::ResourceLimit("input byte"));
    }
    decode_bytes(&Bytes::copy_from_slice(bytes))
}

/// [`decode`] without copying: strings and buffers in the result are
/// reference-counted slices of `input`.
pub fn decode_bytes(input: &Bytes) -> Result<Value> {
    decode_stream(input, false, 0, Rc::new(RefCell::new(DecodeBudget::default())))
}

/// Decode a marshal stream and require that it is consumed exactly.
pub fn decode_exact(bytes: &[u8]) -> Result<Value> {
    if bytes.len() > MAX_INPUT_BYTES {
        return Err(MarshalError::ResourceLimit("input byte"));
    }
    decode_exact_bytes(&Bytes::copy_from_slice(bytes))
}

/// [`decode_exact`] without copying.
pub fn decode_exact_bytes(input: &Bytes) -> Result<Value> {
    decode_stream(input, true, 0, Rc::new(RefCell::new(DecodeBudget::default())))
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
fn clone_extra(value: &Value) -> usize {
    match value {
        Value::BigUnsigned(bytes) | Value::BigSigned(bytes) => bytes.len(),
        Value::Str(text) | Value::WString(text) | Value::Token(text) => {
            if matches!(text, Cow::Owned(_)) {
                text.len()
            } else {
                0
            }
        }
        Value::Tuple(items) | Value::List(items) => items
            .len()
            .saturating_mul(size_of::<Value>())
            .saturating_add(items.iter().map(clone_extra).sum::<usize>()),
        Value::Dict(entries) => entries.len().saturating_mul(size_of::<(Value, Value)>()).saturating_add(
            entries
                .iter()
                .map(|(key, value)| clone_extra(key).saturating_add(clone_extra(value)))
                .sum::<usize>(),
        ),
        Value::Object { name, args } => size_of::<Value>()
            .saturating_mul(2)
            .saturating_add(clone_extra(name))
            .saturating_add(clone_extra(args)),
        Value::ObjectEx { header, list, dict, .. } => size_of::<Value>()
            .saturating_add(list.len().saturating_mul(size_of::<Value>()))
            .saturating_add(dict.len().saturating_mul(size_of::<(Value, Value)>()))
            .saturating_add(clone_extra(header))
            .saturating_add(list.iter().map(clone_extra).sum::<usize>())
            .saturating_add(
                dict.iter()
                    .map(|(key, value)| clone_extra(key).saturating_add(clone_extra(value)))
                    .sum::<usize>(),
            ),
        Value::SubStruct(value) | Value::Checksummed { value, .. } => {
            size_of::<Value>().saturating_add(clone_extra(value))
        }
        Value::SubStream(SubStream::Decoded(value)) => size_of::<Value>().saturating_add(clone_extra(value)),
        Value::PackedRow(row) => size_of::<crate::value::PackedRow>()
            .saturating_add(row.values.len().saturating_mul(size_of::<Value>()))
            .saturating_add(row.values.iter().map(clone_extra).sum::<usize>()),
        Value::None
        | Value::Bool(_)
        | Value::Int(_)
        | Value::Long(_)
        | Value::Real(_)
        | Value::Bytes(_)
        | Value::Buffer(_)
        | Value::SubStream(SubStream::Raw(_))
        | Value::CPickled(_) => 0,
        Value::SavedReference(_) => 0,
        Value::SavedGraph(graph) => clone_extra(graph.root()),
    }
}

fn clone_allocation(value: &Value) -> usize {
    size_of::<Value>().saturating_add(clone_extra(value))
}

#[derive(Default)]
struct DecodeBudget {
    elements: usize,
    allocation: usize,
}

impl DecodeBudget {
    fn value(&mut self) -> Result<()> {
        self.elements = self.elements.checked_add(1).ok_or(MarshalError::ResourceLimit("element count"))?;
        if self.elements > MAX_ELEMENTS {
            return Err(MarshalError::ResourceLimit("element count"));
        }
        self.allocate(size_of::<Value>())
    }

    fn container<T>(&mut self, count: usize) -> Result<()> {
        if count > MAX_ELEMENTS {
            return Err(MarshalError::ResourceLimit("element count"));
        }
        let bytes = count.checked_mul(size_of::<T>()).ok_or(MarshalError::ResourceLimit("allocation"))?;
        self.allocate(bytes)
    }

    fn allocate(&mut self, bytes: usize) -> Result<()> {
        self.allocation =
            self.allocation.checked_add(bytes).ok_or(MarshalError::ResourceLimit("allocation"))?;
        if self.allocation > MAX_DECODE_ALLOCATION {
            return Err(MarshalError::ResourceLimit("allocation"));
        }
        Ok(())
    }
}

fn decode_stream(
    input: &Bytes,
    require_exact_end: bool,
    depth: usize,
    budget: Rc<RefCell<DecodeBudget>>,
) -> Result<Value> {
    if input.len() > MAX_INPUT_BYTES {
        return Err(MarshalError::ResourceLimit("input byte"));
    }
    let bytes: &[u8] = input;
    let mut decoder = Decoder {
        input,
        buf: bytes,
        pos: 0,
        value_end: bytes.len(),
        stored: Vec::new(),
        save_pos: None,
        save_end: None,
        depth,
        saved_refs: 0,
        descriptor: None,
        scratch: Vec::new(),
        budget: Rc::clone(&budget),
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
        if save_count > MAX_ELEMENTS {
            return Err(MarshalError::ResourceLimit("save table"));
        }
        decoder.budget.borrow_mut().container::<Value>(save_count)?;
        decoder.stored = vec![None; save_count];
        decoder.save_pos = Some(bytes.len() - table_bytes);
        decoder.save_end = Some(bytes.len());
        value_end = bytes.len() - table_bytes;
        for index_bytes in bytes[value_end..].as_chunks::<4>().0 {
            let index = i32::from_le_bytes(*index_bytes);
            if index < 1 || index as usize > save_count {
                return Err(MarshalError::SaveTable("bogus map data in marshal stream"));
            }
        }
    }
    decoder.value_end = value_end;

    let value = decoder.value()?;
    if require_exact_end {
        if decoder.pos != value_end {
            return Err(MarshalError::TrailingData("unconsumed payload bytes"));
        }
        if decoder.save_pos != decoder.save_end {
            return Err(MarshalError::TrailingData("unconsumed save-table entries"));
        }
    }
    if decoder.saved_refs > 0 {
        Ok(Value::SavedGraph(SavedGraph::new(value, decoder.stored)))
    } else {
        Ok(value)
    }
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
    stored: Vec<Option<SaveSlot>>,
    value_end: usize,
    save_pos: Option<usize>,
    save_end: Option<usize>,
    depth: usize,
    /// How many PySavedStreamElement references have been resolved.
    saved_refs: usize,
    descriptor: Option<CachedDescriptor>,
    /// Working space for a packed row's fixed-width block, reused across rows.
    pub(crate) scratch: Vec<u8>,
    budget: Rc<RefCell<DecodeBudget>>,
}

impl<'a> Decoder<'a> {
    fn u8(&mut self) -> Result<u8> {
        let byte = *self
            .buf
            .get(self.pos..self.value_end)
            .and_then(|remaining| remaining.first())
            .ok_or(MarshalError::UnexpectedEnd)?;
        self.pos += 1;
        Ok(byte)
    }

    /// A fixed-width read wholly inside the payload. EveJS checks valueEnd
    /// before calling Node's Buffer methods, so truncation is an ordinary Error.
    fn take<const N: usize>(&mut self) -> Result<[u8; N]> {
        let end = self.pos.checked_add(N);
        match end.filter(|end| *end <= self.value_end).and_then(|end| self.buf.get(self.pos..end)) {
            Some(slice) => {
                self.pos += N;
                Ok(slice.try_into().expect("slice has N bytes"))
            }
            // Every public EveJS read helper checks valueEnd before calling
            // Node's Buffer methods, so truncation is an ordinary Error.
            None => Err(MarshalError::UnexpectedEnd),
        }
    }

    fn u32(&mut self) -> Result<u32> {
        self.take::<4>().map(u32::from_le_bytes)
    }

    fn size(&mut self) -> Result<usize> {
        let size = match self.u8()? {
            0xff => self.u32()? as usize,
            size => usize::from(size),
        };
        Ok(size)
    }

    fn byte_length(&mut self) -> Result<usize> {
        let size = self.size()?;
        if size > MAX_STRING_BYTES {
            return Err(MarshalError::ResourceLimit("declared byte/string"));
        }
        Ok(size)
    }

    fn bytes(&mut self, length: usize) -> Result<&'a [u8]> {
        let end = self
            .pos
            .checked_add(length)
            .filter(|end| *end <= self.value_end)
            .ok_or(MarshalError::UnexpectedEnd)?;
        let start = self.pos;
        self.pos = end;
        Ok(&self.buf[start..end])
    }

    /// A zero-copy view of a byte range wholly inside the payload.
    fn chunk(&mut self, length: usize) -> Result<Bytes> {
        let end = self
            .pos
            .checked_add(length)
            .filter(|end| *end <= self.value_end)
            .ok_or(MarshalError::UnexpectedEnd)?;
        let start = self.pos;
        self.pos = end;
        Ok(if start == end { Bytes::new() } else { self.input.slice(start..end) })
    }

    fn save_index(&mut self) -> Result<usize> {
        let (Some(position), Some(end)) = (self.save_pos, self.save_end) else {
            return Err(MarshalError::SaveTable("no save index table"));
        };
        if position + 4 > end {
            return Err(MarshalError::SaveTable("save index table exhausted"));
        }
        let index =
            u32::from_le_bytes(self.buf[position..position + 4].try_into().expect("4 bytes")) as usize;
        self.save_pos = Some(position + 4);
        if index == 0 || index > self.stored.len() {
            return Err(MarshalError::SaveTable("save index outside the declared table"));
        }
        Ok(index)
    }

    fn reserve_saved(&mut self, index: usize) -> Result<SavedReference> {
        if index == 0 || index > self.stored.len() {
            return Err(MarshalError::SaveTable("save index outside the declared table"));
        }
        self.budget.borrow_mut().allocate(size_of::<SavedReference>())?;
        let slot = SaveSlot::default();
        let reference = SavedReference::new(index, &slot);
        self.stored[index - 1] = Some(slot);
        Ok(reference)
    }

    fn finish_saved(&mut self, reference: &SavedReference, value: &Value) -> Result<()> {
        self.budget.borrow_mut().allocate(clone_allocation(value))?;
        reference.publish(value.clone());
        Ok(())
    }

    fn save_after_value(&mut self, value: Value) -> Result<()> {
        let index = self.save_index()?;
        let reference = self.reserve_saved(index)?;
        self.finish_saved(&reference, &value)
    }

    /// A count read from the wire, bounded before it is used to size an
    /// allocation: every element takes at least one byte.
    fn capacity(&self, count: usize) -> usize {
        count.min(self.value_end.saturating_sub(self.pos))
    }

    fn charge_container<T>(&self, count: usize) -> Result<usize> {
        if count > MAX_ELEMENTS {
            return Err(MarshalError::ResourceLimit("element count"));
        }
        let capacity = self.capacity(count);
        self.budget.borrow_mut().container::<T>(capacity)?;
        Ok(capacity)
    }

    pub(crate) fn value(&mut self) -> Result<Value> {
        self.budget.borrow_mut().value()?;
        if self.depth >= MAX_DEPTH {
            return Err(MarshalError::TooDeep);
        }
        self.depth += 1;
        let result = self.value_inner();
        self.depth -= 1;
        result
    }

    fn value_inner(&mut self) -> Result<Value> {
        if self.pos >= self.value_end {
            return Err(MarshalError::UnexpectedEnd);
        }

        let raw = self.u8()?;
        let save = raw & SAVE_MASK != 0;
        let opcode = raw & OPCODE_MASK;

        // The recursion runs through this function, so it is kept a thin
        // dispatcher: every arm with real locals lives in its own function
        // and a deeply nested value costs one small frame per level, not one
        // frame sized for the largest arm.
        let value = match opcode {
            op::ONE_TUPLE | op::TWO_TUPLE | op::TUPLE | op::ONE_LIST | op::LIST => {
                self.sequence(opcode, save)?
            }
            op::DICT
            | op::OBJECT
            | op::OBJECT_EX1
            | op::OBJECT_EX2
            | op::SUB_STRUCT
            | op::CHECKSUMMED_STREAM
            | op::SUB_STREAM
            | op::PACKED_ROW => self.compound(opcode, save)?,
            _ => {
                let value = self.scalar(opcode, raw)?;
                if save && is_late_saved_opcode(opcode) {
                    self.save_after_value(value.clone())?;
                }
                value
            }
        };
        Ok(value)
    }

    /// Tuples and lists.
    #[inline(never)]
    fn sequence(&mut self, opcode: u8, save: bool) -> Result<Value> {
        let tuple = matches!(opcode, op::ONE_TUPLE | op::TWO_TUPLE | op::TUPLE);
        let count = match opcode {
            op::ONE_TUPLE | op::ONE_LIST => 1,
            op::TWO_TUPLE => 2,
            op::TUPLE | op::LIST => self.size()?,
            _ => unreachable!(),
        };
        if count > MAX_ELEMENTS {
            return Err(MarshalError::ResourceLimit("element count"));
        }
        if count > self.value_end.saturating_sub(self.pos) {
            return Err(MarshalError::UnexpectedEnd);
        }
        let link = if save {
            let index = self.save_index()?;
            Some(self.reserve_saved(index)?)
        } else {
            None
        };
        let items = self.charge_container::<Value>(count)?;
        let mut values = Vec::with_capacity(items);
        for _ in 0..count {
            let value = self.value()?;
            if tuple && link.as_ref().is_some_and(|saved| is_reference_to(&value, saved)) {
                return Err(MarshalError::SaveTable("self referencing tuple"));
            }
            values.push(value);
        }
        let value = if tuple { Value::Tuple(values) } else { Value::List(values) };
        if let Some(link) = link {
            self.finish_saved(&link, &value)?;
        }
        Ok(value)
    }

    /// The other opcodes that contain values.
    #[inline(never)]
    fn compound(&mut self, opcode: u8, save: bool) -> Result<Value> {
        match opcode {
            op::DICT => self.dict(save),
            op::OBJECT => {
                let index = if save { Some(self.save_index()?) } else { None };
                let name = self.value()?;
                let link = match index {
                    Some(index) => Some(self.reserve_saved(index)?),
                    None => None,
                };
                let args = self.value()?;
                let value = Value::Object { name: Box::new(name), args: Box::new(args) };
                if let Some(link) = link {
                    self.finish_saved(&link, &value)?;
                }
                Ok(value)
            }
            op::OBJECT_EX1 | op::OBJECT_EX2 => self.object_ex(opcode, save),
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
    fn dict(&mut self, save: bool) -> Result<Value> {
        let count = self.size()?;
        if count > MAX_ELEMENTS {
            return Err(MarshalError::ResourceLimit("element count"));
        }
        if count.checked_mul(2).is_none_or(|slots| slots > self.value_end.saturating_sub(self.pos)) {
            return Err(MarshalError::UnexpectedEnd);
        }
        let link = if save {
            let index = self.save_index()?;
            Some(self.reserve_saved(index)?)
        } else {
            None
        };
        let capacity = self.charge_container::<(Value, Value)>(count)?;
        let mut entries = Vec::with_capacity(capacity);
        for _ in 0..count {
            // The wire stores each pair value first, then key.
            let value = self.value()?;
            let key = self.value()?;
            entries.push((key, value));
        }
        let value = Value::Dict(entries);
        if let Some(link) = link {
            self.finish_saved(&link, &value)?;
        }
        Ok(value)
    }

    #[inline(never)]
    fn sub_stream(&mut self) -> Result<Value> {
        let length = self.size()?;
        let data = self.chunk(length)?;
        // A nested stream that does not decode is kept as raw bytes.
        Ok(match decode_stream(&data, false, self.depth, Rc::clone(&self.budget)) {
            Ok(inner) => Value::SubStream(SubStream::Decoded(Box::new(inner))),
            Err(error @ MarshalError::ResourceLimit(_)) => return Err(error),
            Err(_) => Value::SubStream(SubStream::Raw(data)),
        })
    }

    #[inline(never)]
    fn packed_row(&mut self) -> Result<Value> {
        let descriptor = self.descriptor()?;
        let length = self.size()?;
        let rle = self.chunk(length)?;
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
            let upcoming = self
                .pos
                .checked_add(length)
                .filter(|stop| *stop <= self.value_end)
                .and_then(|stop| self.buf.get(self.pos..stop));
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
                Value::Bytes(self.chunk(length)?)
            }
            op::LONG_STRING => {
                let length = self.byte_length()?;
                Value::Bytes(self.chunk(length)?)
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
                let bytes = units
                    .checked_mul(2)
                    .filter(|length| *length <= MAX_STRING_BYTES)
                    .ok_or(MarshalError::ResourceLimit("declared byte/string"))?;
                Value::WString(Cow::Owned(utf16_lossy(self.bytes(bytes)?)))
            }
            op::WSTRING_UCS2_CHAR => Value::WString(Cow::Owned(utf16_lossy(self.bytes(2)?))),
            op::WSTRING_UTF8 => {
                let length = self.byte_length()?;
                Value::WString(Cow::Owned(String::from_utf8_lossy(self.bytes(length)?).into_owned()))
            }
            op::TOKEN => {
                let length = self.byte_length()?;
                Value::Token(Cow::Owned(String::from_utf8_lossy(self.bytes(length)?).into_owned()))
            }
            op::EMPTY_TUPLE => Value::Tuple(Vec::new()),
            op::EMPTY_LIST => Value::List(Vec::new()),
            op::BUFFER => {
                let length = self.byte_length()?;
                Value::Buffer(self.chunk(length)?)
            }
            op::CPICKLED => {
                let end = crate::pickle::find_end(self.buf, self.pos, self.value_end)?;
                Value::CPickled(self.chunk(end - self.pos)?)
            }
            op::SAVED_STREAM_ELEMENT => {
                self.saved_refs += 1;
                let index = self.size()?;
                let target = index
                    .checked_sub(1)
                    .and_then(|slot| self.stored.get(slot))
                    .and_then(Option::as_ref)
                    .cloned()
                    .ok_or(MarshalError::SaveTable("invalid TY_REFERENCE in stream"))?;
                self.budget.borrow_mut().allocate(size_of::<SavedReference>())?;
                let reference = SavedReference::new(index, &target);
                Value::SavedReference(reference)
            }
            _ => {
                return Err(MarshalError::UnknownOpcode { opcode: raw, position: self.pos - 1 });
            }
        })
    }

    fn var_integer(&mut self) -> Result<Value> {
        let size = self.size()?;
        if size == 0 {
            return Ok(Value::Int(0));
        }
        let bytes = self.bytes(size)?;
        // JS reads each declared byte; a truncated integer reads `undefined`.
        if bytes.len() < size {
            return Err(MarshalError::TruncatedInteger);
        }
        let negative = bytes.last().is_some_and(|byte| byte & 0x80 != 0);
        let mut significant = bytes.len();
        if negative {
            while significant > 1 && bytes[significant - 1] == 0xff && bytes[significant - 2] & 0x80 != 0 {
                significant -= 1;
            }
            let signed_bytes = &bytes[..significant];
            if signed_bytes.len() <= 8 {
                let mut buffer = [0xff; 8];
                buffer[..signed_bytes.len()].copy_from_slice(signed_bytes);
                let number = i64::from_le_bytes(buffer);
                if number >= -9_007_199_254_740_991 {
                    return Ok(Value::Int(number));
                }
            }
            return Ok(Value::BigSigned(signed_bytes.to_vec()));
        }

        while significant > 0 && bytes[significant - 1] == 0 {
            significant -= 1;
        }
        let magnitude_bytes = &bytes[..significant];
        if magnitude_bytes.len() <= 8 {
            let mut buffer = [0u8; 8];
            buffer[..magnitude_bytes.len()].copy_from_slice(magnitude_bytes);
            let magnitude = u64::from_le_bytes(buffer);
            if magnitude <= 9_007_199_254_740_991 {
                return Ok(Value::Int(magnitude as i64));
            }
        }
        Ok(Value::BigUnsigned(magnitude_bytes.to_vec()))
    }

    #[inline(never)]
    fn object_ex(&mut self, opcode: u8, save: bool) -> Result<Value> {
        let index = if save { Some(self.save_index()?) } else { None };
        let header = self.value()?;
        let link = match index {
            Some(index) => Some(self.reserve_saved(index)?),
            None => None,
        };

        let mut list = Vec::new();
        loop {
            if self.buf.get(self.pos..self.value_end).and_then(|remaining| remaining.first())
                == Some(&op::PACKED_TERMINATOR)
            {
                self.pos += 1;
                break;
            }
            if self.pos >= self.value_end {
                return Err(MarshalError::UnexpectedEnd);
            }
            self.budget.borrow_mut().allocate(size_of::<Value>())?;
            list.push(self.value()?);
        }

        let mut dict = Vec::new();
        loop {
            if self.buf.get(self.pos..self.value_end).and_then(|remaining| remaining.first())
                == Some(&op::PACKED_TERMINATOR)
            {
                self.pos += 1;
                break;
            }
            if self.pos >= self.value_end {
                return Err(MarshalError::UnexpectedEnd);
            }
            let key = self.value()?;
            let value = self.value()?;
            self.budget.borrow_mut().allocate(size_of::<(Value, Value)>())?;
            dict.push((key, value));
        }

        let value = Value::ObjectEx {
            kind: if opcode == op::OBJECT_EX1 { ObjectExKind::Ex1 } else { ObjectExKind::Ex2 },
            header: Box::new(header),
            list,
            dict,
        };
        if let Some(link) = link {
            self.finish_saved(&link, &value)?;
        }
        Ok(value)
    }
}

fn is_reference_to(value: &Value, expected: &SavedReference) -> bool {
    matches!(value, Value::SavedReference(reference) if reference.is_same(expected))
}

fn is_late_saved_opcode(opcode: u8) -> bool {
    matches!(opcode, op::TOKEN | op::BUFFER | op::LONG_STRING | op::CPICKLED | op::VAR_INTEGER)
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
    std::char::decode_utf16(bytes.as_chunks::<2>().0.iter().map(|pair| u16::from_le_bytes(*pair)))
        .map(|decoded| decoded.unwrap_or(char::REPLACEMENT_CHARACTER))
        .collect()
}
