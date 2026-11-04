//! The JS value graph the golden fixtures describe, and its EJSON encoding
//! (see `tools/oracle/ejson.js` for the format).

use serde_json::{Map, Value as Json};

/// A JavaScript value as the marshal reference sees it.
#[derive(Debug, Clone, PartialEq)]
pub enum Js {
    Null,
    Bool(bool),

    Num(f64),
    /// A BigInt, as its decimal digits.
    Big(String),
    Str(String),
    /// A Buffer.
    Buf(Vec<u8>),
    Arr(Vec<Js>),
    /// A plain object; `undefined` fields are absent.
    Obj(Vec<(String, Js)>),
}

impl Js {
    pub fn obj(fields: impl IntoIterator<Item = (&'static str, Js)>) -> Js {
        Js::Obj(fields.into_iter().map(|(name, value)| (name.to_owned(), value)).collect())
    }

    /// A field of an object, if this is an object that has it.
    pub fn get(&self, name: &str) -> Option<&Js> {
        match self {
            Js::Obj(fields) => fields.iter().find(|(key, _)| key == name).map(|(_, value)| value),
            _ => None,
        }
    }

    pub fn as_str(&self) -> Option<&str> {
        match self {
            Js::Str(text) => Some(text),
            _ => None,
        }
    }
}

fn hex_decode(text: &str) -> Result<Vec<u8>, String> {
    if !text.len().is_multiple_of(2) {
        return Err(format!("odd-length hex: {text}"));
    }
    (0..text.len())
        .step_by(2)
        .map(|at| {
            u8::from_str_radix(&text[at..at + 2], 16).map_err(|error| format!("bad hex {text}: {error}"))
        })
        .collect()
}

pub fn hex_encode(bytes: &[u8]) -> String {
    const DIGITS: &[u8; 16] = b"0123456789abcdef";
    let mut text = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        text.push(DIGITS[usize::from(byte >> 4)] as char);
        text.push(DIGITS[usize::from(byte & 15)] as char);
    }
    text
}

pub fn hex_to_bytes(text: &str) -> Result<Vec<u8>, String> {
    hex_decode(text)
}

/// Parse an EJSON node.
pub fn from_ejson(node: &Json) -> Result<Js, String> {
    Ok(match node {
        Json::Null => Js::Null,
        Json::Bool(flag) => Js::Bool(*flag),
        Json::String(text) => Js::Str(text.clone()),
        Json::Array(items) => Js::Arr(items.iter().map(from_ejson).collect::<Result<_, _>>()?),
        Json::Number(_) => return Err("bare JSON number in EJSON".into()),
        Json::Object(map) => {
            if map.len() != 1 {
                return Err(format!("bad EJSON tag: {:?}", map.keys().collect::<Vec<_>>()));
            }
            let (tag, body) = map.iter().next().expect("one entry");
            let text = || body.as_str().ok_or_else(|| format!("{tag} needs a string"));
            match tag.as_str() {
                "$int" => Js::Num(text()?.parse::<i64>().map_err(|error| error.to_string())? as f64),
                "$f64" => {
                    let bits = u64::from_str_radix(text()?, 16).map_err(|error| error.to_string())?;
                    Js::Num(f64::from_bits(bits))
                }
                "$big" => Js::Big(text()?.to_owned()),
                "$buf" => Js::Buf(hex_decode(text()?)?),
                "$obj" => {
                    let Json::Object(fields) = body else {
                        return Err("$obj needs an object".into());
                    };
                    Js::Obj(
                        fields
                            .iter()
                            .map(|(name, value)| Ok((name.clone(), from_ejson(value)?)))
                            .collect::<Result<_, String>>()?,
                    )
                }
                other => return Err(format!("unknown EJSON tag {other}")),
            }
        }
    })
}

fn tag(name: &str, body: Json) -> Json {
    let mut map = Map::new();
    map.insert(name.to_owned(), body);
    Json::Object(map)
}

/// Render as EJSON: safe-integer numbers as `$int`, every other number as the
/// exact bits.
pub fn to_ejson(value: &Js) -> Json {
    match value {
        Js::Null => Json::Null,
        Js::Bool(flag) => Json::Bool(*flag),
        Js::Str(text) => Json::String(text.clone()),
        Js::Num(number) => {
            let safe = number.is_finite()
                && number.fract() == 0.0
                && number.abs() <= 9_007_199_254_740_991.0
                && !(*number == 0.0 && number.is_sign_negative());
            if safe {
                tag("$int", Json::String(format!("{}", *number as i64)))
            } else {
                tag("$f64", Json::String(format!("{:016x}", number.to_bits())))
            }
        }
        Js::Big(digits) => tag("$big", Json::String(digits.clone())),
        Js::Buf(bytes) => tag("$buf", Json::String(hex_encode(bytes))),
        Js::Arr(items) => Json::Array(items.iter().map(to_ejson).collect()),
        Js::Obj(fields) => {
            let mut map = Map::new();
            for (name, field) in fields {
                map.insert(name.clone(), to_ejson(field));
            }
            tag("$obj", Json::Object(map))
        }
    }
}
