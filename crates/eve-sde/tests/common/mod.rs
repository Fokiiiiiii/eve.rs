//! Helpers shared by the fetch and ensure tests.
#![allow(dead_code)]

use std::fs::{self, File};
use std::io::Write;
use std::path::{Path, PathBuf};

use eve_sde::fetch::{self, Expect, FetchOptions};
use zip::write::SimpleFileOptions;

/// A scratch directory under the system temp dir, removed on drop.
pub struct Scratch(pub PathBuf);

impl Scratch {
    pub fn new(name: &str) -> Self {
        let dir = std::env::temp_dir().join(format!("eve-sde-fetch-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        Self(dir)
    }

    pub fn path(&self, name: &str) -> PathBuf {
        self.0.join(name)
    }
}

impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

/// Write a zip with the given `(member name, content)` pairs; the first is deflated, the rest stored.
pub fn make_zip(path: &Path, members: &[(&str, &str)]) {
    let mut writer = zip::ZipWriter::new(File::create(path).unwrap());
    for (index, (name, content)) in members.iter().enumerate() {
        let method =
            if index == 0 { zip::CompressionMethod::Deflated } else { zip::CompressionMethod::Stored };
        let options = SimpleFileOptions::default().compression_method(method);
        if name.ends_with('/') {
            writer.add_directory(*name, options).unwrap();
        } else {
            writer.start_file(*name, options).unwrap();
            writer.write_all(content.as_bytes()).unwrap();
        }
    }
    writer.finish().unwrap();
}

pub fn sde_meta(build: u64) -> String {
    format!("{{\"_key\":\"sde\",\"buildNumber\":{build},\"releaseDate\":\"2026-06-16T11:00:00Z\"}}\n")
}

/// A small valid archive of `build` at `path`; returns its hash.
pub fn mini_archive(path: &Path, build: u64) -> String {
    make_zip(path, &[("_sde.jsonl", &sde_meta(build)), ("types.jsonl", "{\"_key\":587,\"groupID\":25}\n")]);
    fetch::sha256_hex(path).unwrap()
}

pub fn file_url(path: &Path) -> String {
    format!("file://{}", path.display())
}

pub fn options(cache: &Scratch, build: u64, sources: Vec<String>, expect: Expect) -> FetchOptions {
    FetchOptions {
        build,
        cache_dir: cache.path("cache"),
        sources,
        expect,
        archive: None,
        keep_archive: false,
    }
}

pub fn names_in(dir: &Path) -> Vec<String> {
    let mut names: Vec<String> = fs::read_dir(dir)
        .unwrap()
        .map(|entry| entry.unwrap().file_name().to_string_lossy().into_owned())
        .collect();
    names.sort();
    names
}
