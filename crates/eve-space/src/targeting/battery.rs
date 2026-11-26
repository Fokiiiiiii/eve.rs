//! POS batteries (protocol 102). A battery is a starbase module that locks targets on its own: its `Targeting` is its own, not a pilot ship's,
//! so the ship paths in `mod.rs` and `lock.rs` never see it. The holder is the pilot whose control grant the Host checked; only that pilot is
//! told (`OnTargetOBO`, as the reference's `notifyStarbaseTargetEvent` does), and no other pilot receives the battery's target ids.
//!
//! The lock uses the ship's rules where they apply to a battery: the target must be selectable, not untargetable, not warping and within the
//! battery's range, the holder's battery holds at most its limit of targets, and the lock takes `lock_duration_ms` of the battery's scan
//! resolution against the target's signature. A battery has no undocking protection, no cloak and no jamming of its own; its range is what the
//! Host gives it (no ship range cap is applied).
use super::*;

use crate::link::BatteryPlacement;
use crate::visibility::{CloakState, Presence};
use eve_marshal::Value;

/// A POS battery in a scene.
#[derive(Debug, Clone)]
pub(crate) struct Battery {
    pub(super) id: i64,
    pub(super) position: Vec3,
    pub(super) radius: f64,
    pub(super) scan_resolution: f64,
    pub(super) max_target_range: f64,
    pub(super) max_locked_targets: i32,
    pub(super) online: bool,
    pub(super) holder: Option<SessionId>,
    pub(super) targeting: Targeting,
    /// The target the battery fires at (protocol 104); always one of its locked targets.
    pub(super) firing: Option<i64>,
    /// The tower whose sentry policy drives the battery while no pilot holds it (protocol 106); `None` when it is not a sentry.
    pub(super) sentry_tower: Option<i64>,
}

/// What the holder asks of a battery's targets.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum BatteryOrder {
    /// Lock the target (a pending lock if it takes time).
    Lock(i64),
    /// Give up a lock that is going.
    Cancel(i64),
    /// Let go of one target, locked or pending.
    Remove(i64),
    /// Let go of every target.
    Clear,
}

/// The answer to an order: why it was refused (`None` when accepted), whether the lock asked for is still going, and what the battery holds.
#[derive(Debug, Clone, PartialEq)]
pub(crate) struct BatteryAnswer {
    pub(crate) reason: Option<&'static str>,
    pub(crate) pending: bool,
    pub(crate) targets: Vec<i64>,
}

/// `OnTargetOBO(what, battery, target, reason)`, told to the holder only. `what` is `add`, `lost` or `clear`; a `clear` has no target.
fn obo(
    holder: SessionId,
    what: &'static str,
    battery_id: i64,
    target: Option<i64>,
    reason: Option<&'static str>,
) -> Out {
    Out::Notify {
        session: holder,
        name: "OnTargetOBO",
        id_type: "charid",
        args: vec![
            word(what),
            id(battery_id),
            target.map_or(Value::None, id),
            reason.map_or(Value::None, text),
        ],
    }
}

impl Battery {
    fn new(id: i64) -> Self {
        Self {
            id,
            position: Vec3::ZERO,
            radius: 0.0,
            scan_resolution: 0.0,
            max_target_range: 0.0,
            max_locked_targets: 0,
            online: false,
            holder: None,
            targeting: Targeting::default(),
            firing: None,
            sentry_tower: None,
        }
    }

    /// Takes the battery offline outside an order: every lock, held or pending, is let go, and the holder that had them is told once with
    /// `OnTargetOBO clear` (the client drops all its targets). Nothing is told when nothing was held.
    fn go_offline(&mut self) -> Vec<Out> {
        let held = !self.targeting.is_empty();
        let holder = self.holder;
        self.targeting = Targeting::default();
        self.firing = None;
        match holder {
            Some(holder) if held => vec![obo(holder, "clear", self.id, None, None)],
            _ => Vec::new(),
        }
    }

    /// Lets go of every lock, telling the holder that had them, and returns what is told.
    fn release_all(&mut self, reason: &'static str) -> Vec<Out> {
        let Some(holder) = self.holder else {
            self.targeting = Targeting::default();
            self.firing = None;
            return Vec::new();
        };
        let out: Vec<Out> = self
            .targeting
            .pending_targets()
            .into_iter()
            .chain(self.targeting.targets())
            .map(|target| obo(holder, "lost", self.id, Some(target), Some(reason)))
            .collect();
        self.targeting = Targeting::default();
        self.firing = None;
        out
    }
}

impl Refusal {
    fn battery_refused(reason: &'static str) -> Self {
        Self::of(reason)
    }
}

impl BatteryAnswer {
    fn refused(reason: &'static str, targets: Vec<i64>) -> Self {
        Self { reason: Some(reason), pending: false, targets }
    }
}

