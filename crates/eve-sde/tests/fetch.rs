//! Tests for fetching an SDE build: sources, hash pin, extraction. Downloads use
//! `file://` URLs and one loopback HTTP server; the loader half is in ensure.rs.
//! They need `curl` on the PATH, as the fetcher does.

use std::fs;
use std::io::{Read, Write};
use std::net::TcpListener;

use eve_sde::fetch::{self, Expect, FetchError, Origin};

mod common;
use common::{Scratch, file_url, make_zip, mini_archive, names_in, options, sde_meta};

#[test]
fn extract_flattens_marks_complete_and_keeps_every_file_inside_the_directory() {
    let scratch = Scratch::new("extract");
    let archive = scratch.path("a.zip");
    make_zip(
        &archive,
        &[
            ("export/_sde.jsonl", &sde_meta(1)),
            ("export/types.jsonl", "{\"_key\":1}\n"),
            ("export/", ""),
            ("../evil.jsonl", "{\"_key\":666}\n"),
            ("win\\groups.jsonl", "{\"_key\":2}\n"),
        ],
    );
    let dir = scratch.path("out/build");
    fs::create_dir_all(dir.parent().unwrap()).unwrap();
    fetch::extract(&archive, &dir).unwrap();

    assert_eq!(names_in(&dir), [".complete", "_sde.jsonl", "evil.jsonl", "groups.jsonl", "types.jsonl"]);
    assert_eq!(fs::read_to_string(dir.join("types.jsonl")).unwrap(), "{\"_key\":1}\n");
    assert_eq!(names_in(dir.parent().unwrap()), ["build"]);
    assert!(!scratch.path("evil.jsonl").exists());
}

#[test]
fn extract_rejects_an_archive_that_is_not_an_sde_and_leaves_nothing_behind() {
    let scratch = Scratch::new("notsde");
    let archive = scratch.path("a.zip");
    make_zip(&archive, &[("readme.txt", "hello")]);
    let dir = scratch.path("build");
    let error = fetch::extract(&archive, &dir).unwrap_err();
    assert!(matches!(error, FetchError::BadArchive(_)), "{error}");
    assert!(!dir.exists());
    assert!(!scratch.path("build.partial").exists());
}

#[test]
fn extract_replaces_a_stale_directory_only_once_the_new_one_is_whole() {
    let scratch = Scratch::new("replace");
    let dir = scratch.path("build");
    fs::create_dir_all(&dir).unwrap();
    fs::write(dir.join("old.jsonl"), "x").unwrap();
    let bad = scratch.path("bad.zip");
    make_zip(&bad, &[("readme.txt", "hello")]);
    assert!(fetch::extract(&bad, &dir).is_err());
    assert!(dir.join("old.jsonl").exists(), "a failed extract must not destroy what was there");

    let good = scratch.path("good.zip");
    mini_archive(&good, 7);
    fetch::extract(&good, &dir).unwrap();
    assert!(!dir.join("old.jsonl").exists());
    assert!(dir.join("_sde.jsonl").exists());
}

#[test]
fn an_extracted_build_is_used_without_touching_any_source() {
    let scratch = Scratch::new("cached");
    let archive = scratch.path("a.zip");
    let hash = mini_archive(&archive, 11);
    let mut first = options(&scratch, 11, vec![], Expect::Sha256(hash.clone()));
    first.archive = Some(archive.clone());
    let extracted = fetch::ensure_extracted(&first).unwrap();
    assert!(matches!(extracted.origin, Origin::LocalArchive(_)));
    assert!(archive.exists(), "the caller's archive is never deleted");

    let second =
        options(&scratch, 11, vec![file_url(&scratch.path("does-not-exist.zip"))], Expect::Sha256(hash));
    let again = fetch::ensure_extracted(&second).unwrap();
    assert_eq!(again.origin, Origin::Cached);
    assert_eq!(again.dir, extracted.dir);
}

#[test]
fn a_source_that_fails_or_has_the_wrong_hash_is_skipped_for_the_next() {
    let scratch = Scratch::new("fallback");
    let good = scratch.path("good.zip");
    let hash = mini_archive(&good, 21);
    let other = scratch.path("other.zip");
    make_zip(&other, &[("_sde.jsonl", &sde_meta(21)), ("types.jsonl", "{\"_key\":999}\n")]);

    let sources = vec![
        file_url(&scratch.path("missing.zip")),
        file_url(&other), // a valid archive, but not the pinned one
        file_url(&good),
    ];
    let extracted =
        fetch::ensure_extracted(&options(&scratch, 21, sources, Expect::Sha256(hash.to_uppercase())))
            .unwrap();
    assert_eq!(extracted.origin, Origin::Downloaded(file_url(&good)));
    assert_eq!(
        fs::read_to_string(extracted.dir.join("types.jsonl")).unwrap(),
        "{\"_key\":587,\"groupID\":25}\n"
    );
    assert_eq!(names_in(&scratch.path("cache")), ["eve-online-static-data-21-jsonl"]);
}

