//! The Standup burst projectors of a controlled structure (ST-8b). The structure's pilot runs the module. Each pulse first asks the Host which of the
//! ships in reach the structure's owner may affect (the per-pulse authorization of the point defense and the strikes, Kind 85 and Kind 86), and only
//! the ships the Host returns are reached. What a pulse lands is the projected effect of the family (`Remote`), with the ship effect's numbers.

use super::{Effect, PendingStructureDamageOperation, Remote};
use crate::ewar::{jam_chance, rolls_under};
use crate::scene::{Out, Scene, SessionId};

impl Scene {
    /// A structure burst's cycle starts: the candidates are every ship in its reach (hostile or not), and the Host's correlated reply reaches the ones it allows.
    pub(super) fn structure_burst_start(
        &mut self,
        session: SessionId,
        effect: &Effect,
        range: f64,
        duration_ms: f64,
        what: &Remote,
        now: i64,
    ) -> Vec<Out> {
        if !range.is_finite() || range <= 0.0 || !duration_ms.is_finite() || duration_ms <= 0.0 {
            return Vec::new();
        }
        let Some(structure_id) = self.burst_source_id(session) else { return Vec::new() };
        let Some(centre) = self.pilots.get(&session).map(|pilot| pilot.ship.position) else {
            return Vec::new();
        };
        let candidates: Vec<i64> = self
            .ships_in_area([centre.x, centre.y, centre.z], range, Some(structure_id), now)
            .into_iter()
            .map(|(_, ship_id)| ship_id)
            .collect();
        let operation = PendingStructureDamageOperation::StructureBurst {
            range_meters: range,
            duration_ms,
            what: what.clone(),
        };
        self.request_structure_damage_candidates(
            session,
            effect.module_id,
            effect.effect_id,
            0,
            candidates,
            operation,
            now,
        )
    }

    /// The Host has authorized this pulse's ships. Each one still in reach, not tethered (and not cloaked, for a jam) is reached by the family's effect:
    /// a jam rolls its sensors as the ECM burst does, a neutralizer takes capacitor, the others hold the ship for the duration as their remote effect does.
    #[allow(clippy::too_many_arguments)]
    pub(crate) fn apply_structure_burst(
        &mut self,
        session: SessionId,
        module_id: i64,
        module_type: i32,
        what: &Remote,
        duration_ms: f64,
        allowed: &[i64],
        now: i64,
    ) -> Vec<Out> {
        let Some(structure_id) = self.burst_source_id(session) else { return Vec::new() };
        let owner = self.combat_owner(session);
        let reached: Vec<SessionId> = self
            .in_entry_order()
            .into_iter()
            .filter(|target| *target != session)
            .filter(|target| {
                self.pilots.get(target).is_some_and(|pilot| allowed.contains(&pilot.ship.spec.ship_id))
            })
            .filter(|target| !self.is_tethered_session(*target, now))
            .collect();
        let mut out = Vec::new();
        if let Remote::Jam { strength } = what {
            let mut told = Vec::with_capacity(reached.len());
            for target in reached {
                let Some((ship_id, type_id, chance, cloaked)) = self.pilots.get(&target).map(|pilot| {
                    (
                        pilot.ship.spec.ship_id,
                        pilot.ship.spec.type_id,
                        jam_chance(
                            strength,
                            &pilot.ship.spec.sensor_strength,
                            pilot.ship.spec.ecm_resistance,
                            1.0,
                        ),
                        pilot.cloak == crate::visibility::CloakState::Cloaked,
                    )
                }) else {
                    continue;
                };
                if cloaked {
                    continue;
                }
                self.aggressions.push(crate::weapons::Aggression {
                    attacker_ship: structure_id,
                    target_ship: ship_id,
                    module_type_id: module_type,
                    source_owner_id: owner,
                    target_type_id: type_id,
                });
                let jammed = rolls_under(self.rng.random(), chance);
                if jammed {
                    out.extend(self.break_locks_except(target, &[], now));
                }
                told.push((ship_id, jammed));
            }
            if !told.is_empty() {
                out.push(Out::EcmBurst { session, ship_id: structure_id, reached: told });
            }
            return out;
        }
        for target in reached {
            let Some((ship_id, type_id, signature)) = self.pilots.get(&target).map(|pilot| {
                (pilot.ship.spec.ship_id, pilot.ship.spec.type_id, pilot.ship.spec.signature_radius)
            }) else {
                continue;
            };
            if what.hostile() {
                self.aggressions.push(crate::weapons::Aggression {
                    attacker_ship: structure_id,
                    target_ship: ship_id,
                    module_type_id: module_type,
                    source_owner_id: owner,
                    target_type_id: type_id,
                });
            }
            match what {
                Remote::Neutralize { amount, signature_resolution } => {
                    // a neutralizer built for bigger ships is weaker against a ship smaller than its resolution (as the projected neutralizer is)
                    let resolved = if *signature_resolution > 0.0 && signature > 0.0 {
                        (signature / signature_resolution).min(1.0)
                    } else {
                        1.0
                    };
                    let strength = amount * resolved * self.capacitor_warfare_multiplier(target);
                    out.extend(self.take_capacitor(target, strength, now).0);
                }
                other => {
                    if let Some(lasting) = other.lasting(1.0) {
                        out.extend(self.hold_for(session, target, module_id, lasting, duration_ms, now));
                    }
                }
            }
        }
        out
    }
}
