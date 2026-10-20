//! Properties a native decoder fed by a network peer must have that the JS
//! reference gets from its runtime: bounded stack, bounded allocation.

use eve_marshal::{
    MAX_DEPTH, MAX_ELEMENTS, MAX_INPUT_BYTES, MarshalError, SubStream, Value, compress_rle, decode,
    decode_exact, decompress_rle, encode, encode_packet, split_packet,
};

const HEADER: [u8; 5] = [0x7e, 0, 0, 0, 0];

fn stream(body: &[u8]) -> Vec<u8> {
    let mut bytes = HEADER.to_vec();
    bytes.extend_from_slice(body);
    bytes
}

/// A tiny deterministic generator so the tests need no dependencies.
struct XorShift(u64);

impl XorShift {
    fn next(&mut self) -> u64 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 7;
        self.0 ^= self.0 << 17;
        self.0
    }
}

#[test]
fn deeply_nested_tuples_fail_cleanly_instead_of_overflowing_the_stack() {
    let mut body = vec![0x25; 100_000]; // PyOneTuple, 100k deep
    body.push(0x01);
    assert_eq!(decode(&stream(&body)), Err(MarshalError::TooDeep));
}

#[test]
fn nesting_at_the_limit_still_decodes() {
    let mut body = vec![0x25; MAX_DEPTH - 1];
    body.push(0x01);
    assert!(decode(&stream(&body)).is_ok());
}

#[test]
fn a_chain_of_substreams_cannot_get_around_the_depth_limit() {
    // Each level wraps the previous stream in a PySubStream.
    let mut inner = stream(&[0x01]);
    for _ in 0..5_000 {
        let mut wrapped = vec![0x2b];
        if inner.len() < 0xff {
            wrapped.push(inner.len() as u8);
        } else {
            wrapped.push(0xff);
            wrapped.extend_from_slice(&(inner.len() as u32).to_le_bytes());
        }
        wrapped.extend_from_slice(&inner);
        inner = stream(&wrapped);
    }
    // No stack overflow; the too-deep part is kept as raw bytes.
    assert!(matches!(decode(&inner), Ok(Value::SubStream(_))));
}

#[test]
fn a_huge_declared_count_does_not_allocate_for_it() {
    for opcode in [0x14u8 /* tuple */, 0x15 /* list */, 0x16 /* dict */] {
        let bytes = stream(&[opcode, 0xff, 0xff, 0xff, 0xff, 0xff]); // count = u32::MAX, no elements
        assert_eq!(decode(&bytes), Err(MarshalError::ResourceLimit("element count")), "opcode {opcode:#x}");
    }
}

#[test]
fn a_huge_declared_save_table_is_rejected() {
    let bytes = [0x7e, 0xff, 0xff, 0xff, 0x7f, 0x01];
    assert!(matches!(decode(&bytes), Err(MarshalError::SaveTable(_))));
}

#[test]
fn marshal_input_size_is_bounded_before_decoding() {
    let input = vec![0; MAX_INPUT_BYTES + 1];
    assert_eq!(decode(&input), Err(MarshalError::ResourceLimit("input byte")));
    assert_eq!(decode_exact(&input), Err(MarshalError::ResourceLimit("input byte")));
}

#[test]
fn element_count_limit_rejects_a_large_declared_list_before_allocation() {
    let count = (MAX_ELEMENTS as u32 + 1).to_le_bytes();
    let bytes = stream(&[0x15, 0xff, count[0], count[1], count[2], count[3]]);
    assert_eq!(decode(&bytes), Err(MarshalError::ResourceLimit("element count")));
}

#[test]
fn truncated_strings_are_lenient_like_the_reference_but_exact_mode_rejects_them() {
    // PyShortString declaring 255 bytes with one present.
    let bytes = stream(&[0x10, 0xff, 0x61]);
    assert_eq!(decode(&bytes), Ok(Value::Bytes(vec![0x61].into())));
    assert!(decode_exact(&bytes).is_err());
}

#[test]
fn arbitrary_bytes_never_panic() {
    let mut random = XorShift(0x9e37_79b9_7f4a_7c15);
    for round in 0..20_000 {
        let length = (random.next() % 64) as usize;
        let mut bytes: Vec<u8> = (0..length).map(|_| random.next() as u8).collect();
        // Half the inputs get a valid header so the value decoder is reached.
        if round % 2 == 0 {
            bytes.splice(0..0, HEADER);
        }
        let _ = decode(&bytes);
        let _ = decode_exact(&bytes);
    }
}

fn generated_value(random: &mut XorShift, depth: usize) -> Value {
    let choice = if depth == 0 { random.next() % 5 } else { random.next() % 8 };
    match choice {
        0 => Value::None,
        1 => Value::Bool(random.next() & 1 != 0),
        2 => Value::Int((random.next() as i16) as i64),
        3 => Value::WString(format!("value-{}", random.next() % 10_000).into()),
        4 => {
            let length = (random.next() % 12) as usize;
            Value::Bytes((0..length).map(|_| random.next() as u8).collect::<Vec<_>>().into())
        }
        5 => {
            let count = (random.next() % 4) as usize;
            Value::List((0..count).map(|_| generated_value(random, depth - 1)).collect())
        }
        6 => {
            let count = (random.next() % 4) as usize;
            Value::Tuple((0..count).map(|_| generated_value(random, depth - 1)).collect())
        }
        _ => Value::Buffer(vec![random.next() as u8; (random.next() % 16) as usize].into()),
    }
}

