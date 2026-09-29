"""Turn the golden fixtures' EJSON (see tools/oracle/ejson.js in EVE.js) into
evecli values with the coercions the JS encoder applies, and decoded values back
into EJSON, so the Python codec can be checked against the recorded results."""

import math
import struct

from evecli import marshal as m


class JsNum(float):
    """A JS number: an integer iff it has no fraction (this includes -0)."""


class JsBig(int):
    """A JS BigInt."""


class JsBuf(bytes):
    """A JS Buffer."""


UNKNOWN = None


def parse_ejson(node):
    if node is None or isinstance(node, (bool, str)):
        return node
    if isinstance(node, list):
        return [parse_ejson(item) for item in node]
    ((tag, body),) = node.items()
    if tag == "$int":
        return JsNum(int(body))
    if tag == "$f64":
        return JsNum(struct.unpack(">d", bytes.fromhex(body))[0])
    if tag == "$big":
        return JsBig(int(body))
    if tag == "$buf":
        return JsBuf(bytes.fromhex(body))
    if tag == "$obj":
        return {key: parse_ejson(value) for key, value in body.items()}
    raise ValueError(f"unknown EJSON tag {tag}")


def _range_error(what):
    return m.MarshalError(f"{what} out of range", "RangeError:ERR_OUT_OF_RANGE")


def _plain_number(number: float):
    if math.isfinite(number) and number == int(number):
        if -(2**63) <= number < 2**63:
            return int(number)
        raise _range_error("integer beyond int64")
    return float(number)


def _big_to_long(digits: int):
    if not -(2**63) <= digits < 2**63:
        raise _range_error("BigInt beyond int64")
    return m.Long(digits)


def _pairs(node):
    if not isinstance(node, list):
        raise m.MarshalError("dict entries missing")
    pairs = []
    for entry in node:
        if not isinstance(entry, list) or len(entry) < 2:
            raise m.MarshalError("dict entry is not a pair")
        pairs.append((js_to_value(entry[0]), js_to_value(entry[1])))
    return pairs


def _items(node):
    if node is None:
        return []
    if not isinstance(node, list):
        raise m.MarshalError("items is not an array")
    return [js_to_value(item) for item in node]


def _buffer_source(node) -> bytes:
    if node is None:
        return b""
    if isinstance(node, JsBuf):
        return bytes(node)
    if isinstance(node, list):
        out = bytearray()
        for item in node:
            if not isinstance(item, JsNum):
                raise m.MarshalError("byte source element is not a number")
            out.append(int(item) & 0xFF)
        return bytes(out)
    raise m.MarshalError("byte source")


def _js_number(node) -> float:
    if node is None:
        return 0.0
    if isinstance(node, bool):
        return float(node)
    if isinstance(node, JsNum):
        return float(node)
    if isinstance(node, JsBig):
        return float(int(node))
    if isinstance(node, str):
        return m.number_from_str(node)
    return math.nan


def _text(node) -> str:
    if not isinstance(node, str):
        raise m.MarshalError("expected a string")
    return node


def js_to_value(js):
    if js is None or isinstance(js, bool):
        return js
    if isinstance(js, JsNum):
        return _plain_number(float(js))
    if isinstance(js, JsBig):
        return _big_to_long(int(js))
    if isinstance(js, str):
        return str(js)
    if isinstance(js, JsBuf):
        return m.Buffer(js)
    if isinstance(js, list):
        return tuple(js_to_value(item) for item in js)
    if isinstance(js, dict):
        return _typed(js)
    raise m.MarshalError("cannot marshal value")


def _field(js, name):
    value = js.get(name)
    return None if value is None else js_to_value(value)


def _typed(js):
    kind = js.get("type")
    if not isinstance(kind, str) or not kind:
        raise m.MarshalError("object without a type")
    if kind == "dict":
        return m.Dict(_pairs(js.get("entries")))
    if kind == "wstring":
        value = js.get("value")
        return m.WString("" if value is None else _text(value))
    if kind == "long":
        value = js.get("value")
        if isinstance(value, JsNum) and math.isfinite(value) and value == int(value):
            if -(2**63) <= value < 2**63:
                return m.Long(int(value))
            raise _range_error("integer beyond int64")
        if isinstance(value, JsBig):
            return _big_to_long(int(value))
        raise _range_error("BigInt() of a non-integer")
    if kind == "real":
        number = _js_number(js.get("value"))
        return float(number) if math.isfinite(number) else 0.0
    if kind == "tuple":
        return tuple(_items(js.get("items")))
    if kind == "list":
        return m.List(_items(js.get("items")))
    if kind == "bytes":
        return m.PyString(_buffer_source(js.get("value")))
    if kind == "rawstr":
        value = js.get("value")
        if value is None:
            return m.PyString(b"")
        if isinstance(value, str):
            return m.PyString(value.encode("utf-8"))
        raise m.MarshalError("rawstr of a non-string")
    if kind == "object":
        return m.Object(_field(js, "name"), _field(js, "args"))
    if kind == "packedrow":
        return _packed_row(js)
    if kind == "substruct":
        return m.SubStruct(_field(js, "value"))
    if kind == "substream":
        return m.SubStream(value=_field(js, "value"))
    if kind == "token":
        return m.Token(_text(js.get("value")))
    if kind in ("objectex1", "objectex2"):
        pairs = js.get("dict")
        return m.ObjectEx(1 if kind == "objectex1" else 2, _field(js, "header"), _items(js.get("list")),
                          _pairs(pairs) if pairs is not None else [])
    if kind == "cpicked":
        return m.CPickled(_buffer_source(js.get("data")))
    raise m.MarshalError("unknown marshal type")


