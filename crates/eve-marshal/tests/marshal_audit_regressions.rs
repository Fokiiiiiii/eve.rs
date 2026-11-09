use eve_marshal::{Bytes, Value, decode, decode_exact, encode};

#[test]
fn pickled_data_uses_stop_and_preserves_the_following_value() {
    let pickle = [0x80, 0x02, 0x58, 0x03, 0, 0, 0, 0x2e, 0x78, 0x2e, 0x2e];
    let expected =
        [0x7e, 0, 0, 0, 0, 0x2c, 0x21, 0x80, 0x02, 0x58, 0x03, 0, 0, 0, 0x2e, 0x78, 0x2e, 0x2e, 0x09];
    let value = Value::Tuple(vec![Value::CPickled(Bytes::from(pickle.to_vec())), Value::Int(1)]);

    assert_eq!(encode(&value).unwrap(), expected);
    assert_eq!(
        decode_exact(&expected).unwrap(),
        Value::Tuple(vec![Value::CPickled(Bytes::from(pickle.to_vec())), Value::Int(1)])
    );
}

#[test]
fn variable_width_integer_uses_twos_complement() {
    assert_eq!(decode_exact(&[0x7e, 0, 0, 0, 0, 0x2f, 1, 0xff]).unwrap(), Value::Int(-1));
    assert_eq!(decode_exact(&[0x7e, 0, 0, 0, 0, 0x2f, 1, 0x80]).unwrap(), Value::Int(-128));
    assert_eq!(decode_exact(&[0x7e, 0, 0, 0, 0, 0x2f, 2, 0x7f, 0xff]).unwrap(), Value::Int(-129));
    assert_eq!(
        decode_exact(&[0x7e, 0, 0, 0, 0, 0x2f, 7, 0, 0, 0, 0, 0, 0, 0xe0]).unwrap(),
        Value::BigSigned(vec![0, 0, 0, 0, 0, 0, 0xe0])
    );
    assert_eq!(
        decode_exact(&[0x7e, 0, 0, 0, 0, 0x2f, 8, 0, 0, 0, 0, 0, 0, 0, 0x80]).unwrap(),
        Value::BigSigned(vec![0, 0, 0, 0, 0, 0, 0, 0x80])
    );
    assert_eq!(
        decode_exact(&[0x7e, 0, 0, 0, 0, 0x2f, 9, 0, 0, 0, 0, 0, 0, 0, 0x80, 0]).unwrap(),
        Value::BigUnsigned(vec![0, 0, 0, 0, 0, 0, 0, 0x80])
    );
}

#[test]
fn variable_width_integer_encoding_preserves_sign_padding() {
    let positive = Value::BigUnsigned(vec![0xff; 8]);
    assert_eq!(
        encode(&positive).unwrap(),
        [0x7e, 0, 0, 0, 0, 0x2f, 9, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0]
    );

    let negative = Value::BigSigned(vec![0, 0, 0, 0, 0, 0, 0, 0x80]);
    assert_eq!(encode(&negative).unwrap(), [0x7e, 0, 0, 0, 0, 0x2f, 8, 0, 0, 0, 0, 0, 0, 0, 0x80]);
}

#[test]
fn payload_reads_and_object_ex_terminators_are_bounded() {
    for wire in [
        [0x7e, 1, 0, 0, 0, 1, 0, 0, 0].as_slice(),
        [0x7e, 0, 0, 0, 0, 0x13, 10, 1, 2, 3].as_slice(),
        [0x7e, 0, 0, 0, 0, 0x22, 1].as_slice(),
        [0x7e, 0, 0, 0, 0, 0x22, 1, 0x2d].as_slice(),
    ] {
        assert!(decode(wire).is_err(), "accepted malformed wire: {wire:02x?}");
    }
}

#[test]
fn saved_references_require_a_published_slot() {
    assert!(decode_exact(&[0x7e, 1, 0, 0, 0, 0x15, 2, 0x41, 0x1b, 1, 1, 0, 0, 0]).is_err());
    assert!(decode(&[0x7e, 1, 0, 0, 0, 0x01, 0, 0, 0, 0]).is_err());
}

#[test]
fn saved_list_publishes_a_resolvable_self_reference() {
    let decoded = decode_exact(&[0x7e, 1, 0, 0, 0, 0x55, 1, 0x1b, 1, 1, 0, 0, 0]).unwrap();
    let Value::SavedGraph(graph) = decoded else { panic!("expected a saved graph") };
    assert_eq!(graph.saved_slot_count(), 1);
    let Value::List(items) = graph.root() else { panic!("expected a saved list") };
    let Value::SavedReference(reference) = &items[0] else { panic!("expected a graph link") };
    assert_eq!(reference.index(), 1);
    let target = reference.value().expect("published list target");
    assert!(matches!(target, Value::List(_)));
}

#[test]
fn saved_reference_targets_are_released_with_the_decoded_graph() {
    let reference = {
        let decoded = decode_exact(&[0x7e, 1, 0, 0, 0, 0x55, 1, 0x1b, 1, 1, 0, 0, 0]).unwrap();
        let Value::SavedGraph(graph) = decoded else { panic!("expected a saved graph") };
        let Value::List(items) = graph.root() else { panic!("expected a saved list") };
        let Value::SavedReference(reference) = &items[0] else { panic!("expected a graph link") };
        reference.clone()
    };

    assert!(reference.value().is_none());
}
