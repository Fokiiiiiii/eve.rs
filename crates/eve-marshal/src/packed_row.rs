//! PyPackedRow: a `DBRowDescriptor` header plus a bit-packed row.
//!
//! Layout (mirrors the JS reference): fixed-width columns are laid out widest
//! first (ties by column index), followed by one bitmap holding every BOOL
//! value and then one null flag per column. That block is RLE-compressed.
//! Columns without a fixed width follow as ordinary marshal values.

use std::sync::{Arc, OnceLock};

use bytes::Bytes;

use crate::decode::Decoder;
use crate::encode::Encoder;
use crate::error::{MarshalError, Result};
use crate::jsnum;
use crate::value::Value;
use crate::wire::{dbtype, op};

/// Marker for a column type the layout does not recognise.
const UNKNOWN_DB_TYPE: i64 = i64::MIN;

/// One column of a packed row's `DBRowDescriptor`.
#[derive(Debug, Clone, PartialEq)]
pub struct PackedColumn {
    pub name: String,
    /// The DBTYPE code (see [`crate::dbtype`]). Codes outside the known set are
    /// carried as-is and laid out like a variable-width value.
    pub db_type: i64,
}

/// A row descriptor: the header as it appears on the wire, the columns it
/// declares, and the row layout derived from them.
///
/// Every row of a rowset repeats the same descriptor, so rows share one
/// through an [`Arc`]. The decoder recognises a repeated descriptor by its
/// bytes and reuses it; the encoder writes the header bytes it produced the
/// first time.
#[derive(Debug)]
pub struct Descriptor {
    pub header: Value,
    pub columns: Vec<PackedColumn>,
    pub(crate) layout: Layout,
    encoded_header: OnceLock<Vec<u8>>,
}

impl PartialEq for Descriptor {
    fn eq(&self, other: &Self) -> bool {
        self.header == other.header && self.columns == other.columns
    }
}

impl Descriptor {
    /// A descriptor with explicit columns (the header is written verbatim and
    /// need not describe them).
    pub fn new(header: Value, columns: Vec<PackedColumn>) -> Arc<Self> {
        let layout = Layout::new(&columns);
        Arc::new(Self { header, columns, layout, encoded_header: OnceLock::new() })
    }

    /// A descriptor whose columns are read from its header.
    pub fn from_header(header: Value) -> Result<Arc<Self>> {
        let columns = columns_from_header(&header)?;
        Ok(Self::new(header, columns))
    }
}

/// A PyPackedRow: a row descriptor plus one value per column.
#[derive(Debug, Clone, PartialEq)]
pub struct PackedRow {
    pub descriptor: Arc<Descriptor>,
    /// One value per column, in column order.
    pub values: Vec<Value>,
    /// The RLE-compressed fixed-width block exactly as received. Only set by
    /// the decoder; the encoder always recompresses.
    pub rle: Option<Bytes>,
}

/// The columns of a `DBRowDescriptor` header, as the raw column entries
/// (`(name, type)` tuples): `header.header[1][0]` of a PyObjectEx.
pub fn descriptor_columns(header: &Value) -> Option<&[Value]> {
    let Value::ObjectEx { header: inner, .. } = header else {
        return None;
    };
    let Value::Tuple(items) = inner.as_ref() else {
        return None;
    };
    let Value::Tuple(second) = items.get(1)? else {
        return None;
    };
    let Value::Tuple(columns) = second.first()? else {
        return None;
    };
    Some(columns)
}

fn column_name(value: Option<&Value>) -> Option<String> {
    match value? {
        Value::Str(text) | Value::WString(text) | Value::Token(text) => Some(text.to_string()),
        Value::Bytes(bytes) | Value::Buffer(bytes) => Some(String::from_utf8_lossy(bytes).into_owned()),
        _ => None,
    }
}

fn column_type(value: Option<&Value>) -> i64 {
    match value {
        Some(Value::Int(code)) => *code,
        Some(Value::Real(code)) if code.is_finite() && code.fract() == 0.0 => *code as i64,
        _ => UNKNOWN_DB_TYPE,
    }
}

/// Resolve the columns from a row descriptor header.
pub fn columns_from_header(header: &Value) -> Result<Vec<PackedColumn>> {
    let entries = descriptor_columns(header)
        .ok_or(MarshalError::InvalidPackedRow("missing DBRowDescriptor columns"))?;
    entries
        .iter()
        .enumerate()
        .map(|(index, entry)| {
            let Value::Tuple(parts) = entry else {
                return Err(MarshalError::InvalidPackedRow("column entry is not a (name, type) tuple"));
            };
            Ok(PackedColumn {
                name: column_name(parts.first()).unwrap_or_else(|| format!("col_{index}")),
                db_type: column_type(parts.get(1)),
            })
        })
        .collect()
}

