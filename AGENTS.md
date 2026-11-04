# eve.rs Development Guidelines

## Goal
Build a fast, maintainable server compatible with the unmodified EVE Online V24.01 client (build 3396210).

## Source Priority
- **Gameplay rules and static data:** CCP official documentation and the matching official SDE first; EVE University Wiki as a secondary reference.
- **Client protocol:** V24.01 eve-logic callsites and real client traces define observed RPC signatures, types, notifications and transitions.
- **Working server behavior:** Treat EveJS v0.12.9 (`Fokiiiiiii/evejs-v0.12.9`) as a **primary executable functional reference**, not merely an example. Trace and, where possible, execute its working end-to-end flows before implementing their eve.rs equivalents. Match its intended client-visible behavior by default; justify and test deviations using client evidence and higher-priority gameplay sources.

## EveJS Implementation Boundary
EveJS is a working functional reference, not an architectural or implementation template. Reproduce its intended gameplay behavior and client compatibility, but do not inherit its bugs, workarounds, inefficient algorithms, duplicated logic, or architectural flaws. Prefer simpler, faster, and more maintainable implementations grounded in CCP specifications, the SDE, and verified client behavior.

## Required Rules
1. First inspect existing code, relevant ADRs, `CLAUDE.md` and `AGENT_OVERRIDE.md`.
2. Trace the whole feature: client action -> RPC/bound object -> EveJS working implementation -> Host provider -> authoritative state -> result/notifications -> next client action.
3. Complete actual Host wiring, mutations, persistence, errors, notifications and subsequent transitions; a registered route is not a working feature.
4. Derive runtime values from the official SDE, authoritative state and verified calculations. Never hardcode captured packets, guessed data or fake successes.
5. Reuse existing shared foundations and state owners. Do not introduce duplicate stores or no-op handlers.
6. Compare the same meaningful scenario against EveJS and eve.rs. Test success/failure, multiple input states, concurrency, retries and restart when relevant.
7. Distinguish passing unit/headless tests from verification with the real graphical client.
8. Keep TODOs and compatibility records evidence-based; do not mark unfinished features, or features without a recorded check, complete.

## Definition of Done
The end-to-end client-visible flow behaves correctly, including wire shape, state changes, persistence and notification order, with passing regression tests. Document any intentional difference from EveJS with evidence.

## Testing
- On the local machine, limit verification to compile-checks for touched projects (`dotnet build` for changed .NET projects and `cargo check` for changed Rust projects) plus the single focused test for the change. Do not run full suites or the full live headless suite locally.
- Run broader selected checks on GitHub Actions with `.github/workflows/agent-test.yml`, or use the push-triggered `.github/workflows/ci.yml` gate. The extended slow-live, benchmark, Windows, skill-training-speed and RPC-fuzz checks run through `.github/workflows/ci-nightly.yml`.
- A skipped test is not a pass. Report skips separately, and inspect and fix failed Actions runs at their root cause as soon as they appear.

Report in Japanese, separating verified statements from the ones that were not checked.
