# TODO rows 0–400 handoff

Snapshot reviewed against `origin/main` after merging `2a23de940e0039578243caee36d4c0d475d29f09`.

## Closed in this pass

- None. The current range contains recorded DONE rows, but this pass did not re-run their named regressions or recheck every code path needed to claim new closures.

## Open rows and blockers

- Project dashboard, lines 11–18: ruleset reconciliation still lacks the pre-checkpoint SDE/report reconciliation; login still has remaining client walks; security, performance and parser matrices lack their stated coverage; FW/CRAB/insurgency retain unbuilt mechanics/providers; weapon banks lack Host state, activation/reload behavior and a walk.
- Service audit, lines 72–96: contracts still lack public/region search and graphical acceptance; season challenge progress/claims have no definitions; player bounty claim/payout notifications and NPC standing live behavior remain; protobuf startup still reports client errors/timeouts; community fitting data has no Host provider. War negotiation, mutual-war invite, personal bill payment, bound war statistics and legacy station/vote RPCs lack the indicated authorities or client-call evidence.
- Runtime providers, lines 135–144: SSO token-to-account mapping has no account authority; insurgency lacks suppression/spread/FOB and a public gateway; ESS lacks the in-space walk; Abyssal still lacks all-tier/reconnect/combat parity; signal tracker lacks a snapshot producer; tower rows lack a provider; QA move logging lacks an audit sink; expert-system ownership and raffle bans lack persisted owners. The blacklist and dungeon/RW rows have no production source.
- Specialized mutations, lines 151–174: dynamic-item client acceptance and unresolved D3/D5/D6/D7 choices remain; Abyssal still lacks full parity and native launch; Void Space has no authority; unanchor completion, container launch and graphical compression acceptance remain. Multi-training is partial; escalation/operation, abyss diagnostic and RW/Victory Point data lack production providers.
- Drones and exploration, lines 195–213: utility/mining drone graphical checks and hybrid-drone behavior remain; Data Analyzer, Data Subverter, and target scan-strength effect bodies are pending.
- Polarized/reactive armor, lines 234–258: client acceptance remains; reactive armor redistribution donor/tie/cap rules and client refresh behavior lack the required checks.
- Module rules, lines 267–312: launcher refusals, propulsion group enforcement, running-state restrictions, and the mining scan candidate query remain. Compression is partly complete.
- P.A.N.I.C., lines 321–331: no fleet-buff action/registry or simulation support; recipient classification, asteroid lock, burnout, timer/reset fan-out, ejection/docking restrictions and client acceptance remain.
- Superweapons, lines 361–366: C3 remains on command bursts, tethering is absent, HOG Host partition tests and client acceptance are pending, and the general Area action/burst projectors are not built.
- Windows, lines 394–397: successful Windows CI rerun, Windows UTF-8 fixture run, console behavior on Windows, and SDK-path selection on the owner’s checkout remain unchecked.

## Evidence checked

- Current TODO rows and cited evidence in the assigned range.
- The merged Host implementation `dotnet/src/Eve.Host/EventLogRpcService.cs` validates the `LogClientStats` arguments, writes through the Host log and returns None. `EventLogHostTests.The_host_registers_event_log_and_records_client_stats_before_returning_none` and current TODO line 80 record Host registration and the regression. This is headless evidence; the client acceptance portion remains open.
- No PostgreSQL lane was used. No test was run in this pass. No graphical-client walk was performed.