impl PackedRow {
    /// A row over an existing descriptor. Nothing is checked until the row is
    /// encoded, as in the JS reference.
    pub fn new(descriptor: Arc<Descriptor>, values: Vec<Value>) -> Self {
        Self { descriptor, values, rle: None }
    }

    /// A row whose columns are read from its descriptor header.
    pub fn from_header(header: Value, values: Vec<Value>) -> Result<Self> {
        Ok(Self::new(Descriptor::from_header(header)?, values))
    }

    pub fn header(&self) -> &Value {
        &self.descriptor.header
    }

    pub fn columns(&self) -> &[PackedColumn] {
        &self.descriptor.columns
    }

    /// The row as a name → value map, including the virtual columns the
    /// header declares (`StackSize`, `Singleton`). A repeated column name
    /// keeps the position of its first occurrence and the value of its last.
    pub fn fields(&self) -> Vec<(String, Value)> {
        let mut fields: Vec<(String, Value)> = Vec::with_capacity(self.descriptor.columns.len());
        for (column, value) in self.descriptor.columns.iter().zip(&self.values) {
            set_field(&mut fields, column.name.clone(), value.clone());
        }
        for (name, resolver) in virtual_columns(&self.descriptor.header) {
            let derived = match resolver.as_str() {
                "eve.common.script.sys.eveCfg.StackSize" => derive_stack_size(&fields),
                "eve.common.script.sys.eveCfg.Singleton" => derive_singleton(&fields),
                _ => continue,
            };
            set_field(&mut fields, name, derived);
        }
        fields
    }
}

fn set_field(fields: &mut Vec<(String, Value)>, name: String, value: Value) {
    match fields.iter_mut().find(|(existing, _)| *existing == name) {
        Some(slot) => slot.1 = value,
        None => fields.push((name, value)),
    }
}

fn field_number(fields: &[(String, Value)], name: &str) -> f64 {
    fields
        .iter()
        .find(|(existing, _)| existing == name)
        .map_or(f64::NAN, |(_, value)| jsnum::value_to_number(value))
}

fn number_value(number: f64) -> Value {
    if jsnum::is_safe_integer(number) { Value::Int(number as i64) } else { Value::Real(number) }
}

fn derive_stack_size(fields: &[(String, Value)]) -> Value {
    let quantity = field_number(fields, "quantity");
    if !quantity.is_finite() {
        return Value::Int(0);
    }
    number_value(if quantity < 0.0 { 1.0 } else { quantity })
}

fn derive_singleton(fields: &[(String, Value)]) -> Value {
    let quantity = field_number(fields, "quantity");
    if quantity.is_finite() && quantity < 0.0 {
        return number_value(-quantity);
    }
    let location = field_number(fields, "locationID");
    let in_station_range = location.is_finite() && (30_000_000.0..40_000_000.0).contains(&location);
    Value::Int(i64::from(in_station_range))
}

/// `(column name, resolver name)` pairs from the header's virtual-column list.
fn virtual_columns(header: &Value) -> Vec<(String, String)> {
    let Value::ObjectEx { header: inner, .. } = header else {
        return Vec::new();
    };
    let Value::Tuple(items) = inner.as_ref() else {
        return Vec::new();
    };
    let entries: &[Value] = match items.get(2) {
        Some(Value::List(entries) | Value::Tuple(entries)) if items.len() >= 3 => entries,
        _ => return Vec::new(),
    };
    entries
        .iter()
        .filter_map(|entry| {
            let Value::Tuple(parts) = entry else {
                return None;
            };
            let name = column_name(parts.first()).filter(|name| !name.is_empty())?;
            let resolver = column_name(parts.get(1)).unwrap_or_default();
            Some((name, resolver))
        })
        .collect()
}

/// The order and geometry of a row's slots.
#[derive(Debug)]
pub(crate) struct Layout {
    /// Column indices sorted widest first, ties by column index.
    order: Vec<(usize, u32)>,
    /// For each BOOL column, its position in the boolean bitmap.
    bool_slot: Vec<Option<usize>>,
    booleans: usize,
    /// Bytes taken by the fixed-width columns.
    pub(crate) data_bytes: usize,
    /// Bytes taken by the booleans + null flags bitmap.
    pub(crate) bitmap_bytes: usize,
}

