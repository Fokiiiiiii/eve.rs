//! The server half of the handshake against the client half of eve_net::client, over in-memory streams.

use std::sync::{Arc, Mutex};

use bytes::Bytes;
use eve_marshal::Value;
use eve_net::client::{self, dict_get, int_of, text_of};
use eve_net::frame::{FrameReader, MAX_FRAME};
use eve_net::handshake::{
    self, AuthError, AuthRequest, Authenticator, Environment, HandshakeError, Login, SessionIds,
};
use eve_net::hostlink::AuthOutcome;
use eve_net::profile;
use tokio::io::{AsyncWriteExt, duplex, split};

struct Accept(u32);
struct Reject(&'static str);
struct Unavailable;
/// Accepts and remembers what it was asked.
struct Recording(Mutex<Vec<AuthRequest>>);

impl Authenticator for Accept {
    async fn authenticate(&self, _: AuthRequest) -> Result<AuthOutcome, AuthError> {
        Ok(AuthOutcome::Accepted { user_id: self.0, role: 0x1_0000_0000_0000, country_code: "GB".into() })
    }
}

impl Authenticator for Reject {
    async fn authenticate(&self, _: AuthRequest) -> Result<AuthOutcome, AuthError> {
        Ok(AuthOutcome::Rejected { reason: self.0.into() })
    }
}

impl Authenticator for Unavailable {
    async fn authenticate(&self, _: AuthRequest) -> Result<AuthOutcome, AuthError> {
        Err(AuthError("the host link is down".into()))
    }
}

impl Authenticator for Recording {
    async fn authenticate(&self, request: AuthRequest) -> Result<AuthOutcome, AuthError> {
        self.0.lock().unwrap().push(request);
        Ok(AuthOutcome::Accepted { user_id: 9, role: 0, country_code: "GB".into() })
    }
}

fn environment() -> Environment {
    Environment {
        node_id: 0xFFAA,
        user_count: 3,
        config_vals: Value::dict([
            ("imageserverurl", Value::str("http://127.0.0.1:26001/")),
            ("defaultPortraitSaveSize", Value::Int(1024)),
        ]),
        signed_func: handshake::tidi_signed_func(),
        session_ids: Arc::new(SessionIds::default()),
    }
}

/// Runs the server half against the library client; returns both results.
async fn run<A: Authenticator + 'static>(
    auth: A,
    user: &str,
) -> (Result<Login, HandshakeError>, Result<client::ClientSession, HandshakeError>) {
    let (client_end, server_end) = duplex(1 << 16);
    let server = tokio::spawn(async move {
        let (read, mut write) = split(server_end);
        let mut reader = FrameReader::new(read, MAX_FRAME);
        handshake::perform(&mut reader, &mut write, &auth, &environment(), "127.0.0.1", 1_790_646_377_000)
            .await
    });
    let (read, mut write) = split(client_end);
    let mut reader = FrameReader::new(read, MAX_FRAME);
    let session = client::login(&mut reader, &mut write, user, &[0xAB; 20], "EN").await;
    // Hang up before waiting for the server, so a client that gave up cannot leave it waiting.
    drop(reader);
    drop(write);
    (server.await.unwrap(), session)
}

#[tokio::test]
async fn a_login_completes_and_the_client_sees_what_the_evecli_tests_expect() {
    let (login, session) = run(Accept(42), "pilot").await;
    let login = login.unwrap();
    let session = session.unwrap();

    assert_eq!(login.user_id, 42);
    assert_eq!(login.user_name, "pilot");
    assert_eq!(login.client_id, 42_000_000 + 0xFFAA);
    assert_eq!(login.session_id, 1_790_646_377_000 * 15 + login.client_id % 15);

    // the version exchange the client checks
    assert_eq!(int_of(&session.server_version[0]), Some(profile::BIRTHDAY));
    assert_eq!(int_of(&session.server_version[1]), Some(profile::MACHO_VERSION));
    assert_eq!(int_of(&session.server_version[2]), Some(3), "cluster user count");
    assert_eq!(session.server_version[3], Value::Real(24.01));
    assert_eq!(int_of(&session.server_version[4]), Some(profile::BOOT_BUILD));
    assert_eq!(text_of(&session.server_version[5]).as_deref(), Some("V24.01@ccp"));

    // what the acknowledgement carries
    assert_eq!(session.user_id, 42);
    assert_eq!(session.client_id, login.client_id as i64);
    assert_eq!(session.session_id, login.session_id as i64);
    assert_eq!(dict_get(&session.session_init, "languageID").and_then(text_of).as_deref(), Some("EN"));
    assert_eq!(dict_get(&session.session_init, "countryCode").and_then(text_of).as_deref(), Some("GB"));
    assert_eq!(dict_get(&session.session_init, "userType").and_then(int_of), Some(30));
    assert_eq!(dict_get(&session.session_init, "role").and_then(int_of), Some(0x1_0000_0000_0000));
    assert_eq!(dict_get(&session.session_init, "address").and_then(text_of).as_deref(), Some("127.0.0.1"));

    // the authentication result the client reads after its login (connectionService.LoginSso, GPS.py merge): both keys are
    // present, None for a password login
    assert_eq!(dict_get(&session.acknowledgement, "access_token"), Some(&Value::None));
    assert_eq!(dict_get(&session.acknowledgement, "computer_hash"), Some(&Value::None));

    // the server handshake dictionary
    assert_eq!(dict_get(&session.handshake, "macho_version").and_then(int_of), Some(496));
    assert_eq!(dict_get(&session.handshake, "proxy_nodeid").and_then(int_of), Some(0xFFAA));
    assert_eq!(dict_get(&session.handshake, "cluster_usercount").and_then(int_of), Some(3));
    let Some(Value::Dict(config)) = dict_get(&session.handshake, "config_vals") else {
        panic!("no config_vals")
    };
    assert_eq!(
        dict_get(config, "imageserverurl").and_then(text_of).as_deref(),
        Some("http://127.0.0.1:26001/")
    );
}

