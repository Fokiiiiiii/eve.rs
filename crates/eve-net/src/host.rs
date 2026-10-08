//! The client end of the link to the service host (`docs/design/adr/0002-front-host-link.md`): one connection,
//! shared by every client session the front serves.
//!
//! Outgoing messages go through one queue to a writer that batches what has piled up. A reader routes what
//! the host sends: replies to the login that asked, packets to the session they are for, the cluster
//! configuration to whoever starts a handshake. A client that cannot keep up is cut off instead of holding
//! back the link.

use std::collections::HashMap;
use std::io;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use bytes::{Bytes, BytesMut};
use eve_marshal::{Value, decode_bytes};
use tokio::io::{AsyncRead, AsyncWrite, AsyncWriteExt, split};
use tokio::sync::{mpsc, oneshot, watch};

use crate::frame::{FrameReader, MAX_FRAME};
use crate::handshake::{AuthError, AuthRequest, Authenticator, Login};
use crate::hostlink::{AuthOutcome, Message, PROTOCOL_VERSION, SessionOpen};

/// Where the host listens.
#[derive(Debug, Clone)]
pub enum Endpoint {
    #[cfg(unix)]
    Unix(PathBuf),
    /// `host:port`, for development on a machine without Unix sockets.
    Tcp(String),
}

/// What the host asks a client's connection to do.
#[derive(Debug)]
pub enum ClientOut {
    /// Write this marshal payload to the client, framed.
    Packet(Bytes),
    /// Tell the client the transport is closed for `reason`, and close it.
    Close(String),
}

#[derive(Debug)]
pub enum LinkError {
    Io(io::Error),
    /// The host said something the protocol does not allow.
    Protocol(String),
    /// The link is down.
    Closed,
}

impl std::fmt::Display for LinkError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Io(error) => write!(f, "{error}"),
            Self::Protocol(detail) => write!(f, "host link protocol error: {detail}"),
            Self::Closed => write!(f, "the host link is closed"),
        }
    }
}

impl std::error::Error for LinkError {}

impl From<io::Error> for LinkError {
    fn from(error: io::Error) -> Self {
        Self::Io(error)
    }
}

/// How many packets may wait for a client before it is cut off.
const CLIENT_QUEUE: usize = 1024;
const LINK_QUEUE: usize = 4096;
const AUTH_TIMEOUT: Duration = Duration::from_secs(15);
const HELLO_TIMEOUT: Duration = Duration::from_secs(10);

struct Shared {
    next_corr: AtomicU32,
    pending: Mutex<HashMap<u32, oneshot::Sender<AuthOutcome>>>,
    sessions: Mutex<HashMap<u64, mpsc::Sender<ClientOut>>>,
    config: Mutex<Arc<Value>>,
}

/// The front's connection to the service host.
pub struct HostLink {
    outbound: mpsc::Sender<Message>,
    shared: Arc<Shared>,
    closed: watch::Receiver<bool>,
}

impl HostLink {
    /// Connects to `endpoint` and runs the hello exchange.
    pub async fn connect(endpoint: &Endpoint, node_id: u32) -> Result<Self, LinkError> {
        match endpoint {
            #[cfg(unix)]
            Endpoint::Unix(path) => Self::attach(tokio::net::UnixStream::connect(path).await?, node_id).await,
            Endpoint::Tcp(address) => {
                let stream = tokio::net::TcpStream::connect(address).await?;
                stream.set_nodelay(true)?;
                Self::attach(stream, node_id).await
            }
        }
    }

