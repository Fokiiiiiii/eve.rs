//! The ships of a scene as each other's audience: who is told when a ship comes, goes or is given an order, and who sees whom.
//!
//! A ship is in the ballpark of another when the rule of [`crate::visibility`] says so. Every pilot that has bound the ballpark keeps the set of
//! ships it has been told of (`sees`); what happens to a ship is told to the pilots whose set holds it, and the sets are brought into line with the
//! rule when a ship comes, goes or is in warp (`reconcile`). A pilot that has not bound the ballpark is told nothing (the client cannot take a
//! destiny update yet); it gets everything in view when it binds.
mod asteroids;
mod ships;
mod wrecks;

use std::collections::BTreeSet;

use eve_marshal::Value;

use crate::payload;

use crate::scene::{Out, Scene, SessionId};

use crate::ship::Ship;

use crate::visibility::{CloakState, Presence, can_see, can_see_subject, grid_of};

use crate::wire::{id, real};

use crate::wreck::Wreck;

use eve_destiny::Vec3;

/// The nearest to the ship a jettisoned can is put (the reference's `buildNearbySpawnState`).
const MIN_JETTISON_DISTANCE: f64 = 50.0;

impl Scene {
    /// Sets what a pilot has in view when it binds the ballpark: every ship the rule lets it see.
    pub(crate) fn take_in_view(&mut self, session: SessionId) {
        let order = self.in_entry_order();
        let seen: BTreeSet<SessionId> =
            order.into_iter().filter(|s| *s != session && self.visible_to(session, *s)).collect();
        let wrecks: BTreeSet<i64> =
            self.wrecks.values().filter(|w| self.sees_place_of(session, w)).map(|w| w.id).collect();
        if let Some(pilot) = self.pilots.get_mut(&session) {
            pilot.bound = true;
            pilot.sees = seen;
            pilot.sees_wrecks = wrecks;
        }
    }

    /// The numbers the bind sends for a ship that is not the pilot's own: agility, mass and top speed.
    pub(crate) fn numbers_of(ship: &Ship, stamp: u32) -> Vec<Value> {
        let ship_id = id(ship.spec.ship_id);
        vec![
            payload::action(stamp, "SetBallAgility", [ship_id.clone(), real(ship.spec.agility)]),
            payload::action(stamp, "SetBallMass", [ship_id.clone(), real(ship.spec.mass)]),
            payload::action(stamp, "SetMaxSpeed", [ship_id, real(ship.spec.max_velocity)]),
        ]
    }
}

#[cfg(test)]
mod tests;
