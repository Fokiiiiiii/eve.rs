# Owner questions log

Date for entries: 2026-10-19. Questions with a recorded decision are marked decided; their evidence and remaining technical blockers stay in this log and the matching status documents.

## Q-02 — Arc 132 simulation kill sites

- **Date:** 2026-10-19
- **Question:** Should missions 17183–17186 stay held until a source-backed NPC manifest exists, or should four generic simulations be approved despite missing counts and rosters?
- **Context:** Matching server SDE build 3396210 contains only dungeon 14100; the client static export contains all four dungeon/objective graphs but no complete NPC site rosters. EveJS has no matching templates. Generic sites would not reproduce the named AIR simulations.
- **Options:** (A) Keep all four unavailable until a reliable manifest is found; the arc remains incomplete. (B) Approve generic objective-category sites; the missions become offerable with intentionally approximate content and unsupported counts/waves.
- **Recommended default:** A, keep them held.
- **New evidence 2026-10-19:** Arc 132 report adds mission-by-mission kits, objectives, object counts and rewards. The AIR tutorial pack is newly available in the 261018.1 zip; NPC type IDs, dungeon mapping and time limits remain to be established. Recommended default remains holding the four missions.
- **Status:** decided (recommended default), 2026-10-19

## Q-03 — Untagged mission levels

- **Date:** 2026-10-19
- **Question:** Should the 1,100 missions with no defensible SDE level remain untagged, or should another explicit per-mission source/rule be authorized?
- **Context:** The SDE rows lack mission-level fields; agent level, division, title, reward, and standing do not define a stable mission-level conversion. Counts: courier 311, kill 187, mining 0, trade 451, other 151.
- **Options:** (A) Keep current level tags only where an explicit SDE/rule source exists. (B) Approve a separately documented mapping for specific rows, accepting its stated evidence and limitations.
- **Recommended default:** A; do not derive tags from proxies.
- **New evidence 2026-10-19:** Report 2 identifies an 2026-08-19 mission backend data-processing update after the proposed June 16 checkpoint. No new level evidence changes the current decision.
- **Status:** decided (recommended default), 2026-10-19

## Q-04 — Public insurgency gateway

- **Date:** 2026-10-19
- **Question:** Should insurgency corruption/suppression handlers be reopened once the client call path and authoritative progress/threshold rules are resolved, or remain deferred?
- **Context:** The client contract, generic HTTP/2 protobuf gateway, and campaign/site-point owner exist. Insurgency handlers, truthful aggregation, threshold data, and notice producers are incomplete; EveJS's gateway response uses placeholders. The client messenger path has a recorded client-side failure.
- **Options:** (A) Defer until the call path and rules can produce truthful UI state. (B) Implement contract-shaped reads/notices over the existing campaign owner after resolving both inputs; cost includes an authoritative progress formula and notification sequencing.
- **Recommended default:** A until the inputs are established, then implement B without placeholder values.
- **New evidence 2026-10-19:** Report 1 supplies candidate cumulative stage effects but no thresholds, decay rule or gateway protobuf definitions. Recommended default remains deferring the public gateway until truthful progress and thresholds are sourced.
- **Status:** decided (recommended default), 2026-10-19

## Q-05 — Planetary Interaction survey

- **Date:** 2026-10-19
- **Question:** Should the project keep limited rule-derived PI values, or authorize an explicitly approximate survey for a modified client?
- **Context:** The native CCP resource harmonic algorithm/data and `SH` buffer format are absent; the client also has a proximity tuple unpack error in the trained/in-range path. Current server calls can return resource identities/project-derived values but not CCP survey bands.
- **Options:** (A) Keep the honest limited response without survey bands. (B) Define and label a project approximation and support a modified client. (C) Defer complete CCP-like survey until native inputs/format and a client-path fix are available.
- **Recommended default:** A now; C for any claim of CCP-like survey behavior.
- **New evidence 2026-10-19:** Report 1 lists Remote Sensing range and PI cycle figures, but the resource yield numbers are player measurements and SH inputs remain absent. No change to the limited survey default.
- **Status:** decided (recommended default), 2026-10-19

