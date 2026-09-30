//! The values a behavior tree passes around: what the Python package keeps in blackboard channels, task attributes and event arguments.
//!
//! Ported from the client's `behaviors` package (V24.01). Python's dynamic values (`None`, numbers, strings, tuples, lists, sets, dicts) are one enum.

use std::cmp::Ordering;
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
            if !out.iter().any(|x| x.py_eq(&v)) {
                out.push(v);
            }
        }
        Value::Set(out)
    }

    /// `entry in self` for a set, list, tuple or the keys of a dict.
    pub fn contains(&self, entry: &Value) -> bool {
        match self {
            Value::Tuple(v) | Value::List(v) | Value::Set(v) => v.iter().any(|x| x.py_eq(entry)),
            Value::Dict(d) => d.iter().any(|(k, _)| k.py_eq(entry)),
            _ => false,
        }
    }

    /// `set.add(entry)`; false when it was there.
    pub fn set_add(&mut self, entry: Value) -> bool {
        match self {
            Value::Set(v) if !v.iter().any(|x| x.py_eq(&entry)) => {
                v.push(entry);
                true
            }
            _ => false,
        }
    }

    /// `set.discard(entry)`; whether it was there.
    pub fn set_discard(&mut self, entry: &Value) -> bool {
        match self {
            Value::Set(v) => match v.iter().position(|x| x.py_eq(entry)) {
                Some(at) => {
                    v.remove(at);
                    true
                }
                None => false,
            },
            _ => false,
        }
    }

    /// `len(value)` of a string, tuple, list, set or dict.
    pub fn len(&self) -> Option<usize> {
        match self {
            Value::Str(s) => Some(s.chars().count()),
            Value::Tuple(v) | Value::List(v) | Value::Set(v) => Some(v.len()),
            Value::Dict(d) => Some(d.len()),
            _ => None,
        }
    }

    pub fn is_empty(&self) -> bool {
        self.len() == Some(0)
    }

    /// `dict.get(key)` of a dict, by Python equality of the keys.
    pub fn dict_get(&self, key: &Value) -> Option<&Value> {
        match self {
            Value::Dict(entries) => entries.iter().find(|(k, _)| k.py_eq(key)).map(|(_, v)| v),
            _ => None,
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

    /// Python 2's order of two values (`<`, `>` and the like never raise there): `None` is below everything, numbers (booleans too) compare by
    /// value and are below everything else, the rest go by the name of their type (`dict`, `list`, `set`, `str`, `tuple`) and then by content.
    pub fn py_cmp(&self, other: &Value) -> Ordering {
        fn rank(v: &Value) -> (u8, &'static str) {
            match v {
                Value::None => (0, ""),
                Value::Bool(_) | Value::Int(_) | Value::Float(_) => (1, ""),
                Value::Dict(_) => (2, "dict"),
                Value::List(_) => (2, "list"),
                Value::Set(_) => (2, "set"),
                Value::Str(_) => (2, "str"),
                Value::Tuple(_) => (2, "tuple"),
            }
        }
        let (a, b) = (rank(self), rank(other));
        if a != b {
            return a.cmp(&b);
        }
        match (self, other) {
            (Value::None, Value::None) => Ordering::Equal,
            (Value::Int(x), Value::Int(y)) => x.cmp(y),
            (x, y) if a.0 == 1 => {
                let (x, y) = (x.as_float().unwrap_or(0.0), y.as_float().unwrap_or(0.0));
                x.partial_cmp(&y).unwrap_or(Ordering::Equal)
            }
            (Value::Str(x), Value::Str(y)) => x.as_bytes().cmp(y.as_bytes()),
            (Value::List(x), Value::List(y)) | (Value::Tuple(x), Value::Tuple(y)) => {
                for (p, q) in x.iter().zip(y) {
                    match p.py_cmp(q) {
                        Ordering::Equal => {}
                        other => return other,
                    }
                }
                x.len().cmp(&y.len())
            }
            (Value::Set(x), Value::Set(y)) => x.len().cmp(&y.len()),
            (Value::Dict(x), Value::Dict(y)) => x.len().cmp(&y.len()),
            _ => Ordering::Equal,
        }
    }

    /// `self < other` in Python 2.
    pub fn py_lt(&self, other: &Value) -> bool {
        self.py_cmp(other) == Ordering::Less
    }

    /// `self <= other` in Python 2.
    pub fn py_le(&self, other: &Value) -> bool {
        self.py_cmp(other) != Ordering::Greater
    }

    /// `self > other` in Python 2.
    pub fn py_gt(&self, other: &Value) -> bool {
        self.py_cmp(other) == Ordering::Greater
    }

    /// `self >= other` in Python 2.
    pub fn py_ge(&self, other: &Value) -> bool {
        self.py_cmp(other) != Ordering::Less
    }

    /// Python's `==` between values: numbers compare by value whatever their type (`1 == 1.0 == True`).
    pub fn py_eq(&self, other: &Value) -> bool {
        match (self, other) {
            (
                Value::Bool(_) | Value::Int(_) | Value::Float(_),
                Value::Bool(_) | Value::Int(_) | Value::Float(_),
            ) => self.py_cmp(other) == Ordering::Equal,
            (Value::List(x), Value::List(y)) | (Value::Tuple(x), Value::Tuple(y)) => {
                x.len() == y.len() && x.iter().zip(y).all(|(p, q)| p.py_eq(q))
            }
            _ => self == other,
        }
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
    fn python_two_orders_none_below_numbers_below_the_rest_and_the_rest_by_type_name() {
        assert!(Value::None.py_lt(&Value::Int(-5)) && Value::None.py_lt(&Value::Str(String::new())));
        assert!(
            Value::Int(3).py_lt(&Value::Float(3.5))
                && Value::Bool(true).py_eq(&Value::Int(1))
                && Value::Int(1).py_eq(&Value::Float(1.0))
        );
        assert!(
            Value::Int(1_000_000).py_lt(&Value::Str("a".into()))
                && Value::Str("zz".into()).py_lt(&Value::Tuple(vec![]))
        );
        assert!(
            Value::List(vec![Value::Int(9)]).py_lt(&Value::Str("a".into()))
                && Value::Str("a".into()).py_lt(&Value::Str("b".into()))
        );
        assert!(
            Value::Tuple(vec![Value::Int(1), Value::Int(2)])
                .py_lt(&Value::Tuple(vec![Value::Int(1), Value::Int(3)]))
        );
        assert!(Value::Tuple(vec![Value::Int(1)]).py_lt(&Value::Tuple(vec![Value::Int(1), Value::Int(0)])));
        assert!(
            Value::None.py_le(&Value::None)
                && !Value::None.py_lt(&Value::None)
                && Value::Int(2).py_ge(&Value::Int(2))
        );
        assert!(Value::Int(5).py_gt(&Value::None));
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
