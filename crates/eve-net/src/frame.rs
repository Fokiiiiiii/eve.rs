//! Length-prefixed frames: `u32 length (little endian) | payload`, on the client's connection and on the
//! host link alike.

use std::io;

use bytes::{Buf, BufMut, Bytes, BytesMut};
use tokio::io::{AsyncRead, AsyncReadExt};

/// The largest frame either side accepts (16 MiB).
pub const MAX_FRAME: usize = 16 << 20;

/// Reads frames from a stream, several per read when they arrive together.
pub struct FrameReader<R> {
    inner: R,
    buffer: BytesMut,
    max: usize,
}

impl<R: AsyncRead + Unpin> FrameReader<R> {
    pub fn new(inner: R, max: usize) -> Self {
        Self { inner, buffer: BytesMut::with_capacity(16 * 1024), max }
    }

    /// The next frame's payload, or `None` when the peer closed cleanly between frames.
    pub async fn next(&mut self) -> io::Result<Option<Bytes>> {
        loop {
            if self.buffer.len() >= 4 {
                let length =
                    u32::from_le_bytes([self.buffer[0], self.buffer[1], self.buffer[2], self.buffer[3]])
                        as usize;
                if length == 0 {
                    return Err(io::Error::new(io::ErrorKind::InvalidData, "empty frame"));
                }
                if length > self.max {
                    return Err(io::Error::new(
                        io::ErrorKind::InvalidData,
                        format!("frame of {length} bytes exceeds {}", self.max),
                    ));
                }
                if self.buffer.len() >= 4 + length {
                    self.buffer.advance(4);
                    return Ok(Some(self.buffer.split_to(length).freeze()));
                }
                self.buffer.reserve(4 + length - self.buffer.len());
            }
            if self.inner.read_buf(&mut self.buffer).await? == 0 {
                return if self.buffer.is_empty() {
                    Ok(None)
                } else {
                    Err(io::Error::new(
                        io::ErrorKind::UnexpectedEof,
                        "the peer closed in the middle of a frame",
                    ))
                };
            }
        }
    }
}

/// Appends `payload` to `out` as a frame.
pub fn put_frame(out: &mut BytesMut, payload: &[u8]) {
    out.reserve(4 + payload.len());
    out.put_u32_le(payload.len() as u32);
    out.put_slice(payload);
}
