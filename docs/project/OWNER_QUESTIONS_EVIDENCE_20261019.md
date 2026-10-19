# Owner questions: evidence review (2026-10-19)

This note records the source review for the open decisions. Static-data claims refer to build 3396210 unless a source is named otherwise. Server-SDE and client-static exports are kept distinct because they contain different dungeon rows.

## Q-02 — Arc 132 simulation kill sites (17183–17186)

### Mission identity and stated objectives

The mission records in `/home/user/eve-client-sde/native/missions.jsonl.gz` identify all four as `agent.missionTemplatizedContent_EpicArcKillMission`, but bind them to native AIR simulation objective chains. They are Arc 132 missions (the IDs and dungeon mapping are also recorded in `docs/reference/compat/DEVIATIONS.md`, mission coverage section, lines 3698–3706). The client title and narrative are:

| Mission | Title | Dungeon | Narrative / objective class from client mission and objective data | Stated count/type/faction |
|---|---|---:|---|---|
| 17183 | Golgothan Fields | 14097 | Historic battle; Amarr simulation against Minmatar. Chain 136 has hostile fleet, flagship, and dreadnought repair objectives. | No numeric kill count or fixed NPC roster. Dungeon faction is Amarr (500003); text names Minmatar as the opponent. |
| 17184 | Caldari Prime Breakout | 14098 | Caldari breakout; rescue/protect civilians and destroy hostile ships. Chain 137 names enemy types 93944 and 93947 plus civilian type 93937. | No numeric kill count. Narrative places the conflict against the Gallente Federation. Dungeon faction is Caldari (500001). |
| 17185 | Intaki Liberation | 14099 | Gallente liberation; chain 138 destroys hostile ships and haulers and protects/attacks planetary-defense and titan objectives. | No numeric kill count or fixed NPC roster. Narrative names Caldari defenses. Dungeon faction is Gallente (500004). |
| 17186 | Battle of Pator | 14100 | Minmatar simulation against Imperial occupiers; chain 139 destroys fighters and a supercarrier and hacks planetary defenses. | No numeric kill count or fixed NPC roster. Dungeon faction is Minmatar (500002); narrative names Amarr. |

The numeric values above are type IDs only where the client objective graph explicitly supplies them; they are not a complete kill list. Objective groups such as `enemy_fleet` and `enemy_hauler` require runtime site content. Mission prose supplies historical context, not NPC wave composition or a count. The mission rows include objective-chain references 132–135 and graph references 1017–1020; dungeon objectives resolve to objective chains 136–139 and graphs 1026–1029. The client static records have no dungeon difficulty field for these four.

### What each source contains

- **Matching server SDE 3396210**: `/home/user/wt-pg/.cache/sde/eve-online-static-data-3396210-jsonl/_sde.jsonl` confirms build 3396210. Its `dungeons.jsonl` has a row for 14100 only: archetype 24, faction 500002, name `AIR Epic Arc - Minmatar`, allowed ship list 971. It has no rows for 14097–14099. Its NPC character/corporation and group data do not provide templates for the absent dungeon IDs.
- **Client static export 3396210**: `/home/user/eve-client-sde/native/_sde.jsonl.gz` identifies client build 3396210. `dungeons.jsonl.gz` has all four dungeon rows. Each has entry type 2894 and a faction/archetype/allowed-ship link; none contains difficulty or an NPC roster. `dungeonobjectives.jsonl.gz` maps them to objective IDs 87–90; `nodegraphs.jsonl.gz` supplies the objective chains described above. The linked connection object IDs 1490440, 1498715, 1498716, 1490443, 1498725, 1490450, and 1490453 did not resolve to site NPC records elsewhere in the scanned native tables.
- **eve-logic**: the V24 mission and dungeon contract carries mission/dungeon/objective identifiers and text, but does not supply the simulation room population. The client objective graph is useful for objective categories and the two Caldari type IDs; it is not a spawn manifest.
- **EveJS v0.12.9**: no matching mission template or dungeon rows were found for 17183–17186 / 14097–14100. It cannot establish their intended NPC site composition.
- **eve.rs**: `known-gaps.json` records all four; `mission-site-status.v1.json` marks them as unavailable. The mission catalog loader returns no template for absent rows. Existing source notes record 363 kill missions tagged from dungeon difficulty, while the generic arc switch only permits a generic site for arcs which have a generic-site mapping.

### Rule-based options and player-visible result

