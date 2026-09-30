//! Modules that run: an effect of a fitted module (an afterburner, for now) is started, goes on cycle after cycle, costs capacitor at the start of
//! each cycle, changes the numbers of the ship while it runs, and is stopped at the end of the cycle it is in when the pilot asks.
//!
//! What an effect does to the ship is worked out by the host (dogma): it says, when the effect is started, what the ship's mass and top speed are
//! with and without it and which attributes the client is to be told changed. The scene keeps the time: the cycles, the capacitor and what the
//! client is sent at each moment, in the order of `oracle/fixtures/scenarios/module.json`. The capacitor recharges along CCP's curve and its charge
//! is told every half second while it is below the capacity, as the reference does.

use eve_marshal::Value;

use crate::payload;
use crate::scene::{Out, Scene, SessionId, TICKS_PER_SECOND};
use crate::wire::{filetime, id, int, real, tuple, word};

/// The attribute of a ship that holds the capacitor's charge.
const CAPACITOR_CHARGE: i32 = 18;
/// The attribute of a module that holds the length of its cycle (told as a real, whatever its value).
const DURATION: i32 = 73;
/// The capacitor's charge is told every half second, worked out at the tick a tenth of a second on from the moment (the reference's ticks).
pub(crate) const SAMPLE_EVERY: i64 = TICKS_PER_SECOND / 2;
pub(crate) const TICK: i64 = TICKS_PER_SECOND / 10;
/// A capacitor within this many GJ of full is full (the reference settles it so).
const FULL_SNAP: f64 = 1.0;

/// The numbers of a ship that a running effect changes.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Numbers {
    pub mass: f64,
    pub max_velocity: f64,
    pub signature_radius: f64,
}

/// A value the client is told an item now has.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Change {
    pub item_id: i64,
    pub attribute_id: i32,
    pub value: f64,
}

/// An effect the host asks the scene to run, with everything the host has worked out for it.
#[derive(Debug, Clone, PartialEq)]
pub struct Effect {
    pub module_id: i64,
    pub module_type: i32,
    pub effect_id: i32,
    /// What the destiny stream calls the effect (`effects.Afterburner`).
    pub fx: String,
    pub duration_ms: f64,
    /// The capacitor a cycle takes, taken at its start.
    pub capacitor_need: f64,
    /// How many cycles the pilot asked for.
    pub repeat: i32,
    pub target_id: i64,
    /// The ship's numbers while the effect runs and when it does not (nothing changes when the effect does not touch them).
    pub on: Option<Numbers>,
    pub off: Option<Numbers>,
    /// What the client is told when the effect starts, and when it ends.
    pub on_changes: Vec<Change>,
    pub off_changes: Vec<Change>,
}

/// The charge of a ship's capacitor as a function of time: `C1 / Cmax = (1 + (sqrt(C0 / Cmax) - 1) * e^(-5 * dt / T))^2`, `T` the recharge time.
pub fn recharged(charge: f64, capacity: f64, recharge_s: f64, seconds: f64) -> f64 {
    if capacity <= 0.0 || charge >= capacity || seconds <= 0.0 || recharge_s <= 0.0 {
        return charge.min(capacity.max(0.0));
    }
    let root = 1.0 + ((charge.max(0.0) / capacity).sqrt() - 1.0) * (-5.0 * seconds / recharge_s).exp();
    (capacity * root * root).min(capacity)
}

/// A ship's capacitor.
#[derive(Debug, Clone, PartialEq)]
pub(crate) struct Capacitor {
    capacity: f64,
    recharge_s: f64,
    /// The charge at `at`.
    charge: f64,
    at: i64,
    /// When the charge is next told, while it is below the capacity.
    next_sample: Option<i64>,
}

impl Capacitor {
    /// A capacitor that holds `ratio` of what it can (its charge is told from the start when it is below full).
    pub(crate) fn filled(capacity: f64, recharge_s: f64, ratio: f64, now: i64) -> Self {
        let ratio = ratio.clamp(0.0, 1.0);
        Self {
            capacity,
            recharge_s,
            charge: capacity * ratio,
            at: now,
            next_sample: (ratio < 1.0).then_some(now),
        }
    }

    /// How full it is at `now`, as a fraction (a ship with no capacitor is full).
    pub(crate) fn ratio_at(&self, now: i64) -> f64 {
        if self.capacity <= 0.0 { 1.0 } else { (self.charge_at(now) / self.capacity).clamp(0.0, 1.0) }
    }

    /// Fills it at `now` and returns the charge before and after.
    pub(crate) fn refill(&mut self, now: i64) -> (f64, f64) {
        let before = self.charge_at(now);
        self.charge = self.capacity;
        self.at = now;
        self.next_sample = None;
        (before, self.capacity)
    }

    pub(crate) fn charge_at(&self, now: i64) -> f64 {
        let charge = recharged(
            self.charge,
            self.capacity,
            self.recharge_s,
            (now - self.at) as f64 / TICKS_PER_SECOND as f64,
        );
        if self.capacity - charge <= FULL_SNAP { self.capacity } else { charge }
    }

    /// Takes `amount` at `now` and returns the charge before and after.
    fn draw(&mut self, now: i64, amount: f64) -> (f64, f64) {
        let before = self.charge_at(now);
        let after = (before - amount).max(0.0);
        self.charge = after;
        self.at = now;
        // the charge is told at once and then at the ticks a tenth of a second past each half second
        self.next_sample = Some(now + TICK);
        (before, after)
    }
}

