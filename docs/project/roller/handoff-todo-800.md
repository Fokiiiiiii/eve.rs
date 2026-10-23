# Roller handoff: TODO lines 800–1200

Updated 2026-10-23. The active range is still incomplete; do not treat the remaining rows below as closed.

## Closed or reconciled in this pass

- Lines 799–817: corrected stale SF-10/SF-11 claims against sections 32 and 36. Siege fuel/locks, stance wire, special-hull bonuses, command-burst/cloak bonuses, and jump-drive rules have the cited tests; remaining simulation transitions and client acceptance stay open.
- Lines 860–867: corrected beacon rows against Track A8c in section 35. Anchored-beacon jump/bridge/conduit, notices, inhibitor behavior, and wormhole/Pochven rules have the cited Host tests. Simulation arrival and graphical-client acceptance remain open.
- Line 1018: GetMapObjects and GetMultiGraphicsEx moved to NOT NEEDED for V24.01 after CBM text searches of home-user-eve-logic returned no callsite for either.
- Line 1042: nes_intro moved to DONE based on NesIntroService, NesIntroStore, NesIntroServiceTests, NesIntroStoreTests, and headless test_a_new_character_reads_and_skips_the_nes_intro_as_the_client_does.
- Line 1138: the A1 agent rowset check moved to DONE based on AgentCatalogSdeTests and its pinned-SDE count and concrete-agent tests.
- Line 1139: the LP wallet/refusal walk moved to DONE based on test_the_loyalty_point_wallet_answers_and_the_concord_exchange_is_refused.
- Line 1112: the corporation transaction access row moved to DONE based on MarketTransactionReadTests for Account Take and Junior Accountant paths and headless test_a_docked_character_reads_the_market_as_the_client_does.
- Lines 893/1077 and 1078: repair and reprocessing rows now describe the NPC-station implementations and focused tests. Repair still lacks a headless walk; reprocessing has test_walk_reprocessing.py, but the TODO range has no recorded passing result for it.
- Line 1171: decline/quit confirmation row now reflects deferred YesNo handling and the decline walk; the quit confirmation walk remains open.
- Line 1135: dialogue row now reflects DoAction coverage; the quit confirmation walk and client-localized labels remain open.
- Line 1137: research and locator row now reflects AG-4/AG-5 implementation; R&D mission generation, localized labels, and the locator standing split remain open.
- Line 1129: replaced the stale “no source read” claim for CONCORD LP exchange with EVE University Wiki eligibility/rates and concrete implementation requirements. The server currently refuses the RPC, so it remains OPEN.

## Remaining open rows and reasons

- SF-10 transformations: triage/bastion/core state transitions, one-per-ship limits, and deactivation rules lack simulation implementation and protocol integration. SF-11 and the stance field have server/wire tests; graphical-client presentation and ship lifecycle acceptance remain outstanding. Strategic Cruiser undock overload checks and silent assembly refusals remain.
- SF-3 fitting: space-loot/structure access checks and in-space fitting providers are absent; no graphical fitting walk is recorded.
- Track A9: Rorqual conduit needs the simulation's active industrial-core state. Beacon simulation arrival, restart notice behavior, and graphical-client walks remain.
- Service audit: the public gateway server is not built; structure monikers still lack the owner provider. Planetary orbital broker work is partial despite its PI walk. Owner-group and bookmark graphical acceptance, ALSC locks, and unbuilt structure behaviors remain.
- Config recordsets: GetMultiLocationsEx does not read structures; GetMultiOwnersEx lacks an SDE agent owner name/type/gender projection despite AgentCatalog; GetPositionEx/GetPositionsEx lack a position provider.
- Character entry: GetSettingsInfo needs CCP signing authority; SSO does not produce its token/computer hash; selection population check and entry are not atomic; population alternatives lack the stargate graph; achievement entry still uses a fixture payload.
- Account/support: reports and votes have no persistent store; redeeming codes have no code source/store; EveGuard has no service endpoint; bannedwords has no authoritative list; offers/localization/community-track state is incomplete.
- Map/structure: visits/history/weather/loyalty/extra-info/deadspace still lack providers. `GetFacWarZoneInfo` now reads occupied systems and upgrade levels through `FactionWarfareComposition`, but lacks a headless client call. The EDENCOM/Triglavian lists lack provenance; GetBeaconCount is not connected to fleet-runtime active-beacon counts (EveJS counts fleet beacons, not mobile cynosural beacons); structure and shipcaster providers remain incomplete; the duplicate unregistered paperDollServer class remains.
- Market: the client tax preview is a client-side LaunchDarkly flag and is now outside server scope. The “highsec security tax” row is PARTIAL: the Wiki market-fee rules and M9 walk do not isolate a 5% surcharge, so the live fee breakdown still needs an assertion. Q5 order tie-break still lacks a live competing-order walk; the three station-selection labels are client-local and now marked outside server scope.
- Agents: exchange implementation, LP donations, mission/FW/insurgency LP credits, mission offers and expiry notifications, mission briefing, and R&D missions remain. The client-visible rule consumer/implant path and LP-store standing requirements/structure purchases/cross-session notifications need reconciliation. A quit walk remains open.

## Evidence/tool limits

Read the current main TODO after fast-forward, root instructions, CLAUDE.md, AGENT_OVERRIDE.md, FOUNDATIONS.md, and both phase agent-rules.md files. Code and docs were read through Serena; graph and client-callsite searches used CBM. No PostgreSQL database was used. No code was changed in this pass. Focused verification: `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Services.Space.Tests/Eve.Services.Space.Tests.csproj --no-restore --filter 'FullyQualifiedName~ShipcasterRpcTests.Landing_pads_use_the_reference_object_ex_shape_and_sde_systems' -p:UseSharedCompilation=false` passed (1/1, 0 skipped). The first `--no-build` attempt could not load the existing test DLL; the build-backed retry passed. The work was committed as `27f3ef1d`, followed by evidence updates in `5e36ea91` and `e8cab357`; the last update was pushed to main in merge commit `2a09bb02`. Focused GitHub Actions run `39940814687` for `ShipcasterRpcTests.Landing_pads_use_the_reference_object_ex_shape_and_sde_systems` passed with no failed tests or jobs; `ci.yml` had not reported a run for that SHA when checked. Nine Rust source deletions were already present when this task began; they were left unstaged and untouched.