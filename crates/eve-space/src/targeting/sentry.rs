//! The POS battery sentry (protocol 106). A battery bound to a tower whose policy is online, and that no pilot holds, fires on its own at the
//! pilots the policy names. The Host gives the policy (`SENTRY_POLICY`), the bindings (`SENTRY_BATTERY`) and what it knows of each pilot toward
//! each tower (`SENTRY_PILOT`). The scene chooses, locks and fires with the battery's own rules: a lock is `battery_lock` (range, visibility,
//! lock limit, scan time) and a fire is `apply_fire`, the rule a holder's fire order uses. A sentry has no holder, so nothing is told.
//!
//! The policy is the stored sentry settings (moonmining.py:1128-1137). The rules follow `evaluateSentryTargetPolicy` in EveJS
//! starbaseDefenseController.js: the owner corporation and its alliance are never named; war is checked first and applies in high security;
//! the standing and security-status thresholds apply only outside high security. Not in this slice: the status-drop receipt (no Host source
//! yet) and NPC ships (they have no pilot facts, so they are not chosen). A pilot with no facts is not a target. An offline tower or battery
//! lets go of the sentry's locks. A battery with a holder is never touched here.
use super::battery::apply_fire;
use super::*;

/// A tower's stored sentry policy as the scene needs it. `online` is false when the tower is offline or out of fuel.
#[derive(Debug, Clone, PartialEq)]
pub(crate) struct SentryPolicy {
    pub(crate) tower_id: i64,
    pub(crate) online: bool,
    pub(crate) standing: Option<f64>,
    pub(crate) security_status: Option<f64>,
    pub(crate) war: bool,
    pub(crate) high_security: bool,
    pub(crate) owner_corporation_id: i64,
    pub(crate) owner_alliance_id: i64,
}

/// What the Host knows of one pilot toward one tower: its corporation and alliance, its security status (`None` for a corporation-only
/// identity), the tower owner's standing toward it (0 with no contact) and whether the owner is at war with it.
#[derive(Debug, Clone, PartialEq)]
pub(crate) struct SentryPilot {
    pub(crate) ship_id: i64,
    pub(crate) corporation_id: i64,
    pub(crate) alliance_id: i64,
    pub(crate) security_status: Option<f64>,
    pub(crate) standing: f64,
    pub(crate) at_war: bool,
}

/// Whether `policy` names `pilot`: the rule of `evaluateSentryTargetPolicy`, without the position and visibility checks the scene makes.
fn names(policy: &SentryPolicy, pilot: &SentryPilot) -> bool {
    let own = pilot.corporation_id == policy.owner_corporation_id
        || (policy.owner_alliance_id != 0 && pilot.alliance_id == policy.owner_alliance_id);
    if own {
        return false;
    }
    if policy.war && pilot.at_war {
        return true;
    }
    if policy.high_security {
        return false;
    }
    if policy.standing.is_some_and(|threshold| pilot.standing < threshold) {
        return true;
    }
    matches!((policy.security_status, pilot.security_status), (Some(threshold), Some(value)) if value < threshold)
}

impl Scene {
    /// Stores a tower's policy. A policy that is not online lets go of its batteries' sentry locks at the next pass.
    pub(crate) fn set_sentry_policy(&mut self, policy: SentryPolicy) {
        self.sentry_policies.insert(policy.tower_id, policy);
    }

    /// Binds a battery to a tower's policy; `tower_id` 0 unbinds it. A battery that is not in the scene is not bound.
    pub(crate) fn bind_sentry_battery(&mut self, battery_id: i64, tower_id: i64) {
        let Some(battery) = self.batteries.get_mut(&battery_id) else {
            return;
        };
        battery.sentry_tower = (tower_id != 0).then_some(tower_id);
        if tower_id == 0 {
            // An unbound battery is no longer visited by the pass, so its sentry locks are let go here.
            self.sentry_let_go(battery_id);
        }
    }

