"""EVE MachoNet marshal codec.

A third implementation of the format, next to the JS reference and the Rust
crate `eve-marshal`. All three are checked against the same golden fixtures
(`oracle/fixtures/marshal/cases.json`), so they agree byte for byte.

Value model (mirrors `eve_marshal::Value`):

    None, bool, int          -> int is written with the smallest integer opcode
    Long                     -> always PyLongLong
    float                    -> PyReal (a zero is PyZeroReal)
    str                      -> string-table item or PyLongString (UTF-8)
    PyString                 -> a Python 2 `str` (PyEmptyString .. PyLongString)
    Buffer / bytes           -> PyBuffer
    WString, Token           -> unicode string / token
    tuple, List, Dict        -> Dict is a list of (key, value) pairs in wire order
    Object, ObjectEx, SubStruct, SubStream, Checksummed, CPickled, PackedRow

The decoder is as lenient as the JS one (a string longer than the input yields
the bytes that are there) and adds bounds: nesting depth and allocation are
limited so that a hostile peer cannot exhaust the stack or memory.
"""

from __future__ import annotations

import math
import struct
from dataclasses import dataclass, field
from typing import Any, Optional

from .string_table import OFFICIAL_STRING_TABLE, STRING_TABLE

MARSHAL_HEADER = 0x7E
SAVE_MASK = 0x40
OPCODE_MASK = 0x3F
MAX_DEPTH = 256

_TABLE_INDEX = {}
for _position, _text in enumerate(STRING_TABLE):
    _TABLE_INDEX.setdefault(_text, _position + 1)  # first occurrence wins

_OFFICIAL_TABLE_INDEX = {text: position + 1 for position, text in enumerate(OFFICIAL_STRING_TABLE)}

def _table_index(table):
    if table is STRING_TABLE:
        return _TABLE_INDEX
    if table is OFFICIAL_STRING_TABLE:
        return _OFFICIAL_TABLE_INDEX
    return {text: position + 1 for position, text in enumerate(table)}

class Op:
    NONE = 0x01
    TOKEN = 0x02
    LONG_LONG = 0x03
    LONG = 0x04
    SIGNED_SHORT = 0x05
    BYTE = 0x06
    MINUS_ONE = 0x07
    ZERO_INTEGER = 0x08
    ONE_INTEGER = 0x09
    REAL = 0x0A
    ZERO_REAL = 0x0B
    BUFFER = 0x0D
    EMPTY_STRING = 0x0E
    CHAR_STRING = 0x0F
    SHORT_STRING = 0x10
    STRING_TABLE_ITEM = 0x11
    WSTRING_UCS2 = 0x12
    LONG_STRING = 0x13
    TUPLE = 0x14
    LIST = 0x15
    DICT = 0x16
    OBJECT = 0x17
    SUB_STRUCT = 0x19
    SAVED_STREAM_ELEMENT = 0x1B
    CHECKSUMMED_STREAM = 0x1C
    TRUE = 0x1F
    FALSE = 0x20
    CPICKLED = 0x21
    OBJECT_EX1 = 0x22
    OBJECT_EX2 = 0x23
    EMPTY_TUPLE = 0x24
    ONE_TUPLE = 0x25
    EMPTY_LIST = 0x26
    ONE_LIST = 0x27
    EMPTY_WSTRING = 0x28
    WSTRING_UCS2_CHAR = 0x29
    PACKED_ROW = 0x2A
    SUB_STREAM = 0x2B
    TWO_TUPLE = 0x2C
    PACKED_TERMINATOR = 0x2D
    WSTRING_UTF8 = 0x2E
    VAR_INTEGER = 0x2F

class DbType:
    EMPTY = 0x00
    I2 = 0x02
    I4 = 0x03
    R4 = 0x04
    R8 = 0x05
    CY = 0x06
    ERROR = 0x0A
    BOOL = 0x0B
    I1 = 0x10
    UI1 = 0x11
    UI2 = 0x12
    UI4 = 0x13
    I8 = 0x14
    UI8 = 0x15
    FILETIME = 0x40
    BYTES = 0x80
    STR = 0x81
    WSTR = 0x82

def db_size_bits(db_type) -> int:
    if db_type in (DbType.CY, DbType.I8, DbType.UI8, DbType.FILETIME, DbType.R8):
        return 64
    if db_type in (DbType.I4, DbType.UI4, DbType.R4):
        return 32
    if db_type in (DbType.I2, DbType.UI2):
        return 16
    if db_type in (DbType.I1, DbType.UI1):
        return 8
    if db_type == DbType.BOOL:
        return 1
    return 0

# ─── Errors ─────────────────────────────────────────────────────────────────

class MarshalError(Exception):
    """A marshal failure. `kind` is the class of failure the JS reference reports:
    "Error", "TypeError", "RangeError:ERR_OUT_OF_RANGE" or
    "RangeError:ERR_BUFFER_OUT_OF_BOUNDS"."""

    def __init__(self, message: str, kind: str = "Error"):
        super().__init__(message)
        self.kind = kind

# ─── Value types ────────────────────────────────────────────────────────────

class Long(int):
    """An integer that is always written as PyLongLong."""

