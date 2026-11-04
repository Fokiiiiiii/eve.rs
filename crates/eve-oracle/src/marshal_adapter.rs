//! Maps the JS value graph to and from [`eve_marshal::Value`].
//!
//! Both directions follow marshal.js exactly, quirks included, because the
//! adapter is what lets golden fixtures recorded from the JS codec drive the
//! typed Rust codec:
//!
//! * `js_to_value` mirrors `encodeValue`: how a plain JS value or a
//!   `{type: "..."}` object is turned into a wire type, and the packed-row
//!   input coercions.
//! * `value_to_js` mirrors `decodeValue`: what JS hands back for each opcode.

use std::borrow::Cow;

use eve_marshal::{
    Bytes, Descriptor, MarshalError, ObjectExKind, PackedColumn, PackedRow, SubStream, Value,
    columns_from_header, dbtype, descriptor_columns,
};

use crate::js::Js;

type Result<T> = std::result::Result<T, MarshalError>;

const I64_LIMIT: f64 = 9_223_372_036_854_775_808.0;
const UNKNOWN_DB_TYPE: i64 = i64::MIN;

// ─── JS -> Value (the encoder's input rules) ────────────────────────────────

pub fn js_to_value(js: &Js) -> Result<Value> {
    match js {
        Js::Null => Ok(Value::None),
        Js::Bool(flag) => Ok(Value::Bool(*flag)),
        Js::Num(number) => plain_number(*number),
        Js::Big(digits) => big_to_long(digits),
        Js::Str(text) => Ok(Value::string(text.clone())),
        Js::Buf(bytes) => Ok(Value::Buffer(Bytes::from(bytes.clone()))),
        Js::Arr(items) => Ok(Value::Tuple(values(items)?)),
        Js::Obj(_) => typed(js),
    }
}

fn values(items: &[Js]) -> Result<Vec<Value>> {
    items.iter().map(js_to_value).collect()
}

/// A JS number: an integer (`Number.isInteger`, so also -0 and 2^60) becomes
/// the smallest integer opcode, anything else a PyReal.
pub fn plain_number(number: f64) -> Result<Value> {
    if number.is_finite() && number.fract() == 0.0 {
        if (-I64_LIMIT..I64_LIMIT).contains(&number) {
            Ok(Value::Int(number as i64))
        } else {
            // BigInt(number) then writeBigInt64LE: a RangeError.
            Err(MarshalError::OutOfRange("integer beyond int64"))
        }
    } else {
        Ok(Value::Real(number))
    }
}

fn big_to_long(digits: &str) -> Result<Value> {
    let number: i128 = digits.parse().map_err(|_| MarshalError::OutOfRange("BigInt beyond i128"))?;
    i64::try_from(number).map(Value::Long).map_err(|_| MarshalError::OutOfRange("BigInt beyond int64"))
}

fn pairs(node: Option<&Js>) -> Result<Vec<(Value, Value)>> {
    let Some(Js::Arr(entries)) = node else {
        return Err(MarshalError::Unencodable("dict entries missing"));
    };
    entries
        .iter()
        .map(|entry| match entry {
            Js::Arr(pair) if pair.len() >= 2 => Ok((js_to_value(&pair[0])?, js_to_value(&pair[1])?)),
            _ => Err(MarshalError::Unencodable("dict entry is not a pair")),
        })
        .collect()
}

fn optional_pairs(node: Option<&Js>) -> Result<Vec<(Value, Value)>> {
    match node {
        None | Some(Js::Null) => Ok(Vec::new()),
        other => pairs(other),
    }
}

fn items_of(node: Option<&Js>) -> Result<Vec<Value>> {
    match node {
        None | Some(Js::Null) => Ok(Vec::new()),
        Some(Js::Arr(items)) => values(items),
        Some(_) => Err(MarshalError::Unencodable("items is not an array")),
    }
}

fn field(js: &Js, name: &str) -> Result<Value> {
    js.get(name).map_or(Ok(Value::None), js_to_value)
}

