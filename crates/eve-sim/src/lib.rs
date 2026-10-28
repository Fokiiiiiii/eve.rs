#![forbid(unsafe_code)]

//! One host at a time; its scenes, ships and sessions end when it disconnects.

use std::io;
use std::sync::Arc;
use std::time::Duration;

use eve_net::frame::FrameReader;
use eve_space::link::{Message, PROTOCOL_VERSION};
use eve_space::sim::{POLL_HZ, Sim, Universe};
use tokio::io::{AsyncRead, AsyncWrite, AsyncWriteExt};
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

async fn write_all<W: AsyncWrite + Unpin>(writer: &mut W, messages: &[Message]) -> io::Result<()> {
    let mut bytes = Vec::new();
    for message in messages {
        message.encode_into(&mut bytes);
    }
    if bytes.is_empty() { Ok(()) } else { writer.write_all(&bytes).await }
}

/// Handles one host stream until it closes or sends an invalid message.
/// `clock` returns FILETIME ticks for simulation updates.
pub async fn serve_connection<S, C>(stream: S, universe: Arc<Universe>, clock: C) -> io::Result<()>
where
    S: AsyncRead + AsyncWrite + Unpin,
    C: Fn() -> i64,
{
    let (reader, mut writer) = tokio::io::split(stream);
    let mut reader = FrameReader::new(reader, MAX_FRAME);
    let mut sim = Sim::new(universe);
    let mut poll = tokio::time::interval(Duration::from_millis(1000 / u64::from(POLL_HZ)));
    poll.set_missed_tick_behavior(MissedTickBehavior::Skip);

    // the host speaks first
    let hello = reader.next().await?.ok_or_else(|| {
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
            frame = reader.next() => {
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

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn a_partial_sim_frame_survives_select_cancellation() {
        use tokio::io::AsyncWriteExt;

        let (mut writer, reader) = tokio::io::duplex(16);
        let mut frames = FrameReader::new(reader, MAX_FRAME);

        writer.write_all(&[3, 0]).await.unwrap();
        tokio::select! {
            frame = frames.next() => panic!("partial header unexpectedly produced {frame:?}"),
            _ = tokio::time::sleep(Duration::from_millis(10)) => {}
        }

        writer.write_all(&[0, 0, 1]).await.unwrap();
        tokio::select! {
            frame = frames.next() => panic!("partial payload unexpectedly produced {frame:?}"),
            _ = tokio::time::sleep(Duration::from_millis(10)) => {}
        }

        writer.write_all(&[2, 3]).await.unwrap();
        assert_eq!(frames.next().await.unwrap().unwrap().as_ref(), &[1, 2, 3]);
    }
}
