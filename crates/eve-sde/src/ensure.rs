//! First-start provisioning: have the SDE in PostgreSQL, fetching it if it is not.
//!
//! [`ensure_loaded`] is what a server calls at start-up. If `sde."_build"` already
//! names the wanted build it does nothing (no network, no disk); otherwise it
//! gets the build extracted ([`fetch::ensure_extracted`]), checks it really is
//! that build, and loads it in one transaction.

use postgres::Client;

use crate::fetch::{self, FetchError, FetchOptions, Origin};
use crate::jsonl::JsonlError;
use crate::load::{self, DEFAULT_SCHEMA, LoadError};
use crate::manifest::{self, SdeManifest};

#[derive(Debug)]
pub enum EnsureError {
    Fetch(FetchError),
    Manifest(JsonlError),
    Load(LoadError),
    Postgres(postgres::Error),
    /// The archive holds another build than the one asked for.
    BuildMismatch {
        wanted: u64,
        found: Option<i64>,
    },
}

impl std::fmt::Display for EnsureError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Fetch(error) => write!(f, "{error}"),
            Self::Manifest(error) => write!(f, "{error}"),
            Self::Load(error) => write!(f, "{error}"),
            Self::Postgres(error) => write!(f, "postgres: {error}"),
            Self::BuildMismatch { wanted, found } => {
                write!(f, "asked for build {wanted} but the archive is build {found:?}")
            }
        }
    }
}

impl std::error::Error for EnsureError {}

impl From<FetchError> for EnsureError {
    fn from(error: FetchError) -> Self {
        Self::Fetch(error)
    }
}
impl From<JsonlError> for EnsureError {
    fn from(error: JsonlError) -> Self {
        Self::Manifest(error)
    }
}
impl From<LoadError> for EnsureError {
    fn from(error: LoadError) -> Self {
        Self::Load(error)
    }
}
impl From<postgres::Error> for EnsureError {
    fn from(error: postgres::Error) -> Self {
        Self::Postgres(error)
    }
}

/// What [`ensure_loaded`] did.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Outcome {
    /// The database already held the build; nothing was fetched or loaded.
    AlreadyLoaded,
    Loaded {
        origin: Origin,
        tables: usize,
        records: u64,
    },
}

/// The build number recorded in `<schema>._build`, if a build has been loaded.
pub fn loaded_build(client: &mut Client, schema: &str) -> Result<Option<i64>, postgres::Error> {
    assert!(manifest::is_valid_table_name(schema), "invalid schema name {schema:?}");
    let exists: bool =
        client.query_one("SELECT to_regclass($1) IS NOT NULL", &[&format!("{schema}._build")])?.get(0);
    if !exists {
        return Ok(None);
    }
    let row = client.query_opt(&format!("SELECT build_number FROM {schema}._build"), &[])?;
    Ok(row.and_then(|row| row.get::<_, Option<i64>>(0)))
}

/// [`ensure_loaded_into`] the default schema (`sde`).
pub fn ensure_loaded(client: &mut Client, options: &FetchOptions) -> Result<Outcome, EnsureError> {
    ensure_loaded_into(client, options, DEFAULT_SCHEMA)
}

/// Load `options.build` into `schema` unless `schema` already holds it.
pub fn ensure_loaded_into(
    client: &mut Client,
    options: &FetchOptions,
    schema: &str,
) -> Result<Outcome, EnsureError> {
    if loaded_build(client, schema)? == i64::try_from(options.build).ok() {
        return Ok(Outcome::AlreadyLoaded);
    }
    let extracted = fetch::ensure_extracted(options)?;
    let manifest = SdeManifest::read(&extracted.dir)?;
    if manifest.build_number != i64::try_from(options.build).ok() {
        return Err(EnsureError::BuildMismatch { wanted: options.build, found: manifest.build_number });
    }
    let report = load::load_into(client, &manifest, schema)?;
    let records = report.tables.iter().map(|(_, count)| count).sum();
    Ok(Outcome::Loaded { origin: extracted.origin, tables: report.tables.len(), records })
}
