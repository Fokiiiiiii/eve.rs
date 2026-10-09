//! eve-net: the network front.
//!
//!   EVE_NET_LISTEN   address clients connect to           (default 127.0.0.1:26000)
//!   EVE_HOST         the service host: a socket path, `unix:/path` or `tcp:host:port`
//!                                                          (default /tmp/eve-host.sock; tcp:127.0.0.1:26010 off Unix)
//!   EVE_NODE_ID      the proxy node id clients are told   (default 65450)
//!
//! The host is waited for (up to 60 s) so the two can be started in either order.

use std::process::ExitCode;
use std::sync::Arc;
use std::time::Duration;

use eve_net::host::{DEFAULT_HOST, Endpoint, HostLink};
use eve_net::profile::PROXY_NODE_ID;
use eve_net::server::{ServeConfig, serve};
use tokio::net::TcpListener;

fn env_or(name: &str, default: &str) -> String {
    std::env::var(name).ok().filter(|value| !value.is_empty()).unwrap_or_else(|| default.to_owned())
}

async fn connect_with_retry(endpoint: &Endpoint, node_id: u32) -> Result<HostLink, String> {
    let deadline = tokio::time::Instant::now() + Duration::from_secs(60);
    loop {
        match HostLink::connect(endpoint, node_id).await {
            Ok(link) => return Ok(link),
            Err(error) if tokio::time::Instant::now() < deadline => {
                eprintln!("eve-net: waiting for the host ({error})");
                tokio::time::sleep(Duration::from_millis(500)).await;
            }
            Err(error) => return Err(format!("cannot reach the host: {error}")),
        }
    }
}

#[tokio::main]
async fn main() -> ExitCode {
    let listen = env_or("EVE_NET_LISTEN", "127.0.0.1:26000");
    let host = Endpoint::parse(&env_or("EVE_HOST", DEFAULT_HOST));
    let node_id: u32 = match env_or("EVE_NODE_ID", &PROXY_NODE_ID.to_string()).parse() {
        Ok(id) => id,
        Err(_) => {
            eprintln!("eve-net: EVE_NODE_ID must be a number");
            return ExitCode::from(2);
        }
    };

    let link = match connect_with_retry(&host, node_id).await {
        Ok(link) => Arc::new(link),
        Err(error) => {
            eprintln!("eve-net: {error}");
            return ExitCode::FAILURE;
        }
    };
    let listener = match TcpListener::bind(&listen).await {
        Ok(listener) => listener,
        Err(error) => {
            eprintln!("eve-net: cannot listen on {listen}: {error}");
            return ExitCode::FAILURE;
        }
    };
    eprintln!("eve-net: listening on {listen}, host {host:?}, node {node_id}");

    let shutdown = async {
        let _ = tokio::signal::ctrl_c().await;
    };
    match serve(listener, link, ServeConfig { node_id, ..ServeConfig::default() }, shutdown).await {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("eve-net: {error}");
            ExitCode::FAILURE
        }
    }
}