/// What a holder asks of a battery's fire (protocol 104): start firing at a target the battery holds, or stop.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum BatteryFire {
    Start(i64),
    Stop,
}

/// The answer to a fire order: `reason` is `None` when accepted, `active` says the battery fires, and `target` is what it fires at (0 for none).
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct BatteryFireAnswer {
    pub(crate) reason: Option<&'static str>,
    pub(crate) active: bool,
    pub(crate) target: i64,
}

impl BatteryFireAnswer {
    fn refused(reason: &'static str) -> Self {
        Self { reason: Some(reason), active: false, target: 0 }
    }
}

/// The fire rule of protocol 104, the one a holder's order and a sentry's pass both use: a start needs a target the battery holds and no other
/// fire; a stop is always accepted. Returns the refusal, or `None` when the order is accepted.
pub(super) fn apply_fire(battery: &mut Battery, order: BatteryFire) -> Option<&'static str> {
    match order {
        BatteryFire::Start(target) => {
            if battery.firing == Some(target) {
                None
            } else if battery.firing.is_some() {
                Some("MODULE_ALREADY_ACTIVE")
            } else if !battery.targeting.locked.iter().any(|l| l.target == target) {
                Some("TARGET_NOT_LOCKED")
            } else {
                battery.firing = Some(target);
                None
            }
        }
        BatteryFire::Stop => {
            battery.firing = None;
            None
        }
    }
}

