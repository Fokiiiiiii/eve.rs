# Deviations from the reference server

The reference (EVE.js) is matched by default (`docs/adr/0001-external-compatibility-only.md`).
A deliberate difference goes here, with the evidence that the client cannot tell or that it
is better off, and how it is tested. A behaviour is either:

- **contract**: the client can observe it; eve.rs matches the reference;
- **tolerated**: the client can observe it but accepts a better behaviour; eve.rs may differ,
  and the difference is listed below;
- **internal**: the client cannot observe it; eve.rs is free.

## Register

| # | Behaviour | Reference does | eve.rs does | Evidence | Status |
|---|---|---|---|---|---|
| 1 | Password check at login | Accepts any password by default (`devSkipPasswordValidation` is true) and creates an account on first use | Checks the password hash by default; an unknown user name still creates the account with the hash it presented; `EVE_DEV_SKIP_PASSWORD=1` restores the reference | The client always sends the hash of the password typed, so only a wrong password can tell the two apart (it is refused, as a real server does) | tolerated (S1); `tools/dev/run-eve-live.sh` runs the wrong-password test |
| 2 | Session ids | `ms * 15 + clientID % 15`, which can repeat within a millisecond | The same value, moved up past the last one issued | The client reads `sessionID` as an opaque long; a repeat replaced a live session in the front (found by a test) | internal (S1) |

## Candidates to examine

Each is matched to the reference today. None is decided; the evidence needed is in the last column.

| Behaviour | Why it may not need to match | Evidence to collect |
|---|---|---|
| A handler that throws is answered with a `None` result, not an error | A `None` where the client expects data is probably worse than an error the client can report | What the client does on each service's `None` and on an error response |
| A service call may reach any method of the JS class by name | Only the methods the client calls matter | `docs/spec/services.tsv` against the reference's method set |
| A received `str` is echoed as `buffer` (`JsSemantics.AsReceived`) | The Python 2 client's `str` is bytes, so it may not tell the two apart | The client's marshal reader for opcodes `str` and `buffer` |
| JavaScript number and truthiness rules in ported handlers (`parseInt`, `\|\|`) | They are accidents of the language unless the client feeds the value | Which inputs the client can send |
| Session and cache objects are sent in the reference's order | The client may not depend on the order | A run of the client with the order changed |
| One JSON document per legacy table, with a synchronous cache-first API | It is storage and internal structure | Nothing observable; decided by the design of each domain module |

## Not yet matched

Behaviour of the reference that eve.rs does not have yet, and where it will come from. These are gaps, not
decisions.

| Behaviour | Comes with |
|---|---|
| The login function also patches portrait upload (`photoUploadSvc`) and seeds the skill-extractor token | S2 (portraits), S3 (skills) |
| `machoNet.SetGlobalConfigValue`, `ForwardCharacterNotification`, `ReloadClientCodeHash`, `GetNodeFromAddress`, `GetClusterGameStatisticsForClient` | as the client calls them |
| The service table lists about 150 service names; eve.rs lists the services it implements | each service group |
| Store entries in the global config (`newEdenStore`) | S3 |
| Cluster user count from all sessions; a real login queue (`QC`) | S6 |
| The reference's optional AES branch of the crypto request (its own comment says it never worked) | not planned |
