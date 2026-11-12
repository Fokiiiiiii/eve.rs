use eve_sde::manifest::is_valid_table_name;

#[test]
fn letters_digits_and_underscores_make_a_valid_table_name() {
    for name in ["types", "invTypes", "dgmTypeAttributes", "Table_2", "_sde", "0"] {
        assert!(is_valid_table_name(name), "{name:?} should be valid");
    }
}

#[test]
fn empty_names_and_names_with_punctuation_spaces_or_non_ascii_are_rejected() {
    for name in ["", " ", "inv types", "types.jsonl", "types-2", "types/x", "a\\b", "t\u{ff}pes", "types\n"] {
        assert!(!is_valid_table_name(name), "{name:?} should be rejected");
    }
}