impl Scene {
    /// Places a battery, or updates it (protocol 102). Going offline outside an order (its fuel ran out, its tower went offline or it was
    /// unanchored) lets go of every lock and tells the holder `clear`, once and only when it held one. A change of holder releases the locks with
    /// `controlChanged`, each told to the holder that had them. The reply is `Err` with the reason when the numbers are not
    /// usable; nothing is changed then.
    pub(crate) fn place_battery(&mut self, placement: BatteryPlacement) -> Result<Vec<Out>, &'static str> {
        let usable = |value: f64| value.is_finite();
        if placement.battery_id <= 0
            || !placement.position.iter().all(|value| usable(*value))
            || !usable(placement.radius)
            || placement.radius <= 0.0
            || !usable(placement.scan_resolution)
            || placement.scan_resolution < 0.0
            || !usable(placement.max_target_range)
            || placement.max_target_range < 0.0
            || placement.max_locked_targets < 0
        {
            return Err("BAD_BATTERY");
        }
        let battery_id = placement.battery_id;
        let holder = (placement.holder_session != 0).then_some(placement.holder_session);
        let mut battery = self.batteries.remove(&battery_id).unwrap_or_else(|| Battery::new(battery_id));
        let out = if !placement.online {
            battery.go_offline()
        } else if battery.holder != holder {
            battery.release_all("controlChanged")
        } else {
            Vec::new()
        };
        battery.position = Vec3::new(placement.position[0], placement.position[1], placement.position[2]);
        battery.radius = placement.radius;
        battery.scan_resolution = placement.scan_resolution;
        battery.max_target_range = placement.max_target_range;
        battery.max_locked_targets = placement.max_locked_targets;
        battery.online = placement.online;
        battery.holder = holder;
        self.batteries.insert(battery_id, battery);
        Ok(out)
    }

    /// An order for a battery's targets, after what is due at `now` has settled. The answer is returned with what the holder is told.
    pub(crate) fn battery_order(
        &mut self,
        battery_id: i64,
        order: BatteryOrder,
        now: i64,
    ) -> (BatteryAnswer, Vec<Out>) {
        let Some(battery) = self.batteries.get(&battery_id) else {
            return (BatteryAnswer::refused("BATTERY_NOT_IN_SPACE", Vec::new()), Vec::new());
        };
        if !battery.online {
            return (BatteryAnswer::refused("BATTERY_OFFLINE", battery.targeting.targets()), Vec::new());
        }
        let Some(holder) = battery.holder else {
            return (BatteryAnswer::refused("NO_CONTROL_GRANT", battery.targeting.targets()), Vec::new());
        };
        let mut out = self.settle_battery(battery_id, now);
        let mut refusal = None;
        match order {
            BatteryOrder::Lock(target) => match self.battery_lock(battery_id, target, now) {
                Ok(told) => out.extend(told),
                Err(found) => refusal = Some(found.reason),
            },
            BatteryOrder::Cancel(target) => {
                out.extend(self.battery_release(battery_id, Some(holder), target, false, None))
            }
            BatteryOrder::Remove(target) => {
                out.extend(self.battery_release(battery_id, Some(holder), target, true, None))
            }
            BatteryOrder::Clear => out.extend(self.battery_clear(battery_id, holder)),
        }
        let Some(battery) = self.batteries.get(&battery_id) else {
            return (BatteryAnswer::refused("BATTERY_NOT_IN_SPACE", Vec::new()), out);
        };
        let pending = match order {
            BatteryOrder::Lock(target) => battery.targeting.pending_targets().contains(&target),
            _ => false,
        };
        (BatteryAnswer { reason: refusal, pending, targets: battery.targeting.targets() }, out)
    }

    /// A fire order for a battery (protocol 104). Starting needs the battery online, held, and a target it holds (the lock is the only way a target
    /// gets there); a stop is always accepted. Firing ends with its lock, when the holder changes, and when the battery goes offline. No damage,
    /// cycle, capacitor or charge is run here: the answer says only whether the battery is set to fire, at what.
    pub(crate) fn battery_fire(
        &mut self,
        battery_id: i64,
        order: BatteryFire,
        now: i64,
    ) -> (BatteryFireAnswer, Vec<Out>) {
        let Some(battery) = self.batteries.get(&battery_id) else {
            return (BatteryFireAnswer::refused("BATTERY_NOT_IN_SPACE"), Vec::new());
        };
        if !battery.online {
            return (BatteryFireAnswer::refused("BATTERY_OFFLINE"), Vec::new());
        }
        if battery.holder.is_none() {
            return (BatteryFireAnswer::refused("NO_CONTROL_GRANT"), Vec::new());
        }
        let out = self.settle_battery(battery_id, now);
        let Some(battery) = self.batteries.get_mut(&battery_id) else {
            return (BatteryFireAnswer::refused("BATTERY_NOT_IN_SPACE"), out);
        };
        let refusal = apply_fire(battery, order);
        (
            BatteryFireAnswer {
                reason: refusal,
                active: battery.firing.is_some(),
                target: battery.firing.unwrap_or(0),
            },
            out,
        )
    }

    /// The checks a battery's lock (or a lock it still holds) must pass; the same for both, so a lock that stops being valid is dropped by
    /// the same rule that refused it.
    pub(super) fn battery_check(&self, battery: &Battery, subject: &Subject) -> Result<(), Refusal> {
        if subject.id == battery.id {
            return Err(Refusal::battery_refused("TARGET_SELF"));
        }
        if subject.protected {
            return Err(Refusal::battery_refused("TARGET_INVULNERABLE"));
        }
        if UNTARGETABLE_GROUPS.contains(&subject.group_id) {
            return Err(Refusal::of_type("TARGET_NOT_TARGETABLE", subject.type_id));
        }
        if subject.warping {
            return Err(Refusal::of_type("TARGET_WARPING", subject.type_id));
        }
        if !subject.static_body {
            // a battery has no cloak: it selects only what an uncloaked observer can select
            let target_cloak = self
                .pilots
                .values()
                .find(|pilot| pilot.ship.spec.ship_id == subject.id)
                .map_or(CloakState::Uncloaked, |pilot| pilot.cloak);
            let observer = Presence { position: battery.position, warping: false };
            let target = Presence { position: subject.position, warping: false };
            if !crate::visibility::can_select_target(observer, target, CloakState::Uncloaked, target_cloak) {
                return Err(Refusal::battery_refused("TARGET_NOT_FOUND"));
            }
        }
        let apart = super::lock::gap_or_infinity(
            distance(battery.position, subject.position) - battery.radius - subject.radius,
        );
        if battery.max_target_range <= 0.0 || !(apart < battery.max_target_range) {
            return Err(Refusal {
                reason: "TARGET_OUT_OF_RANGE",
                target_type: subject.type_id,
                desired_range: battery.max_target_range.max(0.0),
                actual_distance: apart,
            });
        }
        Ok(())
    }

    /// Starts a battery's lock on `target`: an immediate lock when the scan takes no time, else a pending one that `settle_battery` completes.
    /// `Ok` carries what is told now (the finished lock's `add`, when it is immediate).
    pub(super) fn battery_lock(
        &mut self,
        battery_id: i64,
        target: i64,
        now: i64,
    ) -> Result<Vec<Out>, Refusal> {
        let battery =
            self.batteries.get(&battery_id).ok_or(Refusal::battery_refused("BATTERY_NOT_IN_SPACE"))?;
        if battery.targeting.locked.iter().any(|l| l.target == target)
            || battery.targeting.pending.iter().any(|p| p.target == target)
        {
            return Ok(Vec::new());
        }
        let subject = self.subject(target, now).ok_or(Refusal::battery_refused("TARGET_NOT_FOUND"))?;
        self.battery_check(battery, &subject)?;
        let limit = battery.max_locked_targets.max(0) as usize;
        if battery.targeting.locked.len() + battery.targeting.pending.len() >= limit {
            return Err(Refusal {
                desired_range: limit as f64,
                ..Refusal::battery_refused("TARGET_LOCK_LIMIT_REACHED")
            });
        }
        let duration = lock_duration_ms(battery.scan_resolution, subject.signature_radius);
        let sequence = self.next_sequence();
        if duration <= 1.0 {
            return Ok(self.finish_battery_lock(battery_id, subject, sequence));
        }
        let complete_at = now + (duration * TICKS_PER_SECOND as f64 / 1000.0) as i64;
        if let Some(battery) = self.batteries.get_mut(&battery_id) {
            battery.targeting.pending.push(Pending {
                target,
                sequence,
                requested_at: now,
                total_duration_ms: duration,
                complete_at,
                origin: LockOrigin::Player,
            });
        }
        Ok(Vec::new())
    }

    fn finish_battery_lock(&mut self, battery_id: i64, subject: Subject, sequence: u64) -> Vec<Out> {
        let Some(battery) = self.batteries.get_mut(&battery_id) else { return Vec::new() };
        battery.targeting.pending.retain(|p| p.target != subject.id);
        battery.targeting.locked.push(Locked { target: subject.id, sequence });
        battery
            .holder
            .map_or_else(Vec::new, |holder| vec![obo(holder, "add", battery_id, Some(subject.id), None)])
    }

    /// Drops a target from a battery: a pending lock is cancelled, and with `include_locked` a held lock is released too. Each dropped target
    /// is told to the holder as `lost`.
    fn battery_release(
        &mut self,
        battery_id: i64,
        holder: Option<SessionId>,
        target: i64,
        include_locked: bool,
        reason: Option<&'static str>,
    ) -> Vec<Out> {
        let Some(battery) = self.batteries.get_mut(&battery_id) else { return Vec::new() };
        let mut out = Vec::new();
        let pending = battery.targeting.pending.len();
        battery.targeting.pending.retain(|p| p.target != target);
        if battery.targeting.pending.len() != pending {
            out.extend(holder.map(|holder| obo(holder, "lost", battery_id, Some(target), reason)));
        }
        if include_locked {
            let locked = battery.targeting.locked.len();
            battery.targeting.locked.retain(|l| l.target != target);
            if battery.firing == Some(target) {
                battery.firing = None;
            }
            if battery.targeting.locked.len() != locked {
                out.extend(holder.map(|holder| obo(holder, "lost", battery_id, Some(target), reason)));
            }
        }
        out
    }

    fn battery_clear(&mut self, battery_id: i64, holder: SessionId) -> Vec<Out> {
        let Some(battery) = self.batteries.get_mut(&battery_id) else { return Vec::new() };
        if battery.targeting.is_empty() {
            return Vec::new();
        }
        battery.targeting = Targeting::default();
        battery.firing = None;
        vec![obo(holder, "clear", battery_id, None, None)]
    }

    /// What is due for one battery at `now`: a pending lock that has come to its end is had; one that can no longer be had (the target is gone,
    /// or the checks fail) is dropped, and so is a held lock that fails the checks. Nothing is done while the battery is offline or has no holder.
    fn settle_battery(&mut self, battery_id: i64, now: i64) -> Vec<Out> {
        let Some(battery) = self.batteries.get(&battery_id) else { return Vec::new() };
        if !battery.online {
            return Vec::new();
        }
        let holder = battery.holder;
        let mut due: Vec<Pending> = battery.targeting.pending.clone();
        due.sort_by_key(|p| (p.complete_at, p.sequence));
        let mut out = Vec::new();
        for pending in due {
            let subject = self.subject(pending.target, now);
            let valid = match (&subject, self.batteries.get(&battery_id)) {
                (Some(subject), Some(battery)) => self.battery_check(battery, subject).is_ok(),
                _ => false,
            };
            if !valid {
                out.extend(self.battery_release(
                    battery_id,
                    holder,
                    pending.target,
                    false,
                    Some("targetLost"),
                ));
                continue;
            }
            if pending.complete_at <= now
                && let Some(subject) = subject
            {
                out.extend(self.finish_battery_lock(battery_id, subject, pending.sequence));
            }
        }
        let held = self.batteries.get(&battery_id).map(|b| b.targeting.targets()).unwrap_or_default();
        for target in held {
            let subject = self.subject(target, now);
            let valid = match (&subject, self.batteries.get(&battery_id)) {
                (Some(subject), Some(battery)) => self.battery_check(battery, subject).is_ok(),
                _ => false,
            };
            if !valid {
                out.extend(self.battery_release(battery_id, holder, target, true, Some("targetLost")));
            }
        }
        out
    }

    /// Settles every battery of the scene at `now` (the poll and each order call it).
    pub(crate) fn settle_batteries(&mut self, now: i64) -> Vec<Out> {
        let ids: Vec<i64> = self.batteries.keys().copied().collect();
        let mut out = Vec::new();
        for battery_id in ids {
            out.extend(self.settle_battery(battery_id, now));
        }
        self.sentry_pass(now);
        out
    }
}
