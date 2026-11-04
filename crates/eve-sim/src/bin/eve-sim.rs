//!   EVE_SIM_SOCKET   the Unix socket the host connects to        (default /tmp/eve-sim.sock)
//!   EVE_SIM_LISTEN   a TCP address to listen on instead, on any platform (off Unix: 127.0.0.1:26011)
//!   EVE_SDE_DIR      JSONL static data directory                  (default: .cache/sde)
//!

#[cfg(unix)]
use std::path::PathBuf;
use std::process::ExitCode;
use std::sync::Arc;

use eve_sim::{filetime_now, serve_connection};
use eve_space::sde::Sde;
use eve_space::sim::Universe;
use eve_space::world::World;
use tokio::io::{AsyncRead, AsyncWrite};
use tokio::net::TcpListener;
#[cfg(unix)]
use tokio::net::{UnixListener, UnixStream};

/// Used where Unix sockets are unavailable.
#[cfg(not(unix))]
const DEFAULT_SIM_LISTEN: &str = "127.0.0.1:26011";

#[cfg(unix)]
fn env_or(name: &str, default: &str) -> String {
    std::env::var(name).ok().filter(|value| !value.is_empty()).unwrap_or_else(|| default.to_owned())
}

/// Returns true when Ctrl-C arrives first; the accept loop pauses while a host is connected.
async fn serve_host<S>(stream: S, universe: Arc<Universe>) -> bool
where
    S: AsyncRead + AsyncWrite + Unpin,
{
    tokio::select! {
        served = serve_connection(stream, universe, filetime_now) => {
            match served {
                Ok(()) => eprintln!("eve-sim: the host went away"),
                Err(error) => eprintln!("eve-sim: the host connection ended: {error}"),
            }
            false
        }
        _ = tokio::signal::ctrl_c() => true,
    }
}

async fn serve_tcp(address: &str, universe: Arc<Universe>) -> std::io::Result<()> {
    let listener = TcpListener::bind(address).await?;
    eprintln!("eve-sim: listening on {}", listener.local_addr()?);
    loop {
        tokio::select! {
            accepted = listener.accept() => match accepted {
                Ok((stream, _)) => {
                    eprintln!("eve-sim: the host connected");
                    if serve_host(stream, Arc::clone(&universe)).await {
                        break;
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
                    if serve_host(stream, Arc::clone(&universe)).await {
                        break;
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

    let served = match std::env::var("EVE_SIM_LISTEN").ok().filter(|value| !value.is_empty()) {
        Some(address) => serve_tcp(&address, universe).await,
        #[cfg(unix)]
        None => serve_unix(PathBuf::from(env_or("EVE_SIM_SOCKET", "/tmp/eve-sim.sock")), universe).await,
        #[cfg(not(unix))]
        None => serve_tcp(DEFAULT_SIM_LISTEN, universe).await,
    };
    match served {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("eve-sim: cannot listen: {error}");
            ExitCode::FAILURE
        }
    }
}