| Option | Player sees | Cost / consequence |
|---|---|---|
| Keep the four held (recommended) | They are not offered as executable kill missions; no misleading mission briefing/site mismatch. | Arc 132 remains incomplete in eve.rs until authoritative site data becomes available. |
| Enable `EVE_MISSION_INCLUDE_GENERIC_ARCS` for these missions | A generic kill site rather than the named simulation, with generic opponents/objectives. | Client-visible story and simulation objectives would disagree with the generated site. No new information is gained from the toggle because its generic mapping does not fill missing templates. |
| Add rule-derived generic NPC/site content from objective categories | A site could include generic hostile, hauler, fighter or defense objectives, using only types named in the chain where available. | Requires owner approval of a deliberately approximate simulation; count, waves, rooms, and unspecified enemy types remain unsupported. This is not a source-complete recreation. |
| Wait for a source-backed NPC manifest/template | The intended historical AIR simulation could be represented if a reliable manifest is obtained. | No such manifest appears in the reviewed server SDE, client static export, eve-logic, or EveJS. |

**Decision needed:** Should 17183–17186 remain held until a source-backed NPC manifest exists, or should the owner explicitly approve four generic, rule-derived simulations despite missing count/roster data? **Recommended default:** keep them held. The objective categories alone cannot define a complete kill site without inventing content.

## Q-03 — 1,100 untagged missions

The server SDE mission records do not carry a top-level mission level, `missionLevel`, or `agentLevel` for the 1,100 rows without tags. The existing regression assertions are in `dotnet/tests/Eve.Services.Core.Tests/MissionSdeDataTests.cs` (test covering the 1,100 rows); they also establish there is no non-arc untagged kill mission with a mapped dungeon difficulty. For these rows the SDE does not provide a defensible mission-level field.

| Kind | Untagged count | Why related metadata does not produce a level |
|---|---:|---|
| Courier | 311 | Agent level describes the agent/offering context and may vary; it is not a level stored on the mission record. |
| Kill | 187 | Most are epic-arc kills without mapped difficulty; only the already tagged subset has a rule-backed dungeon difficulty. |
| Mining | 0 | All 42 basic mining missions are already tagged. |
| Trade | 451 | Agent division and title identify a mission family, not a numeric level. |
| Other (talk, interaction, heraldry) | 151 | No mission-level field or deterministic numerical mapping. |

The untagged set totals 1,100. Agent division, mission-name conventions, rewards, and standing changes are not a defensible level formula: divisions classify work, titles are narrative/family labels, and reward/standing values do not have a stable published conversion to mission level. Epic-arc reward adjustments further make reward inversion unreliable.

**Recommendation:** leave these rows untagged. Add a level only when a row gains an authoritative level field or an independently documented rule that maps it without relying on title guesses, agent assignment, or reward inference.

## Q-04 — Public gateway: insurgency corruption/suppression (C-G1)

### Evidence trail and missing pieces

- Client requests exist in `eve/client/script/ui/services/insurgency/messenger.py` (`GetCorruption`, `GetSuppression`, stage-threshold reads, and notice subscriptions; lines 15–134). `eve-logic` also defines the public protobuf contract. Thus the client call and protobuf message definitions exist.
- `eve/client/script/ui/services/insurgency/clientDogmaLocation.py` lines 1506–1511 catches a client-side failure from the messenger path. The source identifies the failing call site; the reviewed files do not establish the exact runtime exception text or a successful end-to-end V24 call.
- A generic public gateway transport already exists at `dotnet/src/Eve.Host/PublicGateway/PublicGatewayServer.cs` (HTTP/2 and request-handler registry, lines 18–39 and 63 onward). The old TODO/DEVIATIONS statement that native transport is absent is stale. Insurgency-specific request handlers, a truthful bar read model, thresholds, and notice producers are not present as a complete feature.
- EveJS has `server/src/_secondary/express/gatewayServices/insurgencyGatewayService.js` lines 24–59 and 73 onward. It registers wire handlers but returns placeholder progress/stage/contribution and empty threshold arrays. Those values are not an authoritative behavior source.
- eve.rs has insurgency campaign state and scored-site-point ownership. `docs/project/TODO.md` lines 1500–1512 records remaining stage spread and contribution work. That state is not yet a client-facing progress/threshold read model.

### What can honestly be built

The client call, message contract, gateway transport, and campaign/site-point state exist. Missing pieces are the registered insurgency reads and notice subscriptions, rules-backed aggregation of campaign progress and per-stage contribution, and thresholds sourced from a verified game rule. No missing structure or protobuf service definition is established. A server can implement the handler/notification shape from the contract, but it must not return EveJS's zeros and empty lists as if they were real progress. The client-side failure also means a server-only change has not been shown to make these bars visible in the unmodified client.

