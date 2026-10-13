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
- **Slots** (`CHARACTER_MODIFIERS`, skills 3387, 24625, 3406): manufacturing `1 + Mass Production + Advanced Mass
  Production`; the science activities share `1 + Laboratory Operation`. The base of 1 is the project's basis, not read
  from the SDE.
- **Research limits**: ME research runs left `(10 - ME)`, TE research runs left `(20 - TE) / 2`.
- **Activity compatibility** (`Activity.is_compatible`): invention needs a copy; copying, ME, TE and reaction need an
  original; copying's licensed runs stay within `maxProductionLimit`.

## Per-activity status

| Activity | Install | Completion | Status |
|---|---|---|---|
| Manufacturing (1) | Yes (Factory service at NPC stations) | Outputs, BPC runs decremented | Fixed and unit-tested in this slice. Not run end to end against a live server. |
| ME research (4) | Yes (Laboratory) | ME + 1 per run, cap 10 | Fixed (ids swapped before). Not run live. |
| TE research (3) | Yes (Laboratory) | TE + 2 per run, cap 20 | Fixed. Not run live. |
| Copying (5) | Yes (Laboratory) | One BPC per run, licensed runs, source ME/TE | Checks added. Not run live. |
| Invention (8) | Yes (Laboratory) | Successful attempts, decryptor applied | Implemented in this slice. Not run live. |
| Reaction (9) | Structures only | Outputs | Installs at NPC stations are refused (see Decisions). Structure reaction service not checked. |
| Reverse engineering | Not in the pinned SDE (no activity key) | - | Not built: nothing to implement against. |

## Decisions and cuts

- **Cost index**: the pinned SDE and the client data carry no live cost indices, and the ESI `GET /industry/systems/`
  activity keys could not be confirmed from a page read in this slice. The project rule is `EVE_INDUSTRY_COST_INDEX`
  (a fraction, default 0), applied to every system and activity, and used by the map RPC too. Live indices are the next step.
- **NPC station facility tax**: the SDE `npcCorporations.taxRate` of the station's owning corporation (CCP data; 0 for
  254 corporations, 0.11 for 27, 0.10 for 2). The war-faction modifier (`npcstationtaxmodifier`, applied by the client only
  during a faction war with a war faction) is not applied: cut.
- **NPC station services**: the SDE station operation's service list decides the activity. Service 14 (Factory) runs
  manufacturing; service 15 (Laboratory) runs the science activities. No NPC station service runs reactions, so reactions
  at NPC stations are refused. The basis is the SDE service list and the client's reaction facility rule.
- **Structure reactions**: the structure branch still accepts any industry-capable structure for reactions; the
  reaction service is not checked. Cut for this slice.
- **Distance**: the client's `FACILITY_DISTANCE` rule (jumps to the facility against the Supply Chain Management,
  Scientific Networking and Remote Reactions skills) is not enforced. The install payload carries no distance, and the
  character's jump distance is not computed here. Cut.
- **Lower invention skill probability** (`TYPE_LIST_LOWER_INVENTION_SKILL_PROBABILITY`, 1/40): not applied; every skill
  uses 1/30. Cut.
- **Duration skills**: the Industry, Advanced Industry, required-skill, Science, Metallurgy and Reactions factors come from
  the SDE skill attributes. The client's character dogma (`GetIndustryCharacterModifiers`) is not replicated. The TE
  research skill and invention time skills are not applied. Cut.
- **Delivery**: completed jobs stay in the Ready state until the owner delivers them (the Deliver button, `CompleteJob`).
  The EVE wiki describes that flow. The scheduler is not used for delivery, so no auto-delivery exists. The client raises
  its ready notification from its own timer, as before.
- **Invention decryptors**: the optional item must carry a decryptor attribute (1112, 1113, 1114 or 1124); anything else
  is refused, following the client's decryptor option list.
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