impl Layout {
    pub(crate) fn new(columns: &[PackedColumn]) -> Self {
        let mut order = Vec::with_capacity(columns.len());
        let mut bool_slot = vec![None; columns.len()];
        let mut booleans = 0;
        let mut data_bits = 0usize;
        for (index, column) in columns.iter().enumerate() {
            let size = dbtype::size_bits(column.db_type);
            if column.db_type == dbtype::BOOL {
                bool_slot[index] = Some(booleans);
                booleans += 1;
            }
            if size >= 8 {
                data_bits += size as usize;
            }
            order.push((index, size));
        }
        order.sort_by(|left, right| right.1.cmp(&left.1).then(left.0.cmp(&right.0)));
        Self {
            order,
            bool_slot,
            booleans,
            data_bytes: data_bits >> 3,
            bitmap_bytes: ((booleans + columns.len()) >> 3) + 1,
        }
    }

    fn null_bit(&self, index: usize) -> usize {
        index + self.booleans
    }
}

fn bit_is_set(bitmap: &[u8], bit: usize) -> bool {
    bitmap.get(bit >> 3).is_some_and(|byte| byte & (1 << (bit & 7)) != 0)
}

// ─── Decoding ───────────────────────────────────────────────────────────────

pub(crate) fn decode(decoder: &mut Decoder<'_>, descriptor: Arc<Descriptor>, rle: Bytes) -> Result<Value> {
    let columns = &descriptor.columns;
    let layout = &descriptor.layout;

    let mut packed = std::mem::take(&mut decoder.scratch);
    decompress_rle_into(&rle, layout.data_bytes + layout.bitmap_bytes, &mut packed);
    let bitmap = &packed[layout.data_bytes..];
    let mut values = Vec::with_capacity(columns.len());
    values.resize_with(columns.len(), || Value::None);

    let mut offset = 0;
    for &(index, size) in layout.order.iter().filter(|(_, size)| *size > 1) {
        values[index] = if bit_is_set(bitmap, layout.null_bit(index)) {
            Value::None
        } else {
            fixed_value(columns[index].db_type, &packed, offset)?
        };
        offset += (size >> 3) as usize;
    }

    for &(index, _) in layout.order.iter().filter(|(_, size)| *size == 1) {
        values[index] = if bit_is_set(bitmap, layout.null_bit(index)) {
            Value::None
        } else {
            let slot = layout.bool_slot[index].expect("BOOL column has a bitmap slot");
            Value::Bool(bit_is_set(bitmap, slot))
        };
    }
    // Hand the scratch buffer back before recursing: nested rows reuse it.
    decoder.scratch = packed;

    for &(index, _) in layout.order.iter().filter(|(_, size)| *size == 0) {
        values[index] = decoder.value()?;
    }

    Ok(Value::PackedRow(Box::new(PackedRow { descriptor, values, rle: Some(rle) })))
}

fn fixed_value(db_type: i64, packed: &[u8], offset: usize) -> Result<Value> {
    fn read<const N: usize>(packed: &[u8], offset: usize) -> Result<[u8; N]> {
        packed
            .get(offset..offset + N)
            .and_then(|slice| slice.try_into().ok())
            .ok_or(MarshalError::OutOfRange("packed row data"))
    }

    Ok(match db_type {
        dbtype::CY | dbtype::I8 | dbtype::UI8 | dbtype::FILETIME => {
            let number = i64::from_le_bytes(read(packed, offset)?);
            // The JS reference hands back a number when it is a safe integer.
            if number.unsigned_abs() <= 9_007_199_254_740_991 {
                Value::Int(number)
            } else {
                Value::Long(number)
            }
        }
        dbtype::I4 => Value::Int(i64::from(i32::from_le_bytes(read(packed, offset)?))),
        dbtype::UI4 => Value::Int(i64::from(u32::from_le_bytes(read(packed, offset)?))),
        dbtype::R4 => Value::Real(f64::from(f32::from_le_bytes(read(packed, offset)?))),
        dbtype::R8 => Value::Real(f64::from_le_bytes(read(packed, offset)?)),
        dbtype::I2 => Value::Int(i64::from(i16::from_le_bytes(read(packed, offset)?))),
        dbtype::UI2 => Value::Int(i64::from(u16::from_le_bytes(read(packed, offset)?))),
        dbtype::I1 => Value::Int(i64::from(i8::from_le_bytes(read(packed, offset)?))),
        dbtype::UI1 => Value::Int(i64::from(u8::from_le_bytes(read(packed, offset)?))),
        _ => Value::None,
    })
}