**Decision needed:** Should this be reopened for implementation from authoritative campaign/site-point state after the client-side messenger failure and threshold source are resolved, or remain deferred? **Recommended default:** retain the deferment until the call path and threshold/progress rules can both produce truthful bars; then wire the existing transport to the existing campaign owner. The cheapest honest implementation is a read-only provider plus the contract-defined notices, with no invented progress values or empty “success” thresholds.

## Q-05 — Planetary Interaction survey data

### Evidence trail and missing pieces

- The V24 client calls `GetResourceData` in `eve/client/script/ui/shared/planet/planet.py` (also referenced in `docs/project/PI_DESIGN.md`, section D9); `GetPlanetResourceInfo` returns resource identifiers/values. The RPCs exist in `dotnet/src/Eve.Services.Planets/PlanetMgrService.cs` (`GetResourceData`, `GetPlanetResourceInfo`).
- `docs/project/PI_DESIGN.md` sections D1 and D9 record that the client’s native resource harmonic (`eve/common/lib/PlanetResources`, `_eveplanetresources`) is absent from the source snapshot. Client survey rendering decodes an `SH` buffer via `builder.CreateSHFromBuffer`; its binary format is not present in the reviewed source. The bundled native binary inventory also has no resource-index entry.
- The current project rule selects four PI resource types from category 42 groups 1032, 1033, and 1035 by planet ID. The static `planetResources` data is for sovereignty power/workforce, not PI deposits. The project can return resource IDs and rule-derived values, but cannot claim CCP deposit accuracy from those sources.
- There is a second client-side problem: `clientPlanet.CreateResourceInfo` unpacks `const.planetResourceProximityLimits[proximity]` as a two-value pair (client line 781), while `eve/common/lib/appConst.py` defines five scalar values for that constant (line 1580, values 1139–1143). With trained Remote Sensing and in-range survey the client fails before a server response can fix the path. `docs/project/PI_DESIGN.md` records this trace.
- EveJS has no planet service implementation in the v0.12.9 server source; it supplies no server behavior to copy for the survey.

### Feasible options

| Option | Player sees | Cost / consequence |
|---|---|---|
| Keep current rule-derived resource list and no survey band (recommended) | Resource identities/approximate project values; no fabricated harmonic band. | Honest limited behavior, but not CCP-like deposit distribution or a complete survey UI. |
| Build a project-defined harmonic and teach the client to render it | A usable approximate survey in a modified client. | Requires an explicit approximation decision and client support; it is not an unmodified-client fix and not the CCP resource model. |
| Reproduce CCP resource harmonics | CCP-like survey values and map display if exact native inputs/format are available. | Requires the missing native resource algorithm/data and its exact buffer contract, plus a fix or compatible client change for the proximity unpack error. |

**Decision needed:** Keep the survey limited to rule-derived values, or authorize a clearly approximate project survey/modified-client path? **Recommended default:** keep the limited honest response; do not synthesize an `SH` buffer. A complete CCP survey is blocked by missing native data/format, and the unmodified client path has an independent call-site error.

## Other blocked or explicitly deferred items in TODO.md / DEVIATIONS.md

This is a status scan of entries explicitly marked blocked or described as an owner-held refusal/deferment, not an inventory of every future task.

| Item | Current blocker / status |
|---|---|
| C4 command bursts and buff registry | DEVIATIONS lines 545–556: sim wire lacks fleet recipient lists and live numbers; the buff registry is not built and its SDE value chain resolves to zero. Requires one protocol batch and a rules-backed value before implementation. |
| E-07 weapon banks | TODO line 1191: held on RC-5 weapon-bank semantics; do not infer grouped activation behavior without that dependency. |
| Insurgency public gateway | TODO line 1506 and DEVIATIONS lines 2758–2761: old “transport absent” rationale is stale; feature still lacks insurgency handlers/read model and client path remains affected by messenger failure (see Q-04). |
| Mutual-war invites | TODO line 2147: explicit unavailable refusal remains until invite and blocked-state reads/writes are connected to an authoritative war owner; this is a deliberate stopgap, not a completed service. |
| PI resource survey | PI_DESIGN D9 and DEVIATIONS PI section: native resource harmonic data/format is absent, and the client proximity tuple unpack fails in the described trained/in-range path (see Q-05). |
| Skills injector correction | TODO lines 2125–2174: tier scale correction is present in the working source, while parent regression/acceptance remains pending; it is not marked complete. |

## Scope notes

This review is a static source and data comparison. It did not run the graphical client or exercise live gateway/PI flows. No claim is made that an implementation works end to end based on source presence alone.
