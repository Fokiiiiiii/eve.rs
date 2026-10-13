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
