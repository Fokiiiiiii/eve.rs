//! The login handshake, server side. Only the Placebo crypto pack is spoken: nothing is encrypted.
//!
//! ```text
//!  S -> C  VersionExchangeServer  (birthday, machoVersion, userCount, bootVersion, bootBuild, project, None)
//!  C -> S  VersionExchangeClient  the same shape           (a queue check, `(_, "QC")`, is answered with 0
//!  C -> S  (None, "VK", key)                                 and the version is sent again)
//!  C -> S  CryptoRequest          ("placebo", {})          S -> C  "OK CC"
//!  C -> S  CryptoChallenge        (challenge, {user_name, user_password_hash, user_languageid, ...})
//!  S -> C  2                      (the server wants hashed passwords)
//!  S -> C  CryptoServerHandshake  (challenge, (signedFunc, verify), {}, {macho_version, config_vals, ...})
//!  C -> S  CryptoHandshakeResult  (hash, signedFunc output, signedFunc result)
//!  S -> C  ack dict               {session_init, sessionID, user_clientid, ...}
//! ```
//!
//! A refused login is answered with a pickled `GPSTransportClosed` and the connection is closed.
//! Whether a login is accepted is the [`Authenticator`]'s decision.

use std::future::Future;
use std::io;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};

use bytes::Bytes;
use eve_marshal::{MarshalError, Value, decode_bytes, encode_packet};
use tokio::io::{AsyncRead, AsyncWrite, AsyncWriteExt};

use crate::frame::FrameReader;
use crate::hostlink::AuthOutcome;
use crate::profile;

/// The role bits every login session gets on top of the account's role: ROLE_LOGIN, ROLE_PLAYER and ROLE_IGB. The
/// reference composes it the same way (EveJS `accountRoleProfiles.js` SESSION_BASE_ROLE_MASK; eve.rs
/// `CharUnboundMgrService.SessionBaseRole`), so a normal account (role 0) logs in with these bits.
pub const SESSION_BASE_ROLE: u64 = 0x6000_0000_8000_0000;

/// What the front knows about the login attempt.
#[derive(Debug, Clone)]
pub struct AuthRequest {
    pub user_name: String,
    /// The client's password hash, as sent (the raw digest).
    pub password_hash: Bytes,
    pub language: String,
    pub address: String,
}

/// The authenticator could not be asked (the host link is down).
#[derive(Debug)]
pub struct AuthError(pub String);

/// Decides who may log in.
pub trait Authenticator: Send + Sync {
    fn authenticate(
        &self,
        request: AuthRequest,
    ) -> impl Future<Output = Result<AuthOutcome, AuthError>> + Send;
}

/// What the handshake needs from the running front.
#[derive(Debug, Clone)]
pub struct Environment {
    pub node_id: u32,
    pub user_count: u32,
    /// The global-config dict the client is handed (`config_vals`), from the host.
    pub config_vals: Value,
    /// The Python the client runs at login (see [`tidi_signed_func`]).
    pub signed_func: Bytes,
    /// Where session ids come from; shared by every connection of a front.
    pub session_ids: Arc<SessionIds>,
}

/// A completed login.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Login {
    pub user_name: String,
    pub user_id: u32,
    pub role: u64,
    pub country_code: String,
    pub language: String,
    pub address: String,
    pub client_id: u64,
    pub session_id: u64,
}

#[derive(Debug)]
pub enum HandshakeError {
    Io(io::Error),
    /// The client closed the connection.
    Closed,
    /// The client sent something the handshake does not allow.
    Protocol(String),
    Decode(MarshalError),
    /// The login was refused (the client was told, and the connection should close).
    Rejected(String),
    /// The authenticator could not be asked.
    Auth(String),
}

impl std::fmt::Display for HandshakeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Io(error) => write!(f, "{error}"),
            Self::Closed => write!(f, "the client closed the connection during the handshake"),
            Self::Protocol(detail) => write!(f, "handshake protocol error: {detail}"),
            Self::Decode(error) => write!(f, "undecodable handshake message: {error}"),
            Self::Rejected(reason) => write!(f, "login refused: {reason}"),
            Self::Auth(detail) => write!(f, "authentication unavailable: {detail}"),
        }
    }
}

