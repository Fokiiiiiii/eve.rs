use std::fs::{self, File};
use std::io::{self, Read};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

use serde_json::Value;
use sha2::{Digest, Sha256};

pub const OFFICIAL_BASE: &str = "https://developers.eveonline.com/static-data";

/// `sde/lock.json`, compiled in: the pinned build, its SHA-256 and where to get it.
pub const LOCK: &str = include_str!("../../../sde/lock.json");

#[derive(Debug)]
pub enum FetchError {
    Io(io::Error),
    /// `sde/lock.json` is not what this code expects.
    Lock(String),
    /// `curl` could not be run, or the server answered with an error.
    Download {
        url: String,
        detail: String,
    },
    /// The archive's hash is not the pinned one.
    Sha256 {
        source: String,
        expected: String,
        actual: String,
    },
    /// Not a zip, or not an SDE export.
    BadArchive(String),
    /// Every source failed; one entry per source.
    AllSourcesFailed(Vec<String>),
    NoSources,
}

impl std::fmt::Display for FetchError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Io(error) => write!(f, "{error}"),
            Self::Lock(detail) => write!(f, "sde/lock.json: {detail}"),
            Self::Download { url, detail } => write!(f, "downloading {url}: {detail}"),
            Self::Sha256 { source, expected, actual } => {
                write!(
                    f,
                    "{source}: sha256 is {actual}, the pin says {expected} (use --sha256 skip to accept it)"
                )
            }
            Self::BadArchive(detail) => write!(f, "not an SDE archive: {detail}"),
            Self::AllSourcesFailed(failures) => {
                write!(f, "no source could provide the SDE:")?;
                for failure in failures {
                    write!(f, "\n  {failure}")?;
                }
                Ok(())
            }
            Self::NoSources => write!(f, "no source to download the SDE from"),
        }
    }
}

impl std::error::Error for FetchError {}

impl From<io::Error> for FetchError {
    fn from(error: io::Error) -> Self {
        Self::Io(error)
    }
}

