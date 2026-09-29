//! The whole front over real sockets: clients over TCP, a fake service host on the link.

use std::sync::Arc;
use std::sync::atomic::{AtomicU32, Ordering};
use std::time::Duration;

use bytes::{Bytes, BytesMut};
use eve_marshal::{Value, decode_bytes};
use eve_net::client::{self, ClientSession, dict_get, int_of, packet_state, result_of};
use eve_net::frame::{FrameReader, MAX_FRAME};
use eve_net::host::{Endpoint, HostLink};
use eve_net::hostlink::{AuthOutcome, Message, PROTOCOL_VERSION};
use eve_net::server::{ServeConfig, serve};
use tokio::io::AsyncWriteExt;
use tokio::net::tcp::{OwnedReadHalf, OwnedWriteHalf};
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::{mpsc, oneshot};

/// Something the fake host saw, or is told to do.
enum Seen {
    Auth { user: String, hash: Bytes, address: String },
    Open { session_id: u64, client_id: u64, user_id: u32, address: String },
    Packet { session_id: u64, payload: Bytes },
    Close { session_id: u64 },
}

struct FakeHost {
    address: String,
    seen: mpsc::UnboundedReceiver<Seen>,
    push: mpsc::UnboundedSender<Message>,
    drop_link: Option<oneshot::Sender<()>>,
}

fn config_dict() -> Bytes {
    Bytes::from(
        eve_marshal::encode(&Value::dict([("imageserverurl", Value::str("http://127.0.0.1:26001/"))]))
            .unwrap(),
    )
}

/// A host that accepts every user except "banned", echoes every packet back to its session, and can be
/// told to push messages or drop the link.
async fn fake_host() -> FakeHost {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = listener.local_addr().unwrap().to_string();
    let (seen_tx, seen) = mpsc::unbounded_channel();
    let (push, mut pushed) = mpsc::unbounded_channel::<Message>();
    let (drop_link, dropped) = oneshot::channel();
    let ids = Arc::new(AtomicU32::new(100));

    tokio::spawn(async move {
        let (stream, _) = listener.accept().await.unwrap();
        let (read, mut write) = stream.into_split();
        let mut reader = FrameReader::new(read, MAX_FRAME);
        let Message::Hello { version, .. } = Message::decode(reader.next().await.unwrap().unwrap()).unwrap()
        else {
            panic!("no hello")
        };
        assert_eq!(version, PROTOCOL_VERSION);
        let mut out = BytesMut::new();
        Message::HelloAck { version: PROTOCOL_VERSION }.encode(&mut out);
        Message::ClusterConfig { payload: config_dict() }.encode(&mut out);
        write.write_all(&out).await.unwrap();

        let mut dropped = dropped;
        loop {
            // Pushes first: what a test tells the host to send goes out before it answers anything the
            // front sent, so an answer arriving proves the pushes before it have been delivered.
            tokio::select! {
                biased;
                Some(message) = pushed.recv() => {
                    let mut out = BytesMut::new();
                    message.encode(&mut out);
                    write.write_all(&out).await.unwrap();
                }
                frame = reader.next() => {
                    let Ok(Some(frame)) = frame else { return };
                    let mut out = BytesMut::new();
                    match Message::decode(frame).unwrap() {
                        Message::AuthRequest { corr, user_name, password_hash, address, .. } => {
                            let outcome = if user_name == "banned" {
                                AuthOutcome::Rejected { reason: "ACCOUNTBANNED".into() }
                            } else {
                                AuthOutcome::Accepted { user_id: ids.fetch_add(1, Ordering::Relaxed), role: 0, country_code: "GB".into() }
                            };
                            let _ = seen_tx.send(Seen::Auth { user: user_name, hash: password_hash, address });
                            Message::AuthReply { corr, outcome }.encode(&mut out);
                        }
                        Message::SessionOpen(open) => {
                            let _ = seen_tx.send(Seen::Open { session_id: open.session_id, client_id: open.client_id, user_id: open.user_id, address: open.address });
                        }
                        Message::Packet { session_id, payload } => {
                            let _ = seen_tx.send(Seen::Packet { session_id, payload: payload.clone() });
                            Message::Send { session_id, payload }.encode(&mut out);
                        }
                        Message::SessionClose { session_id } => {
                            let _ = seen_tx.send(Seen::Close { session_id });
                        }
                        other => panic!("a front sent {other:?}"),
                    }
                    if !out.is_empty() {
                        write.write_all(&out).await.unwrap();
                    }
                }
                _ = &mut dropped => return,
            }
        }
    });
    FakeHost { address, seen, push, drop_link: Some(drop_link) }
}

