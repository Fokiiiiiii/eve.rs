//! JavaScript number semantics the marshal reference relies on.
//!
//! The packed-row encoder accepts loosely typed input and coerces it with
//! `Number(...)`, `Math.trunc`, `>>> 0` and `BigInt(...)`. These helpers
//! reproduce those coercions so the Rust codec writes the same bytes.

use crate::value::Value;

const MAX_SAFE_INTEGER: f64 = 9_007_199_254_740_991.0;

pub fn is_safe_integer(value: f64) -> bool {
    value.is_finite() && value.fract() == 0.0 && value.abs() <= MAX_SAFE_INTEGER
}

/// `value >>> 0` (ToUint32).
pub fn to_uint32(value: f64) -> u32 {
    if !value.is_finite() {
        return 0;
    }
    value.trunc().rem_euclid(4_294_967_296.0) as u32
}

fn is_js_whitespace(c: char) -> bool {
    matches!(
        c,
        '\t' | '\n' | '\u{b}' | '\u{c}' | '\r' | ' ' | '\u{a0}' | '\u{1680}' | '\u{2000}'
            ..='\u{200a}' | '\u{2028}' | '\u{2029}' | '\u{202f}' | '\u{205f}' | '\u{3000}' | '\u{feff}'
    )
}

pub fn trim_js(text: &str) -> &str {
    text.trim_matches(is_js_whitespace)
}

fn parse_radix_prefixed(text: &str) -> Option<(u32, &str)> {
    let mut chars = text.chars();
    if chars.next()? != '0' {
        return None;
    }
    let radix = match chars.next()? {
        'x' | 'X' => 16,
        'o' | 'O' => 8,
        'b' | 'B' => 2,
        _ => return None,
    };
    Some((radix, &text[2..]))
}

/// `Number(string)`; NaN when the text is not a numeric literal.
pub fn number_from_str(text: &str) -> f64 {
    let text = trim_js(text);
    if text.is_empty() {
        return 0.0;
    }
    if let Some((radix, digits)) = parse_radix_prefixed(text) {
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
    // Rust also parses "inf", "nan" and friends; JS does not.
    if !text.chars().all(|c| matches!(c, '0'..='9' | '+' | '-' | '.' | 'e' | 'E')) {
        return f64::NAN;
    }
    text.parse::<f64>().unwrap_or(f64::NAN)
}

/// `BigInt(string)` for values that fit in an `i128`; `None` when JS would
/// throw a SyntaxError (or the value is out of `i128` range).
pub fn bigint_from_str(text: &str) -> Option<i128> {
    let text = trim_js(text);
    if text.is_empty() {
        return Some(0);
    }
    if let Some((radix, digits)) = parse_radix_prefixed(text) {
        return i128::from_str_radix(digits, radix).ok();
    }
    let (negative, digits) = match text.as_bytes()[0] {
        b'-' => (true, &text[1..]),
        b'+' => (false, &text[1..]),
        _ => (false, text),
    };
    if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let magnitude: i128 = digits.parse().ok()?;
    Some(if negative { -magnitude } else { magnitude })
}

fn utf8_lossy(bytes: &[u8]) -> std::borrow::Cow<'_, str> {
    String::from_utf8_lossy(bytes)
}

/// `Number(value)` for a decoded value (what the virtual-column resolvers use).
pub fn value_to_number(value: &Value) -> f64 {
    match value {
        Value::None => 0.0,
        Value::Bool(flag) => f64::from(u8::from(*flag)),
        Value::Int(number) | Value::Long(number) => *number as f64,
        Value::Real(number) => *number,
        Value::BigUnsigned(bytes) => bytes.iter().rev().fold(0.0, |acc, &b| acc * 256.0 + f64::from(b)),
        Value::Str(text) | Value::WString(text) | Value::Token(text) => number_from_str(text),
        Value::Bytes(bytes) | Value::Buffer(bytes) => number_from_str(&utf8_lossy(bytes)),
        _ => f64::NAN,
    }
}
