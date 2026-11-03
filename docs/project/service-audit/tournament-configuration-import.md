# Tournament configuration import

When PostgreSQL is enabled, the host applies the `tournament` schema migration. An operator may set `EVE_TOURNAMENT_IMPORT` to the path of a UTF-8 JSON file containing an array of configurations and series. This is a host startup setting, not a client RPC. Each tournament ID is imported only if absent; restarting with the same file does not reset a persisted series. Invalid input fails startup without partially installing that tournament; earlier valid entries in the same file may already have been committed. Ship IDs must exist in the pinned SDE as category 6 (ships); nonzero series system IDs must exist in `mapSolarSystems`. Configuration is trusted operator input: the operator must choose the actual permitted ship types and rules for the event.

Example shape (the tournament and series identifiers and team labels are operator choices; 587 is the SDE Rifter):

```json
[
  {
    "configuration": {"id": 1, "name": "Practice", "maximumPoints": 100,
      "matchLength": 600, "overtimeLength": 300, "externalRulesetId": null,
      "allowedShipTypes": [587]},
    "series": [{"id": 0, "redTeam": "Red", "blueTeam": "Blue", "systemId": 0,
      "currentMatch": 0, "matchCount": 1}]
  }
]
```

`EVE_TOURNAMENT_ARENA_IMPORT` can point to a separate JSON array of arena definitions. Each entry has `solarSystemId`, an existing `ownerCharacterId`, and a nonempty `beacons` array. Each beacon has `typeId` (a pinned-SDE group 310/category 2 celestial beacon, for example 10645), `name`, `position` (three finite coordinates in metres) and `radius` (positive and representable as a nonzero finite f32). The owner, positions and radii are operator-provided; 10645 has no radius in the pinned type data. This ball radius affects surface distance and is distinct from the match boundary radius. Replace example owner/system values with actual local entities:

```json
[{"solarSystemId":30000142,"ownerCharacterId":90000001,"beacons":[
  {"typeId":10645,"name":"Arena Center","position":[1.0,2.0,3.0],"radius":5.0}
]}]
```

The arena import creates real inventory and deployed records together with the arena references and a transactional event. An identical import is idempotent; a changed definition for an existing system fails explicitly. The host lists systems backed by consistent inventory/deployed records and restores their beacon bodies on system entry. Match creation also checks the live simulation. The full tournament flow and graphical client remain incomplete or unverified.

`EVE_TOURNAMENT_RULES_IMPORT` may name a UTF-8 JSON array of rule objects. Each object has `tournamentId`, `hullPoints` (an array of `{ "typeId": <integer>, "points": <positive integer> }`), and `prohibitedFitTypes` (an array of SDE type IDs, which may be empty). Supply one point entry for every and only every ship in that tournament's `allowedShipTypes`. An identical retry is accepted; a changed existing definition fails. The operator must supply the chosen event's actual verified point values and fit restrictions; no tournament rules are inferred from SDE or included by default. Currently this stores the values; roster and fit enforcement are not yet complete.

`EVE_TOURNAMENT_REGION_IMPORT` may point to a UTF-8 JSON array such as:

```json
[{"solarSystemId":30000142,"centerBeaconOrdinal":0,"radiusMeters":125000}]
```

The ordinal is zero-based in the already imported arena's beacon array; the radius is a positive finite distance in metres. Choose a center and placements away from celestial bodies and other obstructions; the coordinates above are illustrative and are not a safe placement recommendation. Startup rejects unknown systems, invalid ordinals, unavailable deployed centers and invalid radii. Re-importing an identical region is allowed even during a match; a changed region is rejected while that system has an active match and otherwise replaces the previous region. Each region commits separately. The runtime validates the actual scene when moving players; an imported region alone does not prove a safe landing.

`EVE_TOURNAMENT_COMPLETION_POLICY_IMPORT` may point to a UTF-8 JSON array such as `[{"tournamentId":1,"policy":"alliance_tournament"}]`. The tournament must already exist. An absent entry or `manual` keeps referee-controlled completion; an identical import is accepted and a changed existing entry is rejected. A new policy cannot be added while a match for that tournament exists. Each entry commits separately. The `alliance_tournament` option is under development: starting a new countdown currently fails explicitly until the authoritative simulation cutoff registration is integrated. For matches already in progress under this policy, the deadline records a durable settlement-pending state; it does not yet settle a winner or apply overtime. The authoritative simulation cutoff and its recovery are required before automatic results can be completed.

Protocol evidence: V24.01 `eve-logic/eve/devtools/script/tournamentRefereeTools.py:537-540,550-599` consumes the setup, pairings, series positions and details; `eve/client/script/ui/extras/tourneyBanUI.py:42-49` consumes ship options as name/type-ID pairs. The [CCP Thunderdome Tool Guide](https://cdn1.eveonline.com/community/thunderdome/Thunderdome_Tool_Guide.pdf), pages 5–9, describes arena selection, referee membership, fleet consent, movement restrictions and cleanup. No graphical-client tournament run has been performed.
