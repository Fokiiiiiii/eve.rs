//! eve-sde ensure [--build N|latest] [--cache DIR] [--pg URL] [--source URL]...
//!                [--archive FILE] [--sha256 HEX|skip] [--keep-archive] [--missions]
//! eve-sde info <dir>
//! eve-sde load <dir> --pg <url>
//! eve-sde verify <dir> --pg <url>
//!
//! `ensure` is the first-start step: it fetches the build if it is not on disk
//! and, given `--pg` (or `EVE_PG_URL`), loads it unless the database already
//! holds it. Progress goes to stderr; stdout gets the extracted directory, if there is one.
//! `EVE_SDE_CACHE` sets the default `--cache` (`.cache/sde`) and
//! `EVE_SDE_BASE_URL` replaces CCP's static-data host.
//! `--missions` is the mission build (`missionPinned` in the lock): with `--pg` only its
//! `missions` and `epicArcs` tables are loaded, into schema `sde_mission`.

use std::path::PathBuf;
use std::process::ExitCode;

use eve_sde::ensure::{self, Outcome};
use eve_sde::fetch::{self, Expect, FetchOptions, Origin};
use eve_sde::{SdeManifest, load};
use postgres::{Client, NoTls};

type Failure = Box<dyn std::error::Error>;

fn usage() -> ExitCode {
    eprintln!(
        "usage: eve-sde ensure [--build N|latest] [--cache DIR] [--pg URL] [--source URL]... [--archive FILE] \
         [--sha256 HEX|skip] [--keep-archive] [--missions]\n       \
         eve-sde info <dir> | load <dir> --pg <url> | verify <dir> --pg <url>"
    );
    ExitCode::from(2)
}

/// The value after `flag`, if the flag is present.
fn value<'a>(args: &'a [String], flag: &str) -> Option<&'a String> {
    args.iter().position(|arg| arg == flag).and_then(|index| args.get(index + 1))
}

/// Every value after each occurrence of `flag`.
fn values<'a>(args: &'a [String], flag: &str) -> Vec<&'a String> {
    args.iter()
        .enumerate()
        .filter(|(_, arg)| *arg == flag)
        .filter_map(|(index, _)| args.get(index + 1))
        .collect()
}

fn ensure_command(args: &[String]) -> Result<ExitCode, Failure> {
    let missions = args.iter().any(|arg| arg == "--missions");
    let pin = if missions { fetch::mission_pinned()? } else { fetch::pinned()? };
    let base = std::env::var("EVE_SDE_BASE_URL").ok().filter(|base| !base.is_empty());
    let official = base.as_deref().unwrap_or(fetch::OFFICIAL_BASE);

    let build = match value(args, "--build").map(String::as_str) {
        None => pin.build,
        Some("latest") => fetch::latest_build(official)?,
        Some(number) => {
            number.parse().map_err(|_| format!("--build wants a number or \"latest\", not {number:?}"))?
        }
    };
    let known = fetch::pin_from(fetch::LOCK, Some(build))?;

    let mut sources: Vec<String> = values(args, "--source").into_iter().cloned().collect();
    if sources.is_empty() {
        sources = match &base {
            Some(base) => vec![fetch::official_url(base, build)],
            None => known.sources.clone(),
        };
    }
    let expect = match value(args, "--sha256").map(String::as_str) {
        Some("skip") => Expect::Unchecked,
        Some(hash) => Expect::Sha256(hash.to_owned()),
        None => known.sha256.map_or(Expect::Unchecked, Expect::Sha256),
    };
    let cache_dir = value(args, "--cache")
        .cloned()
        .or_else(|| std::env::var("EVE_SDE_CACHE").ok().filter(|dir| !dir.is_empty()))
        .unwrap_or_else(|| ".cache/sde".to_owned());
    let options = FetchOptions {
        build,
        cache_dir: PathBuf::from(cache_dir),
        sources,
        expect,
        archive: value(args, "--archive").map(PathBuf::from),
        keep_archive: args.iter().any(|arg| arg == "--keep-archive"),
    };

    let url = value(args, "--pg")
        .cloned()
        .or_else(|| std::env::var("EVE_PG_URL").ok().filter(|url| !url.is_empty()));
    let dir = match url {
        Some(url) => {
            let mut client = Client::connect(&url, NoTls)?;
            let (outcome, schema) = if missions {
                (ensure::ensure_missions(&mut client, &options)?, ensure::MISSION_SCHEMA)
            } else {
                (ensure::ensure_loaded(&mut client, &options)?, load::DEFAULT_SCHEMA)
            };
            match outcome {
                Outcome::AlreadyLoaded => eprintln!("SDE build {build} is already loaded in {schema}"),
                Outcome::Loaded { origin, tables, records } => {
                    eprintln!(
                        "loaded SDE build {build} into {schema} ({}): {tables} tables, {records} records",
                        describe(&origin)
                    );
                }
            }
            options.cache_dir.join(fetch::archive_name(build))
        }
        None => {
            let extracted = fetch::ensure_extracted(&options)?;
            eprintln!("SDE build {build} ready ({})", describe(&extracted.origin));
            extracted.dir
        }
    };
    // With a database that already held the build, nothing was extracted.
    if dir.join(".complete").is_file() {
        println!("{}", dir.display());
    }
    Ok(ExitCode::SUCCESS)
}

fn describe(origin: &Origin) -> String {
    match origin {
        Origin::Cached => "already on disk".to_owned(),
        Origin::LocalArchive(path) => format!("from {}", path.display()),
        Origin::Downloaded(url) => format!("downloaded from {url}"),
    }
}

fn run() -> Result<ExitCode, Failure> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.first().map(String::as_str) == Some("ensure") {
        return ensure_command(&args[1..]);
    }
    let (Some(command), Some(dir)) = (args.first(), args.get(1)) else { return Ok(usage()) };
    let manifest = SdeManifest::read(&PathBuf::from(dir))?;
    let url = value(&args, "--pg");

    match command.as_str() {
        "info" => {
            println!(
                "build {:?}, released {:?}, {} tables",
                manifest.build_number,
                manifest.release_date,
                manifest.files.len()
            );
            for file in &manifest.files {
                println!("  {}", file.table);
            }
        }
        "load" => {
            let Some(url) = url else { return Ok(usage()) };
            let mut client = Client::connect(url, NoTls)?;
            let report = load::load(&mut client, &manifest)?;
            let rows: u64 = report.tables.iter().map(|(_, n)| n).sum();
            println!(
                "loaded build {:?}: {} tables, {rows} records",
                report.build_number,
                report.tables.len()
            );
        }
        "verify" => {
            let Some(url) = url else { return Ok(usage()) };
            let mut client = Client::connect(url, NoTls)?;
            let mismatches = load::verify(&mut client, &manifest)?;
            for (table, in_file, in_db) in &mismatches {
                println!("MISMATCH {table}: {in_file} in the file, {in_db} in the database");
            }
            if !mismatches.is_empty() {
                return Ok(ExitCode::from(1));
            }
            println!("all {} tables match", manifest.files.len());
        }
        _ => return Ok(usage()),
    }
    Ok(ExitCode::SUCCESS)
}

fn main() -> ExitCode {
    match run() {
        Ok(code) => code,
        Err(error) => {
            eprintln!("error: {error}");
            ExitCode::FAILURE
        }
    }
}