    /// Records what the Host knows of a pilot toward a tower. `None` forgets every pilot the tower had.
    pub(crate) fn set_sentry_pilot(&mut self, tower_id: i64, pilot: Option<SentryPilot>) {
        match pilot {
            Some(pilot) => {
                self.sentry_pilots.insert((tower_id, pilot.ship_id), pilot);
            }
            None => self.sentry_pilots.retain(|(tower, _), _| *tower != tower_id),
        }
    }

    /// The pilots a battery's policy names that it could lock now, nearest first (ties by ship id).
    fn sentry_candidates(&self, battery_id: i64, policy: &SentryPolicy, now: i64) -> Vec<i64> {
        let Some(battery) = self.batteries.get(&battery_id) else {
            return Vec::new();
        };
        let mut named: Vec<(f64, i64)> = self
            .sentry_pilots
            .range((policy.tower_id, i64::MIN)..=(policy.tower_id, i64::MAX))
            .filter(|(_, pilot)| names(policy, pilot))
            .filter_map(|(_, pilot)| {
                let subject = self.subject(pilot.ship_id, now)?;
                self.battery_check(battery, &subject).ok()?;
                Some((distance(battery.position, subject.position), pilot.ship_id))
            })
            .collect();
        named.sort_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1)));
        named.into_iter().map(|(_, ship)| ship).collect()
    }

    /// One sentry pass at `now`, from `settle_batteries`. A battery with no holder, online and bound to an online tower's policy drops the locks
    /// the policy no longer names, locks the nearest named pilot when it holds none, and fires at its lock through `apply_fire`. Any other sentry
    /// battery lets go of its sentry locks. A battery with a holder is skipped.
    pub(crate) fn sentry_pass(&mut self, now: i64) {
        let bound: Vec<(i64, i64)> = self
            .batteries
            .iter()
            .filter_map(|(id, battery)| battery.sentry_tower.map(|tower| (*id, tower)))
            .collect();
        for (battery_id, tower_id) in bound {
            let Some(battery) = self.batteries.get(&battery_id) else {
                continue;
            };
            if battery.holder.is_some() {
                continue;
            }
            let online = battery.online;
            let Some(policy) = self.sentry_policies.get(&tower_id).filter(|policy| policy.online).cloned()
            else {
                self.sentry_let_go(battery_id);
                continue;
            };
            if !online {
                self.sentry_let_go(battery_id);
                continue;
            }
            let named = self.sentry_candidates(battery_id, &policy, now);
            let held: Vec<i64> =
                battery.targeting.pending_targets().into_iter().chain(battery.targeting.targets()).collect();
            for target in held.into_iter().filter(|target| !named.contains(target)) {
                self.sentry_drop(battery_id, target);
            }
            let idle = self.batteries.get(&battery_id).is_some_and(|battery| battery.targeting.is_empty());
            if idle {
                for ship in named {
                    if self.battery_lock(battery_id, ship, now).is_ok() {
                        break;
                    }
                }
            }
            if let Some(battery) = self.batteries.get_mut(&battery_id)
                && battery.firing.is_none()
                && let Some(target) = battery.targeting.targets().first().copied()
            {
                apply_fire(battery, BatteryFire::Start(target));
            }
        }
    }

    /// Lets go of a sentry battery's locks and its fire, silently. A battery with a holder is left alone.
    fn sentry_let_go(&mut self, battery_id: i64) {
        if let Some(battery) = self.batteries.get_mut(&battery_id)
            && battery.holder.is_none()
        {
            battery.targeting = Targeting::default();
            battery.firing = None;
        }
    }

    /// Drops one target from a sentry battery, locked or pending, and its fire when the fire is at that target.
    fn sentry_drop(&mut self, battery_id: i64, target: i64) {
        if let Some(battery) = self.batteries.get_mut(&battery_id) {
            battery.targeting.pending.retain(|p| p.target != target);
            battery.targeting.locked.retain(|l| l.target != target);
            if battery.firing == Some(target) {
                battery.firing = None;
            }
        }
    }
}