class BigUnsigned(int):
    """A variable-width integer beyond 2**53 - 1 (decode only; written back as one)."""

class PyString(bytes):
    """A Python 2 `str`: what the string opcodes decode to."""

class Buffer(bytes):
    """A Python `buffer`: PyBuffer."""

class WString(str):
    """A unicode string (PyWStringUTF8 / UCS-2)."""

class Token(str):
    pass

class CPickled(bytes):
    """Raw Python pickle bytes (cPicked)."""

class List(list):
    """A marshal list (as opposed to a tuple)."""

class Dict(list):
    """A marshal dict: (key, value) pairs in wire order. Keys need not be hashable
    or unique, so this is not a `dict`; use `get` / `as_dict` for lookups."""

    def get(self, key, default=None):
        for candidate, value in self:
            if _key_matches(candidate, key):
                return value
        return default

    def as_dict(self) -> dict:
        return {_hashable(key): value for key, value in self}

def _key_matches(candidate, key) -> bool:
    if candidate == key:
        return True
    # A decoded key is often a PyString; callers usually look up with a str.
    if isinstance(candidate, (bytes, bytearray)) and isinstance(key, str):
        return candidate.decode("utf-8", "replace") == key
    return False

def _hashable(key):
    if isinstance(key, (bytes, bytearray)):
        try:
            return bytes(key).decode("utf-8")
        except UnicodeDecodeError:
            return bytes(key)
    return key

@dataclass
class Object:
    name: Any
    args: Any

@dataclass
class ObjectEx:
    kind: int  # 1 or 2
    header: Any
    items: list = field(default_factory=list)
    pairs: list = field(default_factory=list)  # (key, value) tuples

@dataclass
class SubStruct:
    value: Any

@dataclass
class SubStream:
    """A nested marshal stream. `raw` is set (and `value` None) when it did not decode."""

    value: Any = None
    raw: Optional[bytes] = None

@dataclass
class Checksummed:
    checksum: int
    value: Any

@dataclass
class Descriptor:
    """A row descriptor shared by the rows of a rowset."""

    header: Any
    columns: list  # (name, db_type) with db_type None for unknown codes
    order: list = field(default_factory=list)  # (index, size_bits) widest first
    bool_slot: list = field(default_factory=list)
    booleans: int = 0
    data_bytes: int = 0
    bitmap_bytes: int = 0
    encoded_header: Optional[bytes] = None

    def __post_init__(self):
        sizes = []
        booleans = 0
        data_bits = 0
        self.bool_slot = [None] * len(self.columns)
        for index, (_, db_type) in enumerate(self.columns):
            size = db_size_bits(db_type)
            if db_type == DbType.BOOL:
                self.bool_slot[index] = booleans
                booleans += 1
            if size >= 8:
                data_bits += size
            sizes.append((index, size))
        sizes.sort(key=lambda item: (-item[1], item[0]))
        self.order = sizes
        self.booleans = booleans
        self.data_bytes = data_bits >> 3
        self.bitmap_bytes = ((booleans + len(self.columns)) >> 3) + 1

    def null_bit(self, index: int) -> int:
        return index + self.booleans

@dataclass
class PackedRow:
    descriptor: Descriptor
    values: list
    rle: Optional[bytes] = None

    @property
    def header(self):
        return self.descriptor.header

    @property
    def columns(self):
        return self.descriptor.columns

    def fields(self) -> dict:
        """Column name -> value, with the descriptor's virtual columns (StackSize, Singleton)."""
        out: dict = {}
        for (name, _), value in zip(self.descriptor.columns, self.values):
            out[name] = value
        for name, resolver in _virtual_columns(self.descriptor.header):
            if resolver == "eve.common.script.sys.eveCfg.StackSize":
                out[name] = _derive_stack_size(out)
            elif resolver == "eve.common.script.sys.eveCfg.Singleton":
                out[name] = _derive_singleton(out)
        return out

def descriptor_columns(header) -> Optional[list]:
    """header.header[1][0] of a PyObjectEx DBRowDescriptor: the raw column entries."""
    if not isinstance(header, ObjectEx) or not isinstance(header.header, tuple):
        return None
    inner = header.header
    if len(inner) < 2 or not isinstance(inner[1], tuple) or not inner[1]:
        return None
    columns = inner[1][0]
    return columns if isinstance(columns, tuple) else None

def _text(value) -> Optional[str]:
    if isinstance(value, str):
        return str(value)
    if isinstance(value, (bytes, bytearray)):
        return bytes(value).decode("utf-8", "replace")
    return None

def _column_type(value):
    if isinstance(value, bool):
        return None
    if isinstance(value, int) and not isinstance(value, Long):
        return int(value)
    if isinstance(value, float) and math.isfinite(value) and value == int(value):
        return int(value)
    return None

def columns_from_header(header) -> list:
    entries = descriptor_columns(header)
    if entries is None:
        raise MarshalError("invalid packed row: missing DBRowDescriptor columns")
    columns = []
    for index, entry in enumerate(entries):
        if not isinstance(entry, tuple):
            raise MarshalError("invalid packed row: column entry is not a (name, type) tuple")
        name = _text(entry[0]) if entry else None
        columns.append((name if name is not None else f"col_{index}", _column_type(entry[1]) if len(entry) > 1 else None))
    return columns