    /// Runs the hello exchange over `stream`, which must be a fresh connection to a host.
    pub async fn attach<S>(stream: S, node_id: u32) -> Result<Self, LinkError>
    where
        S: AsyncRead + AsyncWrite + Send + 'static,
    {
        let (read, mut write) = split(stream);
        let mut reader = FrameReader::new(read, MAX_FRAME);

        let mut hello = BytesMut::new();
        Message::Hello { version: PROTOCOL_VERSION, node_id }.encode(&mut hello);
        write.write_all(&hello).await?;

        let first = tokio::time::timeout(HELLO_TIMEOUT, async {
            let ack = next_message(&mut reader).await?;
            let config = next_message(&mut reader).await?;
            Ok::<_, LinkError>((ack, config))
        })
        .await
        .map_err(|_| LinkError::Protocol("the host did not answer the hello".into()))??;
        let (Message::HelloAck { version }, Message::ClusterConfig { payload }) = first else {
            return Err(LinkError::Protocol("expected HelloAck and ClusterConfig".into()));
        };
        if version != PROTOCOL_VERSION {
            return Err(LinkError::Protocol(format!(
                "the host speaks version {version}, this front {PROTOCOL_VERSION}"
            )));
        }
        let config = decode_bytes(&payload)
            .map_err(|error| LinkError::Protocol(format!("undecodable ClusterConfig: {error}")))?;

        let shared = Arc::new(Shared {
            next_corr: AtomicU32::new(1),
            pending: Mutex::new(HashMap::new()),
            sessions: Mutex::new(HashMap::new()),
            config: Mutex::new(Arc::new(config)),
        });
        let (outbound, queue) = mpsc::channel(LINK_QUEUE);
        let (closed_tx, closed) = watch::channel(false);
        tokio::spawn(write_loop(write, queue));
        tokio::spawn(read_loop(reader, shared.clone(), closed_tx));
        Ok(Self { outbound, shared, closed })
    }

    /// The global configuration the client is handed at login.
    pub fn cluster_config(&self) -> Value {
        (**self.shared.config.lock().unwrap()).clone()
    }

    /// How many sessions this front has open.
    pub fn user_count(&self) -> u32 {
        self.shared.sessions.lock().unwrap().len() as u32
    }

    /// Resolves when the link to the host is down.
    pub async fn closed(&self) {
        let mut closed = self.closed.clone();
        let _ = closed.wait_for(|down| *down).await;
    }

    /// Registers the client's outgoing queue and tells the host a session is open.
    pub async fn open_session(&self, login: &Login, out: mpsc::Sender<ClientOut>) -> Result<(), LinkError> {
        self.shared.sessions.lock().unwrap().insert(login.session_id, out);
        let open = SessionOpen {
            session_id: login.session_id,
            client_id: login.client_id,
            user_id: login.user_id,
            role: login.role,
            address: login.address.clone(),
            language: login.language.clone(),
            country_code: login.country_code.clone(),
        };
        self.outbound.send(Message::SessionOpen(open)).await.map_err(|_| LinkError::Closed)
    }

    /// Forwards a client packet to the host.
    pub async fn packet(&self, session_id: u64, payload: Bytes) -> Result<(), LinkError> {
        self.outbound.send(Message::Packet { session_id, payload }).await.map_err(|_| LinkError::Closed)
    }

    /// Tells the host the client is gone.
    pub async fn close_session(&self, session_id: u64) {
        self.shared.sessions.lock().unwrap().remove(&session_id);
        let _ = self.outbound.send(Message::SessionClose { session_id }).await;
    }
}

impl Authenticator for HostLink {
    async fn authenticate(&self, request: AuthRequest) -> Result<AuthOutcome, AuthError> {
        let corr = self.shared.next_corr.fetch_add(1, Ordering::Relaxed);
        let (reply, answer) = oneshot::channel();
        self.shared.pending.lock().unwrap().insert(corr, reply);
        let message = Message::AuthRequest {
            corr,
            user_name: request.user_name,
            password_hash: request.password_hash,
            language: request.language,
            address: request.address,
        };
        if self.outbound.send(message).await.is_err() {
            self.shared.pending.lock().unwrap().remove(&corr);
            return Err(AuthError("the host link is closed".into()));
        }
        match tokio::time::timeout(AUTH_TIMEOUT, answer).await {
            Ok(Ok(outcome)) => Ok(outcome),
            Ok(Err(_)) => Err(AuthError("the host link closed before it answered".into())),
            Err(_) => {
                self.shared.pending.lock().unwrap().remove(&corr);
                Err(AuthError("the host did not answer in time".into()))
            }
        }
    }
}

