# evecli: a small headless EVE client

Speaks the wire protocol without the game: the marshal codec, MachoNet packets,
and the login handshake (Placebo crypto: nothing is encrypted). It is a test
tool for servers: EVE.js today, eve.rs as it grows a network front.

```
python3 -m venv .venv && .venv/bin/pip install -r requirements-dev.txt
.venv/bin/python -m pytest -q                          # codec golden tests + mock-server tests
.venv/bin/python -m evecli --host 127.0.0.1 --port 26000 --user me --password pw
```

The CLI logs in, prints the server's release and the session, calls
`machoNet.GetTime`, pings, and can call more methods with `--call SERVICE.METHOD`.

```python
from evecli.client import Client
from evecli import marshal as m

with Client("127.0.0.1", 26000) as client:
    session = client.login("me", "pw")
    time = client.call("machoNet", "GetTime")               # a Long (Windows FILETIME)
    init = m.plain(client.call("machoNet", "GetInitVals"))  # [serviceInfo, globalConfig] as dicts
```

## What is tested

| Test | Needs |
|---|---|
| `test_marshal_golden.py`: replays `oracle/fixtures/marshal/cases.json`, the same file the Rust crate replays, so the Python codec is byte-identical to the JS reference | nothing |
| `test_handshake_mock.py`: the client against a scripted server built from the reference's message shapes (login, calls, errors, rejected logins, wire shape of a call) | nothing |
| `test_live.py`: the client against a running server | `EVE_LIVE_HOST=host:port` |
| `test_login_to_character.py`: the walk from a new account to a session with a character, the skills of a new character of each race, what `charMgr`, `account` and the station services say about one and what is in its inventories (`Client.call_bound` calls a bound object), replayed from `oracle/fixtures/scenarios` and compared with what the reference answered (by shape and value; per-run values by type). `EVE_LIVE_SERVER=reference` expects the recording exactly, `eve.rs` (the default) allows the deviations of `docs/compat/DEVIATIONS.md` | `EVE_LIVE_HOST=host:port` |

`tools/dev/run-evejs-live.sh <evejs> [<sde-dir>]` generates the reference server's
data from an SDE (fetched with `eve-sde ensure` when no directory is given), boots it, and runs `test_live.py`. The reference server loads
every table before it listens (about five minutes on a cold start; the script
waits up to `EVE_LIVE_BOOT_SECONDS`, default 900). It is pointed at the
generated tables with `EVEJS_GAMESTORE_DATA_DIR`; `EVEJS_DATA_ROOT` alone does
not move the static tables.

## Value model

The wire keeps several string types apart, and so does the decoder: most server
strings come back as `PyString` (bytes). Use `marshal.plain(value)` for a
Python-native view (`str`, `int`, `dict`, `list`, `tuple`).

| Python | Wire |
|---|---|
| `None`, `bool`, `int` (smallest integer opcode), `float` | as expected |
| `Long` | always PyLongLong |
| `str` | string table item or long string |
| `PyString`, `Buffer` / `bytes` | Python 2 `str` / `buffer` |
| `WString`, `Token` | unicode string, token |
| `tuple`, `List`, `Dict` (list of pairs) | tuple, list, dict |
| `Object`, `ObjectEx`, `SubStruct`, `SubStream`, `PackedRow`, ... | as named |
