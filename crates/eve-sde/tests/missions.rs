//! The mission build: its pin and all mission and SKINR tables the server reads.
//!
//!   EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve cargo test -p eve-sde --test missions
//!   EVE_SDE_MISSION_DIR=<extracted build 3405148> ... (opt-in: loads the real tables into a scratch schema)
//!
//! The database tests use scratch schemas and skip (loudly) when EVE_PG_URL is not set. The fixtures
//! are written here: tiny synthetic rows, not CCP data.

use eve_sde::ensure::{self, EnsureError, MISSION_TABLES, Outcome};
use eve_sde::fetch::{self, Expect, FetchError, Origin};
use eve_sde::load;
use eve_sde::manifest::SdeManifest;
use postgres::{Client, NoTls};

mod common;
use common::{Scratch, file_url, make_zip, mini_archive, names_in, options, sde_meta};

const MISSION_BUILD: u64 = 3405148;
const PINNED_BUILD: u64 = 3396210;

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
    format!("sde_missions_{name}_{}", std::process::id())
}

fn drop_schema(client: &mut Client, schema: &str) {
    client.batch_execute(&format!("DROP SCHEMA IF EXISTS {schema} CASCADE")).unwrap();
}

fn count(client: &mut Client, schema: &str, table: &str) -> i64 {
    client.query_one(&format!("SELECT count(*) FROM {schema}.\"{table}\""), &[]).unwrap().get(0)
}

fn table_exists(client: &mut Client, schema: &str, table: &str) -> bool {
    client
        .query_one("SELECT to_regclass($1) IS NOT NULL", &[&format!("{schema}.\"{table}\"")])
        .unwrap()
        .get(0)
}

/// An archive with one tiny row in each server mission table.
fn mission_archive(path: &std::path::Path, build: u64) -> String {
    make_zip(
        path,
        &[
            ("_sde.jsonl", &sde_meta(build)),
            (
                "missions.jsonl",
                "{\"_key\":17179,\"name\":{\"en\":\"Fixture A\"}}\n{\"_key\":17180,\"name\":{\"en\":\"Fixture B\"}}\n",
            ),
            (
                "epicArcs.jsonl",
                "{\"_key\":132,\"missions\":[{\"_key\":17183,\"agentID\":3020234,\"nextMissions\":[17189]}]}\n",
            ),
            ("categories.jsonl", "{\"_key\":6}\n"),
            ("groups.jsonl", "{\"_key\":25,\"categoryID\":6}\n"),
            ("types.jsonl", "{\"_key\":587,\"groupID\":25}\n"),
            ("skinrComponentPointValues.jsonl", "{\"_key\":1}\n"),
            ("skinrComponents.jsonl", "{\"_key\":1}\n"),
            ("skinrSlotConfigurations.jsonl", "{\"_key\":1}\n"),
            ("skinrSlots.jsonl", "{\"_key\":1}\n"),
            ("skinrTierThresholds.jsonl", "{\"_key\":1}\n"),
        ],
    );
    fetch::sha256_hex(path).unwrap()
}

#[test]
fn the_mission_pin_is_a_second_build_and_the_pinned_build_is_unchanged() {
    let mission = fetch::mission_pinned().unwrap();
    assert_eq!(mission.build, MISSION_BUILD);
    assert_eq!(
        mission.sha256.as_deref(),
        Some("3d8c75d4533d53739b7cf547b4532673c7b895650bb474f57ccf4499f14afe77")
    );
    assert_eq!(mission.sources.len(), 1, "only CCP's host is recorded for the mission build");
    assert!(
        mission.sources[0].ends_with("eve-online-static-data-3405148-jsonl.zip"),
        "{:?}",
        mission.sources
    );

    let pinned = fetch::pinned().unwrap();
    assert_eq!(pinned.build, PINNED_BUILD);
    assert_eq!(
        pinned.sha256.as_deref(),
        Some("17ae0c7f00fec4c2b79f785a9c35debdc5ae9edde558d18dc26d51cf87279b3e")
    );
    assert_eq!(pinned.sources.len(), 2, "the client build keeps its two sources");
}

