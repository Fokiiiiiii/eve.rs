# Host input hardening handoff

## Change
- `CallRequest.Decode` treats Python list positional arguments like tuples. `PacketDispatcher` answers an unreadable CALL_REQ with wrapped `exceptions.UserError("UnMachoDestination")`.
- `HostServer` disconnects the affected session with `InvalidPacket` after marshal or dispatch failures. Frame reading already rejects truncated and over-limit frames.
- Added dispatcher tests for malformed CALL_REQ replies and valid tuple/list arguments with `machoVersion`; added host tests for malformed marshal packets, truncated frames and oversized frames. The unknown-service integration assertion remains in `HostServerTests`; the session-open test asserts the initial notification is first.
- Commit `b213ee17` corrects the old `packets:arguments` golden row. The prior row kept a list nested as one argument, while the V24.01 wire callsite passes it as the positional sequence. `DEVIATIONS.md` records that intentional difference from the old EveJS recording.

## Client and reference evidence
- V24.01 service dispatch passes `packet.payload[2]` as the positional sequence and optional `payload[3]` as kwargs: `/home/user/eve-logic/carbon/common/script/net/ServiceCallGPCS.py:66-139`. The object-call path strips `machoVersion` from kwargs: `/home/user/eve-logic/carbon/common/script/net/ObjectCallGPCS.py:129-134`.
- Client error wrapping is `ErrorResponse(originalCommand, code, payload)`, with reversed source/destination and copied request context: `/home/user/eve-logic/carbon/common/script/net/machoNetPacket.py:153-165`. Unknown services become wrapped exceptions in transport: `/home/user/eve-logic/carbon/common/script/net/machoNetTransport.py:551-555`. Client exception identities are `exceptions.UserError` (`/home/user/eve-logic/eveexceptions/__init__.py:12-35`) and `exceptions.ServiceNotFound` (`/home/user/eve-logic/eveexceptions/__init__.py:99-109`).
- No Macho `ProxyError` class or wire response appears in the full client-source search; matches are HTTP dependency proxy exceptions. Malformed transport frames close the link, while malformed RPCs use a Macho ErrorResponse.
- Login UI prevents concurrent click-triggered login with `self.connecting`: `/home/user/eve-logic/eve/client/script/ui/login/loginII.py:416-420`; it calls `connection.Login([user, password, serverIP, serverPort, 0])` and handles `UserError` at `loginII.py:481-510`.
- Structure docking sends `structureDocking.Dock(structureID, shipID)` only with ship/system session values, and sends `Undock(session.structureid, shipID, ignoreContraband=...)` behind a state check: `/home/user/eve-logic/eve/client/script/ui/services/structure/structureDocking.py:26-81`. Station/structure undock transitions run through `undockingSvc.py:100-138`.
- EveJS v0.12.9 `decodeCallRequest` accepts array positional sequences, normalizes scalar args to one-element arrays, and returns a structured fallback for malformed bodies: `/home/user/evejs-v0.12.9/server/src/common/pyPacket.js:115-239`.

## Verification
- Focused Host tests after merge: 6 passed, 0 failed, 0 skipped.
- Full `Eve.Host.Tests` without PostgreSQL: 476 passed, 101 failed, 227 skipped (804 total); database-backed tests could not connect because this task forbids database use.
- Full `Eve.Services.Tests` after merge: 261 passed, 0 failed, 0 skipped.
- Focused Agent Actions for the corrected golden case passed (run 40371091379). Push CI for `b213ee17` succeeded (run 40371054260); no failed jobs.
- No graphical client session was used.