def make_descriptor(header, columns=None) -> Descriptor:
    return Descriptor(header, columns if columns is not None else columns_from_header(header))

def _virtual_columns(header):
    if not isinstance(header, ObjectEx) or not isinstance(header.header, tuple) or len(header.header) < 3:
        return []
    entries = header.header[2]
    if isinstance(entries, (List, tuple)):
        entries = list(entries)
    else:
        return []
    out = []
    for entry in entries:
        if not isinstance(entry, tuple):
            continue
        name = _text(entry[0]) if entry else None
        if not name:
            continue
        resolver = _text(entry[1]) if len(entry) > 1 else None
        out.append((name, resolver or ""))
    return out

def _js_number(value) -> float:
    """`Number(value)` for a decoded field value."""
    if value is None:
        return 0.0
    if isinstance(value, bool):
        return float(value)
    if isinstance(value, (int, float)):
        return float(value)
    text = _text(value)
    if text is not None:
        return number_from_str(text)
    return math.nan

def _derive_stack_size(fields: dict):
    quantity = _js_number(fields.get("quantity", math.nan))
    if not math.isfinite(quantity):
        return 0
    quantity = 1.0 if quantity < 0 else quantity
    return int(quantity) if quantity == int(quantity) and abs(quantity) <= 2**53 - 1 else quantity

def _derive_singleton(fields: dict):
    quantity = _js_number(fields.get("quantity", math.nan))
    if math.isfinite(quantity) and quantity < 0:
        return int(-quantity) if -quantity == int(-quantity) else -quantity
    location = _js_number(fields.get("locationID", math.nan))
    return 1 if math.isfinite(location) and 30_000_000 <= location < 40_000_000 else 0

_JS_WS = "\t\n\x0b\x0c\r \xa0                　﻿"

def number_from_str(text: str) -> float:
    """JS `Number(text)`: NaN when the text is not a numeric literal."""
    text = text.strip(_JS_WS)
    if not text:
        return 0.0
    prefix = text[:2].lower()
    if prefix in ("0x", "0o", "0b"):
        base = {"0x": 16, "0o": 8, "0b": 2}[prefix]
        try:
            return float(int(text[2:], base)) if text[2:] else math.nan
        except ValueError:
            return math.nan
    if text in ("Infinity", "+Infinity"):
        return math.inf
    if text == "-Infinity":
        return -math.inf
    if not all(c in "0123456789+-.eE" for c in text):
        return math.nan
    try:
        return float(text)
    except ValueError:
        return math.nan

# ─── RLE ────────────────────────────────────────────────────────────────────

def compress_rle(data: bytes) -> bytes:
    out = bytearray()
    second = False
    control = 0
    position = 0
    zero_chains = 0
    length = len(data)
    while position < length:
        if not second:
            control = len(out)
            out.append(0)
        start = position
        end = min(position + 8, length)
        if data[position] != 0:
            zero_chains = 0
            while True:
                out.append(data[position])
                position += 1
                if not (position < end and data[position] != 0):
                    break
            count = 8 - (position - start)
        else:
            zero_chains += 1
            while position < end and data[position] == 0:
                position += 1
            count = (position - start) + 7
        if second:
            out[control] |= count << 4
        else:
            out[control] = count
        second = not second
    if second and zero_chains:
        zero_chains += 1
    while zero_chains > 1:
        zero_chains -= 2
        if out:
            out.pop()
    return bytes(out)

def decompress_rle(data: bytes, expected: int) -> bytearray:
    out = bytearray(expected)
    read = written = 0
    while read < len(data) and written < expected:
        control = data[read]
        read += 1
        for half in range(2):
            if written >= expected:
                break
            nibble = (control & 0x0F) if half == 0 else (control >> 4)
            if nibble < 8:
                literals = min(8 - nibble, len(data) - read, expected - written)
                out[written : written + literals] = data[read : read + literals]
                read += literals
                written += literals
            else:
                written += min(nibble - 7, expected - written)
            if read >= len(data) and written >= expected:
                break
    return out

# ─── Encoding ───────────────────────────────────────────────────────────────

_INT64 = 2**63

def encode(value, *, string_table: tuple = STRING_TABLE) -> bytes:
    """Encode `value` as a marshal stream (0x7E, zero save count, value)."""
    out = bytearray(b"\x7e\x00\x00\x00\x00")
    _Encoder(out, string_table).value(value)
    return bytes(out)

def encode_packet(value, *, string_table: tuple = STRING_TABLE) -> bytes:
    """A marshal stream with its 4-byte little-endian length prefix."""
    payload = encode(value, string_table=string_table)
    return struct.pack("<I", len(payload)) + payload

