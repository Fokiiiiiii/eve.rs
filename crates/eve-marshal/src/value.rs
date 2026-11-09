use std::borrow::Cow;
use std::fmt;
use std::sync::{Arc, RwLock, Weak};

pub use bytes::Bytes;

pub use crate::packed_row::{Descriptor, PackedColumn, PackedRow};

/// A marshal value.
///
/// One type serves both directions. The decoder never produces a variant the
/// encoder cannot write back, except [`Value::Checksummed`] (which the encoder
/// also writes) and [`Value::BigUnsigned`] / [`Value::BigSigned`] (variable-width
/// integers beyond 2^53, written back as variable-width integers).
///
/// The JS reference collapses several wire types into one JS type (every
/// string opcode decodes to a `Buffer`, every small integer to a `number`).
/// This model keeps them apart because the distinction is meaningful on the
/// wire: [`Value::Bytes`] is a Python `str`, [`Value::Buffer`] a `buffer`,

#[derive(Debug, Clone, PartialEq)]
pub enum Value {
    None,
    Bool(bool),
    /// An integer written with the smallest of PyByte / PySignedShort / PyLong
    /// / PyLongLong (and PyMinusOne / PyZeroInteger / PyOneInteger). Beyond
    /// `i32` it goes out as PyLongLong and decodes as [`Value::Long`].
    Int(i64),
    /// Always written as PyLongLong, however small.
    Long(i64),
    /// PyReal; a zero is written as the one-byte PyZeroReal.
    Real(f64),
    /// A variable-width integer that does not fit in a JS safe integer, as
    /// little-endian unsigned magnitude bytes.
    BigUnsigned(Vec<u8>),
    /// A negative variable-width integer that does not fit in a JS safe
    /// integer, as minimal little-endian two's-complement bytes.
    BigSigned(Vec<u8>),

    Str(Cow<'static, str>),
    /// A Python `str`: PyEmptyString, PyCharString, PyShortString or
    /// PyLongString.
    Bytes(Bytes),
    /// A Python `buffer`: PyBuffer.
    Buffer(Bytes),

    WString(Cow<'static, str>),
    Token(Cow<'static, str>),
    Tuple(Vec<Value>),
    List(Vec<Value>),
    /// Entries in wire order. The wire stores each pair value first, then key.
    Dict(Vec<(Value, Value)>),
    Object {
        name: Box<Value>,
        args: Box<Value>,
    },
    ObjectEx {
        kind: ObjectExKind,
        header: Box<Value>,
        list: Vec<Value>,
        dict: Vec<(Value, Value)>,
    },
    SubStruct(Box<Value>),
    SubStream(SubStream),
    Checksummed {
        checksum: u32,
        value: Box<Value>,
    },
    /// Raw Python pickle bytes (cPicked).
    CPickled(Bytes),
    PackedRow(Box<PackedRow>),
    /// A graph edge to a saved value. This keeps reference identity, including
    /// cycles, without recursively owning the target value.
    SavedReference(SavedReference),
    /// Keeps the save slots alive for references in `root`. References point
    /// back to these slots weakly, so cyclic marshal graphs do not leak.
    SavedGraph(SavedGraph),
}

pub(crate) type SaveSlot = Arc<RwLock<Option<Value>>>;

/// A reference to a value registered in the marshal save table.
#[derive(Clone)]
pub struct SavedReference {
    index: usize,
    target: Weak<RwLock<Option<Value>>>,
}

impl SavedReference {
    pub(crate) fn new(index: usize, target: &SaveSlot) -> Self {
        Self { index, target: Arc::downgrade(target) }
    }

    pub fn index(&self) -> usize {
        self.index
    }

    /// The saved value when its slot has been published.
    pub fn value(&self) -> Option<Value> {
        self.target.upgrade()?.read().expect("saved value lock poisoned").clone()
    }

    pub(crate) fn publish(&self, value: Value) {
        *self
            .target
            .upgrade()
            .expect("save slot remains owned during decode")
            .write()
            .expect("saved value lock poisoned") = Some(value);
    }

    pub(crate) fn is_same(&self, other: &Self) -> bool {
        Weak::ptr_eq(&self.target, &other.target)
    }
}

/// The root value and the save slots that own values referenced from it.
#[derive(Clone)]
pub struct SavedGraph {
    root: Box<Value>,
    slots: Arc<Vec<Option<SaveSlot>>>,
}

impl SavedGraph {
    pub(crate) fn new(root: Value, slots: Vec<Option<SaveSlot>>) -> Self {
        Self { root: Box::new(root), slots: Arc::new(slots) }
    }

    pub fn root(&self) -> &Value {
        &self.root
    }

    pub fn saved_slot_count(&self) -> usize {
        self.slots.len()
    }
}

impl fmt::Debug for SavedGraph {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.debug_tuple("SavedGraph").field(&self.root).finish()
    }
}

impl PartialEq for SavedGraph {
    fn eq(&self, other: &Self) -> bool {
        self.root == other.root
    }
}

impl fmt::Debug for SavedReference {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.debug_tuple("SavedReference").field(&self.index).finish()
    }
}

impl PartialEq for SavedReference {
    fn eq(&self, other: &Self) -> bool {
        self.is_same(other)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ObjectExKind {
    Ex1,

    Ex2,
}

#[derive(Debug, Clone, PartialEq)]
pub enum SubStream {
    /// A nested marshal stream that decoded (or is to be encoded) as a value.
    Decoded(Box<Value>),
    /// A nested stream that failed to decode; the raw bytes are kept.
    Raw(Bytes),
}

impl Value {
    /// A string from a literal, without allocating.
    pub fn str(text: &'static str) -> Self {
        Self::Str(Cow::Borrowed(text))
    }

    /// A string from owned text.
    pub fn string(text: String) -> Self {
        Self::Str(Cow::Owned(text))
    }

    pub fn dict<K, V>(entries: impl IntoIterator<Item = (K, V)>) -> Self
    where
        K: Into<Value>,
        V: Into<Value>,
    {
        Self::Dict(entries.into_iter().map(|(k, v)| (k.into(), v.into())).collect())
    }

    /// A Python `set`. The protocol has no set opcode: `set.__reduce__` gives
    /// `(set, (list(items),))`, so the wire is a PyObjectEx1 whose header is
    /// the token `__builtin__.set` and a tuple holding the list (the JS
    /// reference's `buildPythonSet`, and the client's unpickling).
    pub fn python_set(items: Vec<Value>) -> Self {
        Self::ObjectEx {
            kind: ObjectExKind::Ex1,
            header: Box::new(Self::Tuple(vec![
                Self::Token(Cow::Borrowed("__builtin__.set")),
                Self::Tuple(vec![Self::List(items)]),
            ])),
            list: Vec::new(),
            dict: Vec::new(),
        }
    }
}

impl From<i64> for Value {
    fn from(value: i64) -> Self {
        Self::Int(value)
    }
}

impl From<i32> for Value {
    fn from(value: i32) -> Self {
        Self::Int(i64::from(value))
    }
}

impl From<f64> for Value {
    fn from(value: f64) -> Self {
        Self::Real(value)
    }
}

impl From<bool> for Value {
    fn from(value: bool) -> Self {
        Self::Bool(value)
    }
}

impl From<&'static str> for Value {
    fn from(value: &'static str) -> Self {
        Self::Str(Cow::Borrowed(value))
    }
}

impl From<String> for Value {
    fn from(value: String) -> Self {
        Self::Str(Cow::Owned(value))
    }
}
