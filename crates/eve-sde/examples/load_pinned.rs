//! Load the pinned local SDE archive using the production loader, without another runtime.
use eve_sde::{ensure, fetch};
use std::path::PathBuf;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<_> = std::env::args().collect();
    if args.len() != 4 {
        return Err("usage: load_pinned ARCHIVE PG_URL CACHE_DIR".into());
    }
    let pin = fetch::pinned()?;
    let options = fetch::FetchOptions {
        build: pin.build,
        cache_dir: PathBuf::from(&args[3]),
        sources: pin.sources,
        expect: fetch::Expect::Sha256(pin.sha256.ok_or("the pinned archive has no SHA-256")?),
        archive: Some(PathBuf::from(&args[1])),
        keep_archive: true,
    };
    let mut client = postgres::Client::connect(&args[2], postgres::NoTls)?;
    println!("{:?}", ensure::ensure_loaded(&mut client, &options)?);
    Ok(())
}