class _Encoder:
    def __init__(self, out: bytearray, string_table: tuple):
        self.out = out
        self.string_table = string_table
        self.string_table_index = _table_index(string_table)
        self.depth = 0

    def size(self, size: int) -> None:
        if size < 0xFF:
            self.out.append(size)
        else:
            if size > 0xFFFFFFFF:
                raise MarshalError("length larger than 4 GiB", "RangeError:ERR_OUT_OF_RANGE")
            self.out.append(0xFF)
            self.out += struct.pack("<I", size)

    def integer(self, value: int) -> None:
        out = self.out
        if value == -1:
            out.append(Op.MINUS_ONE)
        elif value == 0:
            out.append(Op.ZERO_INTEGER)
        elif value == 1:
            out.append(Op.ONE_INTEGER)
        elif -128 <= value <= 127:
            out += bytes((Op.BYTE, value & 0xFF))
        elif -32768 <= value <= 32767:
            out.append(Op.SIGNED_SHORT)
            out += struct.pack("<h", value)
        elif -(2**31) <= value <= 2**31 - 1:
            out.append(Op.LONG)
            out += struct.pack("<i", value)
        else:
            self.long(value)

    def long(self, value: int) -> None:
        if not -_INT64 <= value < _INT64:
            raise MarshalError("integer beyond int64", "RangeError:ERR_OUT_OF_RANGE")
        self.out.append(Op.LONG_LONG)
        self.out += struct.pack("<q", value)

    def string(self, text: str) -> None:
        data = text.encode("utf-8")
        if len(data) == 0:
            self.out.append(Op.EMPTY_STRING)
        elif len(data) == 1:
            self.out += bytes((Op.CHAR_STRING, data[0]))
        else:
            index = self.string_table_index.get(text)
            if index is not None:
                self.out += bytes((Op.STRING_TABLE_ITEM, index))
            else:
                self.out.append(Op.LONG_STRING)
                self.size(len(data))
                self.out += data

    def byte_string(self, data: bytes) -> None:
        if len(data) == 0:
            self.out.append(Op.EMPTY_STRING)
        elif len(data) == 1:
            self.out += bytes((Op.CHAR_STRING, data[0]))
        elif len(data) < 0x100:
            self.out += bytes((Op.SHORT_STRING, len(data)))
            self.out += data
        else:
            self.out.append(Op.LONG_STRING)
            self.size(len(data))
            self.out += data

    def sized(self, opcode: int, data: bytes) -> None:
        self.out.append(opcode)
        self.size(len(data))
        self.out += data

    def value(self, value) -> None:
        if self.depth >= MAX_DEPTH:
            raise MarshalError("marshal value nested too deeply")
        self.depth += 1
        try:
            self._value(value)
        finally:
            self.depth -= 1

    def _value(self, value) -> None:
        out = self.out
        if value is None:
            out.append(Op.NONE)
        elif value is True:
            out.append(Op.TRUE)
        elif value is False:
            out.append(Op.FALSE)
        elif isinstance(value, BigUnsigned):
            length = (int(value).bit_length() + 7) // 8
            self.sized(Op.VAR_INTEGER, int(value).to_bytes(length, "little"))
        elif isinstance(value, Long):
            self.long(int(value))
        elif isinstance(value, int):
            self.integer(int(value))
        elif isinstance(value, float):
            if value == 0.0:
                out.append(Op.ZERO_REAL)
            else:
                out.append(Op.REAL)
                out += struct.pack("<d", value)
        elif isinstance(value, WString):
            if len(value) == 0:
                out.append(Op.EMPTY_WSTRING)
            else:
                self.sized(Op.WSTRING_UTF8, value.encode("utf-8"))
        elif isinstance(value, Token):
            self.sized(Op.TOKEN, value.encode("utf-8"))
        elif isinstance(value, str):
            self.string(value)
        elif isinstance(value, PyString):
            data = bytes(value)
            if self.string_table is OFFICIAL_STRING_TABLE:
                try:
                    table_text = data.decode("utf-8")
                except UnicodeDecodeError:
                    table_text = None
                index = self.string_table_index.get(table_text) if table_text is not None else None
                if index is not None:
                    out += bytes((Op.STRING_TABLE_ITEM, index))
                    return
            self.byte_string(data)
        elif isinstance(value, CPickled):
            self.sized(Op.CPICKLED, bytes(value))
        elif isinstance(value, (bytes, bytearray)):
            self.sized(Op.BUFFER, bytes(value))
        elif isinstance(value, Dict):
            out.append(Op.DICT)
            self.size(len(value))
            for key, item in value:  # the wire stores the value first, then the key
                self.value(item)
                self.value(key)
        elif isinstance(value, dict):
            out.append(Op.DICT)
            self.size(len(value))
            for key, item in value.items():
                self.value(item)
                self.value(key)
        elif isinstance(value, list):
            if len(value) == 0:
                out.append(Op.EMPTY_LIST)
            elif len(value) == 1:
                out.append(Op.ONE_LIST)
                self.value(value[0])
            else:
                out.append(Op.LIST)
                self.size(len(value))
                for item in value:
                    self.value(item)
        elif isinstance(value, tuple):
            count = len(value)
            if count == 0:
                out.append(Op.EMPTY_TUPLE)
            elif count == 1:
                out.append(Op.ONE_TUPLE)
            elif count == 2:
                out.append(Op.TWO_TUPLE)
            else:
                out.append(Op.TUPLE)
                self.size(count)
            for item in value:
                self.value(item)
        elif isinstance(value, Object):
            out.append(Op.OBJECT)
            self.value(value.name)
            self.value(value.args)
        elif isinstance(value, ObjectEx):
            out.append(Op.OBJECT_EX1 if value.kind == 1 else Op.OBJECT_EX2)
            self.value(value.header)
            for item in value.items:
                self.value(item)
            out.append(Op.PACKED_TERMINATOR)
            for key, item in value.pairs:
                self.value(key)
                self.value(item)
            out.append(Op.PACKED_TERMINATOR)
        elif isinstance(value, SubStruct):
            out.append(Op.SUB_STRUCT)
            self.value(value.value)
        elif isinstance(value, SubStream):
            out.append(Op.SUB_STREAM)
            if value.raw is not None:
                self.size(len(value.raw))
                out += value.raw
            else:
                inner = bytearray(b"\x7e\x00\x00\x00\x00")
                inner_encoder = _Encoder(inner, self.string_table)
                inner_encoder.depth = self.depth
                inner_encoder.value(value.value)
                self.size(len(inner))
                out += inner
        elif isinstance(value, Checksummed):
            out.append(Op.CHECKSUMMED_STREAM)
            out += struct.pack("<I", value.checksum)
            self.value(value.value)
        elif isinstance(value, PackedRow):
            self.packed_row(value)
        else:
            raise MarshalError(f"cannot marshal value of type {type(value).__name__}")

    def packed_row(self, row: PackedRow) -> None:
        descriptor = row.descriptor
        if len(row.values) != len(descriptor.columns):
            raise MarshalError("invalid packed row: values length does not match the column count")
        out = self.out
        out.append(Op.PACKED_ROW)
        if descriptor.encoded_header is not None:
            out += descriptor.encoded_header
        else:
            start = len(out)
            self.value(descriptor.header)
            descriptor.encoded_header = bytes(out[start:])

        bitmap = bytearray(descriptor.bitmap_bytes)
        fixed = bytearray()
        for index, size in descriptor.order:
            if size <= 1:
                continue
            value = row.values[index]
            if value is None:
                bit = descriptor.null_bit(index)
                bitmap[bit >> 3] |= 1 << (bit & 7)
            fixed += _fixed_bytes(descriptor.columns[index][1], value)
        for index, size in descriptor.order:
            if size == 1 and _pack_bool(row.values[index]):
                slot = descriptor.bool_slot[index]
                bitmap[slot >> 3] |= 1 << (slot & 7)
        fixed += bitmap
        compressed = compress_rle(bytes(fixed))
        self.size(len(compressed))
        out += compressed
        for index, size in descriptor.order:
            if size == 0:
                self.value(row.values[index])