/// `Buffer.from(x)` for a Buffer or an array of numbers.
fn buffer_source(node: Option<&Js>) -> Result<Vec<u8>> {
    match node {
        None | Some(Js::Null) => Ok(Vec::new()),
        Some(Js::Buf(bytes)) => Ok(bytes.clone()),
        Some(Js::Arr(items)) => items
            .iter()
            .map(|item| match item {
                Js::Num(number) => Ok((*number as i64 & 0xff) as u8),
                _ => Err(MarshalError::Unencodable("byte source element is not a number")),
            })
            .collect(),
        Some(_) => Err(MarshalError::Unencodable("byte source")),
    }
}

/// `Number(x)` for the values `{type: "real"}` accepts.
fn js_number(node: Option<&Js>) -> f64 {
    match node {
        None => f64::NAN,
        Some(Js::Null) => 0.0,
        Some(Js::Bool(flag)) => f64::from(u8::from(*flag)),
        Some(Js::Num(number)) => *number,
        Some(Js::Big(digits)) => digits.parse().unwrap_or(f64::NAN),
        Some(Js::Str(text)) => text.trim().parse().unwrap_or(f64::NAN),
        Some(_) => f64::NAN,
    }
}

fn text_of(node: Option<&Js>) -> Result<String> {
    match node {
        Some(Js::Str(text)) => Ok(text.clone()),
        _ => Err(MarshalError::Unencodable("expected a string")),
    }
}

fn typed(js: &Js) -> Result<Value> {
    let Some(kind) = js.get("type").and_then(Js::as_str).filter(|kind| !kind.is_empty()) else {
        return Err(MarshalError::Unencodable("object without a type"));
    };

    match kind {
        "dict" => Ok(Value::Dict(pairs(js.get("entries"))?)),
        "wstring" => Ok(Value::WString(Cow::Owned(match js.get("value") {
            None | Some(Js::Null) => String::new(),
            other => text_of(other)?,
        }))),
        "long" => match js.get("value") {
            Some(Js::Num(number)) if number.is_finite() && number.fract() == 0.0 => {
                if (-I64_LIMIT..I64_LIMIT).contains(number) {
                    Ok(Value::Long(*number as i64))
                } else {
                    Err(MarshalError::OutOfRange("integer beyond int64"))
                }
            }
            Some(Js::Big(digits)) => big_to_long(digits),
            _ => Err(MarshalError::OutOfRange("BigInt() of a non-integer")),
        },
        "real" => {
            let number = js_number(js.get("value"));
            Ok(Value::Real(if number.is_finite() { number } else { 0.0 }))
        }
        "tuple" => Ok(Value::Tuple(items_of(js.get("items"))?)),
        "list" => Ok(Value::List(items_of(js.get("items"))?)),
        "bytes" => Ok(Value::Bytes(buffer_source(js.get("value"))?.into())),
        "rawstr" => match js.get("value") {
            None | Some(Js::Null) => Ok(Value::Bytes(Bytes::new())),
            Some(Js::Str(text)) => Ok(Value::Bytes(text.clone().into_bytes().into())),
            Some(_) => Err(MarshalError::Unencodable("rawstr of a non-string")),
        },
        "object" => {
            Ok(Value::Object { name: Box::new(field(js, "name")?), args: Box::new(field(js, "args")?) })
        }
        "packedrow" => packed_row(js),
        "substruct" => Ok(Value::SubStruct(Box::new(field(js, "value")?))),
        "substream" => Ok(Value::SubStream(SubStream::Decoded(Box::new(field(js, "value")?)))),
        "token" => Ok(Value::Token(Cow::Owned(text_of(js.get("value"))?))),
        "objectex1" | "objectex2" => Ok(Value::ObjectEx {
            kind: if kind == "objectex1" { ObjectExKind::Ex1 } else { ObjectExKind::Ex2 },
            header: Box::new(field(js, "header")?),
            list: items_of(js.get("list"))?,
            dict: optional_pairs(js.get("dict"))?,
        }),
        "cpicked" => Ok(Value::CPickled(buffer_source(js.get("data"))?.into())),
        _ => Err(MarshalError::Unencodable("unknown marshal type")),
    }
}

