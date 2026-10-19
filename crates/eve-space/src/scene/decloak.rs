//! A cloak that ends on the simulation's account: a ship or a body close by breaks it, and the pilot is told why (`OnCloakModuleForceDeactivated`, client
//! `cloakingSvc.py`; EVE.js `scene/cloaking.js` `notifyCloakModuleForceDeactivated` and `findCloakProximityTrigger`).

use super::*;
use crate::deployable::{Deployable, Role};
use crate::modules::Action;
use crate::wire::dict;

/// The distance, surface to surface, within which another object breaks a cloak (EVE.js `MIN_CLOAKING_DISTANCE_METERS`; the module attributes it reads first,
/// `uncloakRange`, `decloakRange` and `decloakFieldRange`, are in no module of the SDE).
pub const CLOAK_PROXIMITY_METERS: f64 = 2_000.0;

/// Observatory: only ships whose stabilization timer has run out are affected; EVE.js `DEFAULT_CLOAK_STABILIZATION_DURATION_MS`).
/// shortcut: every cloak gets the default fifteen minutes; EVE.js reads each module's stabilizeCloakDuration, which `Action::Cloak` does not carry yet.
pub const CLOAK_STABILIZATION_MS: i64 = 15 * 60 * 1000;

/// separately for each ship.
pub const OBSERVATORY_DECLOAK_PROBABILITY: f64 = 0.4;

/// The notice a Mobile Observatory's ping gives the pilot (EVE.js `processRemoteDecloakPing`).
const OBSERVATORY_NOTICE: &str = "Your cloak has been deactivated by a Mobile Observatory decloaking ping.";

/// The object that broke a cloak.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CloakTrigger {
    pub item_id: i64,
    pub type_id: i32,
    /// Metres from surface to surface.
    pub distance: f64,
}

/// Why a cloak was ended.
#[derive(Debug, Clone, PartialEq)]
pub enum DecloakCause {
    /// An object came within [`CLOAK_PROXIMITY_METERS`]: the client is told by the type of the object.
    Proximity(CloakTrigger),
    /// Any other cause: the client shows `notice`; `reason` names the cause (EVE.js `decloakReason`).
    Custom { reason: String, notice: String, trigger: CloakTrigger },
}

impl Scene {
    /// The nearest object within `metres` of the surface of this session's ship that breaks a cloak: another ship that is not in warp, or a body but a belt.
    pub(crate) fn cloak_trigger(&self, session: SessionId, metres: f64) -> Option<CloakTrigger> {
        let ship = &self.pilots.get(&session)?.ship;
        let (at, radius) = ([ship.position.x, ship.position.y, ship.position.z], ship.spec.radius);
        let ships = self
            .pilots
            .iter()
            .filter(|(other, pilot)| {
                **other != session && !pilot.flight.as_ref().is_some_and(|flight| flight.is_warping())
            })
            .filter_map(|(_, pilot)| {
                let other = &pilot.ship;
                let gap = crate::geo::surface_gap(
                    at,
                    radius,
                    [other.position.x, other.position.y, other.position.z],
                    other.spec.radius,
                )?;
                Some(CloakTrigger { item_id: other.spec.ship_id, type_id: other.spec.type_id, distance: gap })
            });
        let bodies =
            self.system().bodies.iter().filter(|body| body.kind != BodyKind::Belt).filter_map(|body| {
                let gap = crate::geo::surface_gap(
                    at,
                    radius,
                    [body.position.x, body.position.y, body.position.z],
                    body.radius,
                )?;
                Some(CloakTrigger { item_id: body.id, type_id: body.type_id, distance: gap })
            });
        ships
            .chain(bodies)
            .filter(|trigger| trigger.distance <= metres)
            .min_by(|a, b| a.distance.total_cmp(&b.distance))
    }