#[test]
fn crc_hqx_is_the_xmodem_crc_and_the_challenge_hash_is_the_references_constant() {
    assert_eq!(client::crc_hqx(b"123456789"), 0x31C3, "CRC-16/XMODEM check value");
    // 55087 is a constant of the reference server, not the CRC of the empty challenge.
    assert_eq!(profile::CHALLENGE_RESPONSE_HASH, "55087");
    assert_eq!(client::crc_hqx(&eve_marshal::encode(&Value::str("")).unwrap()), 9457);
}

#[tokio::test]
async fn the_client_is_asked_to_run_the_time_dilation_handler() {
    let (_, session) = run(Accept(1), "pilot").await;
    let func = session.unwrap().signed_func;
    assert_eq!(func[0], b't');
    let length = i32::from_le_bytes(func[1..5].try_into().unwrap()) as usize;
    assert_eq!(length, func.len() - 5);
    let text = std::str::from_utf8(&func[5..]).unwrap();
    assert!(text.starts_with("eval(compile(\"import blue\\nblue.os.EnableSimDilation(0)\\n"), "{text}");
    assert!(text.contains("__guid__ = 'svc.tidiHandler'"));
    assert!(text.contains("print 'TIDI_HANDLER:OK'"));
    assert!(text.ends_with("\", \"<tidi>\", \"exec\"))"));
    assert!(
        !text.contains("\n"),
        "the source must be one line: newlines are the two characters backslash and n"
    );
}

#[tokio::test]
async fn the_authenticator_gets_what_the_client_sent() {
    let recording = Arc::new(Recording(Mutex::new(Vec::new())));
    struct Shared(Arc<Recording>);
    impl Authenticator for Shared {
        async fn authenticate(&self, request: AuthRequest) -> Result<AuthOutcome, AuthError> {
            self.0.authenticate(request).await
        }
    }
    let (login, session) = run(Shared(recording.clone()), "Some Pilot").await;
    login.unwrap();
    session.unwrap();
    let requests = recording.0.lock().unwrap();
    assert_eq!(requests.len(), 1);
    assert_eq!(requests[0].user_name, "Some Pilot");
    assert_eq!(&requests[0].password_hash[..], &[0xAB; 20]);
    assert_eq!(requests[0].language, "EN");
    assert_eq!(requests[0].address, "127.0.0.1");
}

#[tokio::test]
async fn a_refused_login_is_answered_with_a_transport_closed_pickle() {
    let (login, session) = run(Reject("LoginAuthFailed"), "pilot").await;
    assert!(matches!(login, Err(HandshakeError::Rejected(reason)) if reason == "LoginAuthFailed"));
    let Err(HandshakeError::Rejected(pickle)) = session else { panic!("the client should see a refusal") };
    assert!(pickle.contains("GPSTransportClosed") && pickle.contains("LoginAuthFailed"), "{pickle:?}");
}

#[tokio::test]
async fn an_unreachable_host_ends_the_handshake_without_a_reply() {
    let (login, _) = run(Unavailable, "pilot").await;
    assert!(matches!(login, Err(HandshakeError::Auth(detail)) if detail.contains("down")));
}