def _pack_number(value) -> float:
    if value is None:
        return 0.0
    if isinstance(value, bool):
        return float(value)
    if isinstance(value, (int, float)):
        try:
            number = float(value)
        except OverflowError:  # Number(bigint) beyond a double is Infinity
            return 0.0
    else:
        text = _text(value)
        if text is None:
            return 0.0
        number = number_from_str(text)
    return number if math.isfinite(number) else 0.0

def _pack_i64(value) -> int:
    if value is None:
        return 0
    if isinstance(value, bool):
        return int(value)
    if isinstance(value, int):
        number = int(value)
    elif isinstance(value, float):
        if not math.isfinite(value):
            return 0
        number = int(value)  # trunc
    else:
        text = _text(value)
        if text is None:
            return 0
        text = text.strip(_JS_WS)
        if not text:
            return 0
        try:
            number = int(text, 0) if text[:2].lower() in ("0x", "0o", "0b") else int(text, 10)
        except ValueError:
            return 0
    if not -_INT64 <= number < _INT64:
        raise MarshalError("64-bit column out of range", "RangeError:ERR_OUT_OF_RANGE")
    return number

def _pack_bool(value) -> bool:
    if isinstance(value, bool):
        return value
    if value is None:
        return False
    return _pack_number(value) != 0.0

def _to_uint32(number: float) -> int:
    if not math.isfinite(number):
        return 0
    return int(number) % 4294967296

def _checked_int(value, low: int, high: int, column: str) -> int:
    number = int(_pack_number(value))  # trunc
    if number < low or number > high:
        raise MarshalError(f"{column} out of range", "RangeError:ERR_OUT_OF_RANGE")
    return number

def _fixed_bytes(db_type, value) -> bytes:
    if db_type in (DbType.CY, DbType.I8, DbType.UI8, DbType.FILETIME):
        return struct.pack("<q", _pack_i64(value))
    if db_type == DbType.I4:
        return struct.pack("<i", _checked_int(value, -(2**31), 2**31 - 1, "I4"))
    if db_type == DbType.UI4:
        return struct.pack("<I", _to_uint32(float(int(_pack_number(value)))))
    if db_type == DbType.R4:
        return struct.pack("<f", _clamp_float32(_pack_number(value)))
    if db_type == DbType.R8:
        return struct.pack("<d", _pack_number(value))
    if db_type == DbType.I2:
        return struct.pack("<h", _checked_int(value, -32768, 32767, "I2"))
    if db_type == DbType.UI2:
        return struct.pack("<H", _to_uint32(float(int(_pack_number(value)))) & 0xFFFF)
    if db_type == DbType.I1:
        return struct.pack("<b", _checked_int(value, -128, 127, "I1"))
    if db_type == DbType.UI1:
        return bytes((_to_uint32(float(int(_pack_number(value)))) & 0xFF,))
    return b""

