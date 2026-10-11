//! Host permissions are leased; all movement and combat eligibility stays in the scene.
use super::*;
use crate::link::TetherCandidate;
use crate::modules::{Action, Effect, WARP_SCRAMBLE_STATUS};

#[derive(Debug, Clone, Default)]
pub(super) struct TetherState {
    policy: Option<Policy>,
    structure_id: i64,
}

#[derive(Debug, Clone)]
struct Policy {
    blocked_until: i64,
    expires_at: i64,
    candidates: Vec<TetherCandidate>,
}

fn deadline(now: i64, milliseconds: u64) -> i64 {
    let ticks_per_ms = TICKS_PER_SECOND / 1_000;
    let duration = milliseconds.min((i64::MAX / ticks_per_ms) as u64) as i64 * ticks_per_ms;
    now.saturating_add(duration)
}

fn action_blocks_tether(action: &Action) -> bool {
    match action {
        Action::Cloak { .. } | Action::CynoField { .. } | Action::CommandBurst { .. }
        | Action::EcmBurst { .. } => true,
        Action::ScrambleStatus { delta } => *delta > 0.0,
        Action::Charged { action, fallback, .. } => {
            action_blocks_tether(action) || fallback.as_deref().is_some_and(action_blocks_tether)
        }
        _ => false,
    }
}

fn action_refuses_tether(action: &Action) -> bool {
    match action {
        Action::Smartbomb { .. } | Action::Bomb { .. } => true,
        Action::Charged { action, fallback, .. } => {
            action_refuses_tether(action) || fallback.as_deref().is_some_and(action_refuses_tether)
        }
        _ => false,
    }
}

impl Scene {
    pub(crate) fn set_tether_policy(
        &mut self,
        session: SessionId,
        blocked_for_ms: u64,
        lease_ms: u64,
        mut candidates: Vec<TetherCandidate>,
        now: i64,
    ) -> Vec<Out> {
        if !self.pilots.contains_key(&session) {
            return Vec::new();
        }
        self.settle_all(now);
        let unacknowledged_offense = blocked_for_ms == 0 && self.ongoing_offense(session);
        let pilot = self.pilots.get_mut(&session).expect("policy belongs to a pilot");
        if unacknowledged_offense || candidates.iter().any(|candidate| {
            candidate.structure_id <= 0 || !candidate.range.is_finite() || candidate.range <= 0.0
        }) {
            candidates.clear();
        }
        pilot.tether.policy = Some(Policy {
            blocked_until: deadline(now, blocked_for_ms),
            expires_at: deadline(now, lease_ms),
            candidates,
        });
        let (targeted, companions) = self.tether_blockers();
        let out = self.refresh_tether(session, now, &targeted, &companions);
        self.with_backlog(out)
    }

    fn ongoing_offense(&self, session: SessionId) -> bool {
        self.pilots.iter().any(|(actor, pilot)| {
            (*actor == session || self.controller_of_drone(*actor) == Some(session))
                && (pilot.modules.active_or_pending_effects().any(|effect| effect.offensive()) || self.drone_is_attacking(*actor))
        })
    }

    fn tether_blockers(&self) -> (BTreeSet<i64>, BTreeSet<SessionId>) {
        let mut targeted = BTreeSet::new();
        let mut companions = BTreeSet::new();
        for (session, pilot) in &self.pilots {
            targeted.extend(pilot.targeting.all_targets());
            if let Some(controller) = self.controller_of_drone(*session).filter(|id| *id != 0)
                && (self.is_fighter(*session) || self.drone_is_attacking(*session)
                    || pilot.modules.active_or_pending_effects().any(|effect| effect.offensive()))
            {
                companions.insert(controller);
            }
        }
        (targeted, companions)
    }