/// `unwrapPackedScalarValue`: typed scalar wrappers give up their value.
fn unwrap_scalar(js: &Js) -> &Js {
    if let Js::Obj(_) = js {
        let wrapper = js.get("type").and_then(Js::as_str);
        if matches!(
            wrapper,
            Some("int" | "long" | "float" | "double" | "bool" | "wstring" | "token" | "rawstr")
        ) {
            return match js.get("value") {
                Some(inner) => unwrap_scalar(inner),
                None => &Js::Null,
            };
        }
    }
    js
}

/// A packed-row cell. Variable-width columns are ordinary marshal values;
/// fixed-width and BOOL columns are coerced numerically, so typed wrappers are
/// unwrapped and numbers stay plain doubles.
fn packed_value(js: &Js, db_type: i64) -> Result<Value> {
    if dbtype::size_bits(db_type) == 0 {
        return js_to_value(js);
    }
    Ok(match unwrap_scalar(js) {
        Js::Null => Value::None,
        Js::Bool(flag) => Value::Bool(*flag),
        Js::Num(number) => Value::Real(*number),
        Js::Big(digits) => match digits.parse::<i128>().ok().and_then(|number| i64::try_from(number).ok()) {
            Some(number) => Value::Long(number),
            None => Value::Real(digits.parse().unwrap_or(f64::INFINITY)),
        },
        Js::Str(text) => Value::string(text.clone()),
        Js::Buf(bytes) => Value::Buffer(Bytes::from(bytes.clone())),
        // Anything else coerces to the fallback (zero / false).
        _ => Value::List(Vec::new()),
    })
}

fn packed_column(entry: &Js) -> Result<PackedColumn> {
    let Js::Arr(parts) = entry else {
        return Err(MarshalError::InvalidPackedRow("column entry is not an array"));
    };
    let db_type = match parts.get(1) {
        Some(Js::Num(number)) if number.is_finite() && number.fract() == 0.0 => *number as i64,
        _ => UNKNOWN_DB_TYPE,
    };
    Ok(PackedColumn { name: String::new(), db_type })
}

fn packed_row(js: &Js) -> Result<Value> {
    let header = field(js, "header")?;
    let columns = match js.get("columns") {
        Some(Js::Arr(columns)) => columns.iter().map(packed_column).collect::<Result<Vec<_>>>()?,
        _ => columns_from_header(&header)?,
    };
    let Some(Js::Arr(cells)) = js.get("values") else {
        return Err(MarshalError::InvalidPackedRow("missing values"));
    };
    if cells.len() != columns.len() {
        return Err(MarshalError::InvalidPackedRow("values length does not match the column count"));
    }
    let values = cells
        .iter()
        .zip(&columns)
        .map(|(cell, column)| packed_value(cell, column.db_type))
        .collect::<Result<Vec<_>>>()?;
    Ok(Value::PackedRow(Box::new(PackedRow::new(Descriptor::new(header, columns), values))))
}

// ─── Value -> JS (the decoder's output shapes) ──────────────────────────────

