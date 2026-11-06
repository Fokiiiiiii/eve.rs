# MachoNet: what the client expects

Facts about the wire protocol, gathered from a read-only look at the decompiled
client (V24.01, `machoVersion` 496) and cross-checked against the EVE.js
reference (`server/src/network`). Paths in *italics* name where a fact was
read; nothing is copied. The native parts of the client (the marshal opcodes,
packet framing, crypto) are not in that source: those come from the EVE.js
implementation and its recorded fixtures (`oracle/`).

## Transport and framing

- TCP, default port 26000 (*machoNet.py*).
- Every packet is `[u32 little-endian length][payload]`. The payload is a
  marshal stream: `0x7E`, a `u32` save count, the value (see
  `crates/eve-marshal`).

## Login handshake ("Placebo" crypto)

Nothing is encrypted with the Placebo pack (*evecrypto/settings.py* defaults
`cryptoPack` to `Placebo`); the crypto hash is `str(crc_hqx(marshal(x), 0))`.

| # | Direction | Content |
|---|---|---|
| 1 | S→C | `(170472, 496, userCount, boot.version, boot.build, "codename@region", clientHash)` |
| 2 | C→S | the same shape, `userCount` 0 |
| 3 | C→S | `(None, "VK", key)`; or `(None, "QC")` for a queue-position query, answered with an int |
| 4 | C→S | `(publicKeyVersion, {crypting_sessionkey, crypting_sessioniv})`, `publicKeyVersion` is `"placebo"`; S→C `"OK CC"` |
| 5 | C→S | `(64-byte challenge, {macho_version, boot_version, boot_build, boot_codename, boot_region, user_name, user_password, user_password_hash, user_languageid, user_affiliateid, user_sso_token})` |
| 6 | S→C | the int `2` (the server wants hashed passwords; `1` would make the client resend the plaintext) |
| 7 | S→C | `(serverChallenge, signedFunc, {}, {challenge_responsehash, macho_version, boot_*, cluster_usercount, proxy_nodeid, user_logonqueueposition, config_vals})` |
| 8 | C→S | `(hash(serverChallenge), funcOutput, funcResult)`: the client runs `signedFunc` (Python 2 code) and reports what it printed and returned |
| 9 | S→C | `{live_updates, session_init{userid, role, languageID, countryCode, address, ...}, sessionID, user_clientid, client_hash, ...}` |

A refusal is a `cPicked` `exceptions.GPSTransportClosed(reason, code, {})`
instead of the next reply (`LoginAuthFailed`, `ACCOUNTBANNED`, and the
`HANDSHAKE_INCOMPATIBLE*` family). The version check is one-sided: the server
rejects a client whose build is lower than its own, and the client rejects a
server whose build is higher than its own.

`user_password_hash` is the client's `machobase.PasswordHash(user, password)`
(*carbon/common/script/net/machobase.py*): `salt = lower(utf16le(strip(user)))`;
`h = sha1(utf16le(password) + salt)`; then 1000 times `h = sha1(h + salt)`; the
20-byte digest is sent. `tools/headless-client` implements it.

SSO logins use the user name `EVE-SSO-CONNECTION`, the password `fakePW`, and
the token in `user_sso_token` (*carbon/client/script/remote/connectionService.py*).

## Packets

A packet is `PyObject(<guid>, state)`; the state tuple is

```
(command, source, destination, userID, payload, namedPayload,
 contextKey, journeyID, trace_id, parent_span_id, sampled, ingress_id, sample_rate, cut_parent)
```

| command | name | | command | name |
|---|---|---|---|---|
| 0 / 1 | AUTHENTICATION_REQ / RSP | | 12 | NOTIFICATION |
| 2 / 3 | IDENTIFICATION_REQ / RSP | | 15 | ERRORRESPONSE |
| 6 / 7 | CALL_REQ / RSP | | 16 | SESSIONCHANGENOTIFICATION |
| 8 | TRANSPORTCLOSED | | 18 | SESSIONINITIALSTATENOTIFICATION |
| 10 / 11 | RESOLVE_REQ / RSP | | 20 / 21 | PING_REQ / RSP |

Addresses (*machoNetAddress.py*, `const.ADDRESS_TYPE_*`):

```
node       (1, nodeID, service, callID)
client     (2, clientID, callID, service)
broadcast  (4, broadcastID, narrowcast, idtype)
any        (8, service, callID)
```

- A **service call** goes to the `any` address of the service, from the client
  address carrying the call's `callID`. Its payload is
  `(isBound, SubStream((1, method, args, kwargs)))`; `kwargs` includes
  `machoVersion` (1 unless the call is cached), and `machoTimeout` travels in
  the out-of-band dict.
- The **response** goes to `(2, clientID, callID, None)`; its payload is
  `(SubStream(result),)`.
- A **bound-object** call names the object's OID (`"N=<node>:<id>"`) instead of
  a service; `MachoBindObject` takes `(bindParams, (method, args, kw) | None)`
  and returns `(boundObject, retval)`.
- **ErrorResponse**: payload `(originalCommand, code, (SubStream(detail),))`.
- **SessionChangeNotification**: `(sid, change, nodesOfInterest?)`;
  **SessionInitialStateNotification**: `(sid, sessionType, initialstate)`.
  Session types: 1 context, 2 service, 4 ESP, 5 game.

## Services

The client learns which services exist at run time from
`machoNet.GetServiceInfo` (`{name: resolve}`), and `machoNet.GetInitVals`
returns `[serviceInfo, globalConfig]`. `services.tsv` lists the names and
methods it calls: 170 service names (160 through `RemoteSvc`) and 484
`Service.Method` pairs. The busiest are `slash`, `map`, `charMgr`,
`petitioner` and `corpmgr`.

## The public gateway (gRPC)

The client also talks to a protobuf/gRPC gateway (default
`{tier}-public-gateway.evetech.net`, overridable with
`EVE_PUBLIC_GATEWAY_ADDRESS`; TLS is skipped for the override). There are no
`.proto` files in the client; `tools/spec-extract/protos.py` rebuilds a
`FileDescriptorSet` from the 1,370 generated modules' embedded descriptors:
6,259 messages, 10 services. The three client-facing ones are
`eve_public.gateway.Events.Publish` (bidi stream `Event`→`Empty`),
`Notices.Consume` (`Empty`→`Notice`) and `Requests.Send` (`Request`→`Response`),
each with `Ping`. Envelope messages carry a `google.protobuf.Any` payload with
type URLs under `type.evetech.net/`.