impl std::error::Error for HandshakeError {}

impl From<io::Error> for HandshakeError {
    fn from(error: io::Error) -> Self {
        Self::Io(error)
    }
}

impl From<MarshalError> for HandshakeError {
    fn from(error: MarshalError) -> Self {
        Self::Decode(error)
    }
}

fn protocol(detail: impl Into<String>) -> HandshakeError {
    HandshakeError::Protocol(detail.into())
}

/// The Python the client runs at login, as a marshalled string: it installs the time-dilation handler the
/// server drives with `OnSetTimeDilation`. (The reference also patches the portrait upload and seeds the
/// skill-extractor token here; each of those arrives with the service it calls.)
pub fn tidi_signed_func() -> Bytes {
    const SOURCE: &[&str] = &[
        "import blue",
        "blue.os.EnableSimDilation(0)",
        "class _TiDiHandler(object):",
        " __guid__ = 'svc.tidiHandler'",
        " __notifyevents__ = ['OnSetTimeDilation']",
        " def OnSetTimeDilation(self, maxD, minD, thresh):",
        "  import blue",
        "  blue.os.maxSimDilation = float(maxD)",
        "  blue.os.minSimDilation = float(minD)",
        "  blue.os.dilationOverloadThreshold = int(thresh)",
        "  if int(thresh) == 100000000:",
        "   blue.os.dilationOverloadAdjustment = 0.8254",
        "   blue.os.dilationUnderloadAdjustment = 1000",
        "  else:",
        "   blue.os.dilationOverloadAdjustment = 0.1",
        "   blue.os.dilationUnderloadAdjustment = 1.059254",
        "_h = _TiDiHandler()",
        "sm.RegisterForNotifyEvent(_h, 'OnSetTimeDilation')",
        "__builtins__['__tidiHandler'] = _h",
        "print 'TIDI_HANDLER:OK'",
    ];
    // The lines are joined with a backslash and an "n" that Python turns into newlines inside the literal.
    let expression = format!("eval(compile(\"{}\", \"<tidi>\", \"exec\"))", SOURCE.join("\\n"));
    let mut out = Vec::with_capacity(5 + expression.len());
    out.push(b't');
    out.extend_from_slice(&(expression.len() as i32).to_le_bytes());
    out.extend_from_slice(expression.as_bytes());
    Bytes::from(out)
}

/// The pickled `exceptions.GPSTransportClosed(reason, reason, {})` sent to a refused client.
pub fn transport_closed(reason: &str) -> Value {
    fn unicode(out: &mut Vec<u8>, text: &str) {
        out.push(b'X');
        out.extend_from_slice(&(text.len() as u32).to_le_bytes());
        out.extend_from_slice(text.as_bytes());
    }
    let mut pickle = vec![0x80, 0x02];
    pickle.extend_from_slice(b"cexceptions\nGPSTransportClosed\n");
    unicode(&mut pickle, reason);
    unicode(&mut pickle, reason);
    pickle.extend_from_slice(b"}\x87R.");
    Value::CPickled(Bytes::from(pickle))
}

fn text_of(value: &Value) -> Option<String> {
    match value {
        Value::Str(text) | Value::WString(text) | Value::Token(text) => Some(text.to_string()),
        Value::Bytes(bytes) | Value::Buffer(bytes) => String::from_utf8(bytes.to_vec()).ok(),
        _ => None,
    }
}

fn bytes_of(value: &Value) -> Option<Bytes> {
    match value {
        Value::Bytes(bytes) | Value::Buffer(bytes) => Some(bytes.clone()),
        Value::Str(text) | Value::WString(text) => Some(Bytes::copy_from_slice(text.as_bytes())),
        _ => None,
    }
}

fn dict_get<'a>(entries: &'a [(Value, Value)], key: &str) -> Option<&'a Value> {
    entries.iter().find(|(name, _)| text_of(name).as_deref() == Some(key)).map(|(_, value)| value)
}

