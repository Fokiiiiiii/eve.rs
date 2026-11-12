use std::fs;
use std::path::Path;

use eve_sde::fetch::{self, FetchError};

mod common;
use common::{Scratch, make_zip};

fn names(dir: &Path) -> Vec<String> {
    let mut names: Vec<String> =
        fs::read_dir(dir).unwrap().map(|entry| entry.unwrap().file_name().into_string().unwrap()).collect();
    names.sort();
    names
}

#[test]
fn extract_tables_keeps_only_requested_tables_and_sde_meta() {
    let scratch = Scratch::new("extract-tables");
    let archive = scratch.path("a.zip");
    let meta = common::sde_meta(1);
    make_zip(
        &archive,
        &[
            ("_sde.jsonl", meta.as_str()),
            ("types.jsonl", "{\"_key\":587}\n"),
            ("groups.jsonl", "{\"_key\":25}\n"),
            ("readme.txt", "hello\n"),
        ],
    );

    let dir = scratch.path("tables");
    fetch::extract_tables(&archive, &dir, &["types"]).unwrap();

    assert_eq!(names(&dir), vec!["_sde.jsonl".to_owned(), "types.jsonl".to_owned()]);
    assert_eq!(fs::read_to_string(dir.join("types.jsonl")).unwrap(), "{\"_key\":587}\n");
    assert!(!dir.join(".complete").exists());
    assert!(!scratch.path("tables.partial").exists());
}

#[test]
fn extract_tables_errors_on_a_missing_table_and_leaves_no_directory() {
    let scratch = Scratch::new("extract-tables-missing");
    let archive = scratch.path("a.zip");
    let meta = common::sde_meta(1);
    make_zip(&archive, &[("_sde.jsonl", meta.as_str()), ("types.jsonl", "{\"_key\":587}\n")]);

    let dir = scratch.path("tables");
    match fetch::extract_tables(&archive, &dir, &["types", "nope"]) {
        Err(FetchError::BadArchive(detail)) => assert_eq!(detail, "it has no nope.jsonl"),
        other => panic!("expected a BadArchive error, got {other:?}"),
    }

    assert!(!dir.exists());
    assert!(!scratch.path("tables.partial").exists());
}