struct Front {
    address: String,
    task: tokio::task::JoinHandle<std::io::Result<()>>,
    stop: Option<oneshot::Sender<()>>,
}

async fn start_front(host: &FakeHost, config: ServeConfig) -> Front {
    let link =
        Arc::new(HostLink::connect(&Endpoint::Tcp(host.address.clone()), config.node_id).await.unwrap());
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = listener.local_addr().unwrap().to_string();
    let (stop, stopped) = oneshot::channel::<()>();
    let task = tokio::spawn(serve(listener, link, config, async move {
        let _ = stopped.await;
    }));
    Front { address, task, stop: Some(stop) }
}

struct Client {
    reader: FrameReader<OwnedReadHalf>,
    writer: OwnedWriteHalf,
    session: ClientSession,
    next_call: i64,
}

async fn connect(front: &Front, user: &str) -> Client {
    let stream = TcpStream::connect(&front.address).await.unwrap();
    stream.set_nodelay(true).unwrap();
    let (read, mut writer) = stream.into_split();
    let mut reader = FrameReader::new(read, MAX_FRAME);
    let session = client::login(&mut reader, &mut writer, user, &[0x5A; 20], "EN").await.unwrap();
    Client { reader, writer, session, next_call: 0 }
}

impl Client {
    async fn send(&mut self, value: &Value) {
        self.writer.write_all(&eve_marshal::encode_packet(value).unwrap()).await.unwrap();
    }

    async fn call(&mut self, service: &str, method: &str, args: Vec<Value>) -> Bytes {
        self.next_call += 1;
        let packet = self.session.call_packet(self.next_call, service, method, args);
        self.send(&packet).await;
        self.frame().await
    }

    async fn frame(&mut self) -> Bytes {
        tokio::time::timeout(Duration::from_secs(10), self.reader.next())
            .await
            .expect("timed out")
            .unwrap()
            .expect("closed")
    }
}

async fn next_seen(host: &mut FakeHost) -> Seen {
    tokio::time::timeout(Duration::from_secs(10), host.seen.recv())
        .await
        .expect("the host saw nothing")
        .unwrap()
}

#[tokio::test]
async fn a_client_logs_in_and_its_packets_reach_the_host_byte_for_byte() {
    let mut host = fake_host().await;
    let front = start_front(&host, ServeConfig::default()).await;
    let mut client = connect(&front, "pilot").await;

    let Seen::Auth { user, hash, address } = next_seen(&mut host).await else {
        panic!("expected the login first")
    };
    assert_eq!((user.as_str(), &hash[..], address.as_str()), ("pilot", &[0x5A; 20][..], "127.0.0.1"));
    let Seen::Open { session_id, client_id, user_id, address } = next_seen(&mut host).await else {
        panic!("expected the session to open")
    };
    assert_eq!(user_id, 100);
    assert_eq!(address, "127.0.0.1");
    assert_eq!(client_id, 100_000_000 + 0xFFAA);
    assert_eq!(session_id as i64, client.session.session_id);
    assert_eq!(client_id as i64, client.session.client_id);

    // what the client sends is what the host receives: the front does not touch the marshal bytes
    let packet = client.session.call_packet(
        1,
        "machoNet",
        "GetTime",
        vec![Value::Int(7), Value::string("x".repeat(300))],
    );
    let wire = eve_marshal::encode(&packet).unwrap();
    client.send(&packet).await;
    let Seen::Packet { session_id: for_session, payload } = next_seen(&mut host).await else {
        panic!("expected a packet")
    };
    assert_eq!(for_session, session_id);
    assert_eq!(&payload[..], &wire[..]);

    // and the host's answer comes back framed, unchanged
    let echoed = client.frame().await;
    assert_eq!(&echoed[..], &wire[..]);
    let decoded = decode_bytes(&echoed).unwrap();
    let state = packet_state(&decoded).unwrap();
    assert_eq!(int_of(&state[0]), Some(client::CALL_REQ));
    assert!(result_of(state).is_some());

    // the config from the host reached the client's handshake
    let Some(Value::Dict(config)) = dict_get(&client.session.handshake, "config_vals") else {
        panic!("no config_vals")
    };
    assert!(dict_get(config, "imageserverurl").is_some());

    drop(client);
    assert!(matches!(next_seen(&mut host).await, Seen::Close { session_id: closed } if closed == session_id));
}

