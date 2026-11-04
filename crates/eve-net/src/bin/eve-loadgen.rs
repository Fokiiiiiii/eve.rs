//! Usage: eve-loadgen <host:port> [--clients N] [--calls M] [--pipeline P] [--service S] [--method M] [--prefix NAME]

use std::process::ExitCode;
use std::time::{Duration, Instant};

use eve_marshal::{Value, decode_bytes};
use eve_net::client::{self, CALL_RSP, ERROR_RESPONSE, int_of, packet_state};
use eve_net::frame::{FrameReader, MAX_FRAME};
use tokio::io::AsyncWriteExt;
use tokio::net::TcpStream;

struct Args {
    address: String,
    clients: usize,
    calls: usize,
    pipeline: usize,
    service: String,
    method: String,
    prefix: String,
}

fn parse() -> Result<Args, String> {
    let mut args = std::env::args().skip(1);
    let address = args.next().ok_or("usage: eve-loadgen <host:port> [--clients N] [--calls M] [--pipeline P] [--service S] [--method M] [--prefix NAME]")?;
    let mut parsed = Args {
        address,
        clients: 50,
        calls: 200,
        pipeline: 1,
        service: "machoNet".into(),
        method: "GetTime".into(),
        prefix: "load".into(),
    };
    while let Some(flag) = args.next() {
        let value = args.next().ok_or(format!("{flag} needs a value"))?;
        let number = |text: &str| text.parse::<usize>().map_err(|_| format!("{flag} wants a number"));
        match flag.as_str() {
            "--clients" => parsed.clients = number(&value)?,
            "--calls" => parsed.calls = number(&value)?,
            "--pipeline" => parsed.pipeline = number(&value)?.max(1),
            "--service" => parsed.service = value,
            "--method" => parsed.method = value,
            "--prefix" => parsed.prefix = value,
            other => return Err(format!("unknown flag {other}")),
        }
    }
    Ok(parsed)
}

fn percentile(sorted: &[Duration], p: f64) -> Duration {
    if sorted.is_empty() {
        return Duration::ZERO;
    }
    sorted[((sorted.len() as f64 - 1.0) * p).round() as usize]
}

fn describe(label: &str, mut samples: Vec<Duration>, wall: Duration) {
    samples.sort();
    let count = samples.len();
    println!(
        "{label:<10} {count:>8} in {:>7.3}s  {:>10.0}/s   p50 {:>9.1?}  p90 {:>9.1?}  p99 {:>9.1?}  max {:>9.1?}",
        wall.as_secs_f64(),
        count as f64 / wall.as_secs_f64(),
        percentile(&samples, 0.50),
        percentile(&samples, 0.90),
        percentile(&samples, 0.99),
        samples.last().copied().unwrap_or_default(),
    );
}

struct Connected {
    reader: FrameReader<tokio::net::tcp::OwnedReadHalf>,
    writer: tokio::net::tcp::OwnedWriteHalf,
    session: client::ClientSession,
    login_took: Duration,
}

async fn connect(address: &str, user: &str) -> Result<Connected, String> {
    let started = Instant::now();
    let stream = TcpStream::connect(address).await.map_err(|e| format!("connect: {e}"))?;
    stream.set_nodelay(true).map_err(|e| e.to_string())?;
    let (read, mut writer) = stream.into_split();
    let mut reader = FrameReader::new(read, MAX_FRAME);
    let session = client::login(&mut reader, &mut writer, user, &[7u8; 20], "EN")
        .await
        .map_err(|e| format!("{user}: {e}"))?;
    Ok(Connected { reader, writer, session, login_took: started.elapsed() })
}

/// Reads until the response to `call_id` arrives (other packets, such as the session's initial state, are skipped).
async fn await_response(
    reader: &mut FrameReader<tokio::net::tcp::OwnedReadHalf>,
    call_id: i64,
) -> Result<(), String> {
    loop {
        let frame =
            reader.next().await.map_err(|e| e.to_string())?.ok_or("the server closed the connection")?;
        let value: Value = decode_bytes(&frame).map_err(|e| e.to_string())?;
        let Some(state) = packet_state(&value) else { continue };
        let kind = state.first().and_then(int_of);
        let answers = match state.get(2) {
            Some(Value::Object { args, .. }) => {
                matches!(&**args, Value::Tuple(items) if items.get(2).and_then(int_of) == Some(call_id))
            }
            Some(Value::Tuple(items)) => items.get(2).and_then(int_of) == Some(call_id),
            _ => false,
        };
        if kind == Some(ERROR_RESPONSE) && answers {
            return Err("the server answered with an error".into());
        }
        if kind == Some(CALL_RSP) && answers {
            return Ok(());
        }
    }
}

async fn run_calls(mut connected: Connected, args: &Args) -> Result<Vec<Duration>, String> {
    let mut latencies = Vec::with_capacity(args.calls);
    let mut sent = 0usize;
    let mut done = 0usize;
    let mut in_flight: std::collections::VecDeque<(i64, Instant)> = std::collections::VecDeque::new();
    while done < args.calls {
        // keep `pipeline` calls in flight
        let mut batch = Vec::new();
        while sent < args.calls && in_flight.len() < args.pipeline {
            sent += 1;
            let call_id = sent as i64;
            let packet = connected.session.call_packet(call_id, &args.service, &args.method, vec![]);
            batch.extend_from_slice(&eve_marshal::encode_packet(&packet).map_err(|e| e.to_string())?);
            in_flight.push_back((call_id, Instant::now()));
        }
        if !batch.is_empty() {
            connected.writer.write_all(&batch).await.map_err(|e| e.to_string())?;
        }
        let (call_id, started) = in_flight.pop_front().ok_or("nothing in flight")?;
        await_response(&mut connected.reader, call_id).await?;
        latencies.push(started.elapsed());
        done += 1;
    }
    Ok(latencies)
}

#[tokio::main]
async fn main() -> ExitCode {
    let args = match parse() {
        Ok(args) => std::sync::Arc::new(args),
        Err(error) => {
            eprintln!("{error}");
            return ExitCode::from(2);
        }
    };
    println!(
        "{} clients, {} calls each, {} in flight, {}.{}",
        args.clients, args.calls, args.pipeline, args.service, args.method
    );

    // phase 1: log everyone in at once
    let started = Instant::now();
    let mut logins = Vec::new();
    for n in 0..args.clients {
        let args = args.clone();
        logins
            .push(tokio::spawn(async move { connect(&args.address, &format!("{}{n}", args.prefix)).await }));
    }
    let mut connected = Vec::new();
    for login in logins {
        match login.await {
            Ok(Ok(client)) => connected.push(client),
            Ok(Err(error)) => {
                eprintln!("login failed: {error}");
                return ExitCode::FAILURE;
            }
            Err(error) => {
                eprintln!("{error}");
                return ExitCode::FAILURE;
            }
        }
    }
    describe("logins", connected.iter().map(|c| c.login_took).collect(), started.elapsed());

    // phase 2: every client calls, all at once
    let started = Instant::now();
    let mut runs = Vec::new();
    for client in connected {
        let args = args.clone();
        runs.push(tokio::spawn(async move { run_calls(client, &args).await }));
    }
    let mut latencies = Vec::new();
    for run in runs {
        match run.await {
            Ok(Ok(samples)) => latencies.extend(samples),
            Ok(Err(error)) => {
                eprintln!("calls failed: {error}");
                return ExitCode::FAILURE;
            }
            Err(error) => {
                eprintln!("{error}");
                return ExitCode::FAILURE;
            }
        }
    }
    describe("calls", latencies, started.elapsed());
    ExitCode::SUCCESS
}