#[test]
fn keep_archive_leaves_the_downloaded_zip() {
    let scratch = Scratch::new("keep");
    let good = scratch.path("good.zip");
    let hash = mini_archive(&good, 22);
    let mut wanted = options(&scratch, 22, vec![file_url(&good)], Expect::Sha256(hash));
    wanted.keep_archive = true;
    fetch::ensure_extracted(&wanted).unwrap();
    assert_eq!(
        names_in(&scratch.path("cache")),
        ["eve-online-static-data-22-jsonl", "eve-online-static-data-22-jsonl.zip"]
    );
}

#[test]
fn when_every_source_fails_the_error_names_each_and_nothing_is_left() {
    let scratch = Scratch::new("allfail");
    let good = scratch.path("good.zip");
    mini_archive(&good, 31);
    let text = scratch.path("not-a-zip.zip");
    fs::write(&text, "this is not a zip").unwrap();
    let wrong_hash = "0".repeat(64);
    let sources = vec![file_url(&good), file_url(&text), file_url(&scratch.path("missing.zip"))];

    let error =
        fetch::ensure_extracted(&options(&scratch, 31, sources, Expect::Sha256(wrong_hash))).unwrap_err();
    let FetchError::AllSourcesFailed(failures) = error else { panic!("wrong error: {error}") };
    assert_eq!(failures.len(), 3);
    assert!(failures[0].contains("sha256"), "{}", failures[0]);
    assert!(failures[1].contains("sha256"), "{}", failures[1]);
    assert!(failures[2].contains("missing.zip"), "{}", failures[2]);
    assert!(names_in(&scratch.path("cache")).is_empty());
}

#[test]
fn unchecked_still_refuses_what_is_not_an_sde_archive() {
    let scratch = Scratch::new("unchecked");
    let text = scratch.path("t.zip");
    fs::write(&text, "not a zip").unwrap();
    let good = scratch.path("g.zip");
    mini_archive(&good, 41);
    let error = fetch::ensure_extracted(&options(&scratch, 41, vec![file_url(&text)], Expect::Unchecked))
        .unwrap_err();
    assert!(matches!(error, FetchError::AllSourcesFailed(_)), "{error}");
    let extracted =
        fetch::ensure_extracted(&options(&scratch, 41, vec![file_url(&good)], Expect::Unchecked)).unwrap();
    assert!(matches!(extracted.origin, Origin::Downloaded(_)));
}

#[test]
fn a_zip_that_is_not_an_sde_falls_through_to_the_next_source() {
    let scratch = Scratch::new("wrongzip");
    let wrong = scratch.path("wrong.zip");
    make_zip(&wrong, &[("readme.txt", "hello")]);
    let good = scratch.path("good.zip");
    mini_archive(&good, 45);
    let sources = vec![file_url(&wrong), file_url(&good)];
    let extracted = fetch::ensure_extracted(&options(&scratch, 45, sources, Expect::Unchecked)).unwrap();
    assert_eq!(extracted.origin, Origin::Downloaded(file_url(&good)));
}

#[test]
fn no_sources_and_no_archive_is_an_error_not_a_silent_success() {
    let scratch = Scratch::new("nosources");
    let error = fetch::ensure_extracted(&options(&scratch, 51, vec![], Expect::Unchecked)).unwrap_err();
    assert!(matches!(error, FetchError::NoSources), "{error}");
}

