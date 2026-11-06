//! JavaScript's dynamic-typing rules over the [`Js`] value graph.
//!
//! The destiny reference reads loosely typed entity objects. These helpers
//! evaluate the same expressions (`a ?? b`, `a && a.x`, `Number(v)`,
//! `toFiniteNumber`) so an adapter can turn an entity into a typed model
//! without changing what the reference would have computed.

use eve_destiny::Vec3;
use eve_destiny::ball::IdRef;
use eve_destiny::math::finite_or;

use crate::js::Js;

/// JS whitespace, as trimmed by `String.prototype.trim` and matched by `\s`.
pub fn is_js_whitespace(c: char) -> bool {
    matches!(
        c,
        '\t' | '\n' | '\u{b}' | '\u{c}' | '\r' | ' ' | '\u{a0}' | '\u{1680}' | '\u{2000}'
            ..='\u{200a}' | '\u{2028}' | '\u{2029}' | '\u{202f}' | '\u{205f}' | '\u{3000}' | '\u{feff}'
    )
}

pub fn trim_js(text: &str) -> &str {
    text.trim_matches(is_js_whitespace)
}

impl Js {
    /// JS truthiness.
    pub fn truthy(&self) -> bool {
        match self {
            Js::Null => false,
            Js::Bool(flag) => *flag,
            Js::Num(number) => !(*number == 0.0 || number.is_nan()),
            Js::Big(digits) => digits.trim_start_matches('-') != "0",
            Js::Str(text) => !text.is_empty(),
            Js::Buf(_) | Js::Arr(_) | Js::Obj(_) => true,
        }
    }
}

pub fn truthy(node: Option<&Js>) -> bool {
    node.is_some_and(Js::truthy)
}

/// `value == null`: `undefined` or `null`.
pub fn nullish(node: Option<&Js>) -> bool {
    matches!(node, None | Some(Js::Null))
}

/// `a ?? b ?? c`: the first operand that is not nullish, else the last.
pub fn first_defined<'a>(operands: &[Option<&'a Js>]) -> Option<&'a Js> {
    for operand in operands {
        if !nullish(*operand) {
            return *operand;
        }
    }
    operands.last().copied().flatten()
}

/// `a || b || c`: the first truthy operand, else the last.
pub fn first_truthy<'a>(operands: &[Option<&'a Js>]) -> Option<&'a Js> {
    for operand in operands {
        if truthy(*operand) {
            return *operand;
        }
    }
    operands.last().copied().flatten()
}

/// `source && source.key`: the member when `source` is truthy, else `source`
/// itself (which is falsy: `undefined`, `null`, `0`, `""`, ...).
pub fn member<'a>(source: Option<&'a Js>, key: &str) -> Option<&'a Js> {
    if truthy(source) { source.and_then(|node| node.get(key)) } else { source }
}

/// `Number(text)`; NaN when the text is not a numeric literal.
pub fn number_from_str(text: &str) -> f64 {
    let text = trim_js(text);
    if text.is_empty() {
        return 0.0;
    }
    let radix = match text.get(..2) {
        Some("0x" | "0X") => Some(16),
        Some("0o" | "0O") => Some(8),
        Some("0b" | "0B") => Some(2),
        _ => None,
    };
    if let Some(radix) = radix {
        let digits = &text[2..];
        if digits.is_empty() {
            return f64::NAN;
        }
        let mut value = 0.0f64;
        for c in digits.chars() {
            match c.to_digit(radix) {
                Some(digit) => value = value * f64::from(radix) + f64::from(digit),
                None => return f64::NAN,
            }
        }
        return value;
    }
    match text {
        "Infinity" | "+Infinity" => return f64::INFINITY,
        "-Infinity" => return f64::NEG_INFINITY,
        _ => {}
    }
    if !text.chars().all(|c| matches!(c, '0'..='9' | '+' | '-' | '.' | 'e' | 'E')) {
        return f64::NAN;
    }
    text.parse::<f64>().unwrap_or(f64::NAN)
}