#[test]
fn the_mission_tables_match_the_server_authority() {
    assert_eq!(
        MISSION_TABLES,
        &[
            "missions",
            "epicArcs",
            "categories",
            "groups",
            "types",
            "skinrComponentPointValues",
            "skinrComponents",
            "skinrSlotConfigurations",
            "skinrSlots",
            "skinrTierThresholds"
        ]
    );
    assert_eq!(ensure::MISSION_SCHEMA, "sde_mission");
}

#[test]
fn only_the_named_tables_are_loaded_and_the_build_records_them() {
    let Some(mut client) = connect() else { return };
    let schema = scratch_schema("selected");
    drop_schema(&mut client, &schema);
    let scratch = Scratch::new("missions-selected");
    let archive = scratch.path("mission.zip");
    let hash = mission_archive(&archive, MISSION_BUILD);
    let mut wanted = options(&scratch, MISSION_BUILD, vec![], Expect::Sha256(hash));
    wanted.archive = Some(archive);

    let outcome = ensure::ensure_tables_into(&mut client, &wanted, &schema, MISSION_TABLES).unwrap();
    assert!(matches!(outcome, Outcome::Loaded { tables: 10, records: 11, .. }), "{outcome:?}");
    assert_eq!(count(&mut client, &schema, "missions"), 2);
    assert_eq!(count(&mut client, &schema, "epicArcs"), 1);
    assert!(table_exists(&mut client, &schema, "types"), "the server reads the mission-build type catalog");
    assert_eq!(ensure::loaded_tables(&mut client, &schema).unwrap().len(), MISSION_TABLES.len());

    let key: String = client
        .query_one(&format!("SELECT key FROM {schema}.\"missions\" ORDER BY ord LIMIT 1"), &[])
        .unwrap()
        .get(0);
    assert_eq!(key, "17179");
    drop_schema(&mut client, &schema);
}

#[test]
fn a_named_table_the_archive_lacks_is_an_error_and_nothing_is_loaded() {
    let Some(mut client) = connect() else { return };
    let schema = scratch_schema("missing");
    drop_schema(&mut client, &schema);
    let scratch = Scratch::new("missions-missing");
    let archive = scratch.path("plain.zip");
    mini_archive(&archive, MISSION_BUILD); // has types only
    let mut wanted = options(&scratch, MISSION_BUILD, vec![], Expect::Unchecked);
    wanted.archive = Some(archive);

    let error = ensure::ensure_tables_into(&mut client, &wanted, &schema, MISSION_TABLES).unwrap_err();
    assert!(
        matches!(error, EnsureError::Fetch(FetchError::BadArchive(ref detail)) if detail.contains("missions.jsonl")),
        "{error}"
    );
    assert_eq!(ensure::loaded_build(&mut client, &schema).unwrap(), None);
    assert!(!table_exists(&mut client, &schema, "missions"));
    drop_schema(&mut client, &schema);
}

#[test]
fn a_schema_with_only_some_of_the_tables_is_loaded_again() {
    let Some(mut client) = connect() else { return };
    let schema = scratch_schema("partial");
    drop_schema(&mut client, &schema);
    let scratch = Scratch::new("missions-partial");
    let archive = scratch.path("mission.zip");
    let hash = mission_archive(&archive, MISSION_BUILD);
    let extracted = scratch.path("extracted");
    fetch::extract(&archive, &extracted).unwrap();
    let manifest = SdeManifest::read(&extracted).unwrap();
    load::load_tables_into(&mut client, &manifest, &schema, &["missions"]).unwrap();
    assert_eq!(ensure::loaded_build(&mut client, &schema).unwrap(), Some(MISSION_BUILD as i64));
    assert_eq!(ensure::loaded_tables(&mut client, &schema).unwrap(), vec!["missions".to_owned()]);

    let mut wanted = options(&scratch, MISSION_BUILD, vec![], Expect::Sha256(hash));
    wanted.archive = Some(archive);
    let outcome = ensure::ensure_tables_into(&mut client, &wanted, &schema, MISSION_TABLES).unwrap();
    assert!(matches!(outcome, Outcome::Loaded { tables: 10, .. }), "{outcome:?}");
    assert_eq!(ensure::loaded_tables(&mut client, &schema).unwrap().len(), MISSION_TABLES.len());

    let fresh = Scratch::new("missions-again");
    let again = options(&fresh, MISSION_BUILD, vec![file_url(&fresh.path("missing.zip"))], Expect::Unchecked);
    assert_eq!(
        ensure::ensure_tables_into(&mut client, &again, &schema, MISSION_TABLES).unwrap(),
        Outcome::AlreadyLoaded
    );
    drop_schema(&mut client, &schema);
}

