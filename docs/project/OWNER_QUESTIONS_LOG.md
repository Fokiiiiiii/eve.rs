# Owner questions log

Date for entries: 2026-10-19. Questions with a recorded decision are marked decided; their evidence and remaining technical blockers stay in this log and the matching status documents.

## Q-02 — Arc 132 simulation kill sites

- **Date:** 2026-10-19
- **Question:** Should missions 17183–17186 stay held until a source-backed NPC manifest exists, or should four generic simulations be approved despite missing counts and rosters?
- **Context:** Matching server SDE build 3396210 contains only dungeon 14100; the client static export contains all four dungeon/objective graphs but no complete NPC site rosters. EveJS has no matching templates. Generic sites would not reproduce the named AIR simulations.
- **Options:** (A) Keep all four unavailable until a reliable manifest is found; the arc remains incomplete. (B) Approve generic objective-category sites; the missions become offerable with intentionally approximate content and unsupported counts/waves.
- **Recommended default:** A, keep them held.
- **Status:** decided (recommended default), 2026-10-19

## Q-03 — Untagged mission levels

- **Date:** 2026-10-19
- **Question:** Should the 1,100 missions with no defensible SDE level remain untagged, or should another explicit per-mission source/rule be authorized?
- **Context:** The SDE rows lack mission-level fields; agent level, division, title, reward, and standing do not define a stable mission-level conversion. Counts: courier 311, kill 187, mining 0, trade 451, other 151.
- **Options:** (A) Keep current level tags only where an explicit SDE/rule source exists. (B) Approve a separately documented mapping for specific rows, accepting its stated evidence and limitations.
- **Recommended default:** A; do not derive tags from proxies.
- **Status:** decided (recommended default), 2026-10-19

## Q-04 — Public insurgency gateway

- **Date:** 2026-10-19
- **Question:** Should insurgency corruption/suppression handlers be reopened once the client call path and authoritative progress/threshold rules are resolved, or remain deferred?
- **Context:** The client contract, generic HTTP/2 protobuf gateway, and campaign/site-point owner exist. Insurgency handlers, truthful aggregation, threshold data, and notice producers are incomplete; EveJS's gateway response uses placeholders. The client messenger path has a recorded client-side failure.
- **Options:** (A) Defer until the call path and rules can produce truthful UI state. (B) Implement contract-shaped reads/notices over the existing campaign owner after resolving both inputs; cost includes an authoritative progress formula and notification sequencing.
- **Recommended default:** A until the inputs are established, then implement B without placeholder values.
- **Status:** decided (recommended default), 2026-10-19

## Q-05 — Planetary Interaction survey

- **Date:** 2026-10-19
- **Question:** Should the project keep limited rule-derived PI values, or authorize an explicitly approximate survey for a modified client?
- **Context:** The native CCP resource harmonic algorithm/data and `SH` buffer format are absent; the client also has a proximity tuple unpack error in the trained/in-range path. Current server calls can return resource identities/project-derived values but not CCP survey bands.
- **Options:** (A) Keep the honest limited response without survey bands. (B) Define and label a project approximation and support a modified client. (C) Defer complete CCP-like survey until native inputs/format and a client-path fix are available.
- **Recommended default:** A now; C for any claim of CCP-like survey behavior.
- **Status:** decided (recommended default), 2026-10-19

## Q-06 — Command burst wire and buff registry

- **Date:** 2026-10-19
- **Question:** Should the C4 command-burst track be scheduled with a sim wire batch and a source-backed live buff value?
- **Context:** The earlier zero-value finding was based on reading output attributes before tracing effect 6737 to charge multipliers and module inputs. SDE charge multipliers and module values produce nonzero results; `CommandBurstValueTests` checks Shield Harmonizing (-8 Tech I, -10 Tech II), and `CommandBurstFamiliesTests` checks every published burst charge. The owner superseded the prior deferral on 2026-10-19 and directed the full command-burst build; Host coverage now checks delivery for all six families, including Mining Foreman collection 2474. A new level-V strength regression expects -12 for Armor Energizing, -12 for Shield Harmonizing and +60 for Mining Laser Field Enhancement from pinned SDE inputs. The two-pilot walk is included in the selected slow-workflow check. The live two-pilot trace showed both recipient ids arriving with the cycle, then a Host snapshot with no charge and four zero buff ids. Simlink protocol 81 now carries the consumed SDE charge type into settlement for DogmaWorld evaluation. The selected two-pilot Actions walk still needs a green end-to-end run to confirm receiver notifications, changed dogma and expiry.
- **Decision:** Superseded 2026-10-19: build command bursts, including the shared recipient exchange and timed buff delivery.
- **Status:** superseded by owner direction, 2026-10-19

## Q-07 — Weapon-bank semantics

- **Date:** 2026-10-19
- **Question:** Should weapon-bank behavior remain held on RC-5 until the required grouped-activation semantics are established?
- **Context:** TODO records E-07 as blocked on RC-5. Grouped weapons affect activation, ammo/cycle handling, and client-visible state.
- **Options:** (A) Keep E-07 held until RC-5 is resolved. (B) Approve a dedicated evidence-gathering track for the RC-5 contract and then implementation.
- **Recommended default:** A; do not invent grouping rules.
- **Status:** decided (recommended default), 2026-10-19

## Q-08 — Mutual-war invite provider

- **Date:** 2026-10-19
- **Question:** Should the mutual-war invite refusal remain until the authoritative war owner can support invite state and all client mutations?
- **Context:** TODO records an explicit unavailable refusal for pending/blocked reads and invite send/withdraw/respond/block writes. Returning success without connected state would fabricate behavior.
- **Options:** (A) Keep the explicit refusal until the provider and notifications are connected. (B) Prioritize a provider-backed implementation with permissions, state transitions, idempotency, and target notifications.
- **Recommended default:** A until a complete provider-backed flow is ready.
- **Status:** decided (recommended default), 2026-10-19

## Decisions already made by the owner

- **Decision 8:** Ships and modules are free; structures are free only when required.
- **Decision 9:** Do not add unnecessary refusals.
- **Decision 10:** Decide autonomously; when EveJS lacks a feature, build it in Rust from game rules and the client contract. Code work requires both codebase-memory and Serena.

- **Decisions 11–17 (2026-10-19):** Q-02 keep Arc 132 missions 17183–17186 held; Q-03 keep the approximately 1,100 untagged missions untagged until an authoritative level source exists; Q-04 defer the public insurgency gateway until its client call path and truthful progress/threshold rules exist; Q-05 keep PI survey limited to rule-derived values and do not synthesize an SH buffer; Q-06 superseded 2026-10-19: build command bursts, including recipient exchange and timed buff delivery; Q-07 keep weapon banks held on RC-5; Q-08 retain mutual-war invite refusal.
