use eve_net::host::{Endpoint, HostLink};
use eve_net::server::{ServeConfig, serve};
use std::sync::Arc;
use tokio::net::TcpListener;

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<_> = std::env::args().collect();
    if args.len() != 3 {
        return Err("usage: run_front HOST_ENDPOINT LISTEN_ADDRESS".into());
    }
    let config = ServeConfig::default();
    let link = Arc::new(HostLink::connect(&Endpoint::parse(&args[1]), config.node_id).await?);
    let listener = TcpListener::bind(&args[2]).await?;
    eprintln!("eve-net: listening on {}", listener.local_addr()?);
    serve(listener, link, config, async {
        let _ = tokio::signal::ctrl_c().await;
    })
    .await?;
    Ok(())
}