#[tokio::test]
async fn a_refused_login_never_opens_a_session() {
    let mut host = fake_host().await;
    let front = start_front(&host, ServeConfig::default()).await;
    let stream = TcpStream::connect(&front.address).await.unwrap();
    let (read, mut write) = stream.into_split();
    let mut reader = FrameReader::new(read, MAX_FRAME);
    let refusal = client::login(&mut reader, &mut write, "banned", &[1; 20], "EN").await.unwrap_err();
    assert!(
        matches!(&refusal, eve_net::handshake::HandshakeError::Rejected(pickle) if pickle.contains("ACCOUNTBANNED")),
        "{refusal}"
    );
    assert!(reader.next().await.unwrap().is_none(), "the front closes the connection after a refusal");

    assert!(matches!(next_seen(&mut host).await, Seen::Auth { .. }));
    // and the host is never told about a session
    tokio::time::sleep(Duration::from_millis(100)).await;
    assert!(host.seen.try_recv().is_err());
}

#[tokio::test]
async fn the_host_can_push_to_a_session_and_disconnect_it() {
    let mut host = fake_host().await;
    let front = start_front(&host, ServeConfig::default()).await;
    let mut client = connect(&front, "pilot").await;
    let _ = next_seen(&mut host).await;
    let Seen::Open { session_id, .. } = next_seen(&mut host).await else { panic!() };

    host.push.send(Message::Send { session_id, payload: Bytes::from_static(b"\x7e\0\0\0\0\x0e") }).unwrap();
    assert_eq!(&client.frame().await[..], b"\x7e\0\0\0\0\x0e");

    host.push.send(Message::Disconnect { session_id, reason: "ACCOUNTBANNED".into() }).unwrap();
    let closing = decode_bytes(&client.frame().await).unwrap();
    assert!(
        matches!(&closing, Value::CPickled(pickle) if String::from_utf8_lossy(pickle).contains("ACCOUNTBANNED")),
        "{closing:?}"
    );
    assert!(client.reader.next().await.unwrap().is_none(), "then the connection closes");
}

#[tokio::test]
async fn many_clients_at_once_each_get_only_their_own_answers() {
    let host = fake_host().await;
    let front = Arc::new(start_front(&host, ServeConfig::default()).await);
    let mut tasks = Vec::new();
    for n in 0..24 {
        let front = front.clone();
        tasks.push(tokio::spawn(async move {
            let mut client = connect(&front, &format!("pilot{n}")).await;
            for call in 0..40 {
                let reply = client.call("machoNet", "Echo", vec![Value::Int(n), Value::Int(call)]).await;
                let decoded = decode_bytes(&reply).unwrap();
                let state = packet_state(&decoded).unwrap();
                // the echo is this client's own request: its client id is in the source address
                let Value::Tuple(source) = &state[1] else { panic!("no source") };
                assert_eq!(int_of(&source[1]), Some(client.session.client_id));
                assert_eq!(int_of(&source[2]), Some(call + 1));
            }
        }));
    }
    for task in tasks {
        task.await.unwrap();
    }
}

