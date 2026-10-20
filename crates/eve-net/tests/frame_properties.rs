use std::io::{Cursor, ErrorKind};

use bytes::{Bytes, BytesMut};
use eve_net::frame::{FrameReader, put_frame};

#[tokio::test]
async fn generated_coalesced_frames_round_trip_in_order() {
    let mut seed = 0x9e37_79b9_7f4a_7c15u64;
    for sample in 0..256 {
        let frame_count = (sample % 17) + 1;
        let mut wire = BytesMut::new();
        let mut expected = Vec::new();
        for _ in 0..frame_count {
            seed ^= seed << 13;
            seed ^= seed >> 7;
            seed ^= seed << 17;
            let length = (seed % 128 + 1) as usize;
            let payload: Vec<u8> = (0..length)
                .map(|_| {
                    seed ^= seed << 13;
                    seed ^= seed >> 7;
                    seed ^= seed << 17;
                    seed as u8
                })
                .collect();
            put_frame(&mut wire, &payload);
            expected.push(Bytes::from(payload));
        }

        let mut reader = FrameReader::new(Cursor::new(wire.freeze()), 256);
        for payload in expected {
            assert_eq!(reader.next().await.unwrap(), Some(payload), "sample {sample}");
        }
        assert_eq!(reader.next().await.unwrap(), None);
    }
}

#[tokio::test]
async fn empty_frames_are_preserved_and_incomplete_frames_are_rejected() {
    let mut empty = FrameReader::new(Cursor::new([0u8; 4]), 16);
    assert_eq!(empty.next().await.unwrap().unwrap(), Bytes::new());

    let mut short_header = FrameReader::new(Cursor::new([1u8, 2]), 16);
    assert_eq!(short_header.next().await.unwrap_err().kind(), ErrorKind::UnexpectedEof);

    let mut short_payload = FrameReader::new(Cursor::new([3u8, 0, 0, 0, 0x41]), 16);
    assert_eq!(short_payload.next().await.unwrap_err().kind(), ErrorKind::UnexpectedEof);
}

#[tokio::test]
async fn oversize_declaration_is_rejected_before_payload_read() {
    let mut reader = FrameReader::new(Cursor::new([17u8, 0, 0, 0]), 16);
    assert_eq!(reader.next().await.unwrap_err().kind(), ErrorKind::InvalidData);
}