#[test]
fn ids_follow_the_reference_formulas() {
    assert_eq!(handshake::client_id_for(7, 0xFFAA), 7_000_000 + 65_450);
    // the remainder of the client id modulo fifteen is added: 7,065,451 leaves 1
    assert_eq!(handshake::session_id_for(1_000, 7_065_451), 15_001);
    assert_eq!(handshake::session_id_for(1_000, 7_065_450), 15_000);
}

#[tokio::test]
async fn a_queue_check_is_answered_and_the_version_is_sent_again() {
    let (client_end, server_end) = duplex(1 << 16);
    let server = tokio::spawn(async move {
        let (read, mut write) = split(server_end);
        let mut reader = FrameReader::new(read, MAX_FRAME);
        handshake::perform(&mut reader, &mut write, &Accept(5), &environment(), "10.0.0.1", 1).await
    });
    let (read, mut write) = split(client_end);
    let mut reader = FrameReader::new(read, MAX_FRAME);
    let receive = |frame: Bytes| eve_marshal::decode_bytes(&frame).unwrap();

    let first = receive(reader.next().await.unwrap().unwrap());
    let Value::Tuple(version) = first else { panic!("not a tuple") };
    let reply = Value::Tuple(vec![
        version[0].clone(),
        version[1].clone(),
        Value::Int(0),
        version[3].clone(),
        version[4].clone(),
        version[5].clone(),
        Value::None,
    ]);
    write.write_all(&eve_marshal::encode_packet(&reply).unwrap()).await.unwrap();
    write
        .write_all(&eve_marshal::encode_packet(&Value::Tuple(vec![Value::None, Value::str("QC")])).unwrap())
        .await
        .unwrap();

    assert_eq!(receive(reader.next().await.unwrap().unwrap()), Value::Int(0), "queue position");
    assert!(
        matches!(receive(reader.next().await.unwrap().unwrap()), Value::Tuple(items) if items.len() == 7),
        "the version again"
    );

    // finish the handshake from here
    write.write_all(&eve_marshal::encode_packet(&reply).unwrap()).await.unwrap();
    write
        .write_all(
            &eve_marshal::encode_packet(&Value::Tuple(vec![Value::None, Value::str("VK"), Value::None]))
                .unwrap(),
        )
        .await
        .unwrap();
    write
        .write_all(
            &eve_marshal::encode_packet(&Value::Tuple(vec![Value::str("placebo"), Value::Dict(vec![])]))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(text_of(&receive(reader.next().await.unwrap().unwrap())).as_deref(), Some("OK CC"));
    // dropping one half of a split stream does not close it; shut the writing side down
    write.shutdown().await.unwrap();
    assert!(matches!(server.await.unwrap(), Err(HandshakeError::Closed)));
}

async fn server_result(inputs: Vec<Value>) -> Result<Login, HandshakeError> {
    let (mut client_end, server_end) = duplex(1 << 16);
    let server = tokio::spawn(async move {
        let (read, mut write) = split(server_end);
        let mut reader = FrameReader::new(read, MAX_FRAME);
        handshake::perform(&mut reader, &mut write, &Accept(1), &environment(), "127.0.0.1", 1).await
    });
    for value in inputs {
        client_end.write_all(&eve_marshal::encode_packet(&value).unwrap()).await.unwrap();
    }
    client_end.shutdown().await.unwrap();
    server.await.unwrap()
}

fn version_tuple() -> Value {
    Value::Tuple(vec![
        Value::Int(170_472),
        Value::Int(496),
        Value::Int(0),
        Value::Real(24.01),
        Value::Int(3_396_210),
        Value::str("V24.01@ccp"),
        Value::None,
    ])
}

#[tokio::test]
async fn malformed_or_unsupported_input_is_an_error_not_a_login() {
    // not a version tuple
    let error = server_result(vec![Value::Int(7)]).await.unwrap_err();
    assert!(matches!(error, HandshakeError::Protocol(_)), "{error}");
    // a short version tuple
    let error = server_result(vec![Value::Tuple(vec![Value::Int(1)])]).await.unwrap_err();
    assert!(matches!(error, HandshakeError::Protocol(_)), "{error}");
    // a command that is neither VK nor a queue check
    let error = server_result(vec![version_tuple(), Value::Tuple(vec![Value::None])]).await.unwrap_err();
    assert!(matches!(error, HandshakeError::Protocol(_)), "{error}");
    // a crypto pack other than Placebo
    let command = Value::Tuple(vec![Value::None, Value::str("VK"), Value::None]);
    let error = server_result(vec![
        version_tuple(),
        command.clone(),
        Value::Tuple(vec![Value::str("rsa"), Value::Dict(vec![])]),
    ])
    .await
    .unwrap_err();
    assert!(matches!(error, HandshakeError::Protocol(detail) if detail.contains("rsa")), "unsupported pack");
    // a challenge without a login dictionary
    let placebo = Value::Tuple(vec![Value::str("placebo"), Value::Dict(vec![])]);
    let error = server_result(vec![version_tuple(), command, placebo, Value::Tuple(vec![Value::None])])
        .await
        .unwrap_err();
    assert!(matches!(error, HandshakeError::Protocol(_)), "{error}");
    // the client hangs up first
    assert!(matches!(server_result(vec![]).await.unwrap_err(), HandshakeError::Closed));
}

#[tokio::test]
async fn bytes_that_are_not_marshal_are_an_error() {
    let (mut client_end, server_end) = duplex(1 << 16);
    let server = tokio::spawn(async move {
        let (read, mut write) = split(server_end);
        let mut reader = FrameReader::new(read, MAX_FRAME);
        handshake::perform(&mut reader, &mut write, &Accept(1), &environment(), "127.0.0.1", 1).await
    });
    client_end.write_all(&[4, 0, 0, 0, 0xde, 0xad, 0xbe, 0xef]).await.unwrap();
    assert!(matches!(server.await.unwrap().unwrap_err(), HandshakeError::Decode(_)));
}

#[tokio::test]
async fn an_oversized_frame_is_refused_before_it_is_read() {
    let (mut client_end, server_end) = duplex(1 << 16);
    let reading = tokio::spawn(async move {
        let (read, _write) = split(server_end);
        FrameReader::new(read, 1024).next().await
    });
    client_end.write_all(&(1_000_000u32).to_le_bytes()).await.unwrap();
    let error = reading.await.unwrap().unwrap_err();
    assert_eq!(error.kind(), std::io::ErrorKind::InvalidData);
}

#[tokio::test]
async fn frames_that_arrive_together_or_in_pieces_are_all_delivered() {
    let (mut client_end, server_end) = duplex(1 << 16);
    let reading = tokio::spawn(async move {
        let (read, _write) = split(server_end);
        let mut reader = FrameReader::new(read, MAX_FRAME);
        let mut frames = Vec::new();
        while let Some(frame) = reader.next().await.unwrap() {
            frames.push(frame);
        }
        frames
    });
    let mut wire = Vec::new();
    for payload in [&b"one"[..], b"", b"three three"] {
        wire.extend_from_slice(&(payload.len() as u32).to_le_bytes());
        wire.extend_from_slice(payload);
    }
    // together, then the same bytes a few at a time
    client_end.write_all(&wire).await.unwrap();
    for piece in wire.chunks(3) {
        client_end.write_all(piece).await.unwrap();
        tokio::task::yield_now().await;
    }
    drop(client_end);
    let frames = reading.await.unwrap();
    let expected: Vec<&[u8]> = vec![b"one", b"", b"three three", b"one", b"", b"three three"];
    assert_eq!(frames.iter().map(|f| &f[..]).collect::<Vec<_>>(), expected);
}

#[test]
fn session_ids_never_repeat_even_within_one_millisecond() {
    let ids = SessionIds::default();
    // client ids 3,000,000 apart have the same remainder mod 15, as do any two clients logging in together
    let clients = [100_000_000 + 0xFFAA, 103_000_000 + 0xFFAA, 100_000_000 + 0xFFAA];
    let issued: Vec<u64> = clients.iter().map(|&client| ids.next(1_790_646_377_000, client)).collect();
    assert_eq!(
        issued[0],
        handshake::session_id_for(1_790_646_377_000, clients[0]),
        "the first is the reference's value"
    );
    let unique: std::collections::HashSet<_> = issued.iter().collect();
    assert_eq!(unique.len(), 3, "{issued:?}");
    // and time moving on takes the timestamp form again
    assert_eq!(
        ids.next(1_790_646_400_000, clients[0]),
        handshake::session_id_for(1_790_646_400_000, clients[0])
    );
}

#[test]
fn session_ids_are_unique_across_threads() {
    let ids = Arc::new(SessionIds::default());
    let threads: Vec<_> = (0..8)
        .map(|t| {
            let ids = ids.clone();
            std::thread::spawn(move || {
                (0..2000).map(|i| ids.next(1_000, 100_000_000 * (t + 1) + i)).collect::<Vec<_>>()
            })
        })
        .collect();
    let all: Vec<u64> = threads.into_iter().flat_map(|t| t.join().unwrap()).collect();
    let unique: std::collections::HashSet<_> = all.iter().collect();
    assert_eq!(unique.len(), all.len());
}
