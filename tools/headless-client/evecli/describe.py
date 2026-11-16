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

def decode_marshaled(data: bytes) -> Any:
    """Decode a nested EVE marshal stream into its comparison-safe description."""
    return describe(m.decode(bytes(data)))

def buffer_bytes(value: Any) -> bytes | None:
    if isinstance(value, (bytes, bytearray, m.Buffer)):
        return bytes(value)
    if isinstance(value, str) and value.startswith("$bytes:"):
        try:
            return bytes.fromhex(value[7:])
        except ValueError:
            return None
    if isinstance(value, dict) and isinstance(value.get("$buffer"), str):
        try:
            return bytes.fromhex(value["$buffer"])
        except ValueError:
            return None
    return None

def destiny_state_header(value: Any) -> dict | None:
    """Decode the client-side Destiny state stream's packet type and stamp."""
    data = buffer_bytes(value)
    if data is None or len(data) < 5:
        return None
    packet_type = data[0]
    return {
        "packetType": packet_type,
        "packetTypeName": {0: "SetState", 1: "AddBalls2"}.get(packet_type, "unknown"),
        "stamp": int.from_bytes(data[1:5], "little"),
    }

def destiny_state_fields(value: Any) -> dict | None:
    """Decode ball fields from the concatenated Destiny state buffer.

    This follows the free/rigid ball writers used by EveJS and eve.rs. Unknown
    or truncated records are reported with their offset; the original buffer
    remains in the capture, so the decoder never hides wire bytes.
    """
    data = buffer_bytes(value)
    header = destiny_state_header(data)
    if data is None or header is None:
        return None
    result = dict(header)
    result["balls"] = []
    offset = 5
    try:
        while offset < len(data):
            ball, offset = _destiny_ball(data, offset)
            result["balls"].append(ball)
    except ValueError as error:
        result["decodeError"] = str(error)
    return result

def _destiny_ball(data: bytes, offset: int) -> tuple[dict, int]:
    start = offset

    def read(fmt: str, field: str):
        nonlocal offset
        import struct

        size = struct.calcsize(fmt)
        if offset + size > len(data):
            raise ValueError(f"truncated {field} at offset {offset}")
        values = struct.unpack_from(fmt, data, offset)
        offset += size
        return values[0] if len(values) == 1 else values

    def vec3(field: str) -> tuple[float, float, float]:
        return read("<3d", field)

    import struct

    item_id = read("<q", "ball itemID")
    mode = read("<B", "ball mode")
    radius = read("<f", "ball radius")
    position = vec3("ball position")
    flags = read("<B", "ball flags")
    ball = {
        "itemID": item_id,
        "mode": mode,
        "modeName": _BALL_MODES.get(mode, "unknown"),
        "radius": radius,
        "position": position,
        "flags": flags,
        "free": bool(flags & 0x01),
    }

    if flags & 0x01:
        ball["mass"] = read("<d", "free ball mass")
        ball["cloakMode"] = read("<B", "free ball cloak mode")
        ball["harmonic"] = read("<q", "free ball harmonic")
        ball["corporationID"] = read("<i", "free ball corporationID")
        ball["allianceID"] = read("<i", "free ball allianceID")
        ball["maxVelocity"] = read("<f", "free ball max velocity")
        ball["velocity"] = vec3("free ball velocity")
        ball["inertia"] = read("<f", "free ball inertia")
        ball["speedFraction"] = read("<f", "free ball speed fraction")
        ball["headerTerminator"] = read("<B", "free ball header terminator")
    else:
        if mode != 11:
            ball["mass"] = read("<d", "rigid ball mass")
            ball["unknownByte"] = read("<B", "rigid ball unknown byte")
            ball["allianceID"] = read("<q", "rigid ball allianceID")
            ball["corporationID"] = read("<i", "rigid ball corporationID")
            ball["unknownInt32"] = read("<i", "rigid ball unknown int32")
        # Collision tails are type-specific and opaque. Preserve them as bytes
        # up to the writer's 0xff separator rather than assigning guessed fields.
        tail_start = offset
        if offset < len(data) and data[offset] == 0xFF:
            offset += 1
            ball["staticTail"] = ""
        else:
            terminator = data.find(b"\xff", offset)
            if terminator < 0:
                raise ValueError(f"missing rigid ball terminator at offset {offset}")
            ball["staticTail"] = data[tail_start:terminator].hex()
            offset = terminator + 1
        if flags & 0x40:
            count = read("<H", "mini ball count")
            ball["miniBalls"] = [
                {"center": vec3("mini ball center"), "radius": read("<f", "mini ball radius")}
                for _ in range(count)
            ]
        if flags & 0x80:
            count = read("<H", "mini capsule count")
            ball["miniCapsules"] = [
                {"a": vec3("mini capsule endpoint"), "b": vec3("mini capsule endpoint"), "radius": read("<f", "mini capsule radius")}
                for _ in range(count)
            ]
        if flags & 0x20:
            count = read("<H", "mini box count")
            ball["miniBoxes"] = [
                {
                    "corner": vec3("mini box corner"),
                    "localX": vec3("mini box local X"),
                    "localY": vec3("mini box local Y"),
                    "localZ": vec3("mini box local Z"),
                }
                for _ in range(count)
            ]

    if flags & 0x01:
        _decode_free_mode(ball, read, vec3)
    ball["wireOffset"] = {"start": start, "end": offset}
    return ball, offset

_BALL_MODES = {
    0: "goto", 1: "follow", 2: "stop", 3: "warp", 4: "orbit",
    5: "missile", 6: "mushroom", 7: "boid", 8: "troll", 9: "miniball",
    10: "field", 11: "rigid", 12: "formation",
}