/// An effect that is running.
#[derive(Debug, Clone, PartialEq)]
pub(crate) struct Running {
    effect: Effect,
    /// When the cycle that is on began.
    cycle_start: i64,
    cycles: i32,
    /// The pilot asked for it to stop: it does when the cycle ends.
    stopping: bool,
}

/// The modules of a ship that run, and its capacitor.
#[derive(Debug, Clone, Default)]
pub(crate) struct Modules {
    pub(crate) capacitor: Option<Capacitor>,
    running: Vec<Running>,
}

impl Modules {
    /// The modules of a ship whose capacitor holds `ratio` of what it can.
    pub(crate) fn with_charge(capacity: f64, recharge_s: f64, ratio: f64, now: i64) -> Self {
        Self { capacitor: Some(Capacitor::filled(capacity, recharge_s, ratio, now)), running: Vec::new() }
    }

    pub(crate) fn is_idle(&self) -> bool {
        self.running.is_empty() && self.capacitor.as_ref().is_none_or(|c| c.next_sample.is_none())
    }
}

pub(crate) fn number(value: f64) -> Value {
    if value.fract() == 0.0 && value.abs() < 9.0e15 { int(value as i64) } else { real(value) }
}

/// One line of an `OnModuleAttributeChanges`: `(name, owner, item, attribute, when, new value, old value, when)`.
pub(crate) fn attribute_change(
    owner: i64,
    item: i64,
    attribute: i32,
    when: i64,
    value: Value,
    old: Value,
) -> Value {
    tuple([
        word("OnModuleAttributeChange"),
        id(owner),
        id(item),
        int(i64::from(attribute)),
        filetime(when),
        value,
        old,
        filetime(when),
    ])
}

impl Scene {
    pub(crate) fn dogma_notify(session: SessionId, name: &'static str, args: Vec<Value>) -> Out {
        Out::Notify { session, name, id_type: "clientID", args }
    }

    /// The capacitor's charge, told: `(the ship's charge now, and a tenth of a second before)`.
    pub(crate) fn charge_told(
        session: SessionId,
        owner: i64,
        ship: i64,
        when: i64,
        new: f64,
        old: f64,
    ) -> Out {
        Self::dogma_notify(
            session,
            "OnModuleAttributeChanges",
            vec![Value::List(vec![attribute_change(
                owner,
                ship,
                CAPACITOR_CHARGE,
                when,
                number(new),
                number(old),
            )])],
        )
    }

    /// The values an effect changes, told to the pilot.
    fn changes_told(session: SessionId, owner: i64, changes: &[Change], when: i64) -> Option<Out> {
        if changes.is_empty() {
            return None;
        }
        let lines: Vec<Value> = changes
            .iter()
            .map(|c| {
                let value = if c.attribute_id == DURATION { real(c.value) } else { number(c.value) };
                attribute_change(owner, c.item_id, c.attribute_id, when, value, Value::None)
            })
            .collect();
        Some(Self::dogma_notify(session, "OnModuleAttributeChanges", vec![Value::List(lines)]))
    }

    /// `OnGodmaShipEffect`: an effect went on (`start` and `active`) or came to an end, or began another cycle.
    fn effect_told(
        session: SessionId,
        owner: i64,
        ship: i64,
        run: &Running,
        when: i64,
        on: bool,
        started: i64,
    ) -> Out {
        let effect = &run.effect;
        let environment = tuple([
            id(effect.module_id),
            id(owner),
            id(ship),
            if effect.target_id > 0 { id(effect.target_id) } else { Value::None },
            Value::None,
            tuple([]),
            int(i64::from(effect.effect_id)),
            Value::None,
        ]);
        Self::dogma_notify(
            session,
            "OnGodmaShipEffect",
            vec![
                id(effect.module_id),
                int(i64::from(effect.effect_id)),
                filetime(when),
                int(i64::from(on)),
                int(i64::from(on)),
                environment,
                filetime(started),
                real(effect.duration_ms),
                if on { int(i64::from(run.effect.repeat)) } else { Value::Bool(false) },
                Value::None,
            ],
        )
    }

    /// The action the client plays on the ship for the effect: `OnSpecialFX(ship, module, module type, target, charge, effect, offensive, start, active, duration, …)`.
    fn fx_action(stamp: u32, ship: &Value, effect: &Effect, on: bool) -> Value {
        payload::action(
            stamp,
            "OnSpecialFX",
            [
                ship.clone(),
                id(effect.module_id),
                int(i64::from(effect.module_type)),
                if effect.target_id > 0 { id(effect.target_id) } else { Value::None },
                Value::None,
                crate::wire::text(effect.fx.clone()),
                int(0),
                int(i64::from(on)),
                int(i64::from(on)),
                number(effect.duration_ms),
                Value::None,
                Value::None,
                int(0),
                Value::None,
            ],
        )
    }

