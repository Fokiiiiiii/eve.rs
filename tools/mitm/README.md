# tools/mitm: capture and diff the client's MachoNet traffic

`mitm` sits between a client (or a headless test) and a server, forwards every byte unchanged, and logs each
MachoNet packet in both directions: the handshake, calls, results, errors, notifications, session changes. It
writes a capture (JSON lines) that `mitm diff` compares across servers: the reference (EVE.js) and eve.rs.

It is the standard way to check client-visible behaviour: see **The reference recipe** below, and the rule in
[`docs/design/REFERENCE-TRACES.md`](../../docs/design/REFERENCE-TRACES.md).

```
tools/mitm/mitm capture --upstream 127.0.0.1:26000 --listen 127.0.0.1:26200 --out captures/evejs-login.jsonl
tools/mitm/mitm show captures/evejs-login.jsonl --service machoNet --direction s2c
tools/mitm/mitm diff captures/evejs-login.jsonl captures/eve-rs-login.jsonl --ignore-volatile
```

Python 3 only, the standard library plus the decoders of [`tools/headless-client`](../headless-client/README.md)
(`evecli`, imported by path; no install). `tools/mitm/mitm` is a shell wrapper; on Windows run
`python tools\mitm\mitm.py ...` with the same arguments. Captures stay out of git (`/captures/` is ignored).

## The reference recipe

Use this whenever a change touches client-visible RPCs or notifications. The reference is EVE.js
(`Fokiiiiiii/evejs-v0.12.9`), the scenario is the headless test suite, the comparison is `mitm diff`.

1. Start EVE.js with the harness, and put the proxy in front of it (port 26000 is EVE.js, 26200 the proxy):
   ```
   tools/mitm/mitm capture --upstream 127.0.0.1:26000 --listen 127.0.0.1:26200 --out captures/evejs-live.jsonl &
   EVE_LIVE_HOST=127.0.0.1:26200 tools/dev/run-evejs-live.sh /path/to/evejs
   ```
   `run-evejs-live.sh` boots the server, then runs `tests/test_live.py` against `EVE_LIVE_HOST`, which the
   proxy serves. Stop the capture with Ctrl-C (or `timeout -s INT`) once the run ends.
2. Run the same scenario against eve.rs (the front is on 26100; the proxy is on 26200 again):
   ```
   tools/mitm/mitm capture --upstream 127.0.0.1:26100 --listen 127.0.0.1:26200 --out captures/eve-rs-live.jsonl &
   EVE_DEV_AUTO_CREATE=1 EVE_LIVE_HOST=127.0.0.1:26200 \
     EVE_RUN='cd tools/headless-client && .venv/bin/python -m pytest -q tests/test_live.py' tools/dev/run-eve-live.sh
   ```
   `EVE_RUN` replaces the default test list of `run-eve-live.sh` (which is larger than the EVE.js run); both
   sides run `test_live.py`. `EVE_DEV_AUTO_CREATE=1` lets the host create the test account on first use (the host
   refuses unknown accounts by default); EVE.js does this already. To compare a larger scenario, give the same `pytest` selection to both sides.
3. Compare:
   ```
   tools/mitm/mitm diff captures/evejs-live.jsonl captures/eve-rs-live.jsonl --ignore-volatile
   ```
   Exit 0 means identical under the rules below; exit 1 prints the first divergence. Investigate it against
   the client source (`Fokiiiiiii/eve-logic`) and EVE.js before changing eve.rs; a divergence that the client
   tolerates is a documented deviation (`docs/reference/compat/DEVIATIONS.md`), not a silent pass.

