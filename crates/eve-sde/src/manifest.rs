//! What an SDE directory contains.

use std::fs;
use std::path::{Path, PathBuf};

use crate::jsonl::{self, JsonlError};

/// One `<table>.jsonl` file.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SdeFile {
    /// The table name: the file name without `.jsonl`.
    pub table: String,
    pub path: PathBuf,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SdeManifest {
    pub dir: PathBuf,
    /// From `_sde.jsonl`; `None` if the file is absent or has no `sde` record.
    pub build_number: Option<i64>,
    pub release_date: Option<String>,
    /// Every JSONL file, sorted by table name.
    pub files: Vec<SdeFile>,
}

/// A table name that is safe to use as an identifier: letters, digits, underscore.
pub fn is_valid_table_name(name: &str) -> bool {
    !name.is_empty() && name.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_')
}

impl SdeManifest {
    pub fn read(dir: &Path) -> Result<Self, JsonlError> {
        let mut files = Vec::new();
        for entry in fs::read_dir(dir)? {
            let path = entry?.path();
            let Some(name) = path.file_name().and_then(|n| n.to_str()) else { continue };
            let Some(table) = name.strip_suffix(".jsonl") else { continue };
            if is_valid_table_name(table) {
                files.push(SdeFile { table: table.to_owned(), path });
            }
        }
        files.sort_by(|a, b| a.table.cmp(&b.table));

        let mut build_number = None;
        let mut release_date = None;
        let meta = dir.join("_sde.jsonl");
        if meta.is_file() {
            jsonl::for_each_record::<JsonlError>(&meta, |_, record| {
                if jsonl::key_of(&record).as_deref() == Some("sde") {
                    build_number = record.get("buildNumber").and_then(|v| v.as_i64());
                    release_date = record.get("releaseDate").and_then(|v| v.as_str()).map(str::to_owned);
                }
                Ok(())
            })?;
        }
        Ok(Self { dir: dir.to_owned(), build_number, release_date, files })
    }
}