// ─── Encoding ───────────────────────────────────────────────────────────────

pub(crate) fn encode(encoder: &mut Encoder<'_>, row: &PackedRow) -> Result<()> {
    let descriptor = &*row.descriptor;
    let layout = &descriptor.layout;
    if row.values.len() != descriptor.columns.len() {
        return Err(MarshalError::InvalidPackedRow("values length does not match the column count"));
    }

    encoder.out.push(op::PACKED_ROW);
    match descriptor.encoded_header.get() {
        Some(bytes) => encoder.out.extend_from_slice(bytes),
        None => {
            let start = encoder.out.len();
            encoder.value(&descriptor.header)?;
            let _ = descriptor.encoded_header.set(encoder.out[start..].to_vec());
        }
    }

    let mut bitmap = vec![0u8; layout.bitmap_bytes];
    let mut fixed = Vec::with_capacity(layout.data_bytes + layout.bitmap_bytes);

    for &(index, _) in layout.order.iter().filter(|(_, size)| *size > 1) {
        let value = &row.values[index];
        if matches!(value, Value::None) {
            let bit = layout.null_bit(index);
            bitmap[bit >> 3] |= 1 << (bit & 7);
        }
        fixed_bytes(descriptor.columns[index].db_type, value, &mut fixed)?;
    }

    for &(index, _) in layout.order.iter().filter(|(_, size)| *size == 1) {
        if pack_bool(&row.values[index]) {
            let slot = layout.bool_slot[index].expect("BOOL column has a bitmap slot");
            bitmap[slot >> 3] |= 1 << (slot & 7);
        }
    }

    fixed.extend_from_slice(&bitmap);
    let compressed = compress_rle(&fixed);
    encoder.size(compressed.len())?;
    encoder.out.extend_from_slice(&compressed);

    for &(index, _) in layout.order.iter().filter(|(_, size)| *size == 0) {
        encoder.value(&row.values[index])?;
    }
    Ok(())
}

fn fixed_bytes(db_type: i64, value: &Value, out: &mut Vec<u8>) -> Result<()> {
    match db_type {
        dbtype::CY | dbtype::I8 | dbtype::UI8 | dbtype::FILETIME => {
            out.extend_from_slice(&pack_i64(value)?.to_le_bytes());
        }
        dbtype::I4 => out.extend_from_slice(
            &checked_int(value, i32::MIN.into(), i32::MAX.into(), "I4")?.to_le_bytes()[..4],
        ),
        dbtype::UI4 => out.extend_from_slice(&jsnum::to_uint32(pack_number(value).trunc()).to_le_bytes()),
        dbtype::R4 => out.extend_from_slice(&(pack_number(value) as f32).to_le_bytes()),
        dbtype::R8 => out.extend_from_slice(&pack_number(value).to_le_bytes()),
        dbtype::I2 => out.extend_from_slice(
            &checked_int(value, i16::MIN.into(), i16::MAX.into(), "I2")?.to_le_bytes()[..2],
        ),
        dbtype::UI2 => {
            let masked = jsnum::to_uint32(pack_number(value).trunc()) & 0xffff;
            out.extend_from_slice(&(masked as u16).to_le_bytes());
        }
        dbtype::I1 => out.push(checked_int(value, i8::MIN.into(), i8::MAX.into(), "I1")? as u8),
        dbtype::UI1 => out.push((jsnum::to_uint32(pack_number(value).trunc()) & 0xff) as u8),
        _ => {}
    }
    Ok(())
}

/// A signed column value, failing (like Node's `writeIntXxLE`) outside its width.
fn checked_int(value: &Value, min: i64, max: i64, column: &'static str) -> Result<i64> {
    let number = pack_number(value).trunc();
    if number < min as f64 || number > max as f64 {
        return Err(MarshalError::OutOfRange(column));
    }
    Ok(number as i64)
}

/// `normalizePackedNumber`: any loosely typed value as a finite number, else 0.
fn pack_number(value: &Value) -> f64 {
    let number = match value {
        Value::None => return 0.0,
        Value::Bool(flag) => f64::from(u8::from(*flag)),
        Value::Int(_) | Value::Long(_) | Value::Real(_) | Value::BigUnsigned(_) => {
            jsnum::value_to_number(value)
        }
        Value::Str(_) | Value::WString(_) | Value::Token(_) | Value::Bytes(_) | Value::Buffer(_) => {
            jsnum::value_to_number(value)
        }
        _ => return 0.0,
    };
    if number.is_finite() { number } else { 0.0 }
}