async fn next_message<R: AsyncRead + Unpin>(reader: &mut FrameReader<R>) -> Result<Message, LinkError> {
    let frame = reader.next().await?.ok_or(LinkError::Closed)?;
    Message::decode(frame).map_err(|error| LinkError::Protocol(error.to_string()))
}

/// Writes queued messages, batching whatever has piled up into one write.
async fn write_loop<W: AsyncWrite + Unpin>(mut writer: W, mut queue: mpsc::Receiver<Message>) {
    let mut buffer = BytesMut::with_capacity(64 * 1024);
    while let Some(message) = queue.recv().await {
        message.encode(&mut buffer);
        while buffer.len() < 256 * 1024 {
            match queue.try_recv() {
                Ok(next) => next.encode(&mut buffer),
                Err(_) => break,
            }
        }
        if writer.write_all(&buffer).await.is_err() {
            return;
        }
        buffer.clear();
    }
    let _ = writer.shutdown().await;
}

async fn read_loop<R: AsyncRead + Unpin>(
    mut reader: FrameReader<R>,
    shared: Arc<Shared>,
    closed: watch::Sender<bool>,
) {
    loop {
        let message = match next_message(&mut reader).await {
            Ok(message) => message,
            Err(error) => {
                if !matches!(error, LinkError::Closed) {
                    eprintln!("eve-net: the host link failed: {error}");
                }
                break;
            }
        };
        match message {
            Message::AuthReply { corr, outcome } => {
                if let Some(reply) = shared.pending.lock().unwrap().remove(&corr) {
                    let _ = reply.send(outcome);
                }
            }
            Message::Send { session_id, payload } => {
                let mut sessions = shared.sessions.lock().unwrap();
                if let Some(queue) = sessions.get(&session_id) {
                    match queue.try_send(ClientOut::Packet(payload)) {
                        Ok(()) => {}
                        // A client that cannot keep up is cut off rather than allowed to hold up the link.
                        Err(_) => {
                            sessions.remove(&session_id);
                        }
                    }
                }
            }
            Message::Disconnect { session_id, reason } => {
                if let Some(queue) = shared.sessions.lock().unwrap().remove(&session_id) {
                    let _ = queue.try_send(ClientOut::Close(reason));
                }
            }
            Message::ClusterConfig { payload } => match decode_bytes(&payload) {
                Ok(config) => *shared.config.lock().unwrap() = Arc::new(config),
                Err(error) => eprintln!("eve-net: ignoring an undecodable ClusterConfig: {error}"),
            },
            other => {
                eprintln!("eve-net: the host sent a message only a front may send: {other:?}");
                break;
            }
        }
    }
    // The link is down: nothing more will arrive for anyone.
    shared.pending.lock().unwrap().clear();
    shared.sessions.lock().unwrap().clear();
    let _ = closed.send(true);
}

/// A queue for a new client connection.
pub fn client_queue() -> (mpsc::Sender<ClientOut>, mpsc::Receiver<ClientOut>) {
    mpsc::channel(CLIENT_QUEUE)
}

impl Endpoint {
    /// `unix:/path`, `tcp:host:port`, or a bare path.
    pub fn parse(text: &str) -> Self {
        if let Some(address) = text.strip_prefix("tcp:") {
            return Self::Tcp(address.to_owned());
        }
        #[cfg(unix)]
        {
            Self::Unix(PathBuf::from(text.strip_prefix("unix:").unwrap_or(text)))
        }
        #[cfg(not(unix))]
        {
            Self::Tcp(text.to_owned())
        }
    }
}
