//! The client-facing server: accepts TCP connections, runs the handshake, then relays packets between
//! the client and the host until either side goes away.

use std::future::Future;
use std::io;
use std::net::SocketAddr;
use std::sync::Arc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use bytes::{Bytes, BytesMut};
use tokio::io::{AsyncWrite, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};

use crate::frame::{FrameReader, MAX_FRAME, put_frame};
use crate::handshake::{self, Environment, HandshakeError, SessionIds};
use crate::host::{ClientOut, HostLink, client_queue};

#[derive(Debug, Clone)]
pub struct ServeConfig {
    /// The node id clients are told they are connected to.
    pub node_id: u32,
    /// How long a client has to complete the handshake.
    pub handshake_timeout: Duration,
}

impl Default for ServeConfig {
    fn default() -> Self {
        Self { node_id: crate::profile::PROXY_NODE_ID, handshake_timeout: Duration::from_secs(30) }
    }
}

fn now_ms() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |elapsed| elapsed.as_millis() as u64)
}

/// Serves clients until `shutdown` resolves or the link to the host goes down.
pub async fn serve(
    listener: TcpListener,
    link: Arc<HostLink>,
    config: ServeConfig,
    shutdown: impl Future<Output = ()>,
) -> io::Result<()> {
    let config = Arc::new(config);
    let signed_func = handshake::tidi_signed_func();
    let session_ids = Arc::new(SessionIds::default());
    tokio::pin!(shutdown);
    loop {
        tokio::select! {
            accepted = listener.accept() => {
                let (stream, peer) = accepted?;
                tokio::spawn(connection(stream, peer, link.clone(), config.clone(), signed_func.clone(), session_ids.clone()));
            }
            () = &mut shutdown => return Ok(()),
            () = link.closed() => return Err(io::Error::new(io::ErrorKind::ConnectionAborted, "the link to the host closed")),
        }
    }
}

async fn connection(
    stream: TcpStream,
    peer: SocketAddr,
    link: Arc<HostLink>,
    config: Arc<ServeConfig>,
    signed_func: Bytes,
    session_ids: Arc<SessionIds>,
) {
    let _ = stream.set_nodelay(true);
    let (read, mut write) = stream.into_split();
    let mut reader = FrameReader::new(read, MAX_FRAME);

    let environment = Environment {
        node_id: config.node_id,
        user_count: link.user_count(),
        config_vals: link.cluster_config(),
        signed_func,
        session_ids,
    };
    let address = peer.ip().to_string();
    let attempt = handshake::perform(&mut reader, &mut write, &*link, &environment, &address, now_ms());
    let login = match tokio::time::timeout(config.handshake_timeout, attempt).await {
        Ok(Ok(login)) => login,
        Ok(Err(HandshakeError::Closed)) => return,
        Ok(Err(error)) => {
            eprintln!("eve-net: {peer}: {error}");
            return;
        }
        Err(_) => {
            eprintln!("eve-net: {peer}: the handshake did not finish in {:?}", config.handshake_timeout);
            return;
        }
    };

    let session_id = login.session_id;
    let (queue, outgoing) = client_queue();
    if link.open_session(&login, queue).await.is_err() {
        return;
    }
    let mut writer = tokio::spawn(client_writer(write, outgoing));

    // Relay until the client leaves, the host cuts the client off, or the link fails.
    let relay = async {
        loop {
            match reader.next().await {
                Ok(Some(packet)) => {
                    if link.packet(session_id, packet).await.is_err() {
                        break;
                    }
                }
                Ok(None) => break,
                Err(error) => {
                    eprintln!("eve-net: {peer}: {error}");
                    break;
                }
            }
        }
    };
    tokio::select! {
        () = relay => {}
        _ = &mut writer => {}
    }
    link.close_session(session_id).await;
    writer.abort();
}

/// Writes what the host sends for this client, batching what has piled up into one write.
async fn client_writer<W: AsyncWrite + Unpin>(
    mut writer: W,
    mut outgoing: tokio::sync::mpsc::Receiver<ClientOut>,
) {
    let mut buffer = BytesMut::with_capacity(64 * 1024);
    while let Some(next) = outgoing.recv().await {
        let mut closing = None;
        match next {
            ClientOut::Packet(payload) => put_frame(&mut buffer, &payload),
            ClientOut::Close(reason) => closing = Some(reason),
        }
        while closing.is_none() && buffer.len() < 256 * 1024 {
            match outgoing.try_recv() {
                Ok(ClientOut::Packet(payload)) => put_frame(&mut buffer, &payload),
                Ok(ClientOut::Close(reason)) => closing = Some(reason),
                Err(_) => break,
            }
        }
        if let Some(reason) = &closing
            && let Ok(bytes) = eve_marshal::encode(&handshake::transport_closed(reason))
        {
            put_frame(&mut buffer, &bytes);
        }
        if writer.write_all(&buffer).await.is_err() {
            return;
        }
        buffer.clear();
        if closing.is_some() {
            let _ = writer.shutdown().await;
            return;
        }
    }
}
