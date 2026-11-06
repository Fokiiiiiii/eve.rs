//! Streaming reader for JSON Lines files.

use std::fs::File;
use std::io::{BufRead, BufReader};
use std::path::Path;

use serde_json::Value;

/// A failure reading a JSONL file, with the 1-based line it happened on.
#[derive(Debug)]
pub enum JsonlError {
    Io(std::io::Error),
    Json {
        line: u64,
        source: serde_json::Error,
    },
    /// The record has no usable `_key`.
    MissingKey {
        line: u64,
    },
}

impl std::fmt::Display for JsonlError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Io(error) => write!(f, "{error}"),
            Self::Json { line, source } => write!(f, "line {line}: {source}"),
            Self::MissingKey { line } => write!(f, "line {line}: record has no `_key`"),
        }
    }
}

impl std::error::Error for JsonlError {}

impl From<std::io::Error> for JsonlError {
    fn from(error: std::io::Error) -> Self {
        Self::Io(error)
    }
}

/// Call `on_record(line, record)` for every non-blank line, streaming: the file
/// is never held in memory. Returns the number of records.
pub fn for_each_record<E>(
    path: &Path,
    mut on_record: impl FnMut(u64, Value) -> Result<(), E>,
) -> Result<u64, E>
where
    E: From<JsonlError>,
{
    let file = File::open(path).map_err(|error| E::from(JsonlError::Io(error)))?;
    let mut count = 0;
    for (index, line) in BufReader::new(file).lines().enumerate() {
        let line_number = index as u64 + 1;
        let line = line.map_err(|error| E::from(JsonlError::Io(error)))?;
        let text = line.trim();
        if text.is_empty() {
            continue;
        }
        let value = serde_json::from_str(text)
            .map_err(|source| E::from(JsonlError::Json { line: line_number, source }))?;
        on_record(line_number, value)?;
        count += 1;
    }
    Ok(count)
}

/// The record's `_key` as text: a number in decimal, a string as is.
pub fn key_of(record: &Value) -> Option<String> {
    match record.get("_key")? {
        Value::Number(number) => Some(number.to_string()),
        Value::String(text) => Some(text.clone()),
        _ => None,
    }
}