#[test]
fn the_mission_build_refuses_an_archive_of_another_build() {
    let Some(mut client) = connect() else { return };
    let schema = scratch_schema("mismatch");
    drop_schema(&mut client, &schema);
    let scratch = Scratch::new("missions-mismatch");
    let archive = scratch.path("wrong.zip");
    mission_archive(&archive, PINNED_BUILD);
    let mut wanted = options(&scratch, MISSION_BUILD, vec![], Expect::Unchecked);
    wanted.archive = Some(archive);
    let error = ensure::ensure_tables_into(&mut client, &wanted, &schema, MISSION_TABLES).unwrap_err();
    assert!(
        matches!(error, EnsureError::BuildMismatch { wanted: MISSION_BUILD, found: Some(3396210) }),
        "{error}"
    );
    assert_eq!(ensure::loaded_build(&mut client, &schema).unwrap(), None);
}

#[test]
fn a_missing_mission_archive_is_a_fetch_error_and_loads_nothing() {
    let Some(mut client) = connect() else { return };
    let schema = scratch_schema("unreachable");
    drop_schema(&mut client, &schema);
    let scratch = Scratch::new("missions-unreachable");
    let wanted =
        options(&scratch, MISSION_BUILD, vec![file_url(&scratch.path("nothing.zip"))], Expect::Unchecked);
    let error = ensure::ensure_tables_into(&mut client, &wanted, &schema, MISSION_TABLES).unwrap_err();
    assert!(matches!(error, EnsureError::Fetch(FetchError::AllSourcesFailed(_))), "{error}");
    assert_eq!(ensure::loaded_build(&mut client, &schema).unwrap(), None);
}

/// Opt-in: the real build 3405148, extracted (`eve-sde ensure --missions` writes it under the cache).
/// Loads the server mission and SKINR tables into a scratch schema and checks mission counts.
#[test]
fn the_real_mission_build_has_the_tables_and_ids_the_server_reads() {
    let Ok(dir) = std::env::var("EVE_SDE_MISSION_DIR") else {
        eprintln!("EVE_SDE_MISSION_DIR is not set: skipping the real-data mission test");
        return;
    };
    let Some(mut client) = connect() else { return };
    let schema = scratch_schema("real");
    drop_schema(&mut client, &schema);
    let manifest = SdeManifest::read(std::path::Path::new(&dir)).unwrap();
    assert_eq!(manifest.build_number, Some(MISSION_BUILD as i64), "{dir} is not build {MISSION_BUILD}");
    let report = load::load_tables_into(&mut client, &manifest, &schema, MISSION_TABLES).unwrap();
    assert_eq!(report.tables.len(), MISSION_TABLES.len());
    assert_eq!(count(&mut client, &schema, "missions"), 2892);
    assert_eq!(count(&mut client, &schema, "epicArcs"), 21);
    for id in 17179..=17190 {
        let found: bool = client
            .query_one(
                &format!("SELECT EXISTS (SELECT 1 FROM {schema}.\"missions\" WHERE key = '{id}')"),
                &[],
            )
            .unwrap()
            .get(0);
        assert!(found, "mission {id} is missing from build {MISSION_BUILD}");
    }
    let arc: bool = client
        .query_one(&format!("SELECT EXISTS (SELECT 1 FROM {schema}.\"epicArcs\" WHERE key = '132')"), &[])
        .unwrap()
        .get(0);
    assert!(arc, "epic arc 132 is missing from build {MISSION_BUILD}");
    drop_schema(&mut client, &schema);
}

