# ST-8b Host superweapons handoff

Status: Host research and contract only; implementation remains in progress. AoE and lightning secondary effects must stay fail-closed until the shared per-cycle permission action is merged, then receive a complete successful Host path. No .NET tests were run by this worker.

## Authority and publication

The authoritative local SDE snapshot is build 3396210 (client build 3396210). The data is stored separately in `types.published`, `typeDogma.dogmaEffects[].isDefault`, and `dogmaEffects.published`; module publication and effect publication are distinct facts.

Verified default-effect families:

| Default effect | Effect ID | Module type IDs sharing it | Module type publication | Effect publication |
|---|---:|---|---|---|
| `doomsdayHOG` | 6474 | 40634; 47068 | 40634=true; 47068=false | false |
| `doomsdayAOEECM` | 6513 | 27678, 41350, 47069 | 27678=true; 41350=false; 47069=true | false |
| `doomsdayAOEDamp` | 6481 | 40697, 47070 | both true | false |
| `doomsdayAOEWeb` | 6476 | 40635, 47071 | both true | false |
| `doomsdayAOEPaint` | 6478 | 40698, 47072 | both true | false |
| `doomsdayAOEBubble` | 6482 | 40696, 47073 | both true | false |
| `doomsdayAOETrack` | 6479 | 40699, 47074 | both true | false |
| `doomsdayAOENeut` | 6477 | 35944, 40636 | both true | false |
| `lightningWeapon` | 6447 | 35928 | true | true |

The SDE therefore supports a shared HOG engine body through published ship module 40634, while the Standup HOG type 47068 remains unavailable to ordinary clients. Burst types 47069–47074 are published even though their effects are not. Do not deny those burst types because of the effect publication flag. Type 41350 is an unpublished ECM test/special type and remains denied by type policy.

Existing `DogmaStatic.TypesInGroupsAsync` filters unpublished types, but explicit `EnsureTypesAsync` loads them and `TypeDef` currently drops the publication bit. The Host activation path consequently cannot enforce the SDE distinction at effect level yet. Planned minimal fix: preserve `types.published` on `TypeDef`; the structure HOG handler then returns `MODULE_NOT_PUBLISHED` for 47068 before any effect can be built.

## Existing Host and Sim behavior

- `ModulePackages.Of` resolves the selected activation effect from the fitted type's SDE effect IDs, checks the actual module online state and builds through the shared `ModuleEffects.Default` registry.
- Public ship HOG 40634 is already handled by `HogEffect`. Its SDE-derived `SimAction.Field` body uses a fixed `Moved`/`Held` participant list from the activation snapshot. Reuse this engine body for the HOG family, but do not use its activation-time participant list for structure success once per-pulse legality is required.
- Structure type 47068 currently routes to `StructureHogEffect`, which refuses by name. Replace that placeholder with the explicit unpublished-type refusal and the shared HOG body, retaining a per-cycle permission refusal for any future published structure HOG until the common hook is wired.
- Public Standup Vorton module 35928 is handled by the existing turret path, which uses SDE arc range/target-count attributes to build `SimTurret.Arc`. That does not authorize each secondary chain hop from the structure owner's corporation. Add a structure-only guard until the per-hop permission callback is available; preserve the existing ship path.
- Burst-projector Host handlers for the default effects above are not present in `ModuleEffects.Default`. Their actual Host effects must use the same per-cycle permission hook before the Sim applies an effect to any candidate.

## Required per-cycle permission contract

For every burst execution pulse and every ordered lightning chain hop, Sim must send fresh `candidateIds` correlated with `structureId`, `moduleId`, activation nonce, control-lease generation, and pulse/hop ordinal. Host resolves each candidate against the current structure owner's corporation and returns the correlated `allowedIds`; Sim applies the effect only to those allowed candidates. Repeat the check on every pulse/hop. A fixed activation-time list cannot protect against later entrants, changed standings/war state, stale retries, a released controller, or a new lease generation.

The same hook should cover the shared PointDefense/AoE action without changing its wire ownership. Missing, stale, duplicate, or mismatched permission replies must fail closed. The full nonce/lease/pulse correlation and target-order semantics are part of the action contract; Host must not synthesize an allowlist from activation-time candidates.

## Current policy versus legacy behavior

- CCP/SDE 3396210 is authoritative for module/effect publication. Do not override type publication because EveJS accepts a functional path.
- EveJS v0.12.9 provides a useful structure burst-projector per-pulse behavioral reference. Its runtime behavior does not change the current SDE publication policy.
- The public HOG type 40634 shares `doomsdayHOG` with Standup type 47068. Reusing the existing engine body is supported by the public family; exposing unpublished 47068 is not.
- A non-published effect flag alone does not make a published module unavailable. This is especially important for the Standup burst projectors and Point Defense.

## Remaining work

1. Preserve and enforce module publication for HOG 47068; add an SDE-backed refusal regression.
2. Implement the shared Host candidate-permission hook when the wire owner merges the action contract.
3. Build successful Standup burst effects from live fitted/online module Dogma values: effect duration attribute, capacitor discharge, area geometry, SDE repeat/deactivation/group rules, security restrictions, structure fit type, charge requirements where present, core and controller lease.
4. Re-evaluate current owner-corporation legality on each pulse; Sim must apply only the Host-returned allowed IDs.
5. Route structure lightning through the same hook for the primary target and each ordered secondary hop, preserving the SDE type list, range, maximum chain count, and per-hop falloff.
6. Re-run focused Space and Host tests serially under the parent task, then record those results separately from this worker's work. Real V24.01 client acceptance remains not confirmed.