    /// The ship's numbers become `numbers`; what the client is told: `SetBallMass` and `SetMaxSpeed` (the ones that changed).
    fn renumber(&mut self, session: SessionId, numbers: Numbers, stamp: u32) -> Vec<Value> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let spec = &mut pilot.ship.spec;
        let ship = id(spec.ship_id);
        let mut actions = Vec::new();
        if numbers.mass != spec.mass {
            spec.mass = numbers.mass;
            actions.push(payload::action(stamp, "SetBallMass", [ship.clone(), real(numbers.mass)]));
        }
        if numbers.max_velocity != spec.max_velocity {
            spec.max_velocity = numbers.max_velocity;
            actions.push(payload::action(stamp, "SetMaxSpeed", [ship, real(numbers.max_velocity)]));
        }
        spec.signature_radius = numbers.signature_radius;
        actions
    }

    /// The host asks for an effect to run. Answered with `Accepted` (code 17) or `Refused`: `ALREADY_ACTIVE`, `NOT_ENOUGH_CAPACITOR`.
    pub fn activate(&mut self, session: SessionId, effect: Effect, now: i64) -> Vec<Out> {
        const CODE: u8 = 17;
        if !self.pilots.contains_key(&session) {
            return vec![Out::Refused { session, command: CODE, reason: "NO_SHIP_IN_SPACE" }];
        }
        if effect.duration_ms < 1.0 || effect.repeat < 1 {
            return vec![Out::Refused { session, command: CODE, reason: "BAD_EFFECT" }];
        }
        self.settle_all(now);
        let pilot = &self.pilots[&session];
        if pilot.modules.running.iter().any(|r| r.effect.module_id == effect.module_id) {
            return vec![Out::Refused { session, command: CODE, reason: "ALREADY_ACTIVE" }];
        }
        let Some(capacitor) = pilot.modules.capacitor.as_ref() else {
            return vec![Out::Refused { session, command: CODE, reason: "NO_CAPACITOR" }];
        };
        if capacitor.charge_at(now) < effect.capacitor_need {
            return vec![Out::Refused { session, command: CODE, reason: "NOT_ENOUGH_CAPACITOR" }];
        }
        // the pilot has given up the undocking protection by flying the ship (the host tells the client)
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let pilot = self.pilots.get_mut(&session).expect("the pilot is in the scene");
        pilot.protected_until = 0;
        let run = Running { effect, cycle_start: now, cycles: 1, stopping: false };
        let (before, after) = match pilot.modules.capacitor.as_mut() {
            Some(capacitor) => capacitor.draw(now, run.effect.capacitor_need),
            None => (0.0, 0.0),
        };
        let mut out = vec![Out::Accepted { session, command: CODE, at: now }];
        out.push(Self::charge_told(session, owner, ship_id, now, after, before));
        let stamp = self.next_stamp(now);
        let ship = id(ship_id);
        if let Some(numbers) = run.effect.on {
            let actions = self.renumber(session, numbers, stamp);
            if !actions.is_empty() {
                out.extend(self.tell_all(session, actions));
            }
        }
        out.extend(Self::changes_told(session, owner, &run.effect.on_changes, now));
        out.extend(self.tell_all(session, vec![Self::fx_action(stamp, &ship, &run.effect, true)]));
        out.push(Self::effect_told(session, owner, ship_id, &run, now, true, now));
        if let Some(pilot) = self.pilots.get_mut(&session) {
            pilot.modules.running.push(run);
        }
        out
    }

    /// The pilot asks for an effect to stop: it does at the end of the cycle it is in. `Refused` (`NOT_ACTIVE`) when it is not running.
    pub fn deactivate(&mut self, session: SessionId, module_id: i64, now: i64) -> Vec<Out> {
        const CODE: u8 = 16;
        let Some(pilot) = self.pilots.get_mut(&session) else {
            return vec![Out::Refused { session, command: CODE, reason: "NO_SHIP_IN_SPACE" }];
        };
        match pilot.modules.running.iter_mut().find(|r| r.effect.module_id == module_id) {
            Some(run) => {
                run.stopping = true;
                vec![Out::Accepted { session, command: CODE, at: now }]
            }
            None => vec![Out::Refused { session, command: CODE, reason: "NOT_ACTIVE" }],
        }
    }

    /// What the running effects and the capacitors have come to by `now`: cycles that end (the next begins, or the effect stops), and the charge told.
    pub(crate) fn settle_modules(&mut self, now: i64) -> Vec<Out> {
        let sessions: Vec<SessionId> = self
            .in_entry_order()
            .into_iter()
            .filter(|s| !self.pilots[s].modules.is_idle() || self.pilots[s].ship.health.next_sample.is_some())
            .collect();
        let mut out = Vec::new();
        for session in sessions {
            // a cycle that ends is followed by the charge told at the moments in between, in the order they fall
            while let Some((at, kind)) = self.next_module_moment(session, now) {
                out.extend(self.module_moment(session, at, kind));
            }
        }
        out
    }

    /// The next thing due for a ship's modules by `now`: a cycle's end or a charge to tell.
    fn next_module_moment(&self, session: SessionId, now: i64) -> Option<(i64, Moment)> {
        let pilot = self.pilots.get(&session)?;
        let mut next: Option<(i64, Moment)> = None;
        let mut consider = |at: i64, moment: Moment| {
            if at <= now && next.as_ref().is_none_or(|(best, _)| at < *best) {
                next = Some((at, moment));
            }
        };
        for (index, run) in pilot.modules.running.iter().enumerate() {
            consider(
                run.cycle_start + (run.effect.duration_ms * TICKS_PER_SECOND as f64 / 1000.0) as i64,
                Moment::CycleEnd(index),
            );
        }
        if let Some(sample) = pilot.modules.capacitor.as_ref().and_then(|c| c.next_sample) {
            consider(sample, Moment::Charge);
        }
        // the shield's charge follows the capacitor's at the same tick (the reference works them out in that order)
        if let Some(sample) = pilot.ship.health.next_sample {
            consider(sample, Moment::Shield);
        }
        next
    }

    fn module_moment(&mut self, session: SessionId, at: i64, moment: Moment) -> Vec<Out> {
        match moment {
            Moment::Charge => self.tell_charge(session, at),
            Moment::Shield => self.tell_shield(session, at),
            Moment::CycleEnd(index) => self.end_cycle(session, index, at),
        }
    }

    /// The charge is told: now, and a tenth of a second before; the next time is half a second on while the charge is below the capacity.
    fn tell_charge(&mut self, session: SessionId, at: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let (owner, ship) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let Some(capacitor) = pilot.modules.capacitor.as_mut() else { return Vec::new() };
        let (new, old) = (capacitor.charge_at(at), capacitor.charge_at(at - TICK));
        if old >= capacitor.capacity {
            // it was full a tick ago: nothing more is told
            capacitor.next_sample = None;
            return Vec::new();
        }
        // the tick that fills it is told when it is a tick that tells (the reference tells the change on every fifth tick)
        capacitor.next_sample = (new < capacitor.capacity).then_some(at + SAMPLE_EVERY);
        vec![Self::charge_told(session, owner, ship, at, new, old)]
    }

    /// A cycle ends: the next begins (and costs capacitor), or, when the pilot asked to stop or the capacitor cannot pay, the effect ends.
    fn end_cycle(&mut self, session: SessionId, index: usize, at: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let run = pilot.modules.running[index].clone();
        let affordable =
            pilot.modules.capacitor.as_ref().is_some_and(|c| c.charge_at(at) >= run.effect.capacitor_need);
        let more = run.cycles < run.effect.repeat;
        if run.stopping || !more || !affordable {
            pilot.modules.running.remove(index);
            return self.effect_ended(session, run, owner, ship_id, at);
        }
        let (before, after) = match pilot.modules.capacitor.as_mut() {
            Some(capacitor) => capacitor.draw(at, run.effect.capacitor_need),
            None => (0.0, 0.0),
        };
        let next = &mut pilot.modules.running[index];
        next.cycle_start = at;
        next.cycles += 1;
        let next = next.clone();
        vec![
            Self::charge_told(session, owner, ship_id, at, after, before),
            Self::effect_told(session, owner, ship_id, &next, at, true, at),
        ]
    }

    /// An effect is over: the client is told the values it gave back, that it is off, and the ship's numbers and the effect are taken off in one update.
    fn effect_ended(
        &mut self,
        session: SessionId,
        run: Running,
        owner: i64,
        ship_id: i64,
        at: i64,
    ) -> Vec<Out> {
        let mut out = Vec::new();
        out.extend(Self::changes_told(session, owner, &run.effect.off_changes, at));
        out.push(Self::effect_told(session, owner, ship_id, &run, at, false, run.cycle_start));
        let stamp = self.next_stamp(at);
        let mut actions = match run.effect.off {
            Some(numbers) => self.renumber(session, numbers, stamp),
            None => Vec::new(),
        };
        actions.push(Self::fx_action(stamp, &id(ship_id), &run.effect, false));
        out.extend(self.tell_all(session, actions));
        out
    }
}