impl From<zip::result::ZipError> for FetchError {
    fn from(error: zip::result::ZipError) -> Self {
        Self::BadArchive(error.to_string())
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Expect {
    Sha256(String),
    Unchecked,
}

/// A build as `sde/lock.json` describes it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Pin {
    pub build: u64,
    pub sha256: Option<String>,
    pub sources: Vec<String>,
}

pub fn archive_name(build: u64) -> String {
    format!("eve-online-static-data-{build}-jsonl")
}

pub fn official_url(base: &str, build: u64) -> String {
    format!("{}/tranquility/{}.zip", base.trim_end_matches('/'), archive_name(build))
}

/// The pinned build of the compiled-in lock file.
pub fn pinned() -> Result<Pin, FetchError> {
    pin_from(LOCK, None)
}

/// The mission build of the compiled-in lock file (`missionPinned`): a second build, newer than
/// the client's, that carries the tables the client's build does not have. See [`pinned`].
pub fn mission_pinned() -> Result<Pin, FetchError> {
    let lock: Value = serde_json::from_str(LOCK).map_err(|error| FetchError::Lock(error.to_string()))?;
    let build = lock
        .get("missionPinned")
        .and_then(Value::as_str)
        .and_then(|text| text.parse().ok())
        .ok_or_else(|| FetchError::Lock("no \"missionPinned\" build".to_owned()))?;
    pin_from(LOCK, Some(build))
}

/// What `lock` says about `build`, or about the pinned build when `None`. A build
/// the lock does not list has no hash and only the official source.
pub fn pin_from(lock: &str, build: Option<u64>) -> Result<Pin, FetchError> {
    let lock: Value = serde_json::from_str(lock).map_err(|error| FetchError::Lock(error.to_string()))?;
    let build = match build {
        Some(build) => build,
        None => lock
            .get("pinned")
            .and_then(Value::as_str)
            .and_then(|text| text.parse().ok())
            .ok_or_else(|| FetchError::Lock("no \"pinned\" build".to_owned()))?,
    };
    let entry = lock.get("builds").and_then(|builds| builds.get(build.to_string()));
    let sha256 = entry.and_then(|entry| entry.get("sha256")).and_then(Value::as_str).map(str::to_owned);
    let sources: Vec<String> = entry
        .and_then(|entry| entry.get("sources"))
        .and_then(Value::as_array)
        .map(|urls| urls.iter().filter_map(Value::as_str).map(str::to_owned).collect())
        .filter(|urls: &Vec<String>| !urls.is_empty())
        .unwrap_or_else(|| vec![official_url(OFFICIAL_BASE, build)]);
    Ok(Pin { build, sha256, sources })
}

#[derive(Debug, Clone)]
pub struct FetchOptions {
    pub build: u64,
    pub cache_dir: PathBuf,
    /// Tried in order.
    pub sources: Vec<String>,
    pub expect: Expect,
    /// Use this archive instead of downloading. It is never deleted.
    pub archive: Option<PathBuf>,
    pub keep_archive: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Origin {
    Cached,
    LocalArchive(PathBuf),
    Downloaded(String),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Extracted {
    pub dir: PathBuf,
    pub origin: Origin,
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

pub fn sha256_hex(path: &Path) -> io::Result<String> {
    let mut file = File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buffer = vec![0u8; 1 << 20];
    loop {
        let read = file.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        hasher.update(&buffer[..read]);
    }
    Ok(hex(hasher.finalize().as_slice()))
}

fn curl_command(url: &str) -> Command {
    let mut command = Command::new("curl");
    // A redirect must not lead to a local file.
    command.args(["-fsSL", "--retry", "3", "--connect-timeout", "30", "--proto-redir", "=https,http"]);
    command.arg(url).stdin(Stdio::null()).stderr(Stdio::piped());
    command
}

fn curl_error(url: &str, error: io::Error) -> FetchError {
    let detail = if error.kind() == io::ErrorKind::NotFound {
        "curl is not installed (it does the downloading)".to_owned()
    } else {
        error.to_string()
    };
    FetchError::Download { url: url.to_owned(), detail }
}

fn download(url: &str, destination: &Path) -> Result<(), FetchError> {
    let output =
        curl_command(url).arg("-o").arg(destination).output().map_err(|error| curl_error(url, error))?;
    if output.status.success() {
        return Ok(());
    }
    Err(FetchError::Download {
        url: url.to_owned(),
        detail: String::from_utf8_lossy(&output.stderr).trim().to_owned(),
    })
}

/// The build number CCP currently publishes, read from `<base>/tranquility/latest.jsonl`.
pub fn latest_build(base: &str) -> Result<u64, FetchError> {
    let url = format!("{}/tranquility/latest.jsonl", base.trim_end_matches('/'));
    let output = curl_command(&url).output().map_err(|error| curl_error(&url, error))?;
    if !output.status.success() {
        return Err(FetchError::Download {
            url,
            detail: String::from_utf8_lossy(&output.stderr).trim().to_owned(),
        });
    }
    String::from_utf8_lossy(&output.stdout)
        .lines()
        .filter_map(|line| serde_json::from_str::<Value>(line).ok())
        .find(|record| record.get("_key").and_then(Value::as_str) == Some("sde"))
        .and_then(|record| record.get("buildNumber").and_then(Value::as_u64))
        .ok_or_else(|| FetchError::Download {
            url,
            detail: "no \"sde\" record with a buildNumber".to_owned(),
        })
}

fn verify(path: &Path, source: &str, expect: &Expect) -> Result<(), FetchError> {
    if let Expect::Sha256(expected) = expect {
        let actual = sha256_hex(path)?;
        if !actual.eq_ignore_ascii_case(expected) {
            return Err(FetchError::Sha256 { source: source.to_owned(), expected: expected.clone(), actual });
        }
    }
    let mut archive = zip::ZipArchive::new(File::open(path)?)?;
    let mut has_metadata = false;
    for index in 0..archive.len() {
        let entry = archive.by_index(index)?;
        if base_name(entry.name()?.as_ref()) == Some("_sde.jsonl") {
            has_metadata = true;
            break;
        }
    }
    if !has_metadata {
        return Err(FetchError::BadArchive("it has no _sde.jsonl".to_owned()));
    }
    Ok(())
}

/// The last path component of an archive member, if it is a usable file name.
fn base_name(member: &str) -> Option<&str> {
    member.rsplit(['/', '\\']).next().filter(|name| !name.is_empty() && *name != "." && *name != "..")
}

fn sibling(dir: &Path, suffix: &str) -> PathBuf {
    let mut name = dir.as_os_str().to_owned();
    name.push(suffix);
    PathBuf::from(name)
}

fn remove_dir_if_present(dir: &Path) -> io::Result<()> {
    match fs::remove_dir_all(dir) {
        Err(error) if error.kind() != io::ErrorKind::NotFound => Err(error),
        _ => Ok(()),
    }
}

/// Extract every file of `archive` flat into `dir`, so the JSONL files sit at its
/// top level whatever the archive's layout, and mark it complete. Member paths
/// are reduced to their last component, so nothing lands outside `dir`. The
/// directory appears only when it is whole.
pub fn extract(archive: &Path, dir: &Path) -> Result<(), FetchError> {
    extract_selected(archive, dir, None)?;
    File::create(dir.join(".complete"))?;
    Ok(())
}

/// Extract only `_sde.jsonl` and the `<table>.jsonl` files of `tables` from `archive` flat into `dir`.
/// A named table the archive does not hold is an error, and then nothing is extracted. The directory
/// appears only when it is whole.
pub fn extract_tables(archive: &Path, dir: &Path, tables: &[&str]) -> Result<(), FetchError> {
    let names: Vec<String> = tables.iter().map(|table| format!("{table}.jsonl")).collect();
    extract_selected(archive, dir, Some(&names))
}

fn extract_selected(archive: &Path, dir: &Path, only: Option<&[String]>) -> Result<(), FetchError> {
    let partial = sibling(dir, ".partial");
    remove_dir_if_present(&partial)?;
    fs::create_dir_all(&partial)?;
    let result = extract_into(archive, &partial, only);
    if result.is_err() {
        let _ = fs::remove_dir_all(&partial);
        return result;
    }
    remove_dir_if_present(dir)?;
    fs::rename(&partial, dir)?;
    Ok(())
}

fn extract_into(archive: &Path, partial: &Path, only: Option<&[String]>) -> Result<(), FetchError> {
    let mut zip = zip::ZipArchive::new(File::open(archive)?)?;
    let mut found = Vec::new();
    for index in 0..zip.len() {
        let mut entry = zip.by_index(index)?;
        if entry.is_dir() {
            continue;
        }
        let member = entry.name()?.into_owned();
        let Some(name) = base_name(&member).map(str::to_owned) else { continue };
        if only.is_some_and(|wanted| name != "_sde.jsonl" && !wanted.contains(&name)) {
            continue;
        }
        io::copy(&mut entry, &mut File::create(partial.join(&name))?)?;
        found.push(name);
    }
    if !partial.join("_sde.jsonl").is_file() {
        return Err(FetchError::BadArchive("it has no _sde.jsonl".to_owned()));
    }
    if let Some(missing) = only.and_then(|wanted| wanted.iter().find(|name| !found.contains(name))) {
        return Err(FetchError::BadArchive(format!("it has no {missing}")));
    }
    Ok(())
}

/// Make sure the archive of `options.build` is on disk and verified, without extracting it. A local archive
/// (`options.archive`) is used in place. Otherwise the archive is the one under the cache, or it is downloaded to
/// `<cache>/<name>.zip`; the caller removes that cached archive when it is done, unless `keep_archive` is set.
pub fn fetch_archive(options: &FetchOptions) -> Result<(PathBuf, Origin), FetchError> {
    let name = archive_name(options.build);
    fs::create_dir_all(&options.cache_dir)?;
    if let Some(archive) = &options.archive {
        verify(archive, &archive.display().to_string(), &options.expect)?;
        return Ok((archive.clone(), Origin::LocalArchive(archive.clone())));
    }
    let archive = options.cache_dir.join(format!("{name}.zip"));
    if archive.is_file() && verify(&archive, &archive.display().to_string(), &options.expect).is_ok() {
        return Ok((archive.clone(), Origin::LocalArchive(archive)));
    }
    if options.sources.is_empty() {
        return Err(FetchError::NoSources);
    }
    let partial = options.cache_dir.join(format!("{name}.zip.partial"));
    let mut failures = Vec::new();
    for url in &options.sources {
        let _ = fs::remove_file(&partial);
        match download(url, &partial).and_then(|()| verify(&partial, url, &options.expect)) {
            Ok(()) => {
                fs::rename(&partial, &archive)?;
                return Ok((archive, Origin::Downloaded(url.clone())));
            }
            Err(FetchError::Download { detail, .. }) => failures.push(format!("{url}: {detail}")),
            Err(error) => failures.push(format!("{url}: {error}")),
        }
    }
    let _ = fs::remove_file(&partial);
    Err(FetchError::AllSourcesFailed(failures))
}

pub fn ensure_extracted(options: &FetchOptions) -> Result<Extracted, FetchError> {
    let name = archive_name(options.build);
    let dir = options.cache_dir.join(&name);
    if dir.join(".complete").is_file() {
        return Ok(Extracted { dir, origin: Origin::Cached });
    }
    fs::create_dir_all(&options.cache_dir)?;

    if let Some(archive) = &options.archive {
        verify(archive, &archive.display().to_string(), &options.expect)?;
        extract(archive, &dir)?;
        return Ok(Extracted { dir, origin: Origin::LocalArchive(archive.clone()) });
    }

    let archive = options.cache_dir.join(format!("{name}.zip"));
    if archive.is_file() && verify(&archive, &archive.display().to_string(), &options.expect).is_ok() {
        extract(&archive, &dir)?;
        if !options.keep_archive {
            let _ = fs::remove_file(&archive);
        }
        return Ok(Extracted { dir, origin: Origin::LocalArchive(archive) });
    }

    if options.sources.is_empty() {
        return Err(FetchError::NoSources);
    }
    let partial = options.cache_dir.join(format!("{name}.zip.partial"));
    let mut failures = Vec::new();
    for url in &options.sources {
        let _ = fs::remove_file(&partial);
        match download(url, &partial).and_then(|()| verify(&partial, url, &options.expect)) {
            Ok(()) => {
                fs::rename(&partial, &archive)?;
                extract(&archive, &dir)?;
                if !options.keep_archive {
                    let _ = fs::remove_file(&archive);
                }
                return Ok(Extracted { dir, origin: Origin::Downloaded(url.clone()) });
            }
            Err(FetchError::Download { detail, .. }) => failures.push(format!("{url}: {detail}")),
            Err(error) => failures.push(format!("{url}: {error}")),
        }
    }
    let _ = fs::remove_file(&partial);
    Err(FetchError::AllSourcesFailed(failures))
}
