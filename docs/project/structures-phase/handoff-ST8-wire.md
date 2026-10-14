# ST-8 wire and identity handoff

## Scope and status

This handoff records the shared wire contract for per-step structure damage authorization and damageable-entity identity registration. The wire layer supports PointDefense, area pulses, and ordered chain hops with one correlated candidate exchange per application. The Rust Scene/Out damage loop and Host authorization/identity resolver are owned by Fermat and Pascal respectively; this handoff does not claim those runtime paths are complete.

The wire contract is protocol 69. It includes the earlier protocol-68 candidate messages, Kind87 `RemoveCynoBeacon`, the Kind84 control-lease generation, Kind88/89 entity lifecycle messages, and the optional Vorton arc damage-loss value. The origin `Fokiiiiiii/eve.rs` main `kind.rs` was checked at commit `06abc66021c46362501be928470014ce92db7f52` and ended at Kind84; the combined local allocation uses 85-89. Protocol68 is not a separately shipped intermediate version.

## PointDefense action

`SimAction.PointDefense` is tag 30. It carries `range_meters:f64` followed by exactly four `f64` channels in `[EM, thermal, kinetic, explosive]` order. Rust `Action::PointDefense.damage` is the matching four-channel `health::Damage` vector. The scalar `PointDefenseProjectile.damage` field is missile hull HP and is not a valid conversion source.

Matching EveJS v0.12.9 builds each channel from SDE charge damage attributes 114/118/117/116 times module damage multiplier 64 and charge rate 56. The pinned build-3396210 SDE `dogmaEffects.jsonl` row `_key=6443` is `pointDefense`, unpublished and distinct from Smartbomb. The action fixture uses wire sentinel values only; it does not define gameplay defaults.

Superweapon lightning chain does not extend `Action::Doomsday`: HOG type 40634 has no SDE attributes 2104-2106, so no chain parameters are present for it in build 3396210. Standup Vorton type 35928 / effect 6447 maps attributes 2104=5 and 2105=20000 to the existing `turret.arc`, and carries attribute 2106=0.85. `SimArc.DamageLossTarget` is nullable; the Arc payload appends a presence byte and optional f64 after `targets`. Ship Vortons without 2106 retain `None`. Rust applies the factor to each secondary hop as `DamageLossTarget^(hop_ordinal+1)` (zero-based hop ordinal).

## Per-pulse / per-hop authorization

Kind85 `StructureDamageCandidatesRequest` and Kind86 `StructureDamageCandidatesResult` are generic damage-step messages, not PointDefense-only messages. Every PointDefense cycle, successful area-effect pulse, and ordered chain hop requests fresh IDs from the current Sim scene and uses only a Host reply whose complete correlation tuple matches that step.

The body begins with this exact tuple, in this order:

1. `session_id:u64`
2. `structure_id:i64`
3. `module_id:i64`
4. `effect_id:i32`
5. `activation_nonce:u64`
6. `control_lease_generation:i64`
7. `cycle_id:u64`
8. `pulse_hop_ordinal:u32` (zero-based)

The request appends `candidate_count:u32` and `candidate_ids:i64[]`; the result echoes all eight fields and appends `allowed_count:u32` and `allowed_target_ids:i64[]`. Each list is at most 4096 positive unique IDs. Empty candidates or grants authorize no damage. Host results must be subsets of the exact candidate list; Sim rejects unknown, stale, replayed, duplicate, or mismatched results. The same `cycle_id` remains an additional request identifier while pulse/hop ordinal distinguishes each step.

EveJS `listDamageableEntitiesInRange` iterates every `scene.dynamicEntities` entry other than the source, keeps entities with positive shield, armor, or structure health within center-distance range, then applies local-interaction and Host aggression checks. Sim must preserve that full candidate set; it must not narrow the request to player ships or only IDs already classifiable by Host. Authorization must complete before ammo/charge consumption or damage commit. Host classifies from its current owner-corp context and validated known-NPC profiles; it never trusts an `isNpc` or owner claim in the candidate request.

Kind84 `ControlStructure` appends `control_lease_generation:i64` immediately after `active`. The value is the Host `StructurePilotRecord.Version` from observer acquire/restore. Sim must not create or reset a local generation. Host-owned callsites are `SpaceSessions.StructureControl.cs::SetStructureObserverAsync` and `TryRestoreStructureObserverAsync`.

Kind87 `RemoveCynoBeacon` is system-scoped: `solar_system_id:i32, structure_beacon_id:i64`. `SimPlaceCynoBeacon.Active=false` remains an inactive placement, not removal.

## Damageable-entity identity lifecycle

