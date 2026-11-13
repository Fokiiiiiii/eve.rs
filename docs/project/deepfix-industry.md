# Industry correctness audit

Date: 2026-11-13. Client target: EVE Online V24.01, build 3396210. EveJS reference: `/home/user/evejs-261108.1`.

## Client and reference trace

V24.01's `eve/client/script/industry/industrySvc.py` sends `job.dump()` to `industryManager.InstallJob` (139–144), calls `CompleteJob(jobID, solarSystemID)`, `CompleteManyJobs([(jobID, solarSystemID), ...])` and `CancelJob(jobID, solarSystemID)` (148–161), reads owner jobs (62–83) and requests job slot counts for the logged-in character (242–245). The install button, ready-job button, and bulk-delivery button use those calls in `ui/shared/industry/submitButton.py:200–232` and `browserJobs.py:325–336`.

`facilitySvc.py:53–120, 128–138` reads facility lists and details, the per-activity maximum, taxes, and `industry.Location` rows. `ui/shared/industry/facilityEntry.py:50–75` divides each facility's system cost index by `GetMaxActivityModifier(activityID)` and displays service tax rates; `mixins.py:326–383` reads the SYSTEM cost modifier and resolves structure taxes from `serviceAccess`. The facility tax window sends fractional `taxCorporation` plus nullable fields through `SetFacilityTaxes` (`ui/inflight/facilityTaxWindow.py:25–35, 65–74`).

The V24.01 industry constructor consumes the six-field activity tuple and facility metadata (`eve/common/script/util/industryCommon.py:378–418`); it builds facility modifiers from those tuples and estimates material prices from blueprint materials and adjusted average prices (`485–543`). The structure service mapping is in `/home/user/eve-logic/structures/services.py:120–147`, and tax setting IDs and UI percentage ranges are in `structures/settings.py:22–33, 126–140`.

EveJS 0.12.9 routes the same facility and job RPCs in `server/src/services/industry/facilityManagerService.js:51–133` and `industryManagerService.js:65–148`. Its quote validates the client-selected input/output locations and material quantities, then calculates cost and duration in `industryRuntimeState.js:3332–3489`; it creates the installed-job record at 4499–4525, installs at 4895 onward, delivers at 5206 onward, and cancels at 5519 onward. Its configured system-cost-index source is a flat fallback (`industrySystemCostIndex.js:20–38`), so that approximation is not copied into eve.rs, which has stored per-system ESI rows.

## Changes and verified behavior

- `facilityManager.GetMaxActivityModifiers` previously looked up system ID `0` for each activity. The real ESI index rows are keyed by solar system, so the Host returned zero even when the database had non-zero values. A real registered-Host test first failed with expected manufacturing `0.23` and actual `0`; the handler now returns the maximum stored value per activity, or the configured fallback when that activity has no stored rows. This matches the client's cost-gauge normalization above.
- Facility activity payloads include the system-specific SYSTEM modifier, while the aggregate maxima use the same activity IDs. Facility listing now combines eligible NPC stations and online deployed Upwell facilities in the caller's region, sorted by facility ID.
- Facility locations keep the client-visible location and the backing inventory location/flag separately. Install validates submitted locations against the authoritative locations returned by the facility RPC, canonicalizes them before persistence, consumes materials from the backing inventory location, and delivers products there. Tests cover personal and corporation hangars, containers, a visible blueprint with no take access when the job needs no materials, and rejection of blueprint/material/output locations outside the offered set.
- Facility tax writes persist on the authoritative structure record, require the owning corporation and factory-manager role, commit before `OnFacilitiesUpdated`, and are read by the next facility and job requests. Structure profile percentages populate the service-specific `serviceAccess` rates consumed by the client; `taxCorporation` remains the fallback used when the profile has no explicit rate. Tests assert persisted job tax, wire response, notifications, restart behavior, and unauthorized failure.
- Industry cancellation is exercised through the registered Host route. The test checks committed status, blueprint unlock, consumed materials, absence of output, and the `OnIndustryJob` notification. Existing industry tests cover manufacturing, copying, ME/TE research, invention, reactions, job slots, wallet charges, ready delivery, and structure modifiers.

## Checks run

- Red/green regression: `IndustryFacilityHostFlowTests.Facility_cost_gauge_uses_the_maximum_saved_index_per_activity_across_systems` failed before the handler change and passed after it (Release, PostgreSQL database `eve_p4`, 0 skipped).
- After merging `origin/main`, `Eve.Host.Tests` industry slice (`--filter FullyQualifiedName~Industry`) passed 56, failed 0, skipped 0.
- After merging `origin/main`, `Eve.Services.Core.Tests` job service class passed 17, failed 0, skipped 0.
- After merging `origin/main`, `Eve.Market.Tests.IndustryCostIndexTests` passed 5, failed 0, skipped 0, including PostgreSQL store reads.
- After merging `origin/main`, `CharacterHostTests` passed 80, failed 0, skipped 3. The three skips require `EVE_ABYSSAL_SIM_BINARY` and `EVE_ABYSSAL_SIM_SDE`, which were not configured; they exercise ship-death reporting, nullsec ESS placement, and clone-jump simulation, not industry.
- The first unfiltered Host attempt was cancelled while the default ESI index seed was waiting on network access. It is not counted as a pass; subsequent Host runs used `EVE_INDUSTRY_INDEX_SEED=skip` and seeded database rows directly.
- After merging `origin/main`, direct Release builds passed with 0 warnings and 0 errors for `Eve.Host`, `Eve.Services.Core`, `Eve.Structures`, `Eve.Host.Tests`, and `Eve.Services.Core.Tests`.
- At pre-push audit, the latest completed main CI was run 1765 on `362b4a0b5e36`; the required CI gate and all required jobs succeeded. The advisory latest-stable Rust clippy job failed. The current main tip `9ae04d0f1` only refreshes POS manager verification documentation and has no newer CI run.

## Gaps and not checked

- The graphical V24.01 client was not run. The checks above exercise the real Host registration and PostgreSQL-backed state, not a live graphical-client session.
- EveJS contains POS assembly-facility support and generated V24.01 profiles (`server/src/services/industry/starbaseIndustryRuntime.js` and `starbaseAssemblyProfiles.json`, whose metadata names build 3396210 and the two client resource hashes). eve.rs currently has no persisted POS assembly-array anchor state or control-tower association in its industry facility model; its industry facility list reads NPC stations and the Upwell structure store. POS assembly facilities and their industry windows/jobs remain unsupported and need a separate authoritative deployed-array model.
- The EveJS server has no industry test command in its `server/package.json`; its behavior was source-traced, not executed end to end in this job.
- Online ESI refresh was disabled for local tests with `EVE_INDUSTRY_INDEX_SEED=skip`. The test seeded saved index rows directly; refresh scheduling and live ESI availability were not exercised.
- `/home/user/wt-cx4/.cache/sde` was absent. Tests used the SDE tables available in `eve_p4`; this work did not refresh or commit SDE data.