def _decode_free_mode(ball, read, vec3):
    mode = ball["mode"]
    if mode == 0:
        ball["targetPosition"] = vec3("goto target position")
    elif mode in (1, 4):
        ball["followTarget"] = read("<q", "follow target")
        ball["followRange"] = read("<f", "follow range")
    elif mode == 12:
        ball["followTarget"] = read("<q", "formation target")
        ball["followRange"] = read("<f", "formation range")
        ball["effectStamp"] = read("<i", "formation effect stamp")
    elif mode == 5:
        ball["followTarget"] = read("<q", "missile target")
        ball["followRange"] = read("<f", "missile range")
        ball["modeOwner"] = read("<q", "missile mode owner")
        ball["effectStamp"] = read("<i", "missile effect stamp")
        ball["targetPosition"] = vec3("missile target position")
    elif mode == 3:
        ball["targetPosition"] = vec3("warp target position")
        ball["effectStamp"] = read("<i", "warp effect stamp")
        ball["totalDistance"] = read("<d", "warp total distance")
        ball["stopDistance"] = read("<d", "warp stop distance")
        ball["warpFactor"] = read("<q", "warp factor")
    elif mode == 6:
        ball["followRange"] = read("<f", "mushroom range")
        ball["mushroomSpan"] = read("<d", "mushroom span")
        ball["effectStamp"] = read("<i", "mushroom effect stamp")
        ball["modeOwner"] = read("<q", "mushroom mode owner")
    elif mode == 8:
        ball["effectStamp"] = read("<i", "troll effect stamp")

    if mode not in _BALL_MODES:
        raise ValueError(f"unknown free ball mode {mode}")

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
        if "$objectex" in described:
            return {
                "$objectex": shape(described["$objectex"]),
                "list": [shape(item) for item in described.get("list", [])],
                "dict": [[key, shape(value)] for key, value in described.get("dict", [])],
            }
        if "$substream" in described:
            return {"$substream": shape(described["$substream"])}
        if "$substruct" in described:
            return {"$substruct": shape(described["$substruct"])}
        if "$checksummed" in described:
            return {"$checksummed": shape(described["$checksummed"])}
        if "$row" in described:
            return {"$row": {name: shape(item) for name, item in described["$row"].items()}}
        return {"$" + kind: "value"}
    return kind

def differences(
    expected: Any, actual: Any, path: str = "$", *, volatile_keys: frozenset[str] | set[str] = frozenset(), float_tolerance: float = 0.0,
) -> list[str]:
    """What differs between a recorded description and a live one.

    The value under a dictionary key in `volatile_keys` (a per-run identifier, a time) is compared by type
    only, however deeply it is nested: `["charid", (None, 140000005)]` matches `["charid", (None, 140000123)]`.
    Two reals are equal when they differ by no more than `float_tolerance` of the larger (1.0 at least): numbers two
    servers work out separately (dogma attributes) need not agree to the last bit."""
    out: list[str] = []
    if type_name(expected) != type_name(actual):
        out.append(f"{path}: expected {type_name(expected)} {_brief(expected)}, got {type_name(actual)} {_brief(actual)}")
        return out
    if isinstance(expected, list):
        if len(expected) != len(actual):
            out.append(f"{path}: {len(expected)} items expected, {len(actual)} received")
        for index, (a, b) in enumerate(zip(expected, actual)):
            out += differences(a, b, f"{path}[{index}]", volatile_keys=volatile_keys, float_tolerance=float_tolerance)
    elif isinstance(expected, dict):
        key = next(k for k in expected if k.startswith("$"))
        if key == "$tuple":
            if len(expected[key]) != len(actual[key]):
                out.append(f"{path}: tuple of {len(expected[key])} expected, {len(actual[key])} received")
            for index, (a, b) in enumerate(zip(expected[key], actual[key])):
                out += differences(a, b, f"{path}.{index}", volatile_keys=volatile_keys, float_tolerance=float_tolerance)
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
                    out += differences(exp[name], act[name], f"{path}.{name}", volatile_keys=volatile_keys, float_tolerance=float_tolerance)
        elif key == "$object":
            if expected["$object"] != actual["$object"]:
                out.append(f"{path}: class {expected['$object']} expected, got {actual['$object']}")
            out += differences(expected["args"], actual["args"], f"{path}.args", volatile_keys=volatile_keys, float_tolerance=float_tolerance)
        elif key == "$objectex":
            out += differences(expected["$objectex"], actual["$objectex"], f"{path}.header", volatile_keys=volatile_keys, float_tolerance=float_tolerance)
            for part in ("list", "dict"):
                out += differences(expected.get(part, []), actual.get(part, []), f"{path}.{part}", volatile_keys=volatile_keys, float_tolerance=float_tolerance)
        elif key in ("$substream", "$substruct", "$checksummed"):
            out += differences(expected[key], actual[key], f"{path}.{key[1:]}", volatile_keys=volatile_keys, float_tolerance=float_tolerance)
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
                    out += differences(exp[name], act[name], f"{path}.{name}", volatile_keys=volatile_keys, float_tolerance=float_tolerance)
        elif expected != actual:
            out.append(f"{path}: {_brief(expected)} expected, got {_brief(actual)}")
    elif isinstance(expected, float) and isinstance(actual, float) and float_tolerance > 0:
        if abs(expected - actual) > float_tolerance * max(1.0, abs(expected), abs(actual)):
            out.append(f"{path}: {_brief(expected)} expected, got {_brief(actual)}")
    elif expected != actual:
        out.append(f"{path}: {_brief(expected)} expected, got {_brief(actual)}")
    return out

def _key(described: Any) -> str:
    return described if isinstance(described, str) else str(described)

def _brief(described: Any, limit: int = 80) -> str:
    text = repr(described)
    return text if len(text) <= limit else text[: limit - 1] + "…"