    pub(crate) fn tick_tethers(&mut self, now: i64) -> Vec<Out> {
        let sessions: Vec<_> = self.pilots.iter().filter_map(|(session, pilot)| {
            (pilot.tether.policy.is_some() || pilot.tether.structure_id != 0).then_some(*session)
        }).collect();
        if sessions.is_empty() {
            return Vec::new();
        }
        let (targeted, companions) = self.tether_blockers();
        let mut out = Vec::new();
        for session in sessions {
            out.extend(self.refresh_tether(session, now, &targeted, &companions));
        }
        out
    }

    fn local_tether_eligible(&self, session: SessionId, now: i64) -> bool {
        let Some(pilot) = self.pilots.get(&session) else { return false };
        pilot.flight.is_none()
            && pilot.jump.is_none()
            && pilot.protected_until <= now
            && pilot.disrupted_until <= now
            && matches!(pilot.cloak, crate::visibility::CloakState::Uncloaked | crate::visibility::CloakState::Decloaking)
            && pilot.targeting.is_empty()
            && !pilot.incoming.scrambled_with(self.scramble_status(session))
            && self.overlay(session, pilot.ship.spec.ship_id, WARP_SCRAMBLE_STATUS) <= 0.0
            && !pilot.modules.active_or_pending_effects().any(|effect| self.effect_breaks_tether(session, effect))
    }

    fn tether_surface_distance(&self, session: SessionId, structure_id: i64) -> Option<f64> {
        let pilot = self.pilots.get(&session)?;
        let body = &self.system.bodies[self.structure_index(structure_id)?];
        let radii = pilot.ship.spec.radius + body.radius;
        if !radii.is_finite() || pilot.ship.spec.radius < 0.0 || body.radius < 0.0 {
            return None;
        }
        if ![pilot.ship.position.x, pilot.ship.position.y, pilot.ship.position.z,
            body.position.x, body.position.y, body.position.z].iter().all(|value| value.is_finite()) {
            return None;
        }
        let apart = super::docking::surface_distance(&pilot.ship, body.position, body.radius);
        apart.is_finite().then_some(apart)
    }

    pub(crate) fn is_tethered_session(&self, session: SessionId, now: i64) -> bool {
        let Some(pilot) = self.pilots.get(&session) else { return false };
        let Some(policy) = pilot.tether.policy.as_ref() else { return false };
        let structure_id = pilot.tether.structure_id;
        structure_id != 0
            && now < policy.expires_at
            && now >= policy.blocked_until
            && self.local_tether_eligible(session, now)
            && !self.pilots.iter().any(|(companion, pilot)| {
                self.controller_of_drone(*companion) == Some(session)
                    && (self.is_fighter(*companion) || self.drone_is_attacking(*companion)
                        || pilot.modules.active_or_pending_effects().any(|effect| effect.offensive()))
            })
            && policy.candidates.iter().any(|candidate| {
                candidate.structure_id == structure_id
                    && self.tether_surface_distance(session, structure_id)
                        .is_some_and(|apart| apart <= candidate.range)
            })
    }

    pub(crate) fn is_tethered(&self, ship_id: i64, now: i64) -> bool {
        self.session_of(ship_id).is_some_and(|session| self.is_tethered_session(session, now))
    }