Kind88 `SimDamageableEntityUpserted` carries these fields in order: `solar_system_id:i32, entity_id:i64, type_id:i32, owner_id:i64, corporation_id:i64, alliance_id:i64, war_faction_id:i64`. Kind89 `SimDamageableEntityRemoved` carries `solar_system_id:i32, entity_id:i64`. C# record names are `SimDamageableEntityUpserted` and `SimDamageableEntityRemoved`; Rust link variants are `Message::DamageableEntityUpserted` and `Message::DamageableEntityRemoved`.

Emit an upsert only after successful insertion of a damageable entity and on authoritative identity updates, before that entity can appear in a damage candidate exchange. Emit removal for non-death despawn and system teardown. Existing `SimDestroyed` is the death cleanup path. Upsert is idempotent for register/update. These events are identity claims to validate, not authorization: Host checks the current SDE `TypeId` and damageable Dogma health, positive system/entity/type IDs, and the owner/corporation/alliance/faction tuple against the corresponding live player record or server-owned NPC creation profile. The wire can carry zero for an optional owner-affiliation field; zero alone does not make an entity known or authorize it.

The Host resolver must join existing authoritative sources on every candidate query: player/ejected hull records, active launched/abandoned drone and fighter records, `_npcShips`, and `StructureStore`/`StructureRecord`. The lifecycle registry covers Sim-created NPCs, missiles, and other generic damageable entities that have no matching Host-owned record. Existing Pochven spawn identity is source-backed by `NpcType.type_id` and `NpcType.corporation_id`, with faction derived from the known corporation map (`faction_of_corporation`); for example, Liminal Damavik type 52182 maps to Triglavian corporation 1000298. Host must verify these type/owner facts against the matching SDE/profile and the actual creation path.

The matching SDE is EVE client build 3396210. `types.jsonl`, `typeDogma.jsonl`, and `dogmaEffects.jsonl` were checked by `_key`. Type 40634 Gravitational Transportation Field Oscillator is published even though its default effect 6474 `doomsdayHOG` is unpublished; type 47068 is unpublished and must be rejected by `TypeDef.Published`. Public burst families 27678, 35944, 40635, 40636, 40696-40699 and Standup types 47069-47074 are type-published and must not be rejected solely because an effect is unpublished. Standup type 35928 is published with default effect 6447 `lightningWeapon`; its SDE attributes include duration 73=600000, attribute 1970=1, and channel attributes 114/116/117/118=1,000,000 each.

## Owner and remaining work

- **Peirce (wire):** Kind85/86 generic tuple and bounded unique ID lists; Kind84 generation field; Kind87 system-scoped removal; Kind88/89 identity lifecycle; Protocol 69; C#/Rust codecs, fixture generator/parsers, protocol fixtures, ADR 0013, and this handoff.
- **Fermat (Sim):** emit Kind88 after successful entity insertion/update and Kind89 on non-death removal/teardown; preserve complete fresh candidate scans for PointDefense, each area pulse, and each ordered chain hop; verify full correlation and subset; wait for authorization before ammo/charge/damage commit; finish real burst/chain bodies.
- **Pascal (Host):** synchronously apply upsert/remove in `SpaceSessions.OnReceived`, validate SDE health/type and owner tuple against authoritative creation sources, join the existing player/fighter/drone/NPC/structure records for each query, return only correlated allowed IDs, and remove destroyed entries via `SimDestroyed`.
- **Host observer acquire/restore:** populate Kind84 generation directly from `StructurePilotRecord.Version`.

Kind86 Field results use an optional version-1 tail after the legacy allowed IDs: `extension_version:u8`, then `field_moved_count:u32 + field_moved_target_ids:i64[]`, then `field_held_count:u32 + field_held_target_ids:i64[]`. The tail is omitted when both lists are empty, preserving ordinary result bytes. When present, the moved and held lists must be unique, disjoint, and together equal `allowed_target_ids`. The Host derives the partition from the same request's validated type map and SDE build 3396210 `typeDogma` attribute 1785.

World-backed ordinary ship Fields recompute current in-sphere pilot participants on every pulse from live ship type IDs and the SDE capital-size map; activation-time lists do not select runtime participants. Controlled-structure Fields send the current in-sphere damageable candidate set in Kind85 and apply only the correlated Kind86 partition after rechecking the current geometry through the shared transport-field engine. Kind85 and legacy Kind86 layouts remain unchanged; Protocol 69 and Hello negotiation do not change. Public ship HOG type 40634/effect 6474 follows this path. Standup HOG type 47068 remains refused by Host type-publication policy.

No .NET or Cargo tests were run while Host source was changing and parent builds/tests were paused. The fixture generator produced 196 valid and 81 invalid cases. Host/runtime integration and successful native-client acceptance remain separate verification work; the wire slice alone is not an end-to-end completion claim.