/// A fake build with the mission tables and tables the server must not keep: the rest of the build.
fn fake_full_build(path: &std::path::Path, build: u64) -> String {
    make_zip(
        path,
        &[
            ("_sde.jsonl", &sde_meta(build)),
            ("missions.jsonl", "{\"_key\":17179,\"name\":{\"en\":\"Fixture A\"}}\n"),
            ("epicArcs.jsonl", "{\"_key\":132,\"missions\":[]}\n"),
            ("categories.jsonl", "{\"_key\":6}\n"),
            ("groups.jsonl", "{\"_key\":25,\"categoryID\":6}\n"),
            ("types.jsonl", "{\"_key\":587,\"groupID\":25}\n"),
            ("skinrComponentPointValues.jsonl", "{\"_key\":1}\n"),
            ("skinrComponents.jsonl", "{\"_key\":1}\n"),
            ("skinrSlotConfigurations.jsonl", "{\"_key\":1}\n"),
            ("skinrSlots.jsonl", "{\"_key\":1}\n"),
            ("skinrTierThresholds.jsonl", "{\"_key\":1}\n"),
            ("npcCharacters.jsonl", "{\"_key\":3020234}\n"),
        ],
    );
    fetch::sha256_hex(path).unwrap()
}

#[test]
fn the_loader_keeps_only_the_named_tables_and_leaves_no_copy_of_the_build_on_disk() {
    let Some(mut client) = connect() else { return };
    let schema = scratch_schema("discard");
    drop_schema(&mut client, &schema);
    let scratch = Scratch::new("missions-discard");
    let source = scratch.path("source.zip");
    let hash = fake_full_build(&source, MISSION_BUILD);
    let cache = scratch.path("cache");
    let earlier = cache.join(fetch::archive_name(MISSION_BUILD));
    std::fs::create_dir_all(&earlier).unwrap();
    std::fs::write(earlier.join("types.jsonl"), "{\"_key\":1}\n").unwrap();

    let wanted = options(&scratch, MISSION_BUILD, vec![file_url(&source)], Expect::Sha256(hash));
    let outcome = ensure::ensure_tables_into(&mut client, &wanted, &schema, MISSION_TABLES).unwrap();
    assert!(
        matches!(outcome, Outcome::Loaded { origin: Origin::Downloaded(_), tables: 10, records: 10 }),
        "{outcome:?}"
    );
    assert_eq!(count(&mut client, &schema, "missions"), 1);
    assert_eq!(count(&mut client, &schema, "epicArcs"), 1);
    assert!(table_exists(&mut client, &schema, "types"), "the server reads the mission-build type catalog");
    assert!(!table_exists(&mut client, &schema, "npcCharacters"), "an unnamed table must not be loaded");
    assert_eq!(names_in(&cache), Vec::<String>::new(), "nothing of the build may remain under the cache");
    drop_schema(&mut client, &schema);
}

#[test]
fn a_local_archive_is_read_in_place_and_never_removed() {
    let Some(mut client) = connect() else { return };
    let schema = scratch_schema("local");
    drop_schema(&mut client, &schema);
    let scratch = Scratch::new("missions-local");
    let archive = scratch.path("mine.zip");
    let hash = fake_full_build(&archive, MISSION_BUILD);
    let mut wanted = options(&scratch, MISSION_BUILD, vec![], Expect::Sha256(hash));
    wanted.archive = Some(archive.clone());
    ensure::ensure_tables_into(&mut client, &wanted, &schema, MISSION_TABLES).unwrap();
    assert!(archive.is_file(), "the archive the caller named must stay");
    assert_eq!(names_in(&scratch.path("cache")), Vec::<String>::new());
    drop_schema(&mut client, &schema);
}

#[test]
fn the_mission_pin_names_a_hash_so_a_later_run_can_verify_the_build() {
    let mission = fetch::mission_pinned().unwrap();
    assert!(mission.sha256.as_deref().is_some_and(|hash| hash.len() == 64));
}