async fn send<W: AsyncWrite + Unpin>(writer: &mut W, value: &Value) -> Result<(), HandshakeError> {
    let bytes = encode_packet(value)?;
    writer.write_all(&bytes).await?;
    Ok(())
}

async fn receive<R: AsyncRead + Unpin>(reader: &mut FrameReader<R>) -> Result<Value, HandshakeError> {
    let frame = reader.next().await?.ok_or(HandshakeError::Closed)?;
    Ok(decode_bytes(&frame)?)
}

/// The cluster user count the client shows on the server list. The reference reports at least one (EveJS
/// `getClusterUserCount`, and eve.rs `GetServerStatus`), including while the first login is still being made.
fn online_users(env: &Environment) -> u32 {
    env.user_count.max(1)
}

fn version_exchange(env: &Environment) -> Value {
    Value::Tuple(vec![
        Value::Int(profile::BIRTHDAY),
        Value::Int(profile::MACHO_VERSION),
        Value::Int(i64::from(online_users(env))),
        Value::Real(profile::BOOT_VERSION),
        Value::Int(profile::BOOT_BUILD),
        Value::str(profile::PROJECT_VERSION),
        Value::None,
    ])
}

/// The client's id: one million per account plus the proxy node it is connected to.
pub fn client_id_for(user_id: u32, node_id: u32) -> u64 {
    1_000_000 * u64::from(user_id) + u64::from(node_id)
}

/// The session id the reference would give: a millisecond timestamp times fifteen, plus a client-specific
/// remainder. Two logins in the same millisecond can get the same value, so a front issues them through
/// [`SessionIds`].
pub fn session_id_for(now_ms: u64, client_id: u64) -> u64 {
    now_ms * 15 + client_id % 15
}

/// Issues session ids that are never repeated by this front.
#[derive(Debug, Default)]
pub struct SessionIds {
    last: AtomicU64,
}

impl SessionIds {
    /// [`session_id_for`], moved up past the last id issued when the two would not be distinct.
    pub fn next(&self, now_ms: u64, client_id: u64) -> u64 {
        let wanted = session_id_for(now_ms, client_id);
        let mut last = self.last.load(Ordering::Relaxed);
        loop {
            let id = wanted.max(last + 1);
            match self.last.compare_exchange_weak(last, id, Ordering::Relaxed, Ordering::Relaxed) {
                Ok(_) => return id,
                Err(current) => last = current,
            }
        }
    }
}

