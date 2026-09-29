"""A readable, lossless-enough JSON view of decoded marshal values, and a comparison by shape.

`describe` keeps what a client can tell apart: the string kinds (`str`, `unicode`, `buffer`), plain and long
integers, tuples and lists, dictionaries with their key order, and objects with their class names.
JSON-native values stand for themselves (null, booleans, integers, floats, `str` as text); everything else is a
one-key object: {"$long": 5}, {"$unicode": "x"}, {"$buffer": "ab12"}, {"$tuple": [...]}, {"$dict": [[k, v]]},
{"$object": "name", "args": ...}.

`shape` reduces a description to the types and structure; `differences` compares a recorded description with a
live one, comparing the values under some dictionary keys (identifiers, times: `volatile_keys`) by type only.
"""

from __future__ import annotations

from typing import Any

from . import marshal as m

def describe(value: Any) -> Any:
    if value is None or isinstance(value, (bool, float)):
        return value
    if isinstance(value, m.Long):
        return {"$long": int(value)}
    if isinstance(value, m.BigUnsigned):
        return {"$bigunsigned": int(value)}
    if isinstance(value, int):
        return int(value)
    if isinstance(value, m.PyString):
        return _text(bytes(value))
    if isinstance(value, m.Buffer):
        return {"$buffer": bytes(value).hex()}
    if isinstance(value, m.WString):
        return {"$unicode": str(value)}
    if isinstance(value, m.Token):
        return {"$token": str(value)}
    if isinstance(value, m.CPickled):
        return {"$pickle": bytes(value).hex()}
    if isinstance(value, str):
        return _text(value.encode("utf-8"))
    if isinstance(value, (bytes, bytearray)):
        return _text(bytes(value))
    if isinstance(value, m.Dict):
        return {"$dict": [[describe(k), describe(v)] for k, v in value]}
    if isinstance(value, m.List):
        return [describe(item) for item in value]
    if isinstance(value, tuple):
        return {"$tuple": [describe(item) for item in value]}
    if isinstance(value, m.SubStream):
        return {"$substream": describe(value.value) if value.raw is None else {"$raw": bytes(value.raw).hex()}}
    if isinstance(value, m.SubStruct):
        return {"$substruct": describe(value.value)}
    if isinstance(value, m.Object):
        return {"$object": _text(value.name.encode() if isinstance(value.name, str) else bytes(value.name)), "args": describe(value.args)}
    if isinstance(value, m.ObjectEx):
        return {
            "$objectex": describe(value.header),
            "list": [describe(item) for item in value.items],
            "dict": [[describe(k), describe(v)] for k, v in value.pairs],
        }
    if isinstance(value, m.Checksummed):
        return {"$checksummed": describe(value.value)}
    if isinstance(value, m.PackedRow):
        return {"$row": {name: describe(item) for name, item in value.fields().items()}}
    if isinstance(value, list):
        return [describe(item) for item in value]
    return {"$unknown": repr(value)}


def _text(data: bytes) -> str:
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        return "$bytes:" + data.hex()


def type_name(described: Any) -> str:
    if described is None:
        return "none"
    if isinstance(described, bool):
        return "bool"
    if isinstance(described, int):
        return "int"
    if isinstance(described, float):
        return "float"
    if isinstance(described, str):
        return "str"
    if isinstance(described, list):
        return "list"
    if isinstance(described, dict):
        for key in described:
            if key.startswith("$"):
                return key[1:]
    return "unknown"


def shape(described: Any) -> Any:
    """The structure of a description with every leaf replaced by its type."""
    kind = type_name(described)
    if kind in ("none", "bool", "int", "float", "str"):
        return kind
    if kind == "list":
        return [shape(item) for item in described]
    if isinstance(described, dict):
        if "$tuple" in described:
            return {"$tuple": [shape(item) for item in described["$tuple"]]}
        if "$dict" in described:
            return {"$dict": [[shape(k) if not isinstance(k, str) else k, shape(v)] for k, v in described["$dict"]]}
        if "$object" in described:
            return {"$object": described["$object"], "args": shape(described["args"])}
        if "$substream" in described:
            return {"$substream": shape(described["$substream"])}
        if "$row" in described:
            return {"$row": {name: shape(item) for name, item in described["$row"].items()}}
        return {"$" + kind: "value"}
    return kind


