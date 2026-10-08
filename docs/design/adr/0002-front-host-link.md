# ADR 0002: The link between the network front and the service host

- Status: accepted
- Date: 2026-09-29
- Decided by: the repository owner's direction (ADR 0001); the details are the implementer's.

## Context

`docs/project/README.md` splits the server into a Rust network front (`eve-net`: TCP, handshake, framing, sessions)
and a C# service host (protocol adapters and domain modules, PostgreSQL). Slice S1 needs the link between
them. It carries every client packet, so its cost is paid on every call; it must also stay understandable,
because every later slice adds to it.

## Decision

1. **Two processes joined by a Unix domain socket.** One connection per front; the host accepts several
   fronts later (each announces a node id). A loopback TCP address is allowed for development on hosts
   without Unix sockets.
2. **The front never decodes a call.** After the handshake it forwards each client packet as the raw
   marshal bytes it received, tagged with a session id, and writes back the raw bytes the host returns.
   Decoding, dispatch and encoding happen once, in the host. The front reads only what it must (frame
   length). Consequences: no double codec work, and the front stays small and stable while services change.
3. **The handshake is the front's** (it is transport, and it is per connection), but *who may log in* and
   *what the cluster's configuration is* are the host's: the front sends `AuthRequest` and gets `AuthReply`,
   and receives `ClusterConfig`. The session's attributes (`userid`,
   `role`, ...) are owned by the host, which builds the session-initial-state notification itself.
4. **Framing**: `u32 length (little endian, of everything after it) | u8 kind | body`. Text is
   `u16 length + UTF-8`; byte strings are `u32 length + bytes`; a body that ends in raw bytes says so.
   All integers are little endian. A frame longer than 16 MiB, an unknown kind, a body that is short or has
   trailing bytes, or text that is not UTF-8 closes the link.
5. **Fixed layout, not a schema language.** The message set is small and changes rarely; a hand-written
   layout is the cheapest to read and to run. Both sides are tested against one file of golden frames
   (`oracle/fixtures/hostlink/messages.json`, written by `tools/dev/gen-hostlink-fixtures.py`).
6. **Ordering**: frames of one session are delivered in order; the host processes one session's packets one
   at a time and different sessions in parallel. A request and its reply are matched by `corr` (only auth
   has one today).

### Messages (version 1)

| Kind | Code | Direction | Body |
|---|---|---|---|
| `Hello` | 1 | front -> host | `u16 version`, `u32 nodeId` |
| `HelloAck` | 2 | host -> front | `u16 version` |
| `AuthRequest` | 3 | front -> host | `u32 corr`, text `userName`, bytes `passwordHash`, text `language`, text `address` |
| `AuthReply` | 4 | host -> front | `u32 corr`, `u8 status` (0 ok, 1 rejected); ok: `u32 userId`, `u64 role`, text `countryCode`; rejected: text `reason` |
| `SessionOpen` | 5 | front -> host | `u64 sessionId`, `u64 clientId`, `u32 userId`, `u64 role`, text `address`, text `language`, text `countryCode` |
| `SessionClose` | 6 | front -> host | `u64 sessionId` |
| `Packet` | 7 | front -> host | `u64 sessionId`, raw marshal bytes to the end of the frame |
| `Send` | 8 | host -> front | `u64 sessionId`, raw marshal bytes to the end of the frame |
| `Disconnect` | 9 | host -> front | `u64 sessionId`, text `reason` |
| `ClusterConfig` | 10 | host -> front | raw marshal bytes to the end of the frame: the global-config dict the handshake hands the client (`config_vals`); sent after `HelloAck` and again whenever it changes |

`role` and the ids are what the handshake acknowledgement shows the client. The front picks `clientId`
(`1_000_000 * userId + nodeId`) and `sessionId` (`unix ms * 15 + clientId % 15`), as the client expects.

## Consequences

- Good: one encode and one decode per packet, a front with no game logic, a link that is easy to trace
  (frames are self-describing), and a host that can be tested without a socket by feeding it frames.
- Bad: a second implementation of the layout on each side, and a version to keep in step; mitigated by the
  shared golden frames and by `Hello` carrying the version.
- Later: the host will push session changes and notifications through `Send`; a `Disconnect` reason will be
  shown to the client as a transport-closed message. Ping is forwarded in S1; answering it in the front is
  an optimisation to make when it is measured.

## Notes from S1

- Measured on one machine, the front and host together answered about 11-15x the reference's call rate
  and logged clients in about 20-35x faster (`docs/project/README.md`). Nothing was tuned; the design's
  costs (one socket hop, a copy on each side) did not show up as the limit.
- A client that stops reading is cut off by the front when its queue (1,024 packets) is full; a session with
  10,000 packets waiting in the host is cut off with `Disconnect("Overloaded")`. Both keep one slow party
  from holding up the link.
- Session ids are issued by the front and are unique within it (`SessionIds`).

## Alternatives considered

- **gRPC / protobuf**: schema tooling and generated code on both sides, and an HTTP/2 stack in the hot path,
  for a message set of nine. Revisit if the set grows or a third implementation appears.
- **Shared memory ring buffers**: the fastest, and the most work to make safe across two runtimes. Revisit
  if the socket shows up in a profile.
- **One process** (the front hosting .NET, or the reverse): rejected; it couples the runtimes' lifetimes and
  failure modes, which is what the separation is for.
- **Marshal for the link's own messages**: reuses two codecs, but it ties the internal protocol to the client
  wire format and its quirks.