#[derive(Debug, Clone, Copy)]
enum Moment {
    CycleEnd(usize),
    Charge,
    Shield,
}

#[cfg(test)]
pub(crate) mod tests {
    use std::sync::Arc;

    use super::*;
    use crate::scene::tests::{NOON, STATION, spec, system};
    use crate::scene::{Command, Out};
    use crate::ship::ShipSpec;

    const A: SessionId = 7;
    const B: SessionId = 8;
    const SECOND: i64 = 10_000_000;
    const MODULE: i64 = 9_988_400_001_940;

    pub(crate) fn afterburner() -> Effect {
        Effect {
            module_id: MODULE,
            module_type: 21857,
            effect_id: 6731,
            fx: "effects.Afterburner".to_string(),
            duration_ms: 10_000.0,
            capacitor_need: 5.0,
            repeat: 1000,
            target_id: 0,
            on: Some(Numbers { mass: 1_663_000.0, max_velocity: 522.848918, signature_radius: 28.0 }),
            off: Some(Numbers { mass: 1_163_000.0, max_velocity: 339.25, signature_radius: 28.0 }),
            on_changes: vec![
                Change { item_id: MODULE, attribute_id: 20, value: 60.0 },
                Change { item_id: MODULE, attribute_id: 73, value: 10_000.0 },
                Change { item_id: spec().ship_id, attribute_id: 37, value: 522.848918 },
            ],
            off_changes: vec![
                Change { item_id: MODULE, attribute_id: 20, value: 60.0 },
                Change { item_id: MODULE, attribute_id: 73, value: 10_000.0 },
                Change { item_id: spec().ship_id, attribute_id: 37, value: 339.25 },
            ],
        }
    }

    fn other_spec() -> ShipSpec {
        let mut other = spec();
        other.ship_id += 4;
        other.character_id += 1;
        other.owner_id += 1;
        other
    }

    /// A ship that has bound the ballpark, and one that looks on, thirty seconds after they undocked.
    fn crowd() -> (Scene, i64) {
        let mut scene = Scene::new(Arc::new((*system()).clone()));
        scene.undock(A, spec(), STATION, NOON).unwrap();
        scene.bind(A, NOON);
        scene.undock(B, other_spec(), STATION, NOON).unwrap();
        scene.bind(B, NOON);
        let t = NOON + 40 * SECOND;
        scene.poll(t);
        (scene, t)
    }

    fn poll_until(scene: &mut Scene, from: i64, seconds: f64) -> (Vec<Out>, i64) {
        let mut out = Vec::new();
        let mut t = from;
        while t < from + (seconds * SECOND as f64) as i64 {
            t += SECOND / 4;
            out.extend(scene.poll(t));
        }
        (out, t)
    }