    fn refresh_tether(
        &mut self,
        session: SessionId,
        now: i64,
        targeted: &BTreeSet<i64>,
        companions: &BTreeSet<SessionId>,
    ) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        if pilot.tether.policy.as_ref().is_some_and(|policy| now >= policy.expires_at) {
            pilot.tether.policy = None;
        }
        let next = self.pilots.get(&session).and_then(|pilot| {
            let policy = pilot.tether.policy.as_ref()?;
            if now < policy.blocked_until
                || !self.local_tether_eligible(session, now)
                || targeted.contains(&pilot.ship.spec.ship_id)
                || companions.contains(&session)
            {
                return None;
            }
            policy.candidates.iter().filter_map(|candidate| {
                let apart = self.tether_surface_distance(session, candidate.structure_id)?;
                (apart <= candidate.range).then_some((apart, candidate.structure_id))
            }).min_by(|left, right| {
                left.0.total_cmp(&right.0).then_with(|| left.1.cmp(&right.1))
            }).map(|(_, structure_id)| structure_id)
        }).unwrap_or(0);
        self.change_tether(session, next, now)
    }

    fn change_tether(&mut self, session: SessionId, structure_id: i64, now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let previous_structure_id = pilot.tether.structure_id;
        if previous_structure_id == structure_id {
            return Vec::new();
        }
        pilot.tether.structure_id = structure_id;
        // CCP checks repairs only on engage; renewing permission must not refill a spent capacitor.
        let (mut out, repairing) = if structure_id == 0 {
            (Vec::new(), false)
        } else {
            self.repair_on_tether_engage(session, now)
        };
        out.push(Out::TetherChanged { session, previous_structure_id, structure_id, repairing });
        out
    }

    pub(crate) fn break_tether(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        self.change_tether(session, 0, now)
    }

    pub(crate) fn clear_tether_policy(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let out = self.break_tether(session, now);
        if let Some(pilot) = self.pilots.get_mut(&session) {
            pilot.tether.policy = None;
        }
        out
    }

    pub(super) fn break_structure_tethers(&mut self, structure_id: i64, now: i64) -> Vec<Out> {
        let sessions: Vec<_> = self.pilots.iter()
            .filter_map(|(session, pilot)| (pilot.tether.structure_id == structure_id).then_some(*session))
            .collect();
        sessions.into_iter().flat_map(|session| self.break_tether(session, now)).collect()
    }

    pub(crate) fn effect_breaks_tether(&self, session: SessionId, effect: &Effect) -> bool {
        effect.offensive()
            || effect.action.as_ref().is_some_and(action_blocks_tether)
            || self.pilots.get(&session).is_some_and(|pilot| {
                effect.on_changes.iter().any(|change| {
                    change.item_id == pilot.ship.spec.ship_id
                        && change.attribute_id == WARP_SCRAMBLE_STATUS && change.value > 0.0
                })
            })
    }

    pub(crate) fn tether_refuses_activation(&self, session: SessionId, effect: &Effect, now: i64) -> bool {
        self.is_tethered_session(session, now) && effect.action.as_ref().is_some_and(action_refuses_tether)
    }

    fn repair_on_tether_engage(&mut self, session: SessionId, now: i64) -> (Vec<Out>, bool) {
        let pilot = self.pilots.get_mut(&session).expect("tether belongs to a pilot");
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let mended = pilot.ship.health.restore(now);
        let (before, after) = pilot.modules.capacitor.as_mut().map_or((0.0, 0.0), |cap| cap.refill(now));
        let recharged = after > before + 1e-6;
        let mut heat_changes = Vec::new();
        for (module_id, runtime) in &mut pilot.heat.modules {
            let previous = runtime.profile.damage_hp;
            if previous > 0.0 {
                runtime.profile.damage_hp = 0.0;
                pilot.modules.repair_burnt_out(*module_id);
                heat_changes.push(crate::modules::attribute_change(
                    owner, *module_id, HEAT_ATTRIBUTE_ITEM_DAMAGE, now, real(0.0), real(previous),
                ));
            }
        }
        let repaired = mended.changed || recharged || !heat_changes.is_empty();
        let condition = Self::condition_of(pilot, now);
        let mut out: Vec<_> = Self::health_told(session, owner, ship_id, now, &mended.attribute_changes())
            .into_iter().collect();
        if recharged {
            out.push(Self::charge_told(session, owner, ship_id, now, after, before));
        }
        if !heat_changes.is_empty() {
            out.push(Out::Notify {
                session, name: "OnModuleAttributeChanges", id_type: "clientID",
                args: vec![Value::List(heat_changes)],
            });
        }
        if mended.changed && let Some(subject) = self.subject(ship_id, now) {
            out.extend(self.announce_damage_state(&subject, now));
        }
        if mended.changed || recharged {
            out.push(Out::Condition { session, command: 19, condition });
        }
        (out, repaired)
    }
}
