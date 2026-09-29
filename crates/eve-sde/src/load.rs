//! Loading an SDE directory into PostgreSQL.
//!
//! Every file becomes `sde."<table>"(ord bigint, key text primary key, doc jsonb)`:
//! `ord` is the record's position in the file, `key` its `_key` as text and
//! `doc` the whole record. A build is loaded in one transaction, replacing the
//! tables of the previous load, and recorded in `sde."_build"`.

use std::io::Write;

use postgres::Client;
use serde_json::Value;

use crate::jsonl::{self, JsonlError};
use crate::manifest::SdeManifest;

#[derive(Debug)]
pub enum LoadError {
    Jsonl(JsonlError),
    Postgres(postgres::Error),
    Io(std::io::Error),
    /// A file has two records with the same `_key`.
    DuplicateKey {
        table: String,
        key: String,
    },
}

impl std::fmt::Display for LoadError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Jsonl(error) => write!(f, "{error}"),
            Self::Postgres(error) => write!(f, "postgres: {error}"),
            Self::Io(error) => write!(f, "{error}"),
            Self::DuplicateKey { table, key } => write!(f, "{table}: duplicate _key {key}"),
        }
    }
}

impl std::error::Error for LoadError {}

impl From<JsonlError> for LoadError {
    fn from(error: JsonlError) -> Self {
        Self::Jsonl(error)
    }
}
impl From<postgres::Error> for LoadError {
    fn from(error: postgres::Error) -> Self {
        Self::Postgres(error)
    }
}
impl From<std::io::Error> for LoadError {
    fn from(error: std::io::Error) -> Self {
        Self::Io(error)
    }
}

/// What a load did, per table.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LoadReport {
    pub build_number: Option<i64>,
    pub tables: Vec<(String, u64)>,
}

/// Escape `text` for a field of PostgreSQL's COPY text format.
pub fn copy_escape(text: &str, out: &mut String) {
    for c in text.chars() {
        match c {
            '\\' => out.push_str("\\\\"),
            '\t' => out.push_str("\\t"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            _ => out.push(c),
        }
    }
}

fn quote_ident(name: &str) -> String {
    // Table names were validated as [A-Za-z0-9_]+, so quoting cannot be escaped.
    format!("\"{name}\"")
}

/// The schema the tables go to unless told otherwise.
pub const DEFAULT_SCHEMA: &str = "sde";

/// Load every table of `manifest` into schema `sde` in one transaction.
pub fn load(client: &mut Client, manifest: &SdeManifest) -> Result<LoadReport, LoadError> {
    load_into(client, manifest, DEFAULT_SCHEMA)
}

/// [`load`] into a schema of your choosing (letters, digits and underscores).
pub fn load_into(client: &mut Client, manifest: &SdeManifest, schema: &str) -> Result<LoadReport, LoadError> {
    assert!(crate::manifest::is_valid_table_name(schema), "invalid schema name {schema:?}");
    let mut tx = client.transaction()?;
    tx.batch_execute(&format!("CREATE SCHEMA IF NOT EXISTS {schema}"))?;
    let mut tables = Vec::new();

    for file in &manifest.files {
        let table = quote_ident(&file.table);
        tx.batch_execute(&format!(
            "DROP TABLE IF EXISTS {schema}.{table}; \
             CREATE TABLE {schema}.{table} (ord bigint NOT NULL, key text NOT NULL PRIMARY KEY, doc jsonb NOT NULL)"
        ))?;

        let mut writer = tx.copy_in(&format!("COPY {schema}.{table} (ord, key, doc) FROM STDIN"))?;
        let mut seen = std::collections::HashSet::new();
        let mut line = String::new();
        let count = jsonl::for_each_record::<LoadError>(&file.path, |ordinal, record: Value| {
            let key = jsonl::key_of(&record).ok_or(JsonlError::MissingKey { line: ordinal })?;
            if !seen.insert(key.clone()) {
                return Err(LoadError::DuplicateKey { table: file.table.clone(), key });
            }
            line.clear();
            line.push_str(&ordinal.to_string());
            line.push('\t');
            copy_escape(&key, &mut line);
            line.push('\t');
            copy_escape(&record.to_string(), &mut line);
            line.push('\n');
            writer.write_all(line.as_bytes())?;
            Ok(())
        })?;
        writer.finish()?;
        tables.push((file.table.clone(), count));
    }

    tx.batch_execute(&format!(
        "CREATE TABLE IF NOT EXISTS {schema}._build (\
           id boolean PRIMARY KEY DEFAULT true CHECK (id), \
           build_number bigint, release_date text, loaded_at timestamptz NOT NULL DEFAULT now(), tables jsonb NOT NULL)"
    ))?;
    let listing = Value::Object(tables.iter().map(|(t, n)| (t.clone(), Value::from(*n))).collect());
    tx.execute(
        &format!(
            "INSERT INTO {schema}._build (id, build_number, release_date, tables) VALUES (true, $1, $2, $3) \
             ON CONFLICT (id) DO UPDATE SET build_number = $1, release_date = $2, loaded_at = now(), tables = $3"
        ),
        &[&manifest.build_number, &manifest.release_date, &listing],
    )?;
    tx.commit()?;
    Ok(LoadReport { build_number: manifest.build_number, tables })
}

/// Compare each file's record count with its table's. Returns the mismatches
/// as `(table, in_file, in_database)`.
pub fn verify(client: &mut Client, manifest: &SdeManifest) -> Result<Vec<(String, u64, i64)>, LoadError> {
    verify_in(client, manifest, DEFAULT_SCHEMA)
}

/// [`verify`] for tables loaded with [`load_into`].
pub fn verify_in(
    client: &mut Client,
    manifest: &SdeManifest,
    schema: &str,
) -> Result<Vec<(String, u64, i64)>, LoadError> {
    assert!(crate::manifest::is_valid_table_name(schema), "invalid schema name {schema:?}");
    let mut mismatches = Vec::new();
    for file in &manifest.files {
        let in_file = jsonl::for_each_record::<LoadError>(&file.path, |_, _| Ok(()))?;
        let row =
            client.query_one(&format!("SELECT count(*) FROM {schema}.{}", quote_ident(&file.table)), &[])?;
        let in_database: i64 = row.get(0);
        if in_file as i64 != in_database {
            mismatches.push((file.table.clone(), in_file, in_database));
        }
    }
    Ok(mismatches)
}