/// `normalizePackedBigInt`, then the range check of `writeBigInt64LE`.
fn pack_i64(value: &Value) -> Result<i64> {
    const LIMIT: f64 = 9_223_372_036_854_775_808.0;
    let out_of_range = MarshalError::OutOfRange("64-bit column");
    Ok(match value {
        Value::None => 0,
        Value::Int(number) | Value::Long(number) => *number,
        Value::Bool(flag) => i64::from(*flag),
        Value::Real(number) if number.is_finite() => {
            let truncated = number.trunc();
            if !(-LIMIT..LIMIT).contains(&truncated) {
                return Err(out_of_range);
            }
            truncated as i64
        }
        Value::BigUnsigned(bytes) => {
            if bytes.len() > 8 {
                return Err(out_of_range);
            }
            let mut buffer = [0u8; 8];
            buffer[..bytes.len()].copy_from_slice(bytes);
            i64::try_from(u64::from_le_bytes(buffer)).map_err(|_| out_of_range)?
        }
        Value::Str(text) | Value::WString(text) | Value::Token(text) => text_to_i64(text)?,
        Value::Bytes(bytes) | Value::Buffer(bytes) => text_to_i64(&String::from_utf8_lossy(bytes))?,
        _ => 0,
    })
}

fn text_to_i64(text: &str) -> Result<i64> {
    if jsnum::trim_js(text).is_empty() {
        return Ok(0);
    }
    match jsnum::bigint_from_str(text) {
        // Text JS cannot parse as a BigInt falls back to zero.
        None => Ok(0),
        Some(number) => i64::try_from(number).map_err(|_| MarshalError::OutOfRange("64-bit column")),
    }
}

/// `normalizePackedBoolean`.
fn pack_bool(value: &Value) -> bool {
    match value {
        Value::Bool(flag) => *flag,
        Value::None => false,
        other => pack_number(other) != 0.0,
    }
}

// ─── RLE ────────────────────────────────────────────────────────────────────

/// The row block's RLE: a control byte carries two nibbles; a nibble below 8
/// announces `8 - n` literal bytes, a nibble of 8 or more a run of `n - 7`
/// zero bytes. Trailing all-zero control bytes are dropped.
pub fn compress_rle(input: &[u8]) -> Vec<u8> {
    let mut out: Vec<u8> = Vec::with_capacity(input.len() + input.len() / 8 + 2);
    let mut second_nibble = false;
    let mut control_index = 0;
    let mut position = 0;
    let mut zero_chains = 0u32;

    while position < input.len() {
        if !second_nibble {
            control_index = out.len();
            out.push(0);
        }

        let start = position;
        let end = (position + 8).min(input.len());
        let count = if input[position] != 0 {
            zero_chains = 0;
            loop {
                out.push(input[position]);
                position += 1;
                if !(position < end && input[position] != 0) {
                    break;
                }
            }
            (8 - (position - start)) as u8
        } else {
            zero_chains += 1;
            while position < end && input[position] == 0 {
                position += 1;
            }
            ((position - start) + 7) as u8
        };

        if second_nibble {
            out[control_index] |= count << 4;
        } else {
            out[control_index] = count;
        }
        second_nibble = !second_nibble;
    }

    if second_nibble && zero_chains > 0 {
        zero_chains += 1;
    }
    while zero_chains > 1 {
        zero_chains -= 2;
        out.pop();
    }
    out
}

/// Inverse of [`compress_rle`], producing exactly `expected` bytes (missing
/// input reads as zeros, surplus input is ignored).
pub fn decompress_rle(input: &[u8], expected: usize) -> Vec<u8> {
    let mut out = Vec::new();
    decompress_rle_into(input, expected, &mut out);
    out
}

/// [`decompress_rle`] into a caller-owned buffer, which is reused.
pub(crate) fn decompress_rle_into(input: &[u8], expected: usize, out: &mut Vec<u8>) {
    out.clear();
    out.resize(expected, 0);
    let mut read = 0;
    let mut written = 0;

    while read < input.len() && written < expected {
        let control = input[read];
        read += 1;
        for half in 0..2 {
            if written >= expected {
                break;
            }
            let nibble = if half == 0 { control & 0x0f } else { control >> 4 };
            if nibble < 8 {
                let literals = (8 - usize::from(nibble)).min(input.len() - read).min(expected - written);
                out[written..written + literals].copy_from_slice(&input[read..read + literals]);
                read += literals;
                written += literals;
            } else {
                written += (usize::from(nibble) - 7).min(expected - written);
            }
            if read >= input.len() && written >= expected {
                break;
            }
        }
    }
}
