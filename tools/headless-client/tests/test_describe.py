"""The description of decoded values, and the comparison by shape."""

from evecli import marshal as m
from evecli.describe import describe, differences, shape, type_name


def test_json_native_values_stand_for_themselves():
    assert describe(None) is None
    assert describe(True) is True
    assert describe(5) == 5
    assert describe(1.5) == 1.5
    assert describe(m.PyString(b"text")) == "text"
    assert describe("plain") == "plain"


def test_the_kinds_a_client_can_tell_apart_stay_apart():
    assert describe(m.Long(5)) == {"$long": 5}
    assert describe(m.WString("u")) == {"$unicode": "u"}
    assert describe(m.Buffer(b"\x01\xff")) == {"$buffer": "01ff"}
    assert describe(m.Token("t")) == {"$token": "t"}
    assert describe((1, "a")) == {"$tuple": [1, "a"]}
    assert describe(m.List([1, 2])) == [1, 2]
    assert describe(m.PyString(b"\xff\xfe")) == "$bytes:fffe"


def test_dictionaries_keep_their_order_and_non_text_keys():
    value = m.Dict([("b", 1), (7, 2)])
    assert describe(value) == {"$dict": [["b", 1], [7, 2]]}


def test_objects_keep_their_class_names_and_nested_streams_are_unwrapped():
    value = m.Object("util.KeyVal", m.Dict([("k", m.SubStream(value=5))]))
    assert describe(value) == {"$object": "util.KeyVal", "args": {"$dict": [["k", {"$substream": 5}]]}}


def test_type_names():
    assert [type_name(describe(v)) for v in (None, True, 1, 1.5, "s", m.List([]), (1,), m.Long(1), m.Dict([]))] == [
        "none", "bool", "int", "float", "str", "list", "tuple", "long", "dict",
    ]


def test_shape_keeps_structure_and_replaces_leaves_by_type():
    described = describe((m.Long(5), m.Dict([("name", m.PyString(b"x")), ("n", 3)]), m.List([1, 2])))
    assert shape(described) == {"$tuple": [{"$long": "value"}, {"$dict": [["name", "str"], ["n", "int"]]}, ["int", "int"]]}


def test_equal_descriptions_have_no_differences():
    value = describe(m.Object("util.KeyVal", m.Dict([("a", 1), ("b", (None, m.Long(2)))])))
    assert differences(value, value) == []


def test_a_changed_value_a_changed_type_and_a_changed_length_are_each_reported():
    a = describe(m.Dict([("a", 1), ("b", "x"), ("c", m.List([1, 2]))]))
    assert differences(a, describe(m.Dict([("a", 2), ("b", "x"), ("c", m.List([1, 2]))]))) == ["$.a: 1 expected, got 2"]
    assert "expected str" in differences(a, describe(m.Dict([("a", 1), ("b", 5), ("c", m.List([1, 2]))])))[0]
    assert "2 items expected, 3 received" in differences(a, describe(m.Dict([("a", 1), ("b", "x"), ("c", m.List([1, 2, 3]))])))[0]


def test_missing_and_unexpected_keys_are_reported_by_name():
    a = describe(m.Dict([("a", 1), ("b", 2)]))
    b = describe(m.Dict([("a", 1), ("c", 2)]))
    assert sorted(differences(a, b)) == ["$: missing key b", "$: unexpected key c"]


def test_dictionary_order_does_not_matter():
    assert differences(describe(m.Dict([("a", 1), ("b", 2)])), describe(m.Dict([("b", 2), ("a", 1)]))) == []


def test_volatile_keys_compare_by_type_however_deep():
    expected = describe(m.Dict([("charid", (None, m.Long(5))), ("name", "x")]))
    live = describe(m.Dict([("charid", (None, m.Long(999))), ("name", "x")]))
    assert differences(expected, live) != []
    assert differences(expected, live, volatile_keys={"charid"}) == []
    wrong_type = describe(m.Dict([("charid", (None, "text")), ("name", "x")]))
    assert "shape" in differences(expected, wrong_type, volatile_keys={"charid"})[0]
    # the other keys are still compared by value
    other = describe(m.Dict([("charid", (None, m.Long(999))), ("name", "y")]))
    assert differences(expected, other, volatile_keys={"charid"}) == ["$.name: 'x' expected, got 'y'"]


def test_object_class_names_are_compared():
    a = describe(m.Object("util.KeyVal", ()))
    b = describe(m.Object("util.Row", ()))
    assert "class util.KeyVal expected" in differences(a, b)[0]


def _extended(name, args, state):
    return {"$objectex": {"$tuple": [{"$token": name}, {"$tuple": args}, {"$dict": state}]}, "list": [], "dict": []}


def test_extended_objects_are_compared_inside_and_their_volatile_keys_by_type():
    a = _extended("x.Entry", [3300, 4], [["itemID", {"$long": 5}], ["groupName", "Gunnery"]])
    same = _extended("x.Entry", [3300, 4], [["itemID", {"$long": 5}], ["groupName", "Gunnery"]])
    other_item = _extended("x.Entry", [3300, 4], [["itemID", {"$long": 99}], ["groupName", "Gunnery"]])
    assert differences(a, same) == []
    assert differences(a, other_item) != []
    assert differences(a, other_item, volatile_keys={"itemID"}) == []
    # a volatile key still has to have the same type, and everything else is compared by value
    assert differences(a, _extended("x.Entry", [3300, 4], [["itemID", "text"], ["groupName", "Gunnery"]]), volatile_keys={"itemID"}) != []
    assert "5" in differences(a, _extended("x.Entry", [3300, 5], [["itemID", {"$long": 5}], ["groupName", "Gunnery"]]))[0]
    assert differences(a, _extended("y.Entry", [3300, 4], [["itemID", {"$long": 5}], ["groupName", "Gunnery"]])) != []