/// `Number(value)`.
pub fn to_number(node: Option<&Js>) -> f64 {
    match node {
        None => f64::NAN,
        Some(Js::Null) => 0.0,
        Some(Js::Bool(flag)) => f64::from(u8::from(*flag)),
        Some(Js::Num(number)) => *number,
        Some(Js::Big(digits)) => digits.parse().unwrap_or(f64::NAN),
        Some(Js::Str(text)) => number_from_str(text),
        Some(Js::Buf(bytes)) => number_from_str(&String::from_utf8_lossy(bytes)),
        Some(Js::Arr(items)) if items.is_empty() => 0.0,
        Some(Js::Arr(_) | Js::Obj(_)) => f64::NAN,
    }
}

/// `toFiniteNumber(value, fallback)`.
pub fn to_finite(node: Option<&Js>, fallback: f64) -> f64 {
    finite_or(to_number(node), fallback)
}

/// `toInt32(value, fallback)`: `Math.trunc(toFiniteNumber(value, fallback))`.
pub fn to_int32(node: Option<&Js>, fallback: f64) -> f64 {
    to_finite(node, fallback).trunc()
}

/// `buildVector(source, fallback)`: each component `toFiniteNumber(source &&
/// source.c, fallback.c)`, so a `null` source reads as zeros but `undefined`
/// reads as the fallback.
pub fn build_vector(source: Option<&Js>, fallback: Vec3) -> Vec3 {
    Vec3::new(
        to_finite(member(source, "x"), fallback.x),
        to_finite(member(source, "y"), fallback.y),
        to_finite(member(source, "z"), fallback.z),
    )
}

/// `unwrapEntityID`: a `{type: "long" | "PyLong", value}` wrapper gives up its
/// value.
fn unwrap_entity_id(node: &Js) -> Option<&Js> {
    let is_long_wrapper = matches!(node, Js::Obj(_))
        && matches!(node.get("type").and_then(Js::as_str), Some("long" | "PyLong"));
    if is_long_wrapper { node.get("value") } else { Some(node) }
}

fn integer_id(digits: &str) -> IdRef {
    match digits.parse::<i128>() {
        Ok(number) => i64::try_from(number).map_or(IdRef::OutOfRange, IdRef::Valid),
        // Beyond i128 is certainly beyond int64.
        Err(_) => IdRef::OutOfRange,
    }
}

/// `parseEntityIDBigInt`, keeping "out of int64 range" apart from "not an
/// integer" because the wire writers report them differently.
pub fn parse_entity_id(node: &Js) -> IdRef {
    match unwrap_entity_id(node) {
        Some(Js::Big(digits)) => integer_id(digits),
        Some(Js::Num(number)) => {
            if number.is_finite() && number.fract() == 0.0 && number.abs() <= 9_007_199_254_740_991.0 {
                IdRef::Valid(*number as i64)
            } else {
                IdRef::Invalid
            }
        }
        Some(Js::Str(text)) => {
            let text = trim_js(text);
            let digits = text.strip_prefix('-').unwrap_or(text);
            if !digits.is_empty() && digits.bytes().all(|byte| byte.is_ascii_digit()) {
                integer_id(text)
            } else {
                IdRef::Invalid
            }
        }
        _ => IdRef::Invalid,
    }
}

/// An identity field: `undefined`/`null` is absent, `""` is blank.
pub fn id_ref(node: Option<&Js>) -> IdRef {
    match node {
        None | Some(Js::Null) => IdRef::Absent,
        Some(Js::Str(text)) if text.is_empty() => IdRef::Blank,
        Some(other) => parse_entity_id(other),
    }
}

/// `firstDefined(entity, [key])` then a parse: `undefined`, `null` and `""`
/// all mean "not set".
pub fn id_ref_skipping_blank(node: Option<&Js>) -> IdRef {
    match id_ref(node) {
        IdRef::Blank => IdRef::Absent,
        other => other,
    }
}

/// `hasPositiveIdentity`.
pub fn positive_identity(node: Option<&Js>) -> bool {
    node.map(parse_entity_id).and_then(IdRef::valid).is_some_and(|id| id > 0)
}