pub fn value_to_js(value: &Value) -> Js {
    match value {
        Value::None => Js::Null,
        Value::Bool(flag) => Js::Bool(*flag),
        Value::Int(number) => Js::Num(*number as f64),
        Value::Long(number) => Js::Big(number.to_string()),
        Value::Real(number) => Js::Num(*number),
        Value::BigUnsigned(bytes) => Js::Big(le_bytes_to_decimal(bytes)),
        Value::Str(text) => Js::Str(text.to_string()),
        // Every string opcode and PyBuffer decode to a JS Buffer.
        Value::Bytes(bytes) | Value::Buffer(bytes) => Js::Buf(bytes.to_vec()),
        Value::WString(text) => {
            Js::obj([("type", Js::Str("wstring".into())), ("value", Js::Str(text.to_string()))])
        }
        Value::Token(text) => {
            Js::obj([("type", Js::Str("token".into())), ("value", Js::Str(text.to_string()))])
        }
        Value::Tuple(items) => Js::Arr(items.iter().map(value_to_js).collect()),
        Value::List(items) => Js::obj([
            ("type", Js::Str("list".into())),
            ("items", Js::Arr(items.iter().map(value_to_js).collect())),
        ]),
        Value::Dict(entries) => {
            Js::obj([("type", Js::Str("dict".into())), ("entries", pairs_to_js(entries))])
        }
        Value::Object { name, args } => Js::obj([
            ("type", Js::Str("object".into())),
            ("name", value_to_js(name)),
            ("args", value_to_js(args)),
        ]),
        Value::ObjectEx { kind, header, list, dict } => Js::obj([
            ("type", Js::Str(if *kind == ObjectExKind::Ex1 { "objectex1" } else { "objectex2" }.into())),
            ("header", value_to_js(header)),
            ("list", Js::Arr(list.iter().map(value_to_js).collect())),
            ("dict", pairs_to_js(dict)),
        ]),
        Value::SubStruct(inner) => {
            Js::obj([("type", Js::Str("substruct".into())), ("value", value_to_js(inner))])
        }
        Value::SubStream(SubStream::Decoded(inner)) => {
            Js::obj([("type", Js::Str("substream".into())), ("value", value_to_js(inner))])
        }
        Value::SubStream(SubStream::Raw(bytes)) => {
            Js::obj([("type", Js::Str("substream".into())), ("raw", Js::Buf(bytes.to_vec()))])
        }
        Value::Checksummed { checksum, value } => Js::obj([
            ("type", Js::Str("checksummed".into())),
            ("checksum", Js::Num(f64::from(*checksum))),
            ("value", value_to_js(value)),
        ]),
        Value::CPickled(bytes) => {
            Js::obj([("type", Js::Str("cpicked".into())), ("data", Js::Buf(bytes.to_vec()))])
        }
        Value::PackedRow(row) => packed_row_to_js(row),
    }
}

fn pairs_to_js(entries: &[(Value, Value)]) -> Js {
    Js::Arr(entries.iter().map(|(key, value)| Js::Arr(vec![value_to_js(key), value_to_js(value)])).collect())
}

fn packed_row_to_js(row: &PackedRow) -> Js {
    let columns = descriptor_columns(row.header())
        .map(|entries| entries.iter().map(value_to_js).collect())
        .unwrap_or_default();
    Js::obj([
        ("type", Js::Str("packedrow".into())),
        ("header", value_to_js(row.header())),
        ("columns", Js::Arr(columns)),
        ("values", Js::Arr(row.values.iter().map(value_to_js).collect())),
        (
            "fields",
            Js::Obj(row.fields().iter().map(|(name, value)| (name.clone(), value_to_js(value))).collect()),
        ),
        ("rleData", Js::Buf(row.rle.as_ref().map(|rle| rle.to_vec()).unwrap_or_default())),
    ])
}

/// The decimal digits of a little-endian unsigned integer.
fn le_bytes_to_decimal(bytes: &[u8]) -> String {
    let mut limbs: Vec<u32> = Vec::new(); // base 1e9, little endian
    for &byte in bytes.iter().rev() {
        let mut carry = u64::from(byte);
        for limb in &mut limbs {
            let value = u64::from(*limb) * 256 + carry;
            *limb = (value % 1_000_000_000) as u32;
            carry = value / 1_000_000_000;
        }
        while carry > 0 {
            limbs.push((carry % 1_000_000_000) as u32);
            carry /= 1_000_000_000;
        }
    }
    let Some(top) = limbs.pop() else {
        return "0".into();
    };
    let mut text = top.to_string();
    for limb in limbs.iter().rev() {
        text.push_str(&format!("{limb:09}"));
    }
    text
}

#[cfg(test)]
mod tests {
    use super::le_bytes_to_decimal;

    #[test]
    fn decimal_conversion() {
        assert_eq!(le_bytes_to_decimal(&[]), "0");
        assert_eq!(le_bytes_to_decimal(&[0x2a]), "42");
        assert_eq!(le_bytes_to_decimal(&[0xff; 8]), "18446744073709551615");
        assert_eq!(le_bytes_to_decimal(&[1, 1, 1, 1, 1, 1, 1, 1, 1]), "18519084246547628289");
    }
}