def differences(expected: Any, actual: Any, path: str = "$", *, volatile_keys: frozenset[str] | set[str] = frozenset()) -> list[str]:
    """What differs between a recorded description and a live one.

    The value under a dictionary key in `volatile_keys` (a per-run identifier, a time) is compared by type
    only, however deeply it is nested: `["charid", (None, 140000005)]` matches `["charid", (None, 140000123)]`."""
    out: list[str] = []
    if type_name(expected) != type_name(actual):
        out.append(f"{path}: expected {type_name(expected)} {_brief(expected)}, got {type_name(actual)} {_brief(actual)}")
        return out
    if isinstance(expected, list):
        if len(expected) != len(actual):
            out.append(f"{path}: {len(expected)} items expected, {len(actual)} received")
        for index, (a, b) in enumerate(zip(expected, actual)):
            out += differences(a, b, f"{path}[{index}]", volatile_keys=volatile_keys)
    elif isinstance(expected, dict):
        key = next(k for k in expected if k.startswith("$"))
        if key == "$tuple":
            if len(expected[key]) != len(actual[key]):
                out.append(f"{path}: tuple of {len(expected[key])} expected, {len(actual[key])} received")
            for index, (a, b) in enumerate(zip(expected[key], actual[key])):
                out += differences(a, b, f"{path}.{index}", volatile_keys=volatile_keys)
        elif key == "$dict":
            exp = {_key(k): v for k, v in expected["$dict"]}
            act = {_key(k): v for k, v in actual["$dict"]}
            for name in exp.keys() - act.keys():
                out.append(f"{path}: missing key {name}")
            for name in act.keys() - exp.keys():
                out.append(f"{path}: unexpected key {name}")
            for name in exp.keys() & act.keys():
                if name in volatile_keys:
                    if shape(exp[name]) != shape(act[name]):
                        out.append(f"{path}.{name}: shape {shape(exp[name])} became {shape(act[name])}")
                else:
                    out += differences(exp[name], act[name], f"{path}.{name}", volatile_keys=volatile_keys)
        elif key == "$object":
            if expected["$object"] != actual["$object"]:
                out.append(f"{path}: class {expected['$object']} expected, got {actual['$object']}")
            out += differences(expected["args"], actual["args"], f"{path}.args", volatile_keys=volatile_keys)
        elif key in ("$substream", "$substruct", "$checksummed"):
            out += differences(expected[key], actual[key], f"{path}.{key[1:]}", volatile_keys=volatile_keys)
        elif key == "$row":
            exp, act = expected["$row"], actual["$row"]
            for name in exp.keys() - act.keys():
                out.append(f"{path}: missing column {name}")
            for name in act.keys() - exp.keys():
                out.append(f"{path}: unexpected column {name}")
            for name in exp.keys() & act.keys():
                if name in volatile_keys:
                    if shape(exp[name]) != shape(act[name]):
                        out.append(f"{path}.{name}: shape {shape(exp[name])} became {shape(act[name])}")
                else:
                    out += differences(exp[name], act[name], f"{path}.{name}", volatile_keys=volatile_keys)
        elif expected != actual:
            out.append(f"{path}: {_brief(expected)} expected, got {_brief(actual)}")
    elif expected != actual:
        out.append(f"{path}: {_brief(expected)} expected, got {_brief(actual)}")
    return out


def _key(described: Any) -> str:
    return described if isinstance(described, str) else str(described)


def _brief(described: Any, limit: int = 80) -> str:
    text = repr(described)
    return text if len(text) <= limit else text[: limit - 1] + "…"
