//! First-start provisioning against a real PostgreSQL:
//!
//!   EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve cargo test -p eve-sde
//!
//! The tests use scratch schemas and are skipped (loudly) when it is not set.

use eve_sde::ensure::{self, EnsureError, Outcome};
use eve_sde::fetch::{Expect, FetchError, Origin};
use postgres::{Client, NoTls};

mod common;
use common::{Scratch, file_url, mini_archive, options};

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
    format!("sde_ensure_{name}_{}", std::process::id())
}

fn drop_schema(client: &mut Client, schema: &str) {
    client.batch_execute(&format!("DROP SCHEMA IF EXISTS {schema} CASCADE")).unwrap();
}

#[test]
fn nothing_is_loaded_before_the_first_start() {
    let Some(mut client) = connect() else { return };
    assert_eq!(ensure::loaded_build(&mut client, &scratch_schema("never")).unwrap(), None);
}

#[test]
fn the_first_start_fetches_and_loads_and_the_second_does_nothing() {
    let Some(mut client) = connect() else { return };
    let schema = scratch_schema("first");
    drop_schema(&mut client, &schema);
    let scratch = Scratch::new("ensure-first");
    let archive = scratch.path("a.zip");
    let hash = mini_archive(&archive, 71);

    let wanted = options(&scratch, 71, vec![file_url(&archive)], Expect::Sha256(hash));
    let outcome = ensure::ensure_loaded_into(&mut client, &wanted, &schema).unwrap();
    assert_eq!(
        outcome,
        Outcome::Loaded { origin: Origin::Downloaded(file_url(&archive)), tables: 2, records: 2 }
    );
    assert_eq!(ensure::loaded_build(&mut client, &schema).unwrap(), Some(71));
    let count: i64 = client.query_one(&format!("SELECT count(*) FROM {schema}.types"), &[]).unwrap().get(0);
    assert_eq!(count, 1);

    let fresh = Scratch::new("ensure-first-again");
    let again = options(&fresh, 71, vec![file_url(&fresh.path("missing.zip"))], Expect::Unchecked);
    assert_eq!(ensure::ensure_loaded_into(&mut client, &again, &schema).unwrap(), Outcome::AlreadyLoaded);
    assert!(!fresh.path("cache").exists());
    drop_schema(&mut client, &schema);
}

#[test]
fn a_newer_build_replaces_the_loaded_one() {
    let Some(mut client) = connect() else { return };
    let schema = scratch_schema("replace");
    drop_schema(&mut client, &schema);
    let scratch = Scratch::new("ensure-replace");
    let first = scratch.path("first.zip");
    mini_archive(&first, 72);
    let second = scratch.path("second.zip");
    common::make_zip(
        &second,
        &[
            ("_sde.jsonl", &common::sde_meta(73)),
            ("types.jsonl", "{\"_key\":1}\n{\"_key\":2}\n{\"_key\":3}\n"),
        ],
    );

    let mut wanted = options(&scratch, 72, vec![], Expect::Unchecked);
    wanted.archive = Some(first);
    ensure::ensure_loaded_into(&mut client, &wanted, &schema).unwrap();
    assert_eq!(ensure::loaded_build(&mut client, &schema).unwrap(), Some(72));

    let mut newer = options(&scratch, 73, vec![], Expect::Unchecked);
    newer.archive = Some(second);
    let outcome = ensure::ensure_loaded_into(&mut client, &newer, &schema).unwrap();
    assert!(matches!(outcome, Outcome::Loaded { records: 4, .. }), "{outcome:?}");
    assert_eq!(ensure::loaded_build(&mut client, &schema).unwrap(), Some(73));
    let count: i64 = client.query_one(&format!("SELECT count(*) FROM {schema}.types"), &[]).unwrap().get(0);
    assert_eq!(count, 3);
    drop_schema(&mut client, &schema);
}

#[test]
fn an_archive_of_another_build_is_refused_and_nothing_is_loaded() {
    let Some(mut client) = connect() else { return };
    let schema = scratch_schema("mismatch");
    drop_schema(&mut client, &schema);
    let scratch = Scratch::new("ensure-mismatch");
    let archive = scratch.path("a.zip");
    mini_archive(&archive, 75);

    let mut wanted = options(&scratch, 74, vec![], Expect::Unchecked);
    wanted.archive = Some(archive);
    let error = ensure::ensure_loaded_into(&mut client, &wanted, &schema).unwrap_err();
    assert!(matches!(error, EnsureError::BuildMismatch { wanted: 74, found: Some(75) }), "{error}");
    assert_eq!(ensure::loaded_build(&mut client, &schema).unwrap(), None);
    drop_schema(&mut client, &schema);
}

#[test]
fn a_failed_fetch_is_an_error_and_loads_nothing() {
    let Some(mut client) = connect() else { return };
    let schema = scratch_schema("failed");
    drop_schema(&mut client, &schema);
    let scratch = Scratch::new("ensure-failed");
    let wanted = options(&scratch, 76, vec![file_url(&scratch.path("missing.zip"))], Expect::Unchecked);
    let error = ensure::ensure_loaded_into(&mut client, &wanted, &schema).unwrap_err();
    assert!(matches!(error, EnsureError::Fetch(FetchError::AllSourcesFailed(_))), "{error}");
    assert_eq!(ensure::loaded_build(&mut client, &schema).unwrap(), None);
}