#[test]
fn generated_values_round_trip_through_the_marshal_codec() {
    let mut random = XorShift(0x6a09_e667_f3bc_c909);
    for iteration in 0..2_000 {
        let value = generated_value(&mut random, 5);
        let bytes = encode(&value).unwrap();
        assert_eq!(decode_exact(&bytes), Ok(value), "generated value {iteration}");
    }
}

#[test]
fn rle_round_trips_including_trailing_zeros() {
    let mut random = XorShift(42);
    for round in 0..5_000 {
        let length = (random.next() % 90) as usize;
        let zero_bias = round % 5; // 0..4: from dense to mostly-zero data
        let data: Vec<u8> = (0..length)
            .map(|_| if random.next() % 5 < zero_bias as u64 { 0 } else { random.next() as u8 | 1 })
            .collect();
        let packed = compress_rle(&data);
        assert_eq!(decompress_rle(&packed, data.len()), data, "round {round}: {data:?} -> {packed:?}");
    }
}

#[test]
fn size_fields_switch_to_the_five_byte_form_at_255() {
    let body = |count: usize| encode(&Value::Tuple(vec![Value::Int(0); count])).unwrap()[5..].to_vec();
    assert_eq!(&body(254)[..2], &[0x14, 254]);
    assert_eq!(&body(255)[..6], &[0x14, 0xff, 255, 0, 0, 0]);
}

#[test]
fn a_substream_over_254_bytes_gets_the_wide_size_form() {
    let inner = Value::Buffer(vec![7; 300].into());
    let bytes = encode(&Value::SubStream(SubStream::Decoded(Box::new(inner.clone())))).unwrap();
    assert_eq!(bytes[5], 0x2b);
    assert_eq!(bytes[6], 0xff);
    let length = u32::from_le_bytes(bytes[7..11].try_into().unwrap()) as usize;
    assert_eq!(length, bytes.len() - 11);
    assert_eq!(decode_exact(&bytes), Ok(Value::SubStream(SubStream::Decoded(Box::new(inner)))));
}

#[test]
fn values_round_trip_where_the_model_is_lossless() {
    let value = Value::Tuple(vec![
        Value::None,
        Value::Bool(true),
        Value::Int(-1),
        Value::Int(300),
        Value::Int(-70_000),
        Value::Int(1 << 40),
        Value::Long(5),
        Value::Real(1.5),
        Value::Buffer(vec![1, 2, 3].into()),
        Value::WString("日本語".into()),
        Value::List(vec![Value::Int(1), Value::Int(2)]),
        Value::dict([("machoVersion", 496)]),
        Value::Token("macho.CallReq".into()),
    ]);
    let bytes = encode(&value).unwrap();
    let decoded = decode_exact(&bytes).unwrap();
    // The dict keys are string-table hits or long strings; long strings come
    // back as `Bytes`, so compare the rest structurally.
    let (Value::Tuple(expected), Value::Tuple(actual)) = (&value, &decoded) else { panic!("tuples") };
    assert_eq!(expected.len(), actual.len());
    for (index, (want, got)) in expected.iter().zip(actual).enumerate() {
        match index {
            // Beyond i32 an integer is written as PyLongLong, so `Int` comes
            // back as `Long`: the wire format cannot tell them apart.
            5 => assert_eq!(got, &Value::Long(1 << 40)),
            11 => {}
            _ => assert_eq!(want, got, "element {index}"),
        }
    }
}

#[test]
fn packets_frame_and_split() {
    let packet = encode_packet(&Value::Int(9)).unwrap();
    let payload = &packet[4..];
    assert_eq!(u32::from_le_bytes(packet[..4].try_into().unwrap()) as usize, payload.len());

    let mut two = packet.clone();
    two.extend_from_slice(&packet);
    let (first, rest) = split_packet(&two).unwrap();
    assert_eq!(first, payload);
    assert_eq!(rest, &packet[..]);
    assert_eq!(split_packet(&packet[..packet.len() - 1]), None, "incomplete frame");
    assert_eq!(split_packet(&[1, 2]), None);
}

#[test]
fn the_encoder_is_bounded_too() {
    let mut value = Value::None;
    for _ in 0..1000 {
        value = Value::Tuple(vec![value]);
    }
    assert_eq!(encode(&value), Err(MarshalError::TooDeep));

    // Just inside the limit, on the default 2 MiB test-thread stack.
    let mut value = Value::None;
    for _ in 0..MAX_DEPTH - 1 {
        value = Value::List(vec![value]);
    }
    assert!(encode(&value).is_ok());
    // A value that came out of the decoder always re-encodes.
    let mut body = vec![0x27; MAX_DEPTH - 1];
    body.push(0x01);
    let decoded = decode(&stream(&body)).unwrap();
    assert!(encode(&decoded).is_ok());
}
