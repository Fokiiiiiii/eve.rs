#![forbid(unsafe_code)]

//! The simulation process: it listens on a socket, the service host connects, and everything the host says is handed to
//! a [`Sim`](eve_space::sim::Sim) (`docs/design/adr/0007-space-simulation-process.md`).
//!
//! One host at a time. A connection has its own scenes: when the host goes away (or is restarted) its ships go with it,
//! as its sessions do.

use std::io;
use std::sync::Arc;
use std::time::Duration;

use eve_space::link::{Message, PROTOCOL_VERSION};
use eve_space::sim::{POLL_HZ, Sim, Universe};
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use tokio::time::MissedTickBehavior;

/// The largest frame accepted (a ship's numbers are a few hundred bytes; this is generous).
const MAX_FRAME: usize = 1 << 20;

/// FILETIME ticks (100 ns) since 1601 at the moment of the call.
pub fn filetime_now() -> i64 {
    use std::time::{SystemTime, UNIX_EPOCH};
    const UNIX_EPOCH_TICKS: i64 = 116_444_736_000_000_000;
    let since_epoch = SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default();
    UNIX_EPOCH_TICKS + (since_epoch.as_nanos() / 100) as i64
}

/// Reads one frame's payload; `None` when the peer closed between frames.
async fn read_frame<R: AsyncRead + Unpin>(reader: &mut R) -> io::Result<Option<Vec<u8>>> {
    let mut length = [0u8; 4];
    match reader.read_exact(&mut length).await {
        Ok(_) => {}
        Err(error) if error.kind() == io::ErrorKind::UnexpectedEof => return Ok(None),
        Err(error) => return Err(error),
    }
    let length = u32::from_le_bytes(length) as usize;
    if length > MAX_FRAME {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            format!("frame of {length} bytes exceeds {MAX_FRAME}"),
        ));
    }
    let mut payload = vec![0u8; length];
    reader.read_exact(&mut payload).await?;
    Ok(Some(payload))
}

async fn write_all<W: AsyncWrite + Unpin>(writer: &mut W, messages: &[Message]) -> io::Result<()> {
    let mut bytes = Vec::new();
    for message in messages {
        bytes.extend_from_slice(&message.encode());
    }
    if bytes.is_empty() { Ok(()) } else { writer.write_all(&bytes).await }
}

/// Serves one host over a connected stream until it closes it or says something the simulation cannot take.
///
/// `clock` says what time it is (a FILETIME); tests pass their own.
pub async fn serve_connection<S, C>(stream: S, universe: Arc<Universe>, clock: C) -> io::Result<()>
where
    S: AsyncRead + AsyncWrite + Unpin,
    C: Fn() -> i64,
{
    let (mut reader, mut writer) = tokio::io::split(stream);
    let mut sim = Sim::new(universe);
    let mut poll = tokio::time::interval(Duration::from_millis(1000 / u64::from(POLL_HZ)));
    poll.set_missed_tick_behavior(MissedTickBehavior::Skip);

    // the host speaks first
    let hello = read_frame(&mut reader).await?.ok_or_else(|| {
        io::Error::new(io::ErrorKind::UnexpectedEof, "the host closed before it said hello")
    })?;
    match Message::decode(&hello) {
        Ok(Message::Hello { version }) if version == PROTOCOL_VERSION => {
            let replies = sim.handle(Message::Hello { version }, clock()).map_err(io::Error::other)?;
            write_all(&mut writer, &replies).await?;
        }
        Ok(Message::Hello { version }) => {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                format!("the host speaks version {version}, this is {PROTOCOL_VERSION}"),
            ));
        }
        Ok(other) => {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                format!("the host said kind {} before hello", other.kind()),
            ));
        }
        Err(error) => return Err(io::Error::new(io::ErrorKind::InvalidData, error)),
    }

    loop {
        tokio::select! {
            frame = read_frame(&mut reader) => {
                let Some(payload) = frame? else { return Ok(()) };
                let message = Message::decode(&payload).map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))?;
                let replies = sim.handle(message, clock()).map_err(io::Error::other)?;
                write_all(&mut writer, &replies).await?;
            }
            _ = poll.tick() => {
                let due = sim.poll(clock());
                write_all(&mut writer, &due).await?;
            }
        }
    }
}