    fn label(out: &Out) -> String {
        match out {
            Out::Notify { session, name, args, .. } if *name == "DoDestinyUpdate" => {
                let Some(Value::List(actions)) = args.first() else { panic!("{args:?}") };
                let names: Vec<String> = actions
                    .iter()
                    .map(|a| {
                        let Value::Tuple(pair) = a else { panic!() };
                        let Value::Tuple(inner) = &pair[1] else { panic!() };
                        let Value::Str(name) = &inner[0] else { panic!() };
                        name.to_string()
                    })
                    .collect();
                format!("{session}:destiny[{}]", names.join(","))
            }
            Out::Notify { session, name, args, .. } if *name == "OnModuleAttributeChanges" => {
                let Some(Value::List(lines)) = args.first() else { panic!("{args:?}") };
                let attributes: Vec<String> = lines
                    .iter()
                    .map(|l| {
                        let Value::Tuple(t) = l else { panic!() };
                        match &t[3] {
                            Value::Int(n) => n.to_string(),
                            other => panic!("{other:?}"),
                        }
                    })
                    .collect();
                format!("{session}:attributes[{}]", attributes.join(","))
            }
            Out::Notify { session, name, .. } => format!("{session}:{name}"),
            Out::Accepted { session, command, .. } => format!("{session}:accepted {command}"),
            Out::Refused { session, command, reason } => format!("{session}:refused {command} {reason}"),
            other => format!("{other:?}"),
        }
    }

    fn labels(outs: &[Out]) -> Vec<String> {
        outs.iter().map(label).collect()
    }

    fn charge_of(out: &Out) -> Option<(f64, f64)> {
        let Out::Notify { name: "OnModuleAttributeChanges", args, .. } = out else { return None };
        let Some(Value::List(lines)) = args.first() else { return None };
        let Value::Tuple(t) = &lines[0] else { return None };
        (t[3] == Value::Int(18)).then(|| match (&t[5], &t[6]) {
            (Value::Real(new), Value::Real(old)) => (*new, *old),
            other => panic!("{other:?}"),
        })
    }

    fn charges(outs: &[Out]) -> Vec<(f64, f64)> {
        outs.iter().filter_map(charge_of).collect()
    }

    #[test]
    fn the_capacitor_recharges_along_ccps_curve() {
        // from a charge a tenth of a second on: the recorded first sample of the afterburner's charge
        assert!(
            (recharged(144.5, 149.5, 55.25, 0.1) - 144.5449).abs() < 1e-3,
            "{}",
            recharged(144.5, 149.5, 55.25, 0.1)
        );
        // nothing to wait for when it is full, nothing to recharge without time or a recharge time, and never past the capacity
        assert_eq!(recharged(149.5, 149.5, 55.25, 10.0), 149.5);
        assert_eq!(recharged(100.0, 149.5, 55.25, 0.0), 100.0);
        assert_eq!(recharged(100.0, 149.5, 0.0, 10.0), 100.0);
        assert!(recharged(0.0, 149.5, 55.25, 1e6) <= 149.5);
        // it recharges fastest at about a quarter of the capacity, and a long time fills it
        let (low, mid) = (recharged(1.0, 149.5, 55.25, 1.0) - 1.0, recharged(37.0, 149.5, 55.25, 1.0) - 37.0);
        assert!(mid > low, "{mid} {low}");
        assert!((recharged(0.0, 149.5, 55.25, 600.0) - 149.5).abs() < 0.01);
        // the peak is 10 * capacity / (4 * recharge time) a second, 2.5 * capacity / recharge time
        assert!((recharged(37.375, 149.5, 55.25, 0.001) - 37.375) / 0.001 - 2.5 * 149.5 / 55.25 < 0.05);
    }

    #[test]
    fn an_effect_started_takes_capacitor_changes_the_ship_and_tells_the_client_in_the_reference_order() {
        let (mut scene, t) = crowd();
        let out = scene.activate(A, afterburner(), t);
        assert_eq!(
            labels(&out),
            [
                "7:accepted 17",
                "7:attributes[18]",
                "7:destiny[SetBallMass,SetMaxSpeed]",
                "8:destiny[SetBallMass,SetMaxSpeed]",
                "7:attributes[20,73,37]",
                "7:destiny[OnSpecialFX]",
                "8:destiny[OnSpecialFX]",
                "7:OnGodmaShipEffect",
            ]
        );
        assert_eq!(charges(&out), [(144.5, 149.5)]);
        let ship = scene.ship(A, t).unwrap();
        assert_eq!((ship.spec.mass, ship.spec.max_velocity), (1_663_000.0, 522.848918));
        // both updates are at one stamp
        let stamps: Vec<i64> = out
            .iter()
            .filter(|o| label(o).starts_with("7:destiny"))
            .flat_map(|o| match o {
                Out::Notify { args, .. } => {
                    let Some(Value::List(actions)) = args.first() else { panic!() };
                    actions
                        .iter()
                        .map(|a| match a {
                            Value::Tuple(pair) => match &pair[0] {
                                Value::Int(n) => *n,
                                other => panic!("{other:?}"),
                            },
                            other => panic!("{other:?}"),
                        })
                        .collect::<Vec<_>>()
                }
                _ => Vec::new(),
            })
            .collect();
        assert!(stamps.windows(2).all(|w| w[0] == w[1]), "{stamps:?}");
    }