#[tokio::test]
async fn a_client_that_stops_reading_is_cut_off_without_stalling_the_others() {
    let mut host = fake_host().await;
    let front = start_front(&host, ServeConfig::default()).await;

    let stalled = connect(&front, "stalled").await;
    let _ = next_seen(&mut host).await;
    let Seen::Open { session_id: stalled_id, .. } = next_seen(&mut host).await else { panic!() };
    let mut healthy = connect(&front, "healthy").await;
    let _ = next_seen(&mut host).await;
    let _ = next_seen(&mut host).await;

    // flood the stalled client (which never reads) far beyond what its queue and socket buffers hold
    let chunk = Bytes::from(vec![7u8; 32 * 1024]);
    for _ in 0..6000 {
        host.push.send(Message::Send { session_id: stalled_id, payload: chunk.clone() }).unwrap();
    }
    // the link still serves the healthy client promptly
    let started = std::time::Instant::now();
    let reply = healthy.call("machoNet", "GetTime", vec![]).await;
    assert!(!reply.is_empty());
    assert!(started.elapsed() < Duration::from_secs(5), "the healthy client waited {:?}", started.elapsed());

    // the stalled client is closed by the front: reading drains what was buffered, then ends
    let mut stalled = stalled;
    let drained = tokio::time::timeout(Duration::from_secs(20), async {
        let mut frames = 0u32;
        // ends with a clean end of stream or, if the front closed with unread data, a reset
        while let Ok(Some(_frame)) = stalled.reader.next().await {
            frames += 1;
        }
        frames
    })
    .await
    .expect("the stalled client was never cut off");
    assert!(drained < 6000, "it cannot have received everything ({drained})");
    let _ = &mut healthy;
}

#[tokio::test]
async fn losing_the_host_ends_the_server_and_closes_the_clients() {
    let mut host = fake_host().await;
    let front = start_front(&host, ServeConfig::default()).await;
    let mut client = connect(&front, "pilot").await;
    let _ = next_seen(&mut host).await;
    let _ = next_seen(&mut host).await;

    host.drop_link.take().unwrap().send(()).unwrap();
    let result =
        tokio::time::timeout(Duration::from_secs(10), front.task).await.expect("serve did not end").unwrap();
    assert!(result.is_err(), "a lost host link is an error");
    let end = tokio::time::timeout(Duration::from_secs(10), client.reader.next())
        .await
        .expect("the client was left hanging");
    assert!(matches!(end, Ok(None) | Err(_)));
}

#[tokio::test]
async fn a_client_that_never_finishes_the_handshake_is_dropped() {
    let host = fake_host().await;
    let front = start_front(
        &host,
        ServeConfig { handshake_timeout: Duration::from_millis(200), ..ServeConfig::default() },
    )
    .await;
    let stream = TcpStream::connect(&front.address).await.unwrap();
    let (read, _write) = stream.into_split();
    let mut reader = FrameReader::new(read, MAX_FRAME);
    let _version = reader.next().await.unwrap().unwrap(); // the server speaks first
    let end = tokio::time::timeout(Duration::from_secs(5), reader.next()).await.expect("not dropped");
    assert!(matches!(end, Ok(None) | Err(_)));
}

#[tokio::test]
async fn an_oversized_frame_from_a_client_closes_it_and_tells_the_host() {
    let mut host = fake_host().await;
    let front = start_front(&host, ServeConfig::default()).await;
    let mut client = connect(&front, "pilot").await;
    let _ = next_seen(&mut host).await;
    let Seen::Open { session_id, .. } = next_seen(&mut host).await else { panic!() };
    client.writer.write_all(&(MAX_FRAME as u32 + 1).to_le_bytes()).await.unwrap();
    assert!(matches!(next_seen(&mut host).await, Seen::Close { session_id: closed } if closed == session_id));
    let end = tokio::time::timeout(Duration::from_secs(5), client.reader.next()).await.expect("not closed");
    assert!(matches!(end, Ok(None) | Err(_)));
}

#[tokio::test]
async fn the_second_client_is_told_how_many_users_are_on() {
    let host = fake_host().await;
    let front = start_front(&host, ServeConfig::default()).await;
    let first = connect(&front, "one").await;
    assert_eq!(dict_get(&first.session.handshake, "cluster_usercount").and_then(int_of), Some(0));
    tokio::time::sleep(Duration::from_millis(50)).await;
    let second = connect(&front, "two").await;
    assert_eq!(dict_get(&second.session.handshake, "cluster_usercount").and_then(int_of), Some(1));
    assert_eq!(int_of(&second.session.server_version[2]), Some(1));
}

#[tokio::test]
async fn shutdown_ends_the_server_cleanly() {
    let host = fake_host().await;
    let mut front = start_front(&host, ServeConfig::default()).await;
    front.stop.take().unwrap().send(()).unwrap();
    let result =
        tokio::time::timeout(Duration::from_secs(5), front.task).await.expect("serve did not stop").unwrap();
    assert!(result.is_ok());
}
