# Client Compatibility Verification Rules

1. Model the real V24.01 client callsites, request sequence and response consumers.
2. Preserve RPC argument types, return shapes, error envelopes, request IDs, bound object identity and notification order.
3. Replay equivalent end-to-end scenarios against the **running EveJS v0.12.9 server** and eve.rs; compare observable differences.
4. Cover login, feature completion, follow-up requests, reconnect and reloaded state.
5. Never use captured replies or synthetic success paths as a substitute for authoritative runtime behavior.
6. Distinguish headless protocol verification from real graphical-client verification.
7. Add a relevant scenario test whenever a new client-facing feature is implemented.