def _clamp_float32(number: float) -> float:
    """struct.pack('<f') raises on overflow where JS writes an infinity."""
    if abs(number) > 3.4028235677973366e38:
        return math.copysign(math.inf, number)
    return number

# ─── Decoding ───────────────────────────────────────────────────────────────

def decode(data: bytes, *, string_table: tuple = STRING_TABLE) -> Any:
    """Decode a marshal stream using the selected 1-based wire string table."""
    return _decode_stream(bytes(data), False, 0, string_table)

def decode_exact(data: bytes, *, string_table: tuple = STRING_TABLE) -> Any:
    """Decode one complete marshal stream using the selected string table."""
    return _decode_stream(bytes(data), True, 0, string_table)

def split_packet(data: bytes):
    """Split a [u32 length][payload] frame off the front of `data`.

    Returns (payload, rest), or None when `data` does not yet hold a whole frame."""
    if len(data) < 4:
        return None
    (length,) = struct.unpack_from("<I", data, 0)
    if len(data) < 4 + length:
        return None
    return data[4 : 4 + length], data[4 + length :]

def _decode_stream(data: bytes, require_exact: bool, depth: int, string_table: tuple = STRING_TABLE) -> Any:
    decoder = _Decoder(data, depth, string_table)
    header = decoder.u8()
    if header != MARSHAL_HEADER:
        raise MarshalError(f"invalid marshal header 0x{header:02x} (expected 0x7e)")
    save_count = decoder.u32()
    value_end = len(data)
    if save_count > 0:
        table_bytes = save_count * 4
        if len(data) - decoder.pos < table_bytes:
            raise MarshalError("invalid marshal save table: save table larger than the stream")
        decoder.stored = [None] * save_count
        decoder.save_pos = len(data) - table_bytes
        decoder.save_end = len(data)
        value_end = len(data) - table_bytes
    value = decoder.value()
    if require_exact:
        if decoder.pos != value_end:
            raise MarshalError("marshal stream has unconsumed payload bytes")
        if decoder.save_pos != decoder.save_end:
            raise MarshalError("marshal stream has unconsumed save-table entries")
    return value

