//! The values a behavior tree passes around: what the Python package keeps in blackboard channels, task attributes and event arguments.
//!
//! Ported from the client's `behaviors` package (V24.01). Python's dynamic values (`None`, numbers, strings, tuples, lists, sets, dicts) are one enum.

use std::fmt;

/// A dynamic value.
#[derive(Debug, Clone, PartialEq, Default)]
pub enum Value {
    #[default]
    None,
    Bool(bool),
    Int(i64),
    Float(f64),
    Str(String),
    Tuple(Vec<Value>),
    List(Vec<Value>),
    /// A set (no two equal entries, in the order they came in).
    Set(Vec<Value>),
    Dict(Vec<(Value, Value)>),
}

impl Value {
    pub fn is_none(&self) -> bool {
        matches!(self, Value::None)
    }

    /// Python truthiness.
    pub fn truthy(&self) -> bool {
        match self {
            Value::None => false,
            Value::Bool(b) => *b,
            Value::Int(i) => *i != 0,
            Value::Float(f) => *f != 0.0,
            Value::Str(s) => !s.is_empty(),
            Value::Tuple(v) | Value::List(v) | Value::Set(v) => !v.is_empty(),
            Value::Dict(d) => !d.is_empty(),
        }
    }

    pub fn as_int(&self) -> Option<i64> {
        match self {
            Value::Int(i) => Some(*i),
            Value::Bool(b) => Some(i64::from(*b)),
            Value::Float(f) if f.fract() == 0.0 => Some(*f as i64),
            _ => None,
        }
    }

    pub fn as_float(&self) -> Option<f64> {
        match self {
            Value::Float(f) => Some(*f),
            Value::Int(i) => Some(*i as f64),
            Value::Bool(b) => Some(f64::from(u8::from(*b))),
            _ => None,
        }
    }

    pub fn as_str(&self) -> Option<&str> {
        match self {
            Value::Str(s) => Some(s),
            _ => None,
        }
    }

    pub fn as_bool(&self) -> Option<bool> {
        match self {
            Value::Bool(b) => Some(*b),
            _ => None,
        }
    }

    /// The items of a tuple, list or set.
    pub fn items(&self) -> Option<&[Value]> {
        match self {
            Value::Tuple(v) | Value::List(v) | Value::Set(v) => Some(v),
            _ => None,
        }
    }

    pub fn tuple<const N: usize>(values: [Value; N]) -> Value {
        Value::Tuple(values.into())
    }

    /// A set of these values (duplicates dropped).
    pub fn set(values: impl IntoIterator<Item = Value>) -> Value {
        let mut out: Vec<Value> = Vec::new();
        for v in values {
            if !out.contains(&v) {
                out.push(v);
            }
        }
        Value::Set(out)
    }

    /// `entry in self` for a set, list, tuple or the keys of a dict.
    pub fn contains(&self, entry: &Value) -> bool {
        match self {
            Value::Tuple(v) | Value::List(v) | Value::Set(v) => v.contains(entry),
            Value::Dict(d) => d.iter().any(|(k, _)| k == entry),
            _ => false,
        }
    }

    /// `set.add(entry)`; false when it was there.
    pub fn set_add(&mut self, entry: Value) -> bool {
        match self {
            Value::Set(v) if !v.contains(&entry) => {
                v.push(entry);
                true
            }
            _ => false,
        }
    }

    /// `set.discard(entry)`; whether it was there.
    pub fn set_discard(&mut self, entry: &Value) -> bool {
        match self {
            Value::Set(v) => match v.iter().position(|x| x == entry) {
                Some(at) => {
                    v.remove(at);
                    true
                }
                None => false,
            },
            _ => false,
        }
    }

    /// A position `(x, y, z)`.
    pub fn as_vec3(&self) -> Option<[f64; 3]> {
        match self.items()? {
            [x, y, z] => Some([x.as_float()?, y.as_float()?, z.as_float()?]),
            _ => None,
        }
    }

    pub fn from_vec3(v: [f64; 3]) -> Value {
        Value::Tuple(v.iter().map(|f| Value::Float(*f)).collect())
    }
}

impl From<i64> for Value {
    fn from(v: i64) -> Self {
        Value::Int(v)
    }
}

impl From<f64> for Value {
    fn from(v: f64) -> Self {
        Value::Float(v)
    }
}

impl From<bool> for Value {
    fn from(v: bool) -> Self {
        Value::Bool(v)
    }
}

impl From<&str> for Value {
    fn from(v: &str) -> Self {
        Value::Str(v.to_string())
    }
}

impl fmt::Display for Value {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Value::None => write!(f, "None"),
            Value::Bool(b) => write!(f, "{}", if *b { "True" } else { "False" }),
            Value::Int(i) => write!(f, "{i}"),
            Value::Float(x) => write!(f, "{x}"),
            Value::Str(s) => write!(f, "'{s}'"),
            Value::Tuple(v) => write_seq(f, "(", ")", v),
            Value::List(v) => write_seq(f, "[", "]", v),
            Value::Set(v) => write_seq(f, "{", "}", v),
            Value::Dict(d) => {
                write!(f, "{{")?;
                for (i, (k, v)) in d.iter().enumerate() {
                    if i > 0 {
                        write!(f, ", ")?;
                    }
                    write!(f, "{k}: {v}")?;
                }
                write!(f, "}}")
            }
        }
    }
}

fn write_seq(f: &mut fmt::Formatter<'_>, open: &str, close: &str, v: &[Value]) -> fmt::Result {
    write!(f, "{open}")?;
    for (i, x) in v.iter().enumerate() {
        if i > 0 {
            write!(f, ", ")?;
        }
        write!(f, "{x}")?;
    }
    write!(f, "{close}")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn truthiness_is_pythons() {
        assert!(!Value::None.truthy() && !Value::Int(0).truthy() && !Value::Str(String::new()).truthy());
        assert!(
            Value::Int(-1).truthy()
                && Value::Str("a".into()).truthy()
                && Value::Set(vec![Value::None]).truthy()
        );
        assert!(!Value::Set(vec![]).truthy() && !Value::Float(0.0).truthy());
    }

    #[test]
    fn a_set_keeps_one_of_each() {
        let mut s = Value::set([Value::Int(1), Value::Int(1), Value::Int(2)]);
        assert_eq!(s, Value::Set(vec![Value::Int(1), Value::Int(2)]));
        assert!(!s.set_add(Value::Int(2)) && s.set_add(Value::Int(3)));
        assert!(s.set_discard(&Value::Int(1)) && !s.set_discard(&Value::Int(1)));
        assert!(s.contains(&Value::Int(3)));
    }

    #[test]
    fn a_position_is_three_floats() {
        let p = Value::from_vec3([1.0, 2.0, 3.5]);
        assert_eq!(p.as_vec3(), Some([1.0, 2.0, 3.5]));
        assert_eq!(Value::tuple([Value::Int(1), Value::Int(2)]).as_vec3(), None);
    }
}
