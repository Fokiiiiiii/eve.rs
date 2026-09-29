//! Small constructors for the values a destiny payload is made of, typed the way the client saw them from the reference:
//! a whole number is an int (a long beyond 32 bits), a fraction is a real, and what the reference always sends as a real
//! stays one however whole it is (`1.0`).

use std::borrow::Cow;

use eve_marshal::Value;

/// A name or a piece of text.
pub fn text(text: impl Into<String>) -> Value {
    Value::Str(Cow::Owned(text.into()))
}

/// A dictionary key or a fixed word.
pub fn word(word: &'static str) -> Value {
    Value::Str(Cow::Borrowed(word))
}

/// A whole number: an int, or a long when it does not fit 32 bits (what JavaScript's marshal does).
pub fn id(value: i64) -> Value {
    if i32::try_from(value).is_ok() { Value::Int(value) } else { Value::Long(value) }
}

pub fn int(value: i64) -> Value {
    Value::Int(value)
}

/// A number the client reads as a real, whole or not.
pub fn real(value: f64) -> Value {
    Value::Real(value)
}

pub fn tuple(items: impl IntoIterator<Item = Value>) -> Value {
    Value::Tuple(items.into_iter().collect())
}

pub fn list(items: impl IntoIterator<Item = Value>) -> Value {
    Value::List(items.into_iter().collect())
}

/// A dictionary with text keys, in the order given.
pub fn dict(entries: impl IntoIterator<Item = (&'static str, Value)>) -> Value {
    Value::Dict(entries.into_iter().map(|(k, v)| (word(k), v)).collect())
}

/// A `util.KeyVal`: a dictionary the client reads by attribute.
pub fn key_val(entries: impl IntoIterator<Item = (&'static str, Value)>) -> Value {
    Value::Object { name: Box::new(word("util.KeyVal")), args: Box::new(dict(entries)) }
}

/// A `foo.SlimItem`.
pub fn slim_object(entries: impl IntoIterator<Item = (&'static str, Value)>) -> Value {
    Value::Object { name: Box::new(word("foo.SlimItem")), args: Box::new(dict(entries)) }
}

/// A FILETIME (100 ns ticks since 1601), always a long.
pub fn filetime(ticks: i64) -> Value {
    Value::Long(ticks)
}

/// Three reals (a direction or a point).
pub fn vector(x: f64, y: f64, z: f64) -> [Value; 3] {
    [real(x), real(y), real(z)]
}