    #[test]
    fn the_effect_says_which_module_which_ship_and_for_how_long_and_the_values_the_client_is_told_are_typed_as_the_reference_typed_them()
     {
        let (mut scene, t) = crowd();
        let out = scene.activate(A, afterburner(), t);
        let Some(Out::Notify { args, .. }) =
            out.iter().find(|o| matches!(o, Out::Notify { name: "OnGodmaShipEffect", .. }))
        else {
            panic!("{out:?}")
        };
        let ship = crate::wire::id(spec().ship_id);
        assert_eq!(args[0], crate::wire::id(MODULE));
        assert_eq!(args[1], Value::Int(6731));
        assert_eq!((&args[3], &args[4]), (&Value::Int(1), &Value::Int(1)), "started and active");
        assert_eq!(args[7], Value::Real(10_000.0));
        assert_eq!(args[8], Value::Int(1000), "the cycles asked for");
        let Value::Tuple(environment) = &args[5] else { panic!() };
        assert_eq!(environment[0], crate::wire::id(MODULE));
        assert_eq!(environment[2], ship);
        assert_eq!(environment[3], Value::None, "no target");
        // a whole value is an int, but the length of a cycle is always a real
        let Some(Out::Notify { args, .. }) = out.iter().find(|o| label(o) == "7:attributes[20,73,37]") else {
            panic!()
        };
        let Some(Value::List(lines)) = args.first() else { panic!() };
        let values: Vec<&Value> = lines
            .iter()
            .map(|l| match l {
                Value::Tuple(t) => &t[5],
                other => panic!("{other:?}"),
            })
            .collect();
        assert_eq!(values, [&Value::Int(60), &Value::Real(10_000.0), &Value::Real(522.848918)]);
    }

    #[test]
    fn the_effect_played_on_the_ship_says_it_is_on_when_it_starts_and_off_when_it_ends_and_names_the_module_the_target_and_the_length()
     {
        fn fx_args(out: &[Out], session: SessionId) -> Vec<Vec<Value>> {
            out.iter()
                .filter_map(|o| match o {
                    Out::Notify { session: s, name: "DoDestinyUpdate", args, .. } if *s == session => {
                        let Some(Value::List(actions)) = args.first() else { return None };
                        actions.iter().find_map(|a| {
                            let Value::Tuple(pair) = a else { return None };
                            let Value::Tuple(inner) = &pair[1] else { return None };
                            let Value::Str(name) = &inner[0] else { return None };
                            let Value::Tuple(arguments) = &inner[1] else { return None };
                            (name == "OnSpecialFX").then(|| arguments.clone())
                        })
                    }
                    _ => None,
                })
                .collect()
        }
        let (mut scene, t) = crowd();
        let mut effect = afterburner();
        effect.target_id = 9_988_400_002_215;
        let started = fx_args(&scene.activate(A, effect, t), 8);
        let ship = crate::wire::id(spec().ship_id);
        assert_eq!(
            started,
            [vec![
                ship.clone(),
                crate::wire::id(MODULE),
                Value::Int(21857),
                crate::wire::id(9_988_400_002_215),
                Value::None,
                Value::Str("effects.Afterburner".into()),
                Value::Int(0),
                Value::Int(1),
                Value::Int(1),
                Value::Int(10_000),
                Value::None,
                Value::None,
                Value::Int(0),
                Value::None,
            ]]
        );
        scene.deactivate(A, MODULE, t + SECOND);
        let (end, _) = poll_until(&mut scene, t, 10.5);
        let ended = fx_args(&end, 8);
        assert_eq!(ended.len(), 1, "{end:?}");
        assert_eq!(ended[0][1..3], [crate::wire::id(MODULE), Value::Int(21857)]);
        assert_eq!((&ended[0][7], &ended[0][8]), (&Value::Int(0), &Value::Int(0)), "off and not active");
        // the target is in the environment of the effect the pilot is told of
        let Some(Out::Notify { args, .. }) =
            end.iter().find(|o| matches!(o, Out::Notify { name: "OnGodmaShipEffect", .. }))
        else {
            panic!()
        };
        let Value::Tuple(environment) = &args[5] else { panic!() };
        assert_eq!(environment[3], crate::wire::id(9_988_400_002_215));
    }

    #[test]
    fn a_ship_that_is_told_to_start_an_effect_it_is_running_is_refused() {
        let (mut scene, t) = crowd();
        scene.activate(A, afterburner(), t);
        assert_eq!(labels(&scene.activate(A, afterburner(), t + SECOND)), ["7:refused 17 ALREADY_ACTIVE"]);
        // a ship that is not in space has no modules to start
        assert_eq!(labels(&scene.activate(99, afterburner(), t)), ["99:refused 17 NO_SHIP_IN_SPACE"]);
    }

    #[test]
    fn an_effect_the_capacitor_cannot_pay_for_is_refused_and_costs_nothing() {
        let (mut scene, t) = crowd();
        let mut greedy = afterburner();
        greedy.capacitor_need = 150.0;
        assert_eq!(labels(&scene.activate(A, greedy, t)), ["7:refused 17 NOT_ENOUGH_CAPACITOR"]);
        // it costs exactly what it holds, and no more
        let mut all = afterburner();
        all.capacitor_need = 149.5;
        assert_eq!(labels(&scene.activate(A, all, t))[0], "7:accepted 17");
    }

    #[test]
    fn an_effect_with_no_cycle_or_no_cycles_is_not_one() {
        let (mut scene, t) = crowd();
        let mut quick = afterburner();
        quick.duration_ms = 0.0;
        assert_eq!(labels(&scene.activate(A, quick, t)), ["7:refused 17 BAD_EFFECT"]);
        let mut none = afterburner();
        none.repeat = 0;
        assert_eq!(labels(&scene.activate(A, none, t)), ["7:refused 17 BAD_EFFECT"]);
    }

