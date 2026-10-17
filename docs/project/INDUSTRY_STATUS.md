# Industry: status, formulas and gaps (V24.01, build 3396210)

Scope: manufacturing, copying, material efficiency (ME) research, time efficiency (TE) research, invention, reactions and
reverse engineering, with job installation, cost, delivery and cancellation. Planetary interaction is out of scope.

Sources, in the order used:
- The pinned SDE (`sde/lock.json`, build 3396210). The `blueprints` table carries every activity (`manufacturing`,
  `research_time`, `research_material`, `copying`, `invention`, `reaction`) with materials, products, skills, time and
  `maxProductionLimit`. The pinned SDE has no `industryActivity*` or `industrySystems` table.
- The V24.01 client's industry package in `eve-logic/industry/` (`const.py`, `activity.py`, `job.py`,
  `activities/*.py`, `modifiers.py`) and `eve/common/script/util/industryCommon.py`. These are the formulas the client
  quotes, so the server follows them.
- EveJS v0.12.9 (`server/src/services/industry/`) as the behaviour reference. Its price model (base-price fallback,
  flat cost index) is not copied.
- EVE University wiki and CCP pages for the delivery rule (the Deliver button in the station's industry window).
- The SDE `npcCorporations.taxRate` for the NPC station tax (see "Decisions").

## What existed before this slice

| Piece | Where | State |
|---|---|---|
| Install / complete / complete-many / cancel RPCs, job reads, counts | `Eve.Services.Core/IndustryJobsService.cs`, Host `IndustryComposition.cs`, `PersistenceIndustryJobOperations*.cs` | Code present; the install path read SDE tables that the pinned SDE does not carry, so no install could succeed. |
| Blueprint manager (`GetBlueprintData`, `GetLimits`, owner view) | `Eve.Services.Core/BlueprintManagerService.cs` | Present, unit-tested (`BlueprintManagerServiceTests`). |
| Industry monitor (`ConnectJob`, `DisconnectJob`) | `Eve.Host/IndustryMonitorRpcService.cs` | Present. |
| Facility resolution (NPC station, Upwell structure) | `IndustryComposition.ResolveIndustryFacilityAsync` | NPC stations accepted every activity at tax 0 and time 1; structures by service-name match. |
| Adjusted prices (ESI) | `Eve.Market/AdjustedPrices.cs` | Present; used for item value (EIV). |
| Cost index for the map RPC | `IndustryComposition` `GetIndustryCostModifier` | Read a missing SDE table (an error at call time). |
| Job and outbox events, client notification `OnIndustryJob` | `IndustryComposition` outbox subscriber | Present. |
| Industry migration (`industry.jobs`) | `Eve.Host` migrations | Present. |

## Defects fixed in this slice

1. Every install failed: `industryActivity`, `industryActivityMaterials`, `industryActivityProducts`,
   `industryActivitySkills` and `industrySystems` are not tables of the pinned SDE. Install now reads `blueprints`.
2. The ME and TE research activity ids were swapped (`3` is time efficiency, `4` is material efficiency, per the
   client's `RESEARCH_TIME = 3`, `RESEARCH_MATERIAL = 4`).
3. TE research added one level per run; the client adds `STEP_TIME_EFFICIENCY = 2`, capped at 20.
4. Cancelled jobs were stored as status 101 (the client's `DELIVERED`); cancellation now stores 102 (`CANCELLED`).
5. Job cost was based on the product's value. The client prices the materials (item value (EIV), EIV).
6. Duration ignored the blueprint's TE, and the skill factors were hard-coded.
7. Material efficiency was applied to the whole job with a plain ceiling. The client rounds per run to two decimals,
   rounds up, and keeps at least one unit per run.
8. Invention outputs took the input blueprint's ME and TE; the client gives 2 ME and 4 TE plus the decryptor.
9. Invention ignored decryptors, and copying had no check of the licensed run limit or of blueprint originality.
10. No job slots were enforced.
11. NPC stations accepted every activity.

## Formulas (client, then SDE attributes)

Constants (`industry/const.py`): `COST_PERCENTAGE = 0.02`, `SCC_SURCHARGE_RATE = 0.04` (halved for research),
`INDUSTRY_SECURITY_TAX_RATE = 0.05` (`security/common/const.py`, applied in high-security space),
`MAX_MATERIAL_EFFICIENCY = 10`, `MAX_TIME_EFFICIENCY = 20`, `STEP_TIME_EFFICIENCY = 2`,
`INVENTION_MATERIAL_EFFICIENCY = 2`, `INVENTION_TIME_EFFICIENCY = 4`, `INVENTION_SKILL_PROBABILITY = 1/30`,
`MAX_RUN_LENGTH = 2592000`, `RESEARCH_TIMES` (ten level factors, `1, 29/21, 23/7, ...`).

- **Item value (EIV)** (`industryCommon.GetBlueprintPrice`): a type whose blueprint has the activity is priced as
  the sum of its base materials (recursively); any other type takes its ESI adjusted price; science activities price
  with the manufacturing materials.
- **Base cost** (`Activity.job_cost`): manufacturing `EIV x runs`; ME and TE research `EIV x 0.02 x (research time /
  base time)`; copying `EIV x 0.02 x licensed runs x runs`; invention `EIV(product) x 0.02 x runs`; reaction
  `EIV(reaction) x runs`.
- **Installation cost** (`Job.total_cost`): `cost + tax + scc + security tax`, where each part is the base cost
  (rounded) times its rate: the system cost index, the facility tax, `0.04` (research `0.02`), and `0.05` in
  high-security space.
- **Materials** (`Job._get_materials`): `max(ceil(round(base x modifier x runs, 2)), runs)`. The modifier is
  `1 - ME/100` for manufacturing only. Invention and reactions take no ME.
- **Duration** (`Job._get_time`): base seconds times every modifier, rounded. Manufacturing: `time x runs`, times
  `1 - TE/100`, times the Industry (dogma 440, -4 % per level) and Advanced Industry (1961, -3 % per level) factor
  (additive), times each required skill's `manufactureTimePerLevel` (1982) factor. Copying: time x licensed runs x runs,
  times Science (452). ME research: the level ladder over the runs, times Metallurgy (468). TE research: the level
  ladder only. Reaction: time x runs, times Reactions (2660, -4 % per level). Invention: time x runs.
- **Facility time factor**: NPC station `manufacturingFactor` (manufacturing) and `researchFactor` (science), from the
  SDE station operation; structures keep their `industryTimeMultiplier`.
- **Invention probability** (`Invention.job_probability`): mean product probability x (1 + sum of skill level x 1/30
  over the activity's skills) x decryptor probability multiplier (dogma 1112), clamped to [0, 1].
- **Invention output** (`Invention.job_output_products`): one blueprint copy per successful attempt, with runs =
  product quantity + decryptor runs (1124), ME = 2 + decryptor ME (1113, truncated), TE = 4 + decryptor TE (1114,
  truncated). Successful attempts are drawn with a seed stored in the job, so a retry gives the same result.
- **Slots**: manufacturing is 1 + Mass Production (450) + Advanced Mass Production (450); the science activities are 1 +
  Laboratory Operation (471) + Advanced Laboratory Operation (471), each bonus read from the SDE per level. The base of 1 is
  the project's basis (cut: not read from the SDE).
- **Refusal basis**: each refusal in this slice is one of: an SDE station service list, the client's activity
  compatibility rule, the client's job slot model, the client's research limit, or the client's decryptor list.

## Tests

- `IndustryRulesTests` (11, `Eve.Services.Core.Tests`): materials, efficiency modifiers, cost parts, duration, research
  ladder, research limits, invention probability, deterministic attempts, activity keys.
- Existing `IndustryJobsServiceTests` and `BlueprintManagerServiceTests`: pass unchanged (fake ports).
- Host build: `dotnet build src/Eve.Host` succeeds.
- `IndustryJobPersistenceTests` (5, `Eve.Host.Tests`, PostgreSQL on eve_p4, real `sde` schema): manufacturing install
  (cost = item value of the materials + tax + SCC + security tax, 600 s, 86 units consumed, blueprint locked, wallet debited
  by the cost), completion (product 165 delivered, blueprint unlocked), material research (ME 1, TE 0), time research after
  it (TE 2, ME 1), cancel (status 102, unlocked, materials kept), insufficient materials (no job, no charge, no lock),
  and the single manufacturing slot. Run: `EVE_PG_URL=Host=127.0.0.1;Port=5432;Username=postgres;Database=eve_p4`
  under `/tmp/eve-heavy-eve_p4.lock`; all 5 passed.
- Defects the PostgreSQL tests found and this slice fixed: the job INSERT had one placeholder fewer than its columns,
  so the activity snapshot was written from the integer account key (every install failed); research completion passed
  a negative runs value for an original blueprint, which the item store rejects (originals now pass null).

## Not done in this slice

- Invention, copying and reaction paths in PostgreSQL (the invention output and decryptor effects are covered by unit tests only).
- Headless live walks through `tools/mitm` for each activity (DB-seeded Cockroach, blueprint, materials, skills).
- ESI cost index loader.

## Live walks (eve_p4, eve.rs, 2026-10-13)

`tools/headless-client/tests/test_walk_industry.py` (5 walks, all passed live through the headless client on the
run script: `EVE_MARKET_SEED=skip EVE_SDE_DIR=<sde jsonl> tools/dev/run-eve-live.sh` with `EVE_RUN='cd tools/headless-client &&
python3 -m pytest tests/test_walk_industry.py'`, port 26103, under `/tmp/eve-heavy-eve_p4.lock`):

- manufacturing: install (86 units consumed, status 1), list, cancel (status 102, a cancelled job cannot be delivered);
- manufacturing delivery: the product (165) reaches the hangar, status 100;
- ME then TE research at a station with a Laboratory: ME 1 / TE 0, then ME 1 / TE 2;
- copying at a Laboratory: one copy (blueprint_copy) per run;
- invention: the decryptor the client lists in `materials` is used (consumed, probability 0.393), the copy's runs fall, the
  outputs carry runs 2, ME 4, TE 14.

The walks found three client-payload defects, fixed in this slice: `job.dump()` has no `ownerID` and no
`blueprintLocationID` (the server required both, so every client install was refused), the copy flag is not in the dump
(the item decides it now), and the decryptor is listed in `materials` (the server now reads it from there).

Slice 3:
- Durable readiness: each installed job has a durable scheduler job at its end time (`IndustryReadyJobs`, Eve.Scheduler)
  that sets the job to Ready and sends `OnIndustryJob` through the event bus. The job is idempotent, and a startup sweep
  marks the jobs that ended while the host was down. The client's Ready status is 3 (`eve-logic` const.py); the coordinator's
  104 is not the client's value and was not used. Tests: `IndustryReadyTests` (5).
- Facility selector (`facilityManager`: GetFacility, GetFacilities, GetFacilitiesByID, GetFacilityTaxes,
  GetFacilityLocations): the NPC station's activities from the SDE services (manufacturing 1 with the Factory, 3/4/5/8 with
  the Laboratory), its corporation tax from `npcCorporations`, and the hangar location. Walk: `test_the_facility_selector_*`
  passed live.
- Remote calls the client makes, checked against the server: `industryManager` (GetJob, GetJobsByOwner, InstallJob,
  CompleteJob, CompleteManyJobs, CancelJob), `blueprintManager` (GetBlueprintData, GetBlueprintDataByOwner, GetLimits),
  `facilityManager` (above, GetMaxActivityModifiers, SetFacilityTaxes). The other `industrySvc` and `facilitySvc` methods
  are client-side. Not built: corporation jobs, structure facilities beyond the existing structure lookup.

Known differences, not closed:
- The MITM capture against the real client and the EveJS reference run (`docs/project/MITM_COMPARE.md`) were not run in
  slice 3; no client session was available to capture.
- The MITM capture against the real client and the EveJS reference run (`docs/project/MITM_COMPARE.md`) were not run for the
  industry walks; the shapes were compared by reading the code (InstallJob returns the job ID in both; cancel and
  complete return nothing in both).
- Station Laboratory: the walk's station (60015249) has no Laboratory (service 15), so research there is refused by the SDE
  service list; the walks run research at the nearest Laboratory station, with the Scientific Networking level that reaches it.

## Slice 4: MITM, corporation jobs, structures (2026-10-13)

- **MITM capture**: the industry walk file ran through `tools/mitm` (proxy 26213 to the eve.rs lane port 26103) with
  `tools/dev/run-eve-live.sh` under the lane lock. Capture: `captures/eve-rs-industry.jsonl` (git-ignored). It holds six
  connections and the calls `InstallJob` (6), `CompleteJob`, `CancelJob`, `GetJobsByOwner`, `GetBlueprintData`,
  `GetFacility`, `GetFacilities`, `GetFacilityLocations`, `GetFacilityTaxes`, and five `OnIndustryJob` notifications.
- **EveJS comparison**: by code reading (`server/src/services/industry/industryManagerService.js`). The EveJS reference was not
  run live for this slice (no reference run for the industry walk exists). Differences are in `DEVIATIONS.md` (slice I2).
- **Corporation jobs**: the client's rules are enforced (see DEVIATIONS I2). Not done: corporation notifications to other
  members, and the corporation delivery location.
- **Structures**: not changed (see DEVIATIONS I2).
- **Reverse engineering and duplicating**: not in the client's activities (`industry/const.py`); nothing to add.
- **Tests**: `dotnet test tests/Eve.Host.Tests --filter Industry` passes 21 of 21 (under the lane lock, `EVE_PG_URL` set).
  The walk file `tests/test_walk_industry.py` passes 6 of 6 live through the MITM proxy.

## Slice 5: corporation outputs and announcements, structure activities (2026-10-13)

- **Corporation delivery location**: honoured. The facility lists the deliveries (62) and hangar divisions 1..7; the install
  refuses a personal hangar; a missing output goes to the deliveries. PostgreSQL tests: `IndustryCorporationPersistenceTests`.
- **Corporation notifications**: `OnIndustryJob` to the owner, the installer and online members with Factory Manager. The walk
  shows the manager's session receiving it and the member without the role not (`test_a_corporation_job_is_paid_...`).
- **Corporation walk** (through the MITM proxy): a player corporation seeded in the database (owner names, corporation,
  members, employment, character corporation, wallet division 1000), the director installs from the corporation's division,
  the materials are consumed, the division pays, and the product goes to the chosen corporation hangar division.
- **Structure activities**: from the online service ids (EveJS mapping, see DEVIATIONS I3); the structure payload lists them.
  PostgreSQL test: `A_structure_runs_only_the_activities_its_services_allow`; unit test in `IndustryRulesTests`.
- **Not done**: rig and role bonuses (no fitted rigs in eve.rs; the SDE attributes are 2593 to 2595). The corporation
  notification to all members (EveJS sends to every member; eve.rs sends to the role holders).
- **Rust gate**: `cargo check --workspace --all-targets` passes on the merged tree. `cargo fmt --all -- --check` reports one
  file that is not changed by this slice (`crates/eve-space/src/scene/tests.rs`, a customs office test line).
- **Tests**: `dotnet test tests/Eve.Services.Core.Tests --filter Industry` 28 passed; `dotnet test tests/Eve.Host.Tests
  --filter Industry` 25 passed; `test_walk_industry.py` 7 of 7 through the MITM proxy (`captures/eve-rs-industry.jsonl`).

## Slice 6: rig and role bonuses, corporation recipients (2026-10-13)

- **Rig and role bonuses for structure jobs.** Read from the SDE: a rig is an item in the structure's rig slots (flags 92 to 99); its structure-domain modifiers (dogmaEffects) and attributes (2593 to 2595, scaled by the system's security band 2355 to 2357) give the material, time and cost bonuses by activity and product group or category. An engineering complex's role bonuses (2600 to 2602) and a refinery's (2721) come from the structure type. They multiply the job's material quantity (per-run rounding), duration and cost index part. The facility payload lists them as the client's activity modifiers and `rigModifiers`. Code: `IndustryBonuses` (`Eve.Services.Core/IndustryRules.cs`), `StructureBonusesAsync` (`IndustryComposition.cs`), the install path (`PersistenceIndustryJobOperations.Part2.cs`). Decisions and basis: DEVIATIONS "Industry slice I4".
- **Corporation job recipients.** Now the client's corporation industry view roles (Factory Manager, Rent Factory Slot, Rent Research Slot, from `CanSeeCorpBlueprints`), not Factory Manager only. Members without them are not sent the notification.
- **Tests.** Unit: `IndustryRulesTests` (18 pass, filter `IndustryRulesTests`, includes the 4 bonus tests). PostgreSQL on eve_p4 (under the lane lock): `dotnet test tests/Eve.Host.Tests --filter Industry`, 27 of 27 pass (includes 2 bonus install tests).
- **Not done.** A player fitting a rig through the client is not walked. No live walk with a fitted rig or hull bonus (no client session). The corporation notification is not walked for the new role set. Stacking penalties are not applied (basis: the client's arithmetic, DEVIATIONS I4).

## Slice 7: research audit against the client and EveJS (2026-10-17)

Audit of the research side (copying, ME and TE research, invention, reaction, install and delivery, job slots, R&D agents)
against eve-logic V24.01 (`industry/const.py`, `industry/job.py`), the pinned SDE and EveJS. Code changed: a comment only.

Matches the client, no change needed:
- Activity ids and constants (`const.py`: 1, 3, 4, 5, 8, 9; `IndustryRules` carries the same values, the ten `RESEARCH_TIMES`).
- Base cost: rounded once, then the cost, tax, SCC and security parts as in `job.py:127-133`. The SCC surcharge is halved for
  research only (`job.py:144-145`), as `IndustryRules.CostParts` does.
- Reverse engineering is not an activity of the V24.01 client (`ACTIVITIES` holds manufacturing, ME and TE research, copying,
  invention and reaction), so no job type exists to serve it. Not built.
- The system cost index is no longer missing. The ESI loader (`Eve.Market` `IndustryCostIndexes`: `EsiIndustrySystems`, the
  stored rows, the daily cache, `EVE_INDUSTRY_INDEX_SEED=skip` for offline runs) feeds `IIndustryCostIndexSource`; a system or
  activity that ESI does not list uses the configured rule `EVE_INDUSTRY_COST_INDEX`. The earlier "ESI cost index loader: not
  done" lines in this file are stale.

R&D research agents: open.
- The rate `ResearchRules.PointsPerDay` is the EveJS `computePointsPerDay` formula, (1 + (20 + 5 x negotiation + standing) / 100)
  x (specialty + agent level)^2. EveJS cites the EVE University "Research missions" page for it, the datacore price (100 RP and
  10,000 ISK), the concurrency rule (one project, plus one per Research Project Management level) and the qualification (Science V,
  specialty at the agent level). `ResearchProjectStore` holds the projects in the ESI shape. Nothing calls `StartAsync` yet.
- `AgentDialogueMachine` refuses Start Research (12), Cancel Research (13) and Buy Datacores (14) with the CustomInfo "This agent
  service is not available on this server yet."
- The client's flow is a round trip per button (EveJS `researchDialogue.js`; eve-logic `agents.py`): the server raises
  `SingleChoiceBox` (the field), `YesNo` (cancel) or `GetQuantity` (datacores) on the client's `agents` service, reads the answer
  (a radio answer `radioboxOption<n>Selected`, 1-based; `GetQuantity` returns `qty`), then acts and returns the conversation.
  `ClientSession.SendClientCall` sends such a call with a callback; no path awaits the answer inside the agent RPC yet.
- Missing: the field list, the three dialogue round trips, the start, cancel and datacore purchase (the ISK debit, the RP
  debit, the datacore items in the hangar, `OnDatacoreBought`), and the research state in the conversation. The walk needs a
  client session; the headless client can drive the dialogue.

Verification this slice: `IndustryJobPersistenceTests` 7 of 7 passed on eve_p4 under `/tmp/eve-heavy-eve_p4.lock` (install,
completion, material research, cancel, insufficient materials, the slot, the structure bonuses).

