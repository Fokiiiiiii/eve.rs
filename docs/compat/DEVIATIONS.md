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
| | *(none yet)* | | | | |

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