    #[test]
    fn starting_an_effect_ends_the_protection_of_a_ship_that_has_just_undocked() {
        let mut scene = Scene::new(Arc::new((*system()).clone()));
        scene.undock(A, spec(), STATION, NOON).unwrap();
        scene.undock(B, other_spec(), STATION, NOON).unwrap();
        let t = NOON + 5 * SECOND;
        assert!(scene.pilots[&A].protected_until > t);
        scene.activate(A, afterburner(), t);
        assert_eq!(scene.pilots[&A].protected_until, 0);
        assert!(scene.pilots[&B].protected_until > t);
    }

    #[test]
    fn a_cycle_that_ends_begins_the_next_and_takes_capacitor_again() {
        let (mut scene, t) = crowd();
        scene.activate(A, afterburner(), t);
        let (before, t2) = poll_until(&mut scene, t, 9.5);
        assert!(
            labels(&before).iter().all(|l| l == "7:attributes[18]"),
            "only the capacitor's charge until the cycle ends: {:?}",
            labels(&before)
        );
        let (after, _) = poll_until(&mut scene, t2, 1.0);
        let told = labels(&after);
        let at = told
            .iter()
            .position(|l| l == "7:OnGodmaShipEffect")
            .expect("the effect goes on for another cycle");
        assert_eq!(told[at - 1], "7:attributes[18]", "the capacitor is drawn just before");
        let drawn = charges(&after);
        let (new, old) = drawn[told[..at].iter().filter(|l| *l == "7:attributes[18]").count() - 1];
        assert!(
            (old - new - 5.0).abs() < 0.2,
            "a cycle costs 5, less what it recharged in between: {old} to {new}"
        );
        // the ship goes on with the effect: its numbers are the same
        assert_eq!(scene.ship(A, t + 11 * SECOND).unwrap().spec.mass, 1_663_000.0);
    }

    #[test]
    fn a_new_cycle_says_when_it_began_and_the_same_effect_again() {
        let (mut scene, t) = crowd();
        scene.activate(A, afterburner(), t);
        let (out, _) = poll_until(&mut scene, t, 10.5);
        let Some(Out::Notify { args, .. }) =
            out.iter().find(|o| matches!(o, Out::Notify { name: "OnGodmaShipEffect", .. }))
        else {
            panic!("{out:?}")
        };
        // when and started are the moment the cycle began: exactly ten seconds after the first
        assert_eq!(args[2], Value::Long(t + 10 * SECOND));
        assert_eq!(args[6], Value::Long(t + 10 * SECOND));
        assert_eq!((&args[3], &args[4], &args[8]), (&Value::Int(1), &Value::Int(1), &Value::Int(1000)));
    }

    #[test]
    fn an_effect_stops_when_it_has_run_the_cycles_asked_for() {
        let (mut scene, t) = crowd();
        let mut once = afterburner();
        once.repeat = 2;
        scene.activate(A, once, t);
        let (out, _) = poll_until(&mut scene, t, 22.0);
        let told = labels(&out);
        assert_eq!(
            told.iter().filter(|l| *l == "7:OnGodmaShipEffect").count(),
            2,
            "one more cycle, then it is off: {told:?}"
        );
        assert_eq!(scene.ship(A, t + 23 * SECOND).unwrap().spec.mass, 1_163_000.0);
    }

    #[test]
    fn an_effect_stops_when_the_capacitor_cannot_pay_for_the_next_cycle() {
        let (mut scene, t) = crowd();
        let mut all = afterburner();
        all.capacitor_need = 149.0; // the first cycle nearly empties it, and it recharges too slowly to pay for another in ten seconds
        scene.activate(A, all, t);
        let (out, _) = poll_until(&mut scene, t, 11.0);
        let told = labels(&out);
        assert!(told.iter().any(|l| l == "7:OnGodmaShipEffect"), "{told:?}");
        assert_eq!(
            scene.ship(A, t + 11 * SECOND).unwrap().spec.mass,
            1_163_000.0,
            "the ship has its own numbers again"
        );
    }

    #[test]
    fn asking_for_an_effect_to_stop_says_so_and_it_stops_at_the_end_of_the_cycle_it_is_in() {
        let (mut scene, t) = crowd();
        scene.activate(A, afterburner(), t);
        let (_, t2) = poll_until(&mut scene, t, 4.0);
        assert_eq!(labels(&scene.deactivate(A, MODULE, t2)), ["7:accepted 16"]);
        // nothing changes until the cycle is over, six seconds on
        let (during, t3) = poll_until(&mut scene, t2, 5.5);
        assert!(labels(&during).iter().all(|l| l == "7:attributes[18]"), "{:?}", labels(&during));
        assert_eq!(scene.ship(A, t3).unwrap().spec.max_velocity, 522.848918);
        let (end, _) = poll_until(&mut scene, t3, 1.0);
        let told: Vec<String> = labels(&end).into_iter().filter(|l| l != "7:attributes[18]").collect();
        assert_eq!(
            told,
            [
                "7:attributes[20,73,37]",
                "7:OnGodmaShipEffect",
                "7:destiny[SetBallMass,SetMaxSpeed,OnSpecialFX]",
                "8:destiny[SetBallMass,SetMaxSpeed,OnSpecialFX]"
            ]
        );
        let ship = scene.ship(A, t + 12 * SECOND).unwrap();
        assert_eq!((ship.spec.mass, ship.spec.max_velocity), (1_163_000.0, 339.25));
        // the effect that ended says it is off, with the start of the cycle it ended
        let Some(Out::Notify { args, .. }) =
            end.iter().find(|o| matches!(o, Out::Notify { name: "OnGodmaShipEffect", .. }))
        else {
            panic!()
        };
        assert_eq!((&args[3], &args[4], &args[8]), (&Value::Int(0), &Value::Int(0), &Value::Bool(false)));
        assert_eq!(args[2], Value::Long(t + 10 * SECOND));
        // and nothing costs capacitor any more
        assert_eq!(labels(&scene.deactivate(A, MODULE, t + 13 * SECOND)), ["7:refused 16 NOT_ACTIVE"]);
    }