    /// A Mobile Observatory's ping (EVE.js `processRemoteDecloakPing`): each cloaked ship of this system whose cloak is no longer stabilized is decloaked with
    /// [`OBSERVATORY_DECLOAK_PROBABILITY`], by its own roll, and told the reason `mobileObservatory`. An observatory that is not anchored here does nothing.
    pub(crate) fn remote_decloak_ping(&mut self, observatory_id: i64, now: i64) -> Vec<Out> {
        let Some(Deployable { role: Role::MobileObservatory { active: true }, spec, .. }) =
            self.deployables.get(&observatory_id)
        else {
            return Vec::new();
        };
        let trigger = CloakTrigger { item_id: observatory_id, type_id: spec.charge_type_id, distance: 0.0 };
        let mut out = Vec::new();
        for session in self.in_entry_order() {
            let Some(pilot) = self.pilots.get(&session) else { continue };
            if pilot.cloak != crate::visibility::CloakState::Cloaked || pilot.cloak_stabilized_until > now {
                continue;
            }
            if self.rng.random() >= OBSERVATORY_DECLOAK_PROBABILITY {
                continue;
            }
            let cause = DecloakCause::Custom {
                reason: "mobileObservatory".to_string(),
                notice: OBSERVATORY_NOTICE.to_string(),
                trigger,
            };
            out.extend(self.force_decloak(session, cause, now));
        }
        out
    }

    /// The cloaked ships that have an object within [`CLOAK_PROXIMITY_METERS`] lose their cloak (a ship in warp keeps a covert one).
    pub(crate) fn decloak_by_proximity(&mut self, now: i64) -> Vec<Out> {
        let hit: Vec<(SessionId, CloakTrigger)> = self
            .in_entry_order()
            .into_iter()
            .filter(|session| {
                let pilot = &self.pilots[session];
                pilot.cloak == crate::visibility::CloakState::Cloaked
                    && !pilot.flight.as_ref().is_some_and(|flight| flight.is_warping())
            })
            .filter_map(|session| Some((session, self.cloak_trigger(session, CLOAK_PROXIMITY_METERS)?)))
            .collect();
        let mut out = Vec::new();
        for (session, trigger) in hit {
            out.extend(self.force_decloak(session, DecloakCause::Proximity(trigger), now));
        }
        out
    }

    /// Ends the cloaking module this session's ship runs and tells the pilot why. The proximity notice goes first, any other after the module has stopped (EVE.js
    /// `forceDecloakEntity`). Nothing happens when the ship is not cloaked by a module.
    pub fn force_decloak(&mut self, session: SessionId, cause: DecloakCause, now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        if pilot.cloak != crate::visibility::CloakState::Cloaked {
            return Vec::new();
        }
        let Some(module_id) = pilot
            .modules
            .running
            .iter()
            .find(|run| matches!(run.effect.action, Some(Action::Cloak { .. })))
            .map(|run| run.effect.module_id)
        else {
            return Vec::new();
        };
        let (first, notice) = match cause {
            DecloakCause::Proximity(trigger) => (
                true,
                Self::cloak_notice(
                    session,
                    "CloakDeactivatedByProximityToType",
                    [("proximityTypeID", int(i64::from(trigger.type_id)))],
                ),
            ),
            DecloakCause::Custom { reason, notice, trigger } => (
                false,
                Self::cloak_notice(
                    session,
                    "CustomNotify",
                    [
                        ("notify", text(notice)),
                        ("decloakReason", text(reason)),
                        ("itemID", id(trigger.item_id)),
                        ("distanceMeters", real((trigger.distance.max(0.0) * 1000.0).round() / 1000.0)),
                    ],
                ),
            ),
        };
        let ended = self.offline_module(session, module_id, now);
        if first { [vec![notice], ended].concat() } else { [ended, vec![notice]].concat() }
    }

    fn cloak_notice(
        session: SessionId,
        message: &str,
        args: impl IntoIterator<Item = (&'static str, Value)>,
    ) -> Out {
        Out::Notify {
            session,
            name: "OnCloakModuleForceDeactivated",
            id_type: "shipid",
            args: vec![text(message), dict(args)],
        }
    }
}