_WRAPPERS = ("int", "long", "float", "double", "bool", "wstring", "token", "rawstr")


def _unwrap_scalar(js):
    while isinstance(js, dict) and js.get("type") in _WRAPPERS:
        js = js.get("value")
    return js


def _packed_value(js, db_type):
    if m.db_size_bits(db_type) == 0:
        return js_to_value(js)
    js = _unwrap_scalar(js)
    if js is None or isinstance(js, bool):
        return js
    if isinstance(js, JsNum):
        return float(js)
    if isinstance(js, JsBig):
        return m.Long(int(js)) if -(2**63) <= int(js) < 2**63 else float(int(js))
    if isinstance(js, str):
        return str(js)
    if isinstance(js, JsBuf):
        return m.Buffer(js)
    return m.List()  # anything else coerces to the fallback (zero / false)


def _column(entry):
    if not isinstance(entry, list):
        raise m.MarshalError("invalid packed row: column entry is not an array")
    kind = entry[1] if len(entry) > 1 else None
    db_type = int(kind) if isinstance(kind, JsNum) and math.isfinite(kind) and kind == int(kind) else None
    return ("", db_type)


def _packed_row(js):
    header = _field(js, "header")
    explicit = js.get("columns")
    columns = [_column(c) for c in explicit] if isinstance(explicit, list) else m.columns_from_header(header)
    cells = js.get("values")
    if not isinstance(cells, list):
        raise m.MarshalError("invalid packed row: missing values")
    if len(cells) != len(columns):
        raise m.MarshalError("invalid packed row: values length does not match the column count")
    values = [_packed_value(cell, column[1]) for cell, column in zip(cells, columns)]
    return m.PackedRow(m.Descriptor(header, columns), values)


# ─── Decoded value -> EJSON ─────────────────────────────────────────────────


def _number(value):
    number = float(value)
    safe = math.isfinite(number) and number == int(number) and abs(number) <= 2**53 - 1
    if safe and not (number == 0 and math.copysign(1.0, number) < 0):
        return {"$int": str(int(number))}
    return {"$f64": struct.pack(">d", number).hex()}


def _obj(**fields):
    return {"$obj": fields}


def _pairs_json(pairs):
    return [[to_ejson(k), to_ejson(v)] for k, v in pairs]


def to_ejson(value):
    if value is None or isinstance(value, bool):
        return value
    if isinstance(value, (m.Long, m.BigUnsigned)):
        return {"$big": str(int(value))}
    if isinstance(value, int):
        return _number(value)
    if isinstance(value, float):
        return _number(value)
    if isinstance(value, m.WString):
        return _obj(type="wstring", value=str(value))
    if isinstance(value, m.Token):
        return _obj(type="token", value=str(value))
    if isinstance(value, str):
        return str(value)
    if isinstance(value, m.CPickled):
        return _obj(type="cpicked", data={"$buf": bytes(value).hex()})
    if isinstance(value, (bytes, bytearray)):
        return {"$buf": bytes(value).hex()}
    if isinstance(value, m.Dict):
        return _obj(type="dict", entries=_pairs_json(value))
    if isinstance(value, m.List):
        return _obj(type="list", items=[to_ejson(v) for v in value])
    if isinstance(value, tuple):
        return [to_ejson(v) for v in value]
    if isinstance(value, m.Object):
        return _obj(type="object", name=to_ejson(value.name), args=to_ejson(value.args))
    if isinstance(value, m.ObjectEx):
        return _obj(type=f"objectex{value.kind}", header=to_ejson(value.header),
                    list=[to_ejson(v) for v in value.items], dict=_pairs_json(value.pairs))
    if isinstance(value, m.SubStruct):
        return _obj(type="substruct", value=to_ejson(value.value))
    if isinstance(value, m.SubStream):
        if value.raw is not None:
            return _obj(type="substream", raw={"$buf": value.raw.hex()})
        return _obj(type="substream", value=to_ejson(value.value))
    if isinstance(value, m.Checksummed):
        return _obj(type="checksummed", checksum=_number(value.checksum), value=to_ejson(value.value))
    if isinstance(value, m.PackedRow):
        columns = m.descriptor_columns(value.header) or ()
        return _obj(
            type="packedrow",
            header=to_ejson(value.header),
            columns=[to_ejson(c) for c in columns],
            values=[to_ejson(v) for v in value.values],
            fields={"$obj": {name: to_ejson(v) for name, v in value.fields().items()}},
            rleData={"$buf": (value.rle or b"").hex()},
        )
    raise TypeError(f"cannot render {type(value).__name__}")