    #[test]
    fn a_ship_that_is_not_running_the_effect_or_not_in_space_has_nothing_to_stop() {
        let (mut scene, t) = crowd();
        assert_eq!(labels(&scene.deactivate(A, MODULE, t)), ["7:refused 16 NOT_ACTIVE"]);
        assert_eq!(labels(&scene.deactivate(99, MODULE, t)), ["99:refused 16 NO_SHIP_IN_SPACE"]);
    }

    #[test]
    fn the_charge_is_told_at_once_and_then_every_half_second_a_tenth_of_a_second_on_until_it_is_full() {
        let (mut scene, t) = crowd();
        let mut once = afterburner();
        once.repeat = 1;
        let first = scene.activate(A, once, t);
        let (out, _) = poll_until(&mut scene, t, 3.0);
        let series: Vec<(f64, f64)> = charges(&first).into_iter().chain(charges(&out)).collect();
        assert!(series.len() >= 6, "{series:?}");
        assert_eq!(series[0], (144.5, 149.5));
        // then a sample of the curve at 0.1 s, 0.6 s, 1.1 s…, each with the value a tenth of a second before
        for (k, (new, old)) in series[1..6].iter().enumerate() {
            let seconds = 0.1 + 0.5 * k as f64;
            assert!((new - recharged(144.5, 149.5, 55.25, seconds)).abs() < 1e-6, "{k}: {new}");
            assert!(
                (old - recharged(144.5, 149.5, 55.25, (seconds - 0.1).max(0.0))).abs() < 1e-6,
                "{k}: {old}"
            );
        }
    }

    #[test]
    fn a_capacitor_within_a_gigajoule_of_full_is_full_and_that_is_not_told() {
        let cap =
            Capacitor { capacity: 149.5, recharge_s: 55.25, charge: 148.6, at: NOON, next_sample: None };
        assert_eq!(cap.charge_at(NOON), 149.5);
        let cap = Capacitor { charge: 148.4, ..cap };
        assert!(cap.charge_at(NOON) < 149.5);
        // a ship that draws a little and recharges past the threshold stops being told without a last word
        let (mut scene, t) = crowd();
        let mut small = afterburner();
        small.capacitor_need = 2.0;
        small.repeat = 1;
        scene.activate(A, small, t);
        let (out, _) = poll_until(&mut scene, t, 60.0);
        let told = charges(&out);
        assert!(told.iter().all(|(new, _)| *new < 149.5 - 1.0), "{told:?}");
        assert!(scene.pilots[&A].modules.is_idle());
    }

    #[test]
    fn a_full_capacitor_is_not_told_and_the_telling_stops_when_it_is_full() {
        let (mut scene, t) = crowd();
        let (quiet, _) = poll_until(&mut scene, t, 5.0);
        assert!(charges(&quiet).is_empty(), "{quiet:?}");
        // a small draw fills up again in a while, and then nothing more is told
        let mut small = afterburner();
        small.capacitor_need = 3.0;
        small.repeat = 1;
        scene.activate(A, small, t + 5 * SECOND);
        let (recharging, t2) = poll_until(&mut scene, t + 5 * SECOND, 600.0);
        assert!(!charges(&recharging).is_empty());
        let (after, _) = poll_until(&mut scene, t2, 5.0);
        assert!(charges(&after).is_empty(), "{after:?}");
        assert!(scene.pilots[&A].modules.is_idle());
    }

    #[test]
    fn only_the_pilot_is_told_of_the_effect_the_ones_who_look_on_see_the_ship_change() {
        let (mut scene, t) = crowd();
        let out = scene.activate(A, afterburner(), t);
        for o in &out {
            if let Out::Notify { session: 8, name, .. } = o {
                assert_eq!(*name, "DoDestinyUpdate");
            }
        }
        let (later, _) = poll_until(&mut scene, t, 3.0);
        assert!(later.iter().all(|o| !matches!(o, Out::Notify { session: 8, .. })), "{later:?}");
    }

    #[test]
    fn a_ship_that_goes_faster_with_the_effect_gathers_speed_for_the_new_top_speed() {
        let (mut scene, t) = crowd();
        scene.command(A, Command::GotoDirection(eve_destiny::Vec3::new(1.0, 0.0, 0.0)), t);
        let (_, t) = poll_until(&mut scene, t, 60.0);
        let before = scene.ship(A, t).unwrap().velocity;
        let speed = |v: eve_destiny::Vec3| (v.x * v.x + v.y * v.y + v.z * v.z).sqrt();
        assert!((speed(before) - 339.25).abs() < 1.0, "{}", speed(before));
        scene.activate(A, afterburner(), t);
        let (_, t) = poll_until(&mut scene, t, 9.0);
        assert!(
            speed(scene.ship(A, t).unwrap().velocity) > 450.0,
            "{}",
            speed(scene.ship(A, t).unwrap().velocity)
        );
    }

    #[test]
    fn a_ship_that_leaves_takes_its_effects_with_it() {
        let (mut scene, t) = crowd();
        scene.activate(A, afterburner(), t);
        scene.leave(A, crate::scene::Departure::LoggedOff, t + SECOND);
        let (out, _) = poll_until(&mut scene, t + SECOND, 12.0);
        assert!(out.iter().all(|o| !matches!(o, Out::Notify { session: 7, .. })), "{out:?}");
    }
}