class _Decoder:
    def __init__(self, data: bytes, depth: int, string_table: tuple):
        self.buf = data
        self.string_table = string_table
        self.pos = 0
        self.stored: list = []
        self.save_pos: Optional[int] = None
        self.save_end: Optional[int] = None
        self.depth = depth
        self.saved_refs = 0
        self.descriptor_cache: Optional[tuple] = None

    def u8(self) -> int:
        if self.pos >= len(self.buf):
            raise MarshalError("unexpected end of marshal data")
        byte = self.buf[self.pos]
        self.pos += 1
        return byte

    def take(self, fmt: str, width: int):
        """A fixed-width read that fails the way Node's Buffer.readXxx does."""
        end = self.pos + width
        if end > len(self.buf):
            kind = "RangeError:ERR_BUFFER_OUT_OF_BOUNDS" if len(self.buf) < width else "RangeError:ERR_OUT_OF_RANGE"
            raise MarshalError("read past end of stream", kind)
        (value,) = struct.unpack_from(fmt, self.buf, self.pos)
        self.pos = end
        return value

    def u32(self) -> int:
        return self.take("<I", 4)

    def size(self) -> int:
        first = self.u8()
        return self.u32() if first == 0xFF else first

    def chunk(self, length: int) -> bytes:
        """The JS `readBytes`: clamps to the input but always advances by the declared length."""
        start = min(self.pos, len(self.buf))
        end = min(self.pos + length, len(self.buf))
        self.pos += length
        return self.buf[start:end]

    def save_index(self) -> int:
        if self.save_pos is None or self.save_end is None:
            raise MarshalError("invalid marshal save table: no save index table")
        if self.save_pos + 4 > self.save_end:
            raise MarshalError("invalid marshal save table: save index table exhausted")
        (index,) = struct.unpack_from("<I", self.buf, self.save_pos)
        self.save_pos += 4
        return index

    def value(self):
        if self.depth >= MAX_DEPTH:
            raise MarshalError("marshal value nested too deeply")
        self.depth += 1
        try:
            return self._value()
        finally:
            self.depth -= 1

    def _value(self):
        if self.pos >= len(self.buf):
            raise MarshalError("unexpected end of marshal data")
        raw = self.u8()
        save = bool(raw & SAVE_MASK)
        opcode = raw & OPCODE_MASK
        storage_index = self.save_index() if save else 0
        value = self._decode_opcode(opcode, raw)
        if save and 0 < storage_index <= len(self.stored):
            self.stored[storage_index - 1] = value
        return value

    def items(self, count: int) -> list:
        items = []
        for _ in range(count):
            items.append(self.value())
        return items

    def _decode_opcode(self, opcode: int, raw: int):
        if opcode == Op.NONE:
            return None
        if opcode == Op.TRUE:
            return True
        if opcode == Op.FALSE:
            return False
        if opcode == Op.ZERO_INTEGER:
            return 0
        if opcode == Op.ONE_INTEGER:
            return 1
        if opcode == Op.MINUS_ONE:
            return -1
        if opcode == Op.BYTE:
            byte = self.u8()
            return byte - 256 if byte >= 128 else byte
        if opcode == Op.SIGNED_SHORT:
            return self.take("<h", 2)
        if opcode == Op.LONG:
            return self.take("<i", 4)
        if opcode == Op.LONG_LONG:
            return Long(self.take("<q", 8))
        if opcode == Op.VAR_INTEGER:
            return self.var_integer()
        if opcode == Op.ZERO_REAL:
            return 0.0
        if opcode == Op.REAL:
            return self.take("<d", 8)
        if opcode == Op.EMPTY_STRING:
            return PyString(b"")
        if opcode == Op.CHAR_STRING:
            return PyString(bytes((self.u8(),)))
        if opcode == Op.SHORT_STRING:
            return PyString(self.chunk(self.u8()))
        if opcode == Op.LONG_STRING:
            return PyString(self.chunk(self.size()))
        if opcode == Op.STRING_TABLE_ITEM:
            index = self.u8()
            if 1 <= index <= len(self.string_table):
                value = self.string_table[index - 1]
                return PyString(value.encode("utf-8")) if self.string_table is OFFICIAL_STRING_TABLE else value
            return f"<stringtable:{index}>"
        if opcode == Op.EMPTY_WSTRING:
            return WString("")
        if opcode == Op.WSTRING_UCS2:
            return WString(_utf16(self.chunk(self.size() * 2)))
        if opcode == Op.WSTRING_UCS2_CHAR:
            return WString(_utf16(self.chunk(2)))
        if opcode == Op.WSTRING_UTF8:
            return WString(self.chunk(self.size()).decode("utf-8", "replace"))
        if opcode == Op.TOKEN:
            return Token(self.chunk(self.size()).decode("utf-8", "replace"))
        if opcode == Op.EMPTY_TUPLE:
            return ()
        if opcode == Op.ONE_TUPLE:
            return (self.value(),)
        if opcode == Op.TWO_TUPLE:
            first = self.value()
            return (first, self.value())
        if opcode == Op.TUPLE:
            return tuple(self.items(self.size()))
        if opcode == Op.EMPTY_LIST:
            return List()
        if opcode == Op.ONE_LIST:
            return List([self.value()])
        if opcode == Op.LIST:
            return List(self.items(self.size()))
        if opcode == Op.DICT:
            count = self.size()
            pairs = Dict()
            for _ in range(count):
                value = self.value()  # the wire stores the value first, then the key
                key = self.value()
                pairs.append((key, value))
            return pairs
        if opcode == Op.OBJECT:
            name = self.value()
            return Object(name, self.value())
        if opcode in (Op.OBJECT_EX1, Op.OBJECT_EX2):
            return self.object_ex(1 if opcode == Op.OBJECT_EX1 else 2)
        if opcode == Op.SUB_STRUCT:
            return SubStruct(self.value())
        if opcode == Op.SUB_STREAM:
            data = self.chunk(self.size())
            try:
                return SubStream(value=_decode_stream(data, False, self.depth, self.string_table))
            except MarshalError:
                return SubStream(raw=data)
        if opcode == Op.CHECKSUMMED_STREAM:
            checksum = self.u32()
            return Checksummed(checksum, self.value())
        if opcode == Op.BUFFER:
            return Buffer(self.chunk(self.size()))
        if opcode == Op.CPICKLED:
            return CPickled(self.chunk(self.size()))
        if opcode == Op.SAVED_STREAM_ELEMENT:
            self.saved_refs += 1
            index = self.size()
            if 0 < index <= len(self.stored):
                return _clone(self.stored[index - 1])
            return None
        if opcode == Op.PACKED_ROW:
            return self.packed_row()
        raise MarshalError(f"unknown marshal opcode 0x{raw:02x} at position {self.pos - 1}")

    def var_integer(self):
        size = self.size()
        if size == 0:
            return 0
        data = self.chunk(size)
        if len(data) < size:
            raise MarshalError("truncated variable-width integer", "TypeError")
        number = int.from_bytes(data, "little")
        return number if number <= 2**53 - 1 else BigUnsigned(number)

    def object_ex(self, kind: int) -> ObjectEx:
        header = self.value()
        items = []
        while self.pos < len(self.buf):
            if self.buf[self.pos] & OPCODE_MASK == Op.PACKED_TERMINATOR:
                self.pos += 1
                break
            items.append(self.value())
        pairs = []
        while self.pos < len(self.buf):
            if self.buf[self.pos] & OPCODE_MASK == Op.PACKED_TERMINATOR:
                self.pos += 1
                break
            key = self.value()
            pairs.append((key, self.value()))
        return ObjectEx(kind, header, items, pairs)

    def descriptor(self) -> Descriptor:
        """Read a packed row's DBRowDescriptor. Every row of a rowset repeats the same
        one, so identical bytes are skipped and the parsed descriptor is shared, unless
        decoding them touched save-table state."""
        if self.descriptor_cache is not None:
            (start, end), cached = self.descriptor_cache
            length = end - start
            if self.buf[self.pos : self.pos + length] == self.buf[start:end] and self.pos + length <= len(self.buf):
                self.pos += length
                return cached
        start = self.pos
        save_before, refs_before = self.save_pos, self.saved_refs
        header = self.value()
        descriptor = make_descriptor(header)
        if self.save_pos == save_before and self.saved_refs == refs_before:
            self.descriptor_cache = ((start, self.pos), descriptor)
        return descriptor

    def packed_row(self) -> PackedRow:
        descriptor = self.descriptor()
        rle = self.chunk(self.size())
        packed = decompress_rle(rle, descriptor.data_bytes + descriptor.bitmap_bytes)
        bitmap = packed[descriptor.data_bytes :]
        values: list = [None] * len(descriptor.columns)

        def bit(position: int) -> bool:
            byte = position >> 3
            return byte < len(bitmap) and bool(bitmap[byte] & (1 << (position & 7)))

        offset = 0
        for index, size in descriptor.order:
            if size <= 1:
                continue
            if bit(descriptor.null_bit(index)):
                values[index] = None
            else:
                values[index] = _fixed_value(descriptor.columns[index][1], packed, offset)
            offset += size >> 3
        for index, size in descriptor.order:
            if size != 1:
                continue
            if bit(descriptor.null_bit(index)):
                values[index] = None
            else:
                values[index] = bit(descriptor.bool_slot[index])
        for index, size in descriptor.order:
            if size == 0:
                values[index] = self.value()
        return PackedRow(descriptor, values, bytes(rle))

