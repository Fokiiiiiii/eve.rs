//! Reader and loader tests. The database tests need a PostgreSQL to talk to:
//!
//!   EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve cargo test -p eve-sde
//!
//! and are skipped (loudly) when it is not set.

use std::fs;
use std::path::PathBuf;

use eve_sde::SdeManifest;
use eve_sde::jsonl::{self, JsonlError};
use eve_sde::load::{self, LoadError, copy_escape};
use postgres::{Client, NoTls};

/// A scratch directory under the system temp dir, removed on drop.
struct Scratch(PathBuf);

impl Scratch {
    fn new(name: &str) -> Self {
        let dir = std::env::temp_dir().join(format!("eve-sde-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        Self(dir)
    }

    fn write(&self, file: &str, text: &str) {
        fs::write(self.0.join(file), text).unwrap();
    }
}

impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

/// A small SDE with awkward content: tabs, newlines, backslashes, quotes, unicode.
fn mini_sde(dir: &Scratch) {
    dir.write(
        "_sde.jsonl",
        "{\"_key\":\"sde\",\"buildNumber\":3396210,\"releaseDate\":\"2026-06-16T11:00:00Z\"}\n",
    );
    dir.write(
        "types.jsonl",
        concat!(
            "{\"_key\":587,\"groupID\":25,\"name\":{\"en\":\"Rifter\",\"ja\":\"リフター\"},\"mass\":1067000.0}\n",
            "\n",
            "{\"_key\":588,\"groupID\":25,\"name\":{\"en\":\"tab\\there\\nnewline \\\\ backslash \\\"quote\\\"\"}}\n",
            "{\"_key\":\"text-key\",\"note\":\"C:\\\\path\\\\to\\ttab\"}\n",
        ),
    );
    dir.write("groups.jsonl", "{\"_key\":25,\"categoryID\":6}\n");
    dir.write("readme.txt", "not a table\n");
    dir.write("bad name.jsonl", "{\"_key\":1}\n");
}

#[test]
fn manifest_lists_valid_tables_and_reads_the_build() {
    let dir = Scratch::new("manifest");
    mini_sde(&dir);
    let manifest = SdeManifest::read(&dir.0).unwrap();
    assert_eq!(manifest.build_number, Some(3396210));
    assert_eq!(manifest.release_date.as_deref(), Some("2026-06-16T11:00:00Z"));
    let tables: Vec<_> = manifest.files.iter().map(|f| f.table.as_str()).collect();
    assert_eq!(tables, ["_sde", "groups", "types"], "readme.txt and 'bad name' are not tables");
}

#[test]
fn a_directory_without_meta_has_no_build() {
    let dir = Scratch::new("nometa");
    dir.write("groups.jsonl", "{\"_key\":1}\n");
    let manifest = SdeManifest::read(&dir.0).unwrap();
    assert_eq!((manifest.build_number, manifest.release_date), (None, None));
}

#[test]
fn jsonl_errors_carry_the_line_number() {
    let dir = Scratch::new("badline");
    dir.write("t.jsonl", "{\"_key\":1}\n\n{not json}\n");
    let error = jsonl::for_each_record::<JsonlError>(&dir.0.join("t.jsonl"), |_, _| Ok(())).unwrap_err();
    assert!(matches!(error, JsonlError::Json { line: 3, .. }), "{error:?}");
}

#[test]
fn keys_are_text_of_numbers_or_strings() {
    let key = |text: &str| jsonl::key_of(&serde_json::from_str(text).unwrap());
    assert_eq!(key("{\"_key\":587}").as_deref(), Some("587"));
    assert_eq!(key("{\"_key\":\"sde\"}").as_deref(), Some("sde"));
    assert_eq!(key("{\"_key\":-1}").as_deref(), Some("-1"));
    assert_eq!(key("{\"name\":1}"), None);
    assert_eq!(key("{\"_key\":[1]}"), None);
}

#[test]
fn copy_escaping_covers_the_text_format_specials() {
    let mut out = String::new();
    copy_escape("a\\b\tc\nd\re", &mut out);
    assert_eq!(out, "a\\\\b\\tc\\nd\\re");
}

fn connect() -> Option<Client> {
    match std::env::var("EVE_PG_URL") {
        Ok(url) => Some(Client::connect(&url, NoTls).expect("EVE_PG_URL is set but does not connect")),
        Err(_) => {
            eprintln!("EVE_PG_URL is not set: skipping the database test");
            None
        }
    }
}

fn scratch_schema(name: &str) -> String {
    format!("sde_test_{name}_{}", std::process::id())
}

#[test]
fn loads_into_postgres_and_round_trips_awkward_content() {
    let Some(mut client) = connect() else { return };
    let dir = Scratch::new("load");
    mini_sde(&dir);
    let manifest = SdeManifest::read(&dir.0).unwrap();
    let schema = scratch_schema("load");

    let report = load::load_into(&mut client, &manifest, &schema).unwrap();
    assert_eq!(report.build_number, Some(3396210));
    assert_eq!(report.tables, [("_sde".to_owned(), 1), ("groups".to_owned(), 1), ("types".to_owned(), 3)]);

    let count: i64 = client.query_one(&format!("SELECT count(*) FROM {schema}.types"), &[]).unwrap().get(0);
    assert_eq!(count, 3);

    let row = client
        .query_one(
            &format!(
                "SELECT doc->'name'->>'ja', (doc->>'mass')::float8 FROM {schema}.types WHERE key = '587'"
            ),
            &[],
        )
        .unwrap();
    assert_eq!(row.get::<_, String>(0), "リフター");
    assert_eq!(row.get::<_, f64>(1), 1067000.0);

    let name: String = client
        .query_one(&format!("SELECT doc->'name'->>'en' FROM {schema}.types WHERE key = '588'"), &[])
        .unwrap()
        .get(0);
    assert_eq!(name, "tab\there\nnewline \\ backslash \"quote\"");
    let note: String = client
        .query_one(&format!("SELECT doc->>'note' FROM {schema}.types WHERE key = 'text-key'"), &[])
        .unwrap()
        .get(0);
    assert_eq!(note, "C:\\path\\to\ttab");

    let ords: Vec<i64> = client
        .query(&format!("SELECT ord FROM {schema}.types ORDER BY ord"), &[])
        .unwrap()
        .iter()
        .map(|r| r.get(0))
        .collect();
    assert_eq!(ords, [1, 3, 4]);

    let build: i64 =
        client.query_one(&format!("SELECT build_number FROM {schema}._build"), &[]).unwrap().get(0);
    assert_eq!(build, 3396210);
    assert!(load::verify_in(&mut client, &manifest, &schema).unwrap().is_empty());

    load::load_into(&mut client, &manifest, &schema).unwrap();
    let count: i64 = client.query_one(&format!("SELECT count(*) FROM {schema}.types"), &[]).unwrap().get(0);
    assert_eq!(count, 3);

    client.batch_execute(&format!("DROP SCHEMA {schema} CASCADE")).unwrap();
}

#[test]
fn a_failed_load_leaves_the_previous_one_intact() {
    let Some(mut client) = connect() else { return };
    let schema = scratch_schema("atomic");

    let good = Scratch::new("atomic-good");
    mini_sde(&good);
    load::load_into(&mut client, &SdeManifest::read(&good.0).unwrap(), &schema).unwrap();

    let bad = Scratch::new("atomic-bad");
    bad.write("types.jsonl", "{\"_key\":1}\n{\"_key\":1}\n");
    bad.write("groups.jsonl", "{\"_key\":99}\n");
    let error = load::load_into(&mut client, &SdeManifest::read(&bad.0).unwrap(), &schema).unwrap_err();
    assert!(
        matches!(error, LoadError::DuplicateKey { ref table, ref key } if table == "types" && key == "1"),
        "{error}"
    );

    let types: i64 = client.query_one(&format!("SELECT count(*) FROM {schema}.types"), &[]).unwrap().get(0);
    let groups: i64 = client.query_one(&format!("SELECT count(*) FROM {schema}.groups"), &[]).unwrap().get(0);
    assert_eq!((types, groups), (3, 1), "the earlier load is untouched");

    client.batch_execute(&format!("DROP SCHEMA {schema} CASCADE")).unwrap();
}

#[test]
fn a_record_without_a_key_is_rejected() {
    let Some(mut client) = connect() else { return };
    let dir = Scratch::new("nokey");
    dir.write("t.jsonl", "{\"name\":\"no key\"}\n");
    let schema = scratch_schema("nokey");
    let error = load::load_into(&mut client, &SdeManifest::read(&dir.0).unwrap(), &schema).unwrap_err();
    assert!(matches!(error, LoadError::Jsonl(JsonlError::MissingKey { line: 1 })), "{error}");
    let _ = client.batch_execute(&format!("DROP SCHEMA IF EXISTS {schema} CASCADE"));
}
