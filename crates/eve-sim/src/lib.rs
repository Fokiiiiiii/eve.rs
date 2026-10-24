#![forbid(unsafe_code)]

//! One host at a time; its scenes, ships and sessions end when it disconnects.

use std::io;
use std::sync::Arc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use eve_space::link::{Message, PROTOCOL_VERSION};
use eve_space::sim::{POLL_HZ, Sim, Universe};
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use tokio::time::{Instant, MissedTickBehavior};

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
        message.encode_into(&mut bytes);
    }
    if bytes.is_empty() { Ok(()) } else { writer.write_all(&bytes).await }
}

fn delay_to_next_second(now: SystemTime) -> Duration {
    let millis = now.duration_since(UNIX_EPOCH).unwrap_or_default().as_millis() % 1000;
    Duration::from_millis(1000 - millis as u64)
}

fn hold_tick_aligned(messages: Vec<Message>, held: &mut Vec<Message>) -> Vec<Message> {
    let mut immediate = Vec::with_capacity(messages.len());
    let mut preserve_notification_order = !held.is_empty();
    for message in messages {
        let jump_follows_held_update = matches!(
            &message,
            Message::Jumping { session_id, .. }
                if held.iter().any(|held| matches!(
                    held,
                    Message::Notify { session_id: held_session, name, .. }
                        if held_session == session_id && name == "DoDestinyUpdate"
                ))
        );
        if matches!(&message, Message::Notify { name, .. } if name == "DoDestinyUpdate") {
            held.push(message);
            preserve_notification_order = true;
        } else if jump_follows_held_update
            || preserve_notification_order && matches!(&message, Message::Notify { .. })
        {
            held.push(message);
        } else {
            immediate.push(message);
        }
    }
    immediate
}

/// Handles one host stream until it closes or sends an invalid message.
/// `clock` returns FILETIME ticks for simulation updates.
pub async fn serve_connection<S, C>(stream: S, universe: Arc<Universe>, clock: C) -> io::Result<()>
where
    S: AsyncRead + AsyncWrite + Unpin,
    C: Fn() -> i64,
{
    let (mut reader, mut writer) = tokio::io::split(stream);
    let mut sim = Sim::new(universe);
    let mut poll = tokio::time::interval(Duration::from_millis(1000 / u64::from(POLL_HZ)));
    poll.set_missed_tick_behavior(MissedTickBehavior::Skip);
    let first_flush = Instant::now() + delay_to_next_second(SystemTime::now());
    let mut flush = tokio::time::interval_at(first_flush, Duration::from_secs(1));
    flush.set_missed_tick_behavior(MissedTickBehavior::Skip);
    let mut held = Vec::new();

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
                write_all(&mut writer, &hold_tick_aligned(replies, &mut held)).await?;
            }
            _ = poll.tick() => {
                let due = sim.poll(clock());
                write_all(&mut writer, &hold_tick_aligned(due, &mut held)).await?;
            }
            _ = flush.tick() => {
                write_all(&mut writer, &std::mem::take(&mut held)).await?;
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_destiny_update_waits_for_the_next_whole_second_boundary() {
        let now = UNIX_EPOCH + Duration::from_millis(12_345);
        assert_eq!(delay_to_next_second(now), Duration::from_millis(655));
    }

    #[test]
    fn tick_aligned_updates_preserve_notification_order_and_keep_replies_immediate() {
        let destiny = Message::Notify {
            session_id: 1,
            name: "DoDestinyUpdate".into(),
            id_type: "clientID".into(),
            args: Vec::new(),
        };
        let followup = Message::Notify {
            session_id: 1,
            name: "OnInvulnCancelled".into(),
            id_type: "clientID".into(),
            args: Vec::new(),
        };
        let reply = Message::Accepted { session_id: 1, command: 4, at: 0 };
        let mut held = Vec::new();
        let immediate = hold_tick_aligned(vec![destiny.clone(), reply.clone()], &mut held);
        assert_eq!(immediate.as_slice(), std::slice::from_ref(&reply));
        assert_eq!(held, [destiny,]);

        let immediate = hold_tick_aligned(vec![followup.clone(), reply.clone()], &mut held);
        assert_eq!(immediate, [reply]);
        assert_eq!(
            held,
            [
                Message::Notify {
                    session_id: 1,
                    name: "DoDestinyUpdate".into(),
                    id_type: "clientID".into(),
                    args: Vec::new(),
                },
                followup,
            ]
        );
        held.clear();
        let destiny = Message::Notify {
            session_id: 1,
            name: "DoDestinyUpdate".into(),
            id_type: "clientID".into(),
            args: Vec::new(),
        };
        let jumping = Message::Jumping {
            session_id: 1,
            command: 9,
            destination_system: 30_000_002,
            destination_gate: 50_000_002,
        };
        let immediate = hold_tick_aligned(vec![destiny.clone(), jumping.clone()], &mut held);
        assert!(immediate.is_empty());
        assert_eq!(held, [destiny, jumping]);
    }
}