/// Runs the handshake. `reader` and `writer` are the two halves of the client's connection.
pub async fn perform<R, W, A>(
    reader: &mut FrameReader<R>,
    writer: &mut W,
    auth: &A,
    env: &Environment,
    address: &str,
    now_ms: u64,
) -> Result<Login, HandshakeError>
where
    R: AsyncRead + Unpin,
    W: AsyncWrite + Unpin,
    A: Authenticator,
{
    // 1-3: the version exchange and the command that follows it.
    loop {
        send(writer, &version_exchange(env)).await?;
        let version = receive(reader).await?;
        match &version {
            Value::Tuple(items) if items.len() >= 6 => {}
            other => return Err(protocol(format!("expected the client's version tuple, got {other:?}"))),
        }
        match receive(reader).await? {
            Value::Tuple(items) if items.len() == 3 => break, // (None, "VK", key)
            Value::Tuple(items) if items.len() == 2 => {
                // A queue check: there is no queue.
                send(writer, &Value::Int(0)).await?;
            }
            other => return Err(protocol(format!("expected a command tuple, got {other:?}"))),
        }
    }

    // 4: the crypto request. Only Placebo, which encrypts nothing.
    let Value::Tuple(request) = receive(reader).await? else {
        return Err(protocol("expected the crypto request tuple"));
    };
    match request.first().and_then(text_of).as_deref() {
        Some("placebo") => {}
        other => return Err(protocol(format!("unsupported crypto pack {other:?}"))),
    }
    send(writer, &Value::str("OK CC")).await?;

    // 5: the login.
    let Value::Tuple(challenge) = receive(reader).await? else {
        return Err(protocol("expected the crypto challenge tuple"));
    };
    let Some(Value::Dict(login)) = challenge.get(1) else {
        return Err(protocol("the crypto challenge has no login dictionary"));
    };
    let user_name = dict_get(login, "user_name").and_then(text_of).unwrap_or_default();
    let password_hash = dict_get(login, "user_password_hash").and_then(bytes_of).unwrap_or_default();
    let language = dict_get(login, "user_languageid").and_then(text_of).unwrap_or_else(|| "EN".to_owned());

    send(writer, &Value::Int(2)).await?;
    let outcome = auth
        .authenticate(AuthRequest {
            user_name: user_name.clone(),
            password_hash,
            language: language.clone(),
            address: address.to_owned(),
        })
        .await
        .map_err(|error| HandshakeError::Auth(error.0))?;
    let (user_id, role, country_code) = match outcome {
        AuthOutcome::Accepted { user_id, role, country_code } => (user_id, role, country_code),
        AuthOutcome::Rejected { reason } => {
            send(writer, &transport_closed(&reason)).await?;
            writer.flush().await?;
            return Err(HandshakeError::Rejected(reason));
        }
    };
    let client_id = client_id_for(user_id, env.node_id);
    let session_id = env.session_ids.next(now_ms, client_id);

    send(
        writer,
        &Value::Tuple(vec![
            Value::str(""),
            Value::Tuple(vec![Value::Buffer(env.signed_func.clone()), Value::Bool(false)]),
            Value::Dict(vec![]),
            Value::dict([
                ("challenge_responsehash", Value::str(profile::CHALLENGE_RESPONSE_HASH)),
                ("macho_version", Value::Int(profile::MACHO_VERSION)),
                ("boot_version", Value::Real(profile::BOOT_VERSION)),
                ("boot_build", Value::Int(profile::BOOT_BUILD)),
                ("boot_codename", Value::str(profile::PROJECT_CODENAME)),
                ("boot_region", Value::str(profile::PROJECT_REGION)),
                ("cluster_usercount", Value::Int(i64::from(online_users(env)))),
                ("proxy_nodeid", Value::Int(i64::from(env.node_id))),
                ("user_logonqueueposition", Value::Int(1)),
                ("config_vals", env.config_vals.clone()),
            ]),
        ]),
    )
    .await?;

    // 6: the result of the function the client ran.
    match receive(reader).await? {
        Value::Tuple(items) if items.len() >= 2 => {}
        other => return Err(protocol(format!("expected the handshake result tuple, got {other:?}"))),
    }

    // 7: the acknowledgement.
    send(
        writer,
        &Value::dict([
            ("live_updates", Value::List(vec![])),
            (
                "session_init",
                Value::dict([
                    ("languageID", Value::string(language.clone())),
                    ("countryCode", Value::string(country_code.clone())),
                    ("userid", Value::Int(i64::from(user_id))),
                    ("userType", Value::Int(30)),
                    // The login session's role: the account's bits plus the base bits every session has.
                    ("role", Value::Long((role | SESSION_BASE_ROLE) as i64)),
                    ("address", Value::string(address.to_owned())),
                    // The client reads inDetention unguarded (mailSvc.SendMsgDlg), so the key is always present.
                    ("inDetention", Value::None),
                ]),
            ),
            ("sessionID", Value::Long(session_id as i64)),
            ("client_hash", Value::None),
            ("user_clientid", Value::Long(client_id as i64)),
            // The reference's authentication result: no access token (the SSO token is not exchanged here) and no computer
            // hash (the client sends none). The client reads both keys after an SSO login (connectionService.LoginSso).
            ("access_token", Value::None),
            ("computer_hash", Value::None),
        ]),
    )
    .await?;
    writer.flush().await?;

    Ok(Login {
        user_name,
        user_id,
        role,
        country_code,
        language,
        address: address.to_owned(),
        client_id,
        session_id,
    })
}