## Q-06 — Command burst wire and buff registry

- **Date:** 2026-10-19
- **Question:** Should the C4 command-burst track be scheduled with a sim wire batch and a source-backed live buff value?
- **Context:** The earlier zero-value finding was based on reading output attributes before tracing effect 6737 to charge multipliers and module inputs. SDE charge multipliers and module values produce nonzero results; `CommandBurstValueTests` checks Shield Harmonizing (-8 Tech I, -10 Tech II), and `CommandBurstFamiliesTests` checks every published burst charge. The owner superseded the prior deferral on 2026-10-19 and directed the full command-burst build; Host coverage now checks delivery for all six families, including Mining Foreman collection 2474. A new level-V strength regression expects -12 for Armor Energizing, -12 for Shield Harmonizing and +60 for Mining Laser Field Enhancement from pinned SDE inputs. The two-pilot walk is included in the selected slow-workflow check. The live two-pilot trace showed both recipient ids arriving with the cycle, then a Host snapshot with no charge and four zero buff ids. Simlink protocol 81 now carries the consumed SDE charge type into settlement for DogmaWorld evaluation. The live trace identified the consumed-charge settlement gap; protocol 81 carries the SDE charge type through Host evaluation. Run 45 confirmed both receiver notifications and the changed dogma value (0.92 to 0.844928), then failed because its assertion expected -8% rather than the Bifrost Command Destroyers I value of -8.16%. The walk now checks nested Destiny actions and the SDE-derived value. Run 46 is queued to verify the value and expiry end to end.
- **Decision:** Superseded 2026-10-19: build command bursts, including the shared recipient exchange and timed buff delivery.
- **New evidence 2026-10-19:** Report 1 lists dogma attribute IDs and example charge values, while formula ordering and stacking remain open. Current charge registry/tests cover published families; retain the full build decision.
- **Status:** superseded by owner direction, 2026-10-19

## Q-07 — Weapon-bank semantics

- **Date:** 2026-10-19
- **Question:** Should weapon-bank behavior remain held on RC-5 until the required grouped-activation semantics are established?
- **Context:** TODO records E-07 as blocked on RC-5. Grouped weapons affect activation, ammo/cycle handling, and client-visible state.
- **Options:** (A) Keep E-07 held until RC-5 is resolved. (B) Approve a dedicated evidence-gathering track for the RC-5 contract and then implementation.
- **Recommended default:** A; do not invent grouping rules.
- **New evidence 2026-10-19:** Report 1 gives the grouped activation/reload/unoffline behavior as a candidate from Quantum Rise. This does not establish current client RPC contract or edge behavior. Keep the RC-5 check open.
- **Status:** decided (recommended default), 2026-10-19

## Q-08 — Mutual-war invite provider

- **Date:** 2026-10-19
- **Question:** Should the mutual-war invite refusal remain until the authoritative war owner can support invite state and all client mutations?
- **Context:** TODO records an explicit unavailable refusal for pending/blocked reads and invite send/withdraw/respond/block writes. Returning success without connected state would fabricate behavior.
- **Options:** (A) Keep the explicit refusal until the provider and notifications are connected. (B) Prioritize a provider-backed implementation with permissions, state transitions, idempotency, and target notifications.
- **Recommended default:** A until a complete provider-backed flow is ready.
- **New evidence 2026-10-19:** Report 1 found no transition rules for mutual-war invites. Retain the provider-backed flow requirement.
- **Status:** decided (recommended default), 2026-10-19

## Q-12 — Pochven Triglavian NPC bounty payouts

- **Date:** 2026-10-19
- **Question:** Should Pochven Triglavian NPC kills pay bounties, and should their loot be changed?
- **Decision:** Do not pay bounties for Pochven Triglavian NPCs. The owner rule is that non-pirate ships basically have no bounty and bounty payouts exist only in New Eden proper, essentially nowhere outside it. Leave loot and drops unchanged because this decision does not specify them.
- **Status:** decided 2026-10-19