/// Serve `routes` (path, status, body) on a loopback port, one connection each in order of arrival.
fn serve(routes: Vec<(&'static str, u16, Vec<u8>)>) -> String {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let base = format!("http://127.0.0.1:{}", listener.local_addr().unwrap().port());
    std::thread::spawn(move || {
        for stream in listener.incoming().take(routes.len() * 2) {
            let Ok(mut stream) = stream else { continue };
            let mut request = Vec::new();
            let mut buffer = [0u8; 1024];
            while !request.windows(4).any(|window| window == b"\r\n\r\n") {
                match stream.read(&mut buffer) {
                    Ok(0) | Err(_) => break,
                    Ok(read) => request.extend_from_slice(&buffer[..read]),
                }
            }
            let request = String::from_utf8_lossy(&request);
            let path = request.split_whitespace().nth(1).unwrap_or("/");
            let (status, body) = routes
                .iter()
                .find(|(route, _, _)| *route == path)
                .map_or((404, Vec::new()), |(_, status, body)| (*status, body.clone()));
            let head =
                format!("HTTP/1.1 {status} X\r\nContent-Length: {}\r\nConnection: close\r\n\r\n", body.len());
            let _ = stream.write_all(head.as_bytes());
            let _ = stream.write_all(&body);
        }
    });
    base
}

#[test]
fn an_http_error_moves_on_to_the_next_source() {
    let scratch = Scratch::new("http");
    let good = scratch.path("good.zip");
    let hash = mini_archive(&good, 61);
    let bytes = fs::read(&good).unwrap();
    let base = serve(vec![("/gone.zip", 404, b"nope".to_vec()), ("/build.zip", 200, bytes)]);

    let sources = vec![format!("{base}/gone.zip"), format!("{base}/build.zip")];
    let extracted = fetch::ensure_extracted(&options(&scratch, 61, sources, Expect::Sha256(hash))).unwrap();
    assert_eq!(extracted.origin, Origin::Downloaded(format!("{base}/build.zip")));
    assert!(extracted.dir.join("_sde.jsonl").is_file());
}

#[test]
fn latest_build_is_the_sde_record_of_latest_jsonl() {
    let scratch = Scratch::new("latest");
    let dir = scratch.path("static/tranquility");
    fs::create_dir_all(&dir).unwrap();
    fs::write(
        dir.join("latest.jsonl"),
        "{\"_key\":\"other\",\"buildNumber\":1}\n{\"_key\":\"sde\",\"buildNumber\":3400001,\"releaseDate\":\"x\"}\n",
    )
    .unwrap();
    let base = format!("file://{}", scratch.path("static").display());
    assert_eq!(fetch::latest_build(&base).unwrap(), 3_400_001);
    assert_eq!(fetch::latest_build(&format!("{base}/")).unwrap(), 3_400_001, "a trailing slash is fine");

    fs::write(dir.join("latest.jsonl"), "{\"_key\":\"other\",\"buildNumber\":1}\n").unwrap();
    assert!(matches!(fetch::latest_build(&base).unwrap_err(), FetchError::Download { .. }));
}

#[test]
fn the_compiled_in_lock_pins_a_build_with_a_hash_and_sources() {
    let pin = fetch::pinned().unwrap();
    assert_eq!(pin.build, 3_396_210);
    let hash = pin.sha256.expect("the pinned build has a hash");
    assert_eq!(hash.len(), 64);
    assert!(hash.bytes().all(|byte| byte.is_ascii_hexdigit()));
    assert!(pin.sources.iter().all(|url| url.starts_with("https://")), "{:?}", pin.sources);
    assert_eq!(
        pin.sources[0],
        fetch::official_url(fetch::OFFICIAL_BASE, 3_396_210),
        "CCP's host comes first"
    );
}

#[test]
fn a_build_the_lock_does_not_list_has_no_hash_and_only_the_official_source() {
    let pin = fetch::pin_from(fetch::LOCK, Some(1_000_000)).unwrap();
    assert_eq!(pin.sha256, None);
    assert_eq!(pin.sources, [fetch::official_url(fetch::OFFICIAL_BASE, 1_000_000)]);
    assert_eq!(
        fetch::official_url("https://example.test/static-data/", 5),
        "https://example.test/static-data/tranquility/eve-online-static-data-5-jsonl.zip"
    );
}

#[test]
fn pin_from_rejects_a_lock_without_a_pinned_build() {
    let error = fetch::pin_from("{\"builds\":{}}", None).unwrap_err();
    assert!(matches!(error, FetchError::Lock(_)), "{error}");
    let error = fetch::pin_from("not json", None).unwrap_err();
    assert!(matches!(error, FetchError::Lock(_)), "{error}");

    let lock = "{\"pinned\":\"9\",\"builds\":{\"9\":{\"sha256\":\"ab\",\"sources\":[\"https://a.test/9.zip\",\"https://b.test/9.zip\"]}}}";
    let pin = fetch::pin_from(lock, None).unwrap();
    assert_eq!(pin.build, 9);
    assert_eq!(pin.sha256.as_deref(), Some("ab"));
    assert_eq!(pin.sources, ["https://a.test/9.zip", "https://b.test/9.zip"]);
}

#[test]
fn sha256_hex_is_the_standard_digest() {
    let scratch = Scratch::new("sha");
    fs::write(scratch.path("abc"), "abc").unwrap();
    assert_eq!(
        fetch::sha256_hex(&scratch.path("abc")).unwrap(),
        "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
    );
}
