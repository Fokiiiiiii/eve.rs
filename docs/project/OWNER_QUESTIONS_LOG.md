# Owner questions log

Date for entries: 2026-10-19. All questions below remain open until the owner records a decision.

## Q-02 — Arc 132 simulation kill sites

- **Date:** 2026-10-19
- **Question:** Should missions 17183–17186 stay held until a source-backed NPC manifest exists, or should four generic simulations be approved despite missing counts and rosters?
- **Context:** Matching server SDE build 3396210 contains only dungeon 14100; the client static export contains all four dungeon/objective graphs but no complete NPC site rosters. EveJS has no matching templates. Generic sites would not reproduce the named AIR simulations.
- **Options:** (A) Keep all four unavailable until a reliable manifest is found; the arc remains incomplete. (B) Approve generic objective-category sites; the missions become offerable with intentionally approximate content and unsupported counts/waves.
- **Recommended default:** A, keep them held.
- **Status:** open

## Q-03 — Untagged mission levels

- **Date:** 2026-10-19
- **Question:** Should the 1,100 missions with no defensible SDE level remain untagged, or should another explicit per-mission source/rule be authorized?
- **Context:** The SDE rows lack mission-level fields; agent level, division, title, reward, and standing do not define a stable mission-level conversion. Counts: courier 311, kill 187, mining 0, trade 451, other 151.
- **Options:** (A) Keep current level tags only where an explicit SDE/rule source exists. (B) Approve a separately documented mapping for specific rows, accepting its stated evidence and limitations.
- **Recommended default:** A; do not derive tags from proxies.
- **Status:** open

## Q-04 — Public insurgency gateway

- **Date:** 2026-10-19
- **Question:** Should insurgency corruption/suppression handlers be reopened once the client call path and authoritative progress/threshold rules are resolved, or remain deferred?
- **Context:** The client contract, generic HTTP/2 protobuf gateway, and campaign/site-point owner exist. Insurgency handlers, truthful aggregation, threshold data, and notice producers are incomplete; EveJS's gateway response uses placeholders. The client messenger path has a recorded client-side failure.
- **Options:** (A) Defer until the call path and rules can produce truthful UI state. (B) Implement contract-shaped reads/notices over the existing campaign owner after resolving both inputs; cost includes an authoritative progress formula and notification sequencing.
- **Recommended default:** A until the inputs are established, then implement B without placeholder values.
- **Status:** open

## Q-05 — Planetary Interaction survey

- **Date:** 2026-10-19
- **Question:** Should the project keep limited rule-derived PI values, or authorize an explicitly approximate survey for a modified client?
- **Context:** The native CCP resource harmonic algorithm/data and `SH` buffer format are absent; the client also has a proximity tuple unpack error in the trained/in-range path. Current server calls can return resource identities/project-derived values but not CCP survey bands.
- **Options:** (A) Keep the honest limited response without survey bands. (B) Define and label a project approximation and support a modified client. (C) Defer complete CCP-like survey until native inputs/format and a client-path fix are available.
- **Recommended default:** A now; C for any claim of CCP-like survey behavior.
- **Status:** open

## Q-06 — Command burst wire and buff registry

- **Date:** 2026-10-19
- **Question:** Should the C4 command-burst track be scheduled with a sim wire batch and a source-backed live buff value?
- **Context:** DEVIATIONS records missing recipient-list/live-number wire fields and no buff registry; the current SDE value chain evaluates to zero. The work needs protocol, registry, delivery, and value semantics.
- **Options:** (A) Defer until the value source is settled and schedule the shared wire/registry work together. (B) Approve a targeted research/implementation track to find authoritative values and add the wire/registry; this carries protocol compatibility and cross-service work.
- **Recommended default:** A, no zero-value burst behavior.
- **Status:** open

## Q-07 — Weapon-bank semantics

- **Date:** 2026-10-19
- **Question:** Should weapon-bank behavior remain held on RC-5 until the required grouped-activation semantics are established?
- **Context:** TODO records E-07 as blocked on RC-5. Grouped weapons affect activation, ammo/cycle handling, and client-visible state.
- **Options:** (A) Keep E-07 held until RC-5 is resolved. (B) Approve a dedicated evidence-gathering track for the RC-5 contract and then implementation.
- **Recommended default:** A; do not invent grouping rules.
- **Status:** open

## Q-08 — Mutual-war invite provider

- **Date:** 2026-10-19
- **Question:** Should the mutual-war invite refusal remain until the authoritative war owner can support invite state and all client mutations?
- **Context:** TODO records an explicit unavailable refusal for pending/blocked reads and invite send/withdraw/respond/block writes. Returning success without connected state would fabricate behavior.
- **Options:** (A) Keep the explicit refusal until the provider and notifications are connected. (B) Prioritize a provider-backed implementation with permissions, state transitions, idempotency, and target notifications.
- **Recommended default:** A until a complete provider-backed flow is ready.
- **Status:** open

## Decisions already made by the owner

- **Decision 8:** Ships and modules are free; structures are free only when required.
- **Decision 9:** Do not add unnecessary refusals.
- **Decision 10:** Decide autonomously; when EveJS lacks a feature, build it in Rust from game rules and the client contract. Code work requires both codebase-memory and Serena.