## Q-13 — Wormhole Local membership

- **Date:** 2026-10-19
- **Question:** Which Local mode applies in wormhole systems?
- **Decision:** Use DELAYED mode: the Local member list contains only characters who have spoken in that system.
- **Status:** decided 2026-10-19
- **Affected records:** TODO chat service and wormhole Local rows; DEVIATIONS, wormhole Local.

## Q-14 — Customs office tax defaults

- **Date:** 2026-10-19
- **Question:** Which POCO tax rates and standing discounts should planetary interaction use?
- **Decision:** Follow the EVE University Wiki default rates and standing discounts, including the high-security NPC component and Customs Code Expertise effects. Taxes apply on import and export and the owner-set portion is paid to the owning corporation wallet. Sources: [Planetary Industry](https://wiki.eveuniversity.org/Planetary_Industry), [Colony management](https://wiki.eveuniversity.org/Colony_management), and [Player Owned Customs Office](https://wiki.eveuniversity.org/Player_Owned_Customs_Office).
- **Status:** decided 2026-10-19
- **Affected records:** TODO customs office row; DEVIATIONS, Planetary interaction; PI_DESIGN.md.

## Q-15 — ESS linkable hulls in tests

- **Date:** 2026-10-19
- **Question:** How should ESS linkable hulls be selected and made available for acceptance tests?
- **Decision:** Derive eligible hulls from the SDE module fitting attributes. Grant an eligible hull fitted with an online ESS link module to test characters through a role-gated GM command.
- **Status:** decided 2026-10-19
- **Affected records:** TODO ESS row; DEVIATIONS, Encounter Surveillance System; GM_QA_DESIGN.md.

## Decisions already made by the owner

- **Decision 8:** Ships and modules are free; structures are free only when required.
- **Decision 9:** Do not add unnecessary refusals.
- **Decision 10:** Decide autonomously; when EveJS lacks a feature, build it in Rust from game rules and the client contract. Code work requires both codebase-memory and Serena.

- **Decisions 11–17 (2026-10-19):** Q-02 keep Arc 132 missions 17183–17186 held; Q-03 keep the approximately 1,100 untagged missions untagged until an authoritative level source exists; Q-04 defer the public insurgency gateway until its client call path and truthful progress/threshold rules exist; Q-05 keep PI survey limited to rule-derived values and do not synthesize an SH buffer; Q-06 superseded 2026-10-19: build command bursts, including recipient exchange and timed buff delivery; Q-07 keep weapon banks held on RC-5; Q-08 retain mutual-war invite refusal.

## Q-09 — Embedded client data

- **Date:** 2026-10-19
- **Question:** Should embedded `client-*` exports be removed or replaced during the temporary public period?
- **Decision:** Keep the embedded client data as is. No data change now.
- **Status:** decided 2026-10-19

## Q-10 — CI triggers after the repository returns to private

- **Date:** 2026-10-19
- **Question:** Which GitHub Actions triggers should run after the repository returns to private?
- **Decision:** Make all workflows manual only: `workflow_dispatch` for `ci.yml`, `live-slow.yml`, and Windows jobs, with no push, schedule, or pull request triggers. The repository is public temporarily; the owner will switch it back and announce the change. Until then, keep the current automatic mode. `tools/dev/ci-mode.py` switches trigger blocks between modes.
- **Status:** decided 2026-10-19

## Q-11 — Placebo login hash exposure

- **Date:** 2026-10-19
- **Question:** How should the reusable login hash exposure be mitigated while preserving the unmodified V24.01 client contract?
- **Decision:** Use per-connection nonce binding, validity windows, per-account and per-IP attempt limits, and never log the hash. Document TLS termination as the production setup. Security agent cx35 is implementing the server-side mitigations.
- **Status:** decided 2026-10-19

## Q-09 — Pochven Triglavian NPC bounties

- **Date:** 2026-10-19
- **Question:** Should Pochven Triglavian NPC kills produce bounties?
- **Decision:** Do not pay bounties for Pochven Triglavian NPCs. Bounty payments are restricted to New Eden proper; other locations generally have none. Existing loot and drops are unchanged.
- **Status:** decided 2026-10-19
- **Affected records:** TODO bounty/loot status; DEVIATIONS, Encounter Surveillance System and NPC bounty rules.


## Q-16 — Single ruleset checkpoint

- **Date:** 2026-10-19
- **Question:** Should `24.01@2026-06-16` and SDE release 2026-06-16 be the single target for client build 3396210, with later patch changes excluded?
- **Context:** Supplied report 2 proposes this mapping and identifies a conflict: current FW Battlefield reward code uses 150,000 LP while the report says the checkpoint value was 50,000 LP before the 2026-07-01 change.
- **Recommended default:** Adopt the report's checkpoint pending primary metadata confirmation; correct post-checkpoint values in a separate implementation task.
- **Decision:** Adopt the per-item checkpoint calls. FW Battlefield LP at build 3396210 is 50,000 per battlefield; the 150,000 increase on 2026-07-01 is excluded. Other post-checkpoint values are corrected only when the report and patch timeline agree, with evidence recorded per item.
- **Status:** decided 2026-10-19

## Q-17 — AIR arc 132 simulation sites

- **Date:** 2026-10-19
- **Question:** Should the project reconstruct arc 132 sites from the UniWiki mission descriptions and the EveJS air-tutorial pack, using generic hostile hulls by faction and size where NPC identities are absent?
- **Context:** The report adds mission objectives, kits and counts; exact hostile type IDs, dungeon mapping and time limits remain missing. This could change Q-02's current keep-held default.
- **Recommended default:** Keep missions held until hostile identities and client dungeon mapping are established.
- **Decision:** Keep missions 17183-17186 held. EveJS 261018.1 air-tutorial pack has four unrelated tutorial templates (9103, 9105, 10461, 9245), no mission IDs 17183-17186 or dungeons 14097-14100, and no spawn profiles. It cannot supply the required NPC identities/compositions or simulation dungeon structure. EveJS authored content is approximate and not client-verified.
- **Status:** decided 2026-10-19

## Q-18 — Advanced Contracting skill value

- **Date:** 2026-10-19
- **Question:** Should Advanced Contracting +4 outstanding contracts per level be adopted after a source check?
- **Context:** Supplied report itself flags this figure as doubtful.
- **Recommended default:** Do not adopt until corroborated.
- **Decision:** Adopt Advanced Contracting +4 outstanding contracts per level as an approximate, unconfirmed configurable value; add only a limit hook at the contract owner seam.
- **Status:** decided 2026-10-19

## Q-19 — Zarzakh rules coverage

- **Date:** 2026-10-19
- **Question:** Which Zarzakh facts from the report should be prioritized for code reconciliation against the pinned client and SDE?
- **Context:** The report lists patch facts alongside multiple unknown operational mechanics.
- **Recommended default:** Reconcile only rules that have a current implementation path and source.
- **Status:** open

## Q-20 — CRAB candidate mechanics

- **Date:** 2026-10-19
- **Question:** Should the reported CEQ/interference regeneration, 60-second link tail, resistance bonus, exclusive reward window, BPC cost/materials and deployment limits be added to CRAB reconciliation as acceptance criteria?
- **Context:** The current design records several constants but these additional details need direct source/code comparison.
- **Recommended default:** Use the report as a checklist only; adopt each value after primary/client/reference verification.
- **Decision:** Adopt all CRAB values in report 1 section 9 as the target ruleset. Preserve existing owner-decided constants where the report is silent, including per-type bounties, wave 2 rosters and integer internals.
- **Status:** decided 2026-10-19

## Q-21 — Mission LP formula

- **Date:** 2026-10-19
- **Question:** Should the reported mission LP formula be used after independent corroboration?
- **Context:** Supplied report labels the formula high confidence but also says its sources do not corroborate the formula.
- **Recommended default:** Do not encode it until source-backed.
- **Decision:** Adopt the reported mission LP formula as an approximate, unconfirmed configuration value, including the reported Security Connections bonus.
- **Status:** decided 2026-10-19
