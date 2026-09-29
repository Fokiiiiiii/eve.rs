//! Reading the static data the world is built from: JSONL tables, streamed, with records kept as JSON.

use std::collections::HashMap;
use std::path::{Path, PathBuf};

use eve_sde::jsonl::{JsonlError, for_each_record};
use serde_json::Value;

/// Why a table could not be read.
#[derive(Debug)]
pub enum SdeError {
    Read(JsonlError),
    /// A record lacks a field the world needs.
    Missing {
        table: &'static str,
        key: i64,
        field: &'static str,
    },
    /// The system is not in the data.
    UnknownSystem(i32),
}

impl std::fmt::Display for SdeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Read(error) => write!(f, "{error}"),
            Self::Missing { table, key, field } => write!(f, "{table} {key} has no {field}"),
            Self::UnknownSystem(id) => write!(f, "solar system {id} is not in the static data"),
        }
    }
}

impl std::error::Error for SdeError {}

impl From<JsonlError> for SdeError {
    fn from(error: JsonlError) -> Self {
        Self::Read(error)
    }
}

/// A directory of `<table>.jsonl` files (one build of the static data).
#[derive(Debug, Clone)]
pub struct Sde {
    dir: PathBuf,
}

impl Sde {
    pub fn open(dir: impl Into<PathBuf>) -> Self {
        Self { dir: dir.into() }
    }

    /// The static data of this checkout or the machine: `EVE_SDE_DIR`, else the newest `.cache/sde/*jsonl` found at or above the
    /// working directory. None when there is none.
    pub fn locate() -> Option<Self> {
        if let Some(dir) = std::env::var_os("EVE_SDE_DIR") {
            let dir = PathBuf::from(dir);
            return dir.join("_sde.jsonl").exists().then(|| Self::open(dir));
        }
        let mut here = std::env::current_dir().ok()?;
        loop {
            let cache = here.join(".cache").join("sde");
            if let Ok(entries) = std::fs::read_dir(&cache) {
                let mut found: Vec<PathBuf> = entries
                    .filter_map(Result::ok)
                    .map(|e| e.path())
                    .filter(|p| p.join("_sde.jsonl").exists())
                    .collect();
                found.sort();
                if let Some(dir) = found.pop() {
                    return Some(Self::open(dir));
                }
            }
            if !here.pop() {
                return None;
            }
        }
    }

    pub fn dir(&self) -> &Path {
        &self.dir
    }

    /// Calls `f(key, record)` for every record of `table` whose line contains `needle` (a cheap filter before parsing; `None`
    /// reads every record).
    pub fn scan(
        &self,
        table: &str,
        needle: Option<&str>,
        mut f: impl FnMut(i64, &Value),
    ) -> Result<(), SdeError> {
        let path = self.dir.join(format!("{table}.jsonl"));
        let file = std::fs::File::open(&path).map_err(|e| SdeError::Read(JsonlError::Io(e)))?;
        use std::io::{BufRead, BufReader};
        for (index, line) in BufReader::new(file).lines().enumerate() {
            let line_number = index as u64 + 1;
            let line = line.map_err(|e| SdeError::Read(JsonlError::Io(e)))?;
            if line.trim().is_empty() || needle.is_some_and(|n| !line.contains(n)) {
                continue;
            }
            let record: Value = serde_json::from_str(&line)
                .map_err(|source| SdeError::Read(JsonlError::Json { line: line_number, source }))?;
            let Some(key) = record.get("_key").and_then(Value::as_i64) else {
                return Err(SdeError::Read(JsonlError::MissingKey { line: line_number }));
            };
            f(key, &record);
        }
        Ok(())
    }

    /// Every record of a table by key.
    pub fn table(&self, table: &str) -> Result<HashMap<i64, Value>, SdeError> {
        let path = self.dir.join(format!("{table}.jsonl"));
        let mut all = HashMap::new();
        for_each_record::<SdeError>(&path, |_, record| {
            if let Some(key) = record.get("_key").and_then(Value::as_i64) {
                all.insert(key, record);
            }
            Ok(())
        })?;
        Ok(all)
    }
}

/// The English text of a localized name (`{"en": ..}`).
pub fn english(name: &Value) -> String {
    name.get("en").and_then(Value::as_str).unwrap_or_default().to_string()
}

pub fn i(record: &Value, field: &str) -> Option<i64> {
    record.get(field).and_then(Value::as_i64)
}

pub fn f(record: &Value, field: &str) -> Option<f64> {
    record.get(field).and_then(Value::as_f64)
}