Any other headless command works the same way: point `EVE_LIVE_HOST` (or the tests' host setting) at the
proxy. The headless client drives what its test files script (login, calls, pings, the character walk, undock,
movement, warp, jump, targeting, modules and others); a scenario that needs something its tests do not script
must be written as a test first. Interactions that only exist in the graphical client (window state, clicks,
drag and drop) cannot be driven, so they are not covered by this recipe.

## Commands

| Command | What it does |
|---|---|
| `capture --upstream H:P [--listen H:P] [--out FILE] [--quiet]` | Listens (default `127.0.0.1:26200`), connects upstream per client connection, forwards bytes first and analyses after. Prints one readable line per packet unless `--quiet`. |
| `show FILE` | Prints a capture as readable lines. |
| `diff REF CAND [--ignore-volatile]` | Compares two captures (below). |

Filters `--service`, `--method`, `--direction c2s|s2c|both`, `--notifications-only` apply to `show` and to the
printed lines of `capture`. The capture file always holds every packet: a filtered capture is not a valid input
to `diff`.

Readable lines look like:
```
#12 C>S CallReq call=1 machoNet.GetServiceInfo args={"$tuple":[]}
#13 S>C CallRsp call=1 machoNet -> {"$dict":[["machoNet",null],...]}
#16 -- closed
```

## Capture format

One JSON object per line. Every record has `seq` (order in the file), `t` (ms since the start; volatile),
`conn` (connection number) and `stage`:

- `handshake` (`dir`, `encoding`, `value`): the login frames, described (see `evecli.describe`).
- `macho` (`dir`, `encoding`, `type`, `service`, `method`, `call_id`, then per type): `CallReq` has `args`
  (and `kwargs`); `CallRsp` has `result`; `ErrorResponse` has `error` (`code`, `payload`); `Notification` has
  `method` and `args`; `SessionChangeNotification`, `SessionInitialStateNotification` and the rest have `payload`.
  Pings are recorded without payload (their timings differ per run).
- `undecoded` (`dir`, `bytes`, `error`, `hex`): a payload that does not decode, as raw hex (first 256 bytes), or
  a stream that cannot be framed any more (an impossible length: that direction is no longer parsed).
- `event` (`note`): a connection opened, refused upstream, or closed.

`encoding` is `zlib` when the payload was zlib-wrapped (the client wraps its inbound packets, a payload starting
with `0x78`); it is inflated before decoding.

Values use evecli's description: `{"$long": n}`, `{"$tuple": [...]}`, `{"$dict": [[k, v]]}`, `{"$object": name,
"args": ...}` and so on. Strings are text.

## What `diff` compares

Two streams are compared, each in capture order: the **client calls** (every `CallReq`), and the **server events**
(results, errors, notifications, session changes). Results and errors take the service and method of their call,
matched by connection and call id. Pings and the handshake are not compared.

The two streams are compared separately, not interleaved: a server notification can race the client's first call on
a new connection, and that order depends on timing. For the first position where a stream differs, `diff` reports
the stream and the kind:

1. **call order**: a different service or method (or a result that answers a different call).
2. **argument / result / notification shape**: the same event with different structure (tuple lengths, dictionary
   keys, types).
3. **value**: the same shape, different values.
4. **length**: one stream has more events.

`--ignore-volatile` does not compare what legitimately differs between runs and servers:

- values under the dictionary keys `sessionID`, `sessionid`, `session_id`, `sessionKey`, `clientID`, `clientid`,
  `user_clientid`, `callID`, `callId`, `time`, `timestamp`, `fileTime`, `now`, `seed`, `randomSeed`, `nonce`,
  `serverTime` are compared by type only;
- the result of `GetTime` and `GetCurrentTime` is compared by shape only;
- bound-object ids (`OID+n`, `OID-n`) in a service address are masked to `OID`;
- the session id that leads a `SessionChangeNotification` or `SessionInitialStateNotification` payload.

Call ids are never compared; they are matched, not checked. Shape and order still are: a volatile key does not
hide a different structure. The first element of a session notification (the session id) is masked too.

Limit: within a stream, the order is the capture order. If a server answers pipelined calls in a different order,
the first divergence is reported at the first reordered event even when every answer is right. Look at the
reported kind before concluding.

## What it covers on the wire

- The framing is `u32 little-endian length` then the marshal payload, in both directions (the same framing as
  `evecli.handshake.Connection` and eve.rs `eve-net`).
- zlib-wrapped payloads are inflated in either direction. The host code has a zlib path only for inbound
  packets (`MarshalCodec.DecodeInbound`); no outbound compression was found in it.
- No TLS, no encryption: the Placebo handshake is plain. A stage that is not Placebo is not handled; it would
  show up as `undecoded` records with hex while the bytes keep being forwarded.

## Verified and not verified

Verified (run in this repository, this session):

- `tools/headless-client/tests/test_mitm.py` (15 tests, passing): every packet of `oracle/fixtures/services/
  dispatch.json` decodes and survives an encode/decode round trip with the same meaning; frames split across reads
  and zlib-wrapped frames are reassembled; an undecodable payload is logged as hex and the stream goes on; the
  proxy forwards the bytes exactly in both directions; a real login (handshake) and a `GetTime` call through the
  proxy to the headless `MockServer` are decoded and answered; the diff rules behave as described above.
- The CLI end to end against the mock server (`capture`, then `show`, then `diff` of two runs).
- The recipe, both sides, with `tests/test_live.py`:
  - EVE.js (`evejs-v0.12.9`, `tools/dev/run-evejs-live.sh`, its SDE): 4 passed, 1 skipped (the strict-password test).
  - eve.rs (`tools/dev/run-eve-live.sh` with `EVE_RUN`, and `EVE_DEV_AUTO_CREATE=1` because the test account is
    created on first use): 4 passed, 1 skipped; the same run with `EVE_STRICT_RPC=1` also passed.
  - `mitm diff` of the EVE.js capture against the eve.rs capture: the client calls match; the first server
    divergence is the `SessionInitialStateNotification` payload: EVE.js omits `maxSessionTime` and `inDetention`,
    and eve.rs sends them. This is a real difference and is not fixed by this change.
  - `mitm diff` of the eve.rs default run against the `EVE_STRICT_RPC=1` run: the first divergence is the
    unknown-service call, which strict mode answers with an error packet carrying a correlation id, as designed.

Not verified:

- The real graphical client through the proxy. It cannot run on this machine, so the handshake and the call shapes
  are checked only against evecli and the recorded reference fixtures.
- The other test files of the headless suite through the proxy: only `test_live.py` was run through it.

## Known gaps

- evecli writes object headers with opcode `0x10` where the reference writes `0x13`. The decoder reads both, and
  the proxy never re-encodes, so captures are not affected; `test_mitm.py` therefore compares meaning, not bytes.
- No golden captures are committed. The oracle fixtures are not a capture format: `oracle/fixtures/services/
  dispatch.json` needs the bound-object state after each step, which a proxy cannot observe. Captures are
  therefore kept local under `captures/` (ignored by git).
- One packet in `dispatch.json` has a state tuple shorter than a MachoNet packet's; it is logged as `undecoded`
  with its hex, and the test expects exactly that.
