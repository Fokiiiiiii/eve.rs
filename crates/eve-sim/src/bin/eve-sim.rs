//! eve-sim: the space simulation.
//!
//!   EVE_SIM_SOCKET   the Unix socket the host connects to        (default /tmp/eve-sim.sock)
//!   EVE_SDE_DIR      the static data (the SDE's JSONL files)      (default: .cache/sde, as the tools find it)
//!
//! One host at a time; a host that goes away takes its ships with it.

use std::path::PathBuf;
use std::process::ExitCode;
use std::sync::Arc;

use eve_sim::{filetime_now, serve_connection};
use eve_space::sde::Sde;
use eve_space::sim::Universe;
use eve_space::world::World;
use tokio::net::UnixListener;

fn env_or(name: &str, default: &str) -> String {
    std::env::var(name).ok().filter(|value| !value.is_empty()).unwrap_or_else(|| default.to_owned())
}

#[tokio::main(flavor = "current_thread")]
async fn main() -> ExitCode {
    let socket = PathBuf::from(env_or("EVE_SIM_SOCKET", "/tmp/eve-sim.sock"));
    let Some(sde) = Sde::locate() else {
        eprintln!("eve-sim: no static data found (set EVE_SDE_DIR to the SDE's JSONL directory)");
        return ExitCode::FAILURE;
    };
    let started = std::time::Instant::now();
    let world = match World::load(sde) {
        Ok(world) => world,
        Err(error) => {
            eprintln!("eve-sim: cannot read the static data: {error}");
            return ExitCode::FAILURE;
        }
    };
    eprintln!("eve-sim: static data read in {:.1}s", started.elapsed().as_secs_f64());
    let universe = Arc::new(Universe::new(world));

    // a socket file left by an earlier run is in the way; a live one is not ours to take
    if socket.exists() {
        if tokio::net::UnixStream::connect(&socket).await.is_ok() {
            eprintln!("eve-sim: {} is in use by another simulation", socket.display());
            return ExitCode::FAILURE;
        }
        let _ = std::fs::remove_file(&socket);
    }
    let listener = match UnixListener::bind(&socket) {
        Ok(listener) => listener,
        Err(error) => {
            eprintln!("eve-sim: cannot listen on {}: {error}", socket.display());
            return ExitCode::FAILURE;
        }
    };
    eprintln!("eve-sim: listening on {}", socket.display());

    loop {
        tokio::select! {
            accepted = listener.accept() => match accepted {
                Ok((stream, _)) => {
                    eprintln!("eve-sim: the host connected");
                    // one host at a time: the next one waits until this one is done
                    match serve_connection(stream, Arc::clone(&universe), filetime_now).await {
                        Ok(()) => eprintln!("eve-sim: the host went away"),
                        Err(error) => eprintln!("eve-sim: the host connection ended: {error}"),
                    }
                }
                Err(error) => eprintln!("eve-sim: accept failed: {error}"),
            },
            _ = tokio::signal::ctrl_c() => break,
        }
    }
    let _ = std::fs::remove_file(&socket);
    ExitCode::SUCCESS
}
