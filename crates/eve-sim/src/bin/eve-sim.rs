//! eve-sim: the space simulation.
//!
//!   EVE_SIM_SOCKET   the Unix socket the host connects to        (default /tmp/eve-sim.sock)
//!   EVE_SDE_DIR      the static data (the SDE's JSONL files)      (default: .cache/sde, as the tools find it)
//!
//! One host at a time; a host that goes away takes its ships with it.

#[cfg(unix)]
use std::path::PathBuf;
use std::process::ExitCode;
use std::sync::Arc;

use eve_sim::{filetime_now, serve_connection};
use eve_space::sde::Sde;
use eve_space::sim::Universe;
use eve_space::world::World;
use tokio::net::TcpListener;
#[cfg(unix)]
use tokio::net::{UnixListener, UnixStream};

#[cfg(unix)]
fn env_or(name: &str, default: &str) -> String {
    std::env::var(name).ok().filter(|value| !value.is_empty()).unwrap_or_else(|| default.to_owned())
}

async fn serve_tcp(address: &str, universe: Arc<Universe>) -> std::io::Result<()> {
    let listener = TcpListener::bind(address).await?;
    eprintln!("eve-sim: listening on {}", listener.local_addr()?);
    loop {
        tokio::select! {
            accepted = listener.accept() => match accepted {
                Ok((stream, _)) => {
                    eprintln!("eve-sim: the host connected");
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
    Ok(())
}

#[cfg(unix)]
async fn serve_unix(socket: PathBuf, universe: Arc<Universe>) -> std::io::Result<()> {
    if socket.exists() {
        if UnixStream::connect(&socket).await.is_ok() {
            return Err(std::io::Error::new(
                std::io::ErrorKind::AddrInUse,
                format!("{} is in use by another simulation", socket.display()),
            ));
        }
        let _ = std::fs::remove_file(&socket);
    }
    let listener = UnixListener::bind(&socket)?;
    eprintln!("eve-sim: listening on {}", socket.display());
    loop {
        tokio::select! {
            accepted = listener.accept() => match accepted {
                Ok((stream, _)) => {
                    eprintln!("eve-sim: the host connected");
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
    Ok(())
}

#[tokio::main(flavor = "current_thread")]
async fn main() -> ExitCode {
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

    if let Ok(address) = std::env::var("EVE_SIM_LISTEN") {
        return match serve_tcp(&address, universe).await {
            Ok(()) => ExitCode::SUCCESS,
            Err(error) => {
                eprintln!("eve-sim: cannot listen on {address}: {error}");
                ExitCode::FAILURE
            }
        };
    }

    #[cfg(unix)]
    {
        let socket = PathBuf::from(env_or("EVE_SIM_SOCKET", "/tmp/eve-sim.sock"));
        match serve_unix(socket, universe).await {
            Ok(()) => ExitCode::SUCCESS,
            Err(error) => {
                eprintln!("eve-sim: cannot listen on the Unix socket: {error}");
                ExitCode::FAILURE
            }
        }
    }
    #[cfg(not(unix))]
    {
        eprintln!("eve-sim: set EVE_SIM_LISTEN to a local TCP address on this platform");
        ExitCode::FAILURE
    }
}
