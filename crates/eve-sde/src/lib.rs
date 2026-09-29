#![forbid(unsafe_code)]
//! The EVE static data export (SDE) in its JSONL distribution.
//!
//! An SDE build is a directory of `<table>.jsonl` files, one JSON object per
//! line, each with a `_key`; `_sde.jsonl` carries the build number and release
//! date. This crate reads such a directory ([`manifest`], [`jsonl`]) and loads
//! it into PostgreSQL ([`load`]) without needing to know any table's fields:
//! every file becomes a table `sde."<file>"(ord, key, doc jsonb)`. Typed models
//! for the tables the server uses are built on top of that, table by table.
//!
//! The data itself is CCP's and is fetched at development time (see
//! `sde/fetch.sh`); it is never committed.

pub mod jsonl;
pub mod load;
pub mod manifest;

pub use manifest::{SdeFile, SdeManifest};
