use eve_marshal::{Value, decode_exact, encode};

#[test]
fn a_python_set_is_a_set_object_ex1_and_round_trips() {
    let set = Value::python_set(vec![Value::Int(3), Value::Int(7)]);
    let bytes = encode(&set).expect("a set encodes");
    // 0x7e header, zero save count, then PyObjectEx1 (0x22); no set opcode exists.
    assert_eq!(bytes[5], 0x22);
    assert_eq!(decode_exact(&bytes).expect("a set decodes"), set);
}

#[test]
fn an_empty_python_set_keeps_its_list() {
    let set = Value::python_set(Vec::new());
    let bytes = encode(&set).expect("an empty set encodes");
    assert_eq!(decode_exact(&bytes).expect("decodes"), set);
}