def _fixed_value(db_type, packed: bytearray, offset: int):
    if db_type in (DbType.CY, DbType.I8, DbType.UI8, DbType.FILETIME):
        (number,) = struct.unpack_from("<q", packed, offset)
        return number if abs(number) <= 2**53 - 1 else Long(number)
    fmt = {
        DbType.I4: "<i", DbType.UI4: "<I", DbType.R4: "<f", DbType.R8: "<d",
        DbType.I2: "<h", DbType.UI2: "<H", DbType.I1: "<b", DbType.UI1: "<B",
    }.get(db_type)
    if fmt is None:
        return None
    (number,) = struct.unpack_from(fmt, packed, offset)
    return number

def _utf16(data: bytes) -> str:
    data = data[: len(data) // 2 * 2]
    return data.decode("utf-16-le", "replace")

def _clone(value):
    """A deep copy, as the JS reference clones a saved element when it is referenced."""
    if isinstance(value, (bytes, bytearray, str, int, float)) or value is None:
        return value
    if isinstance(value, tuple):
        return tuple(_clone(item) for item in value)
    if isinstance(value, Dict):
        return Dict((_clone(k), _clone(v)) for k, v in value)
    if isinstance(value, List):
        return List(_clone(item) for item in value)
    if isinstance(value, Object):
        return Object(_clone(value.name), _clone(value.args))
    if isinstance(value, ObjectEx):
        return ObjectEx(value.kind, _clone(value.header), [_clone(i) for i in value.items],
                        [(_clone(k), _clone(v)) for k, v in value.pairs])
    if isinstance(value, SubStruct):
        return SubStruct(_clone(value.value))
    if isinstance(value, SubStream):
        return SubStream(_clone(value.value), value.raw)
    if isinstance(value, Checksummed):
        return Checksummed(value.checksum, _clone(value.value))
    if isinstance(value, PackedRow):
        return PackedRow(value.descriptor, [_clone(v) for v in value.values], value.rle)
    return value

def plain(value):
    """A Python-native view of a decoded value, for application code.

    The wire keeps several string types apart: a `PyString` (what most server
    strings decode to) becomes `str`, or stays `bytes` if it is not UTF-8;
    `WString`/`Token` become `str`; `Long`/`BigUnsigned` become `int`; `Dict`
    becomes a `dict` (keys converted the same way); `List` a `list`; a
    `SubStream` is unwrapped; an `Object` becomes `(name, args)`."""
    if isinstance(value, PyString):
        try:
            return bytes(value).decode("utf-8")
        except UnicodeDecodeError:
            return bytes(value)
    if isinstance(value, (Long, BigUnsigned)):
        return int(value)
    if isinstance(value, (WString, Token)):
        return str(value)
    if isinstance(value, Dict):
        return {_hashable(plain(key)): plain(item) for key, item in value}
    if isinstance(value, List):
        return [plain(item) for item in value]
    if isinstance(value, tuple):
        return tuple(plain(item) for item in value)
    if isinstance(value, SubStream):
        return plain(value.value) if value.raw is None else value.raw
    if isinstance(value, SubStruct):
        return plain(value.value)
    if isinstance(value, Object):
        return (plain(value.name), plain(value.args))
    if isinstance(value, PackedRow):
        return {name: plain(item) for name, item in value.fields().items()}
    return value
