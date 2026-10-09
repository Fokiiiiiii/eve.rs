//! What a ship can take and what it has taken: shield, armor and hull, the damage that goes through them and the shield's recharge.
//!
//! The host says what a ship can hold and how it resists (`Vitals`, worked out by dogma from the ship, its fitting and its pilot's skills) and what
//! it has taken when it comes into space (`Condition`, fractions, as the reference keeps them). The scene keeps the rest: damage goes through the
//! shield, then the armor, then the hull, each layer's resonances taken off the four kinds of damage; whatever a hit does not use in one layer goes
//! on to the next. The shield recharges along CCP's curve while it is below full. The reference rounds what it keeps of a hit to six places (the
//! stored fractions, not the points it tells), and this does the same, so the numbers a client is told are the same to the last digit.

use crate::scene::TICKS_PER_SECOND;

/// The attribute of a ship that holds its shield's charge, in points.
pub const SHIELD_CHARGE: i32 = 264;
/// The attribute that holds how much of its armor is gone, in points.
pub const ARMOR_DAMAGE: i32 = 266;
/// The attribute that holds how much of its hull is gone, in points.
pub const HULL_DAMAGE: i32 = 3;

/// The kinds of damage, in the order a vector of damage is written: em, thermal, kinetic, explosive.
pub type Damage = [f64; 4];

/// A shield within this many points of full is full (the reference settles it so).
const FULL_SNAP_POINTS: f64 = 1.0;
/// What a station's or an unhurtable thing's damage state says the shield's recharge is.
pub const UNHURTABLE_TAU_MS: f64 = 110_000.0;

pub(crate) fn round6(value: f64) -> f64 {
    (value * 1_000_000.0).round() / 1_000_000.0
}

/// How the three layers resist each kind of damage: the share of a hit that gets through, as dogma's resonance attributes give it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Resonances {
    pub shield: Damage,
    pub armor: Damage,
    pub structure: Damage,
}

impl Default for Resonances {
    fn default() -> Self {
        Self { shield: [1.0; 4], armor: [1.0; 4], structure: [1.0; 4] }
    }
}

/// What a ship can hold, in points, and how it resists. `resonances` are the ship's own (the host's, without a running reactive armor hardener's
/// adaptive profile, which the simulation keeps); `armor_pinned` is set while a fitted Polarized weapon holds the armor's resonances at one, and nothing
/// lifts it.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Vitals {
    pub shield_capacity: f64,
    pub armor_hp: f64,
    pub structure_hp: f64,
    pub resonances: Resonances,
    pub armor_pinned: bool,
}

impl Vitals {
    fn layers(&self) -> Layers {
        Layers {
            shield: self.shield_capacity.max(0.0),
            armor: self.armor_hp.max(0.0),
            structure: self.structure_hp.max(0.0),
        }
    }

    /// Whether there is anything to hurt (a thing with no points at all is shown whole and takes nothing).
    pub fn hurtable(&self) -> bool {
        let layers = self.layers();
        layers.shield > 0.0 || layers.armor > 0.0 || layers.structure > 0.0
    }
}

/// What a ship has taken, as fractions: `damage` of the hull, `armor_damage` of the armor, the `shield_charge` it has left of the shield, and how
/// full its capacitor is (`charge`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Condition {
    pub damage: f64,
    pub armor_damage: f64,
    pub shield_charge: f64,
    pub charge: f64,
}

impl Condition {
    /// A ship that has taken nothing.
    pub const FULL: Condition = Condition { damage: 0.0, armor_damage: 0.0, shield_charge: 1.0, charge: 1.0 };
}

/// Points left in each layer.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Layers {
    pub shield: f64,
    pub armor: f64,
    pub structure: f64,
}

/// What the client is told of a ship's health, `((shield, shield tau, time), armor, structure)`: fractions, and how long the shield takes to
/// recharge (a fifth of its recharge time, in milliseconds).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct DamageState {
    pub shield: f64,
    pub shield_tau_ms: f64,
    pub armor: f64,
    pub structure: f64,
}

impl DamageState {
    /// A station: whole, and always with the same recharge.
    pub const fn whole(shield_tau_ms: f64) -> Self {
        Self { shield: 1.0, shield_tau_ms, armor: 1.0, structure: 1.0 }
    }
}

/// A layer of a ship's health.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Layer {
    Shield,
    Armor,
    Structure,
}

/// What a hit or a repair did: the points before and after, and what the client is told changed.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Applied {
    pub before: Layers,
    pub after: Layers,
    /// What the layers took, after resonances.
    pub taken: Layers,
    /// How much armor was gone, in points, before and after (from the stored fractions).
    pub armor_damage: (f64, f64),
    max_structure: f64,
    /// The hull is gone.
    pub destroyed: bool,
    /// The shield, armor or hull is not what it was (by more than a millionth).
    pub changed: bool,
}

impl Applied {
    /// The attributes the pilot is told changed, `(attribute, new value, old value)`: the shield's charge, the armor gone and the hull gone, in
    /// points, those that differ.
    pub fn attribute_changes(&self) -> Vec<(i32, f64, f64)> {
        let hull = |layers: &Layers| round6((self.max_structure - layers.structure).max(0.0));
        [
            (SHIELD_CHARGE, round6(self.after.shield), round6(self.before.shield)),
            (ARMOR_DAMAGE, round6(self.armor_damage.1), round6(self.armor_damage.0)),
            (HULL_DAMAGE, hull(&self.after), hull(&self.before)),
        ]
        .into_iter()
        .filter(|(_, new, old)| new != old)
        .collect()
    }
}

/// The adaptive resonances of a reactive armor hardener that runs on the ship: the module's multipliers of the armor's resonances (`profile`, the
/// effective armor resonance is the ship's own times these), the damage the armor took this cycle per kind, weighted by the resonance it had (`tally`),
/// and the most a kind gives up each cycle (`step`). Owned by the simulation: nothing else writes it (see `crate::resistance_shift`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Shift {
    pub profile: Damage,
    pub tally: Damage,
    pub step: f64,
}

/// A ship's health: its vitals, what it has taken and the shield's recharge.
#[derive(Debug, Clone, PartialEq)]
pub struct Health {
    vitals: Vitals,
    /// How long the shield takes to recharge, in seconds.
    recharge_s: f64,
    /// The recharge as the client is told it (`shieldRechargeRate / 5`, in milliseconds).
    tau_ms: f64,
    /// The fractions as they were at `at` (the shield has recharged since).
    condition: Condition,
    at: i64,
    /// When the shield's charge is next told to its pilot, while it is below full.
    pub(crate) next_sample: Option<i64>,
    /// The reactive armor hardener's adaptive resonances, while one runs (a ship has at most one).
    shift: Option<Shift>,
}

impl Health {
    pub fn new(vitals: Vitals, shield_recharge_ms: f64, condition: Condition, now: i64) -> Self {
        Self {
            vitals,
            recharge_s: shield_recharge_ms / 1000.0,
            tau_ms: shield_recharge_ms / 5.0,
            condition: Condition {
                damage: condition.damage.clamp(0.0, 1.0),
                armor_damage: condition.armor_damage.clamp(0.0, 1.0),
                shield_charge: condition.shield_charge.clamp(0.0, 1.0),
                charge: condition.charge.clamp(0.0, 1.0),
            },
            at: now,
            next_sample: if condition.shield_charge < 1.0 && vitals.shield_capacity > 0.0 {
                Some(now)
            } else {
                None
            },
            shift: None,
        }
    }

    /// The armor's resonances as they are now: the ship's own, each times the adaptive profile of a running reactive armor hardener, or all one while
    /// a Polarized weapon pins them (the pin wins: the profile is not applied over it).
    pub fn armor_resonance(&self) -> Damage {
        if self.vitals.armor_pinned {
            return [1.0; 4];
        }
        let base = self.vitals.resonances.armor;
        match self.shift {
            Some(shift) => std::array::from_fn(|i| base[i] * shift.profile[i]),
            None => base,
        }
    }

    /// The adaptive profile of the running reactive armor hardener, if one runs.
    pub fn shift_profile(&self) -> Option<Damage> {
        self.shift.map(|shift| shift.profile)
    }

    /// A reactive armor hardener starts on the ship: its resonances are `seed` (the module's own values, in the order of a damage vector) and move
    /// by `step` a cycle. Its tally starts from nothing, so a hardener that starts again starts from its seed.
    pub fn start_shift(&mut self, seed: Damage, step: f64) {
        self.shift = Some(Shift { profile: seed.map(|r| r.clamp(0.0, 1.0)), tally: [0.0; 4], step });
    }

    /// The hardener stops: the armor is the ship's own again, and nothing is kept for a later start.
    pub fn stop_shift(&mut self) {
        self.shift = None;
    }

    /// A cycle of the running hardener ends: its resonances move as the damage the armor took in the cycle says, and the tally starts again. The new
    /// profile when it moved (the armor's resonances then change), None when nothing runs, when it did not move, or when the armor is pinned (a pinned
    /// armor takes no shift and keeps no tally).
    pub fn shift_cycle(&mut self) -> Option<Damage> {
        let pinned = self.vitals.armor_pinned;
        let shift = self.shift.as_mut()?;
        let tally = std::mem::replace(&mut shift.tally, [0.0; 4]);
        if pinned {
            return None;
        }
        let next = crate::resistance_shift::next_profile(shift.profile, tally, shift.step)?;
        shift.profile = next;
        Some(next)
    }

    /// The ship has new numbers: its absolute damage stays the same, while capacitor charge keeps its fraction.
    pub fn respec(&mut self, vitals: Vitals, shield_recharge_ms: f64, now: i64) {
        let condition = self.condition_at(now);
        let before = self.vitals.layers();
        let after = vitals.layers();
        let shield_damage = (before.shield * (1.0 - condition.shield_charge)).max(0.0);
        let armor_damage = (before.armor * condition.armor_damage).max(0.0);
        let hull_damage = (before.structure * condition.damage).max(0.0);

        self.vitals = vitals;
        self.recharge_s = shield_recharge_ms / 1000.0;
        self.tau_ms = shield_recharge_ms / 5.0;
        self.condition = Condition {
            damage: if after.structure > 0.0 { hull_damage / after.structure } else { 0.0 },
            armor_damage: if after.armor > 0.0 { armor_damage / after.armor } else { 0.0 },
            shield_charge: if after.shield > 0.0 { 1.0 - shield_damage / after.shield } else { 1.0 },
            charge: condition.charge,
        };
        self.at = now;
    }

    pub fn vitals(&self) -> &Vitals {
        &self.vitals
    }

    /// The shield's fraction at `at`: it recharges along `C1/Cmax = (1 + (sqrt(C0/Cmax) - 1) * e^(-5 dt / T))²`, from one point when it is empty, and is
    /// full within a point of it.
    pub fn shield_ratio_at(&self, now: i64) -> f64 {
        let capacity = self.vitals.shield_capacity;
        let start = self.condition.shield_charge;
        if capacity <= 0.0 || start >= 1.0 {
            return start;
        }
        let seconds = (now - self.at) as f64 / TICKS_PER_SECOND as f64;
        if seconds <= 0.0 || self.recharge_s <= 0.0 {
            return start;
        }
        let seed = if start > 0.0 { start } else { (1.0 / capacity.max(1.0)).min(1.0) };
        let root = 1.0 + (seed.sqrt() - 1.0) * (-5.0 * seconds / self.recharge_s).exp();
        let ratio = (root * root).clamp(0.0, 1.0);
        if ratio >= 1.0 || capacity * (1.0 - ratio) <= FULL_SNAP_POINTS { 1.0 } else { ratio }
    }

    /// What it has taken at `now`, with the shield as it has recharged to.
    pub fn condition_at(&self, now: i64) -> Condition {
        Condition { shield_charge: self.shield_ratio_at(now), ..self.condition }
    }

    /// The points left in each layer at `now`.
    pub fn layers_at(&self, now: i64) -> Layers {
        let max = self.vitals.layers();
        let shield =
            if max.shield > 0.0 { max.shield * self.shield_ratio_at(now).clamp(0.0, 1.0) } else { 0.0 };
        let armor = if max.armor > 0.0 {
            max.armor * (1.0 - self.condition.armor_damage).clamp(0.0, 1.0)
        } else {
            0.0
        };
        let structure = if max.structure > 0.0 {
            max.structure * (1.0 - self.condition.damage).clamp(0.0, 1.0)
        } else {
            0.0
        };
        Layers { shield, armor, structure }
    }

    /// What the client is told of the ship's health at `now`.
    pub fn state_at(&self, now: i64) -> DamageState {
        if !self.vitals.hurtable() {
            return DamageState::whole(UNHURTABLE_TAU_MS);
        }
        let max = self.vitals.layers();
        let layers = self.layers_at(now);
        let ratio = |left: f64, most: f64| if most > 0.0 { (left / most).clamp(0.0, 1.0) } else { 0.0 };
        DamageState {
            shield: ratio(layers.shield, max.shield),
            shield_tau_ms: if max.shield > 0.0 { self.tau_ms.max(0.0) } else { 0.0 },
            armor: ratio(layers.armor, max.armor),
            structure: ratio(layers.structure, max.structure),
        }
    }

    /// The points left in all the layers together at `now`, and the points the layers hold when whole.
    pub fn points_left(&self, now: i64) -> (f64, f64) {
        let left = self.layers_at(now);
        let whole = self.vitals.layers();
        (left.shield + left.armor + left.structure, whole.shield + whole.armor + whole.structure)
    }

    fn armor_gone(&self) -> f64 {
        self.vitals.armor_hp.max(0.0) * self.condition.armor_damage.max(0.0)
    }

    /// A hit of `raw` damage at `now`: the shield takes what it can, then the armor, then the hull. None when there is nothing to hurt.
    pub fn apply(&mut self, now: i64, raw: Damage) -> Option<Applied> {
        if !self.vitals.hurtable() {
            return None;
        }
        let max = self.vitals.layers();
        let before = self.layers_at(now);
        let armor_before = self.armor_gone();
        let mut left = raw.map(|d| d.max(0.0));
        let mut now_layers = before;
        let mut taken = Layers { shield: 0.0, armor: 0.0, structure: 0.0 };
        let pinned = self.vitals.armor_pinned;
        let resonances = Resonances { armor: self.armor_resonance(), ..self.vitals.resonances };
        for layer in 0..3 {
            let (most, hp, resonance) = match layer {
                0 => (max.shield, now_layers.shield, resonances.shield),
                1 => (max.armor, now_layers.armor, resonances.armor),
                _ => (max.structure, now_layers.structure, resonances.structure),
            };
            if most <= 0.0 || hp <= 0.0 || left.iter().sum::<f64>() <= 0.0 {
                continue;
            }
            let effective: f64 = left.iter().zip(resonance).map(|(d, r)| d * r).sum();
            if effective <= 0.0 {
                continue;
            }
            // what the layer takes of each kind, before its resonance
            let mut part_of = [0.0; 4];
            let (used, remaining) = if effective <= hp + 1e-9 {
                part_of = left;
                left = [0.0; 4];
                (effective, hp - effective)
            } else {
                let fraction = hp / effective;
                let mut used = 0.0;
                for (i, (d, r)) in left.iter_mut().zip(resonance).enumerate() {
                    let part = *d * fraction;
                    part_of[i] = part;
                    *d = (*d - part).max(0.0);
                    used += part * r;
                }
                (used, 0.0)
            };
            // a running reactive armor hardener is told what the armor took per kind (the damage it counts), unless a Polarized weapon holds it
            if layer == 1
                && !pinned
                && let Some(shift) = self.shift.as_mut()
            {
                for (i, part) in part_of.iter().enumerate() {
                    shift.tally[i] += part * resonance[i];
                }
            }
            match layer {
                0 => (now_layers.shield, taken.shield) = (remaining.max(0.0), used),
                1 => (now_layers.armor, taken.armor) = (remaining.max(0.0), used),
                _ => (now_layers.structure, taken.structure) = (remaining.max(0.0), used),
            }
        }
        let armor_after = armor_before + taken.armor;
        let unit = |left: f64, most: f64| (left / most).clamp(0.0, 1.0);
        self.condition = Condition {
            damage: if max.structure > 0.0 {
                round6(1.0 - unit(now_layers.structure, max.structure))
            } else {
                1.0
            },
            armor_damage: if max.armor > 0.0 { round6(armor_after / max.armor) } else { 0.0 },
            shield_charge: if max.shield > 0.0 { round6(unit(now_layers.shield, max.shield)) } else { 0.0 },
            charge: self.condition.charge,
        };
        self.at = now;
        // the pilot is told the shield's charge from the tick after a hit, every half second, while it recharges
        if self.condition.shield_charge < 1.0 && max.shield > 0.0 {
            self.next_sample.get_or_insert(now + crate::modules::TICK);
        }
        Some(Applied {
            before,
            after: now_layers,
            taken,
            armor_damage: (armor_before, self.armor_gone()),
            max_structure: max.structure,
            destroyed: max.structure > 0.0 && now_layers.structure <= 1e-9 && raw.iter().sum::<f64>() > 0.0,
            changed: before != now_layers,
        })
    }

    /// A repair of `amount` points to one layer at `now` (what a repairer or a booster does each cycle): the layer is given back that many points, up to what it holds. None when
    /// there is nothing to mend (a thing with no points, no such layer, or nothing to give back).
    pub fn repair(&mut self, now: i64, layer: Layer, amount: f64) -> Option<Applied> {
        if !self.vitals.hurtable() || amount.is_nan() || amount <= 0.0 {
            return None;
        }
        let max = self.vitals.layers();
        let most = match layer {
            Layer::Shield => max.shield,
            Layer::Armor => max.armor,
            Layer::Structure => max.structure,
        };
        if most <= 0.0 {
            return None;
        }
        let before = self.layers_at(now);
        let armor_before = self.armor_gone();
        let mut after = before;
        match layer {
            Layer::Shield => after.shield = (before.shield + amount).min(most),
            Layer::Armor => after.armor = (before.armor + amount).min(most),
            Layer::Structure => after.structure = (before.structure + amount).min(most),
        }
        let unit = |left: f64, most: f64| (left / most).clamp(0.0, 1.0);
        self.condition = Condition {
            damage: if max.structure > 0.0 {
                round6(1.0 - unit(after.structure, max.structure))
            } else {
                1.0
            },
            armor_damage: if max.armor > 0.0 { round6(1.0 - unit(after.armor, max.armor)) } else { 0.0 },
            shield_charge: if max.shield > 0.0 { round6(unit(after.shield, max.shield)) } else { 0.0 },
            charge: self.condition.charge,
        };
        self.at = now;
        if self.condition.shield_charge < 1.0 && max.shield > 0.0 {
            self.next_sample.get_or_insert(now + crate::modules::TICK);
        } else if max.shield > 0.0 {
            self.next_sample = None;
        }
        Some(Applied {
            before,
            after,
            taken: Layers { shield: 0.0, armor: 0.0, structure: 0.0 },
            armor_damage: (armor_before, self.armor_gone()),
            max_structure: max.structure,
            destroyed: false,
            changed: before != after,
        })
    }

    /// The ship is mended: full shield, armor and hull.
    pub fn restore(&mut self, now: i64) -> Applied {
        let max = self.vitals.layers();
        let before = self.layers_at(now);
        let before_condition = self.condition_at(now);
        let armor_before = self.armor_gone();
        self.condition =
            Condition { damage: 0.0, armor_damage: 0.0, shield_charge: 1.0, charge: self.condition.charge };
        self.at = now;
        self.next_sample = None;
        let after = self.layers_at(now);
        let changed = (before_condition.shield_charge - 1.0).abs() > 1e-6
            || before_condition.armor_damage.abs() > 1e-6
            || before_condition.damage.abs() > 1e-6;
        Applied {
            before,
            after,
            taken: Layers { shield: 0.0, armor: 0.0, structure: 0.0 },
            armor_damage: (armor_before, 0.0),
            max_structure: max.structure,
            destroyed: false,
            changed,
        }
    }

    /// The shield's charge to tell its pilot at `at`, `(new, old)` in points, `old` being the value a tick before, when the shield is still
    /// recharging. None when the shield was full a tick before (nothing more is told) or the two are the same to the millionth.
    pub(crate) fn shield_told(&self, at: i64) -> Option<(f64, f64)> {
        let capacity = self.vitals.shield_capacity;
        let before = at - crate::modules::TICK;
        if capacity <= 0.0 || self.shield_ratio_at(before) >= 1.0 {
            return None;
        }
        let (new, old) =
            (round6(capacity * self.shield_ratio_at(at)), round6(capacity * self.shield_ratio_at(before)));
        (new != old).then_some((new, old))
    }

    /// Whether the shield is full at `at`.
    pub(crate) fn shield_full_at(&self, at: i64) -> bool {
        self.vitals.shield_capacity <= 0.0 || self.shield_ratio_at(at) >= 1.0
    }
}

use eve_marshal::Value;

use crate::modules::{SAMPLE_EVERY, attribute_change, number};
use crate::scene::{Out, Pilot, Scene, SessionId};

impl Scene {
    /// The pilot is told which of its ship's attributes changed (one notification for all of them), or nothing when none did.
    pub(crate) fn health_told(
        session: SessionId,
        owner: i64,
        ship: i64,
        when: i64,
        changes: &[(i32, f64, f64)],
    ) -> Option<Out> {
        if changes.is_empty() {
            return None;
        }
        let lines: Vec<Value> = changes
            .iter()
            .map(|&(attribute, new, old)| {
                attribute_change(owner, ship, attribute, when, number(new), number(old))
            })
            .collect();
        Some(Self::dogma_notify(session, "OnModuleAttributeChanges", vec![Value::List(lines)]))
    }

    /// What a pilot's ship has taken, and how full its capacitor is, at `now`.
    pub(crate) fn condition_of(pilot: &Pilot, now: i64) -> Condition {
        Condition {
            charge: pilot.modules.capacitor.as_ref().map_or(1.0, |c| c.ratio_at(now)),
            ..pilot.ship.health.condition_at(now)
        }
    }

    /// A hit of `raw` damage lands on the ship `ship_id` at `now`: its layers take it, the pilot is told the attributes that changed and everyone who
    /// can see the ship, has it locked or flies it is told its damage state. What the hit did comes back (None when the ship is not here or has
    /// nothing to hurt).
    pub fn damage(&mut self, ship_id: i64, raw: Damage, now: i64) -> (Vec<Out>, Option<Applied>) {
        let Some(session) = self.pilots.iter().find(|(_, p)| p.ship.spec.ship_id == ship_id).map(|(s, _)| *s)
        else {
            return (Vec::new(), None);
        };
        let pilot = self.pilots.get_mut(&session).expect("the pilot is in the scene");
        let owner = pilot.ship.spec.character_id;
        let Some(applied) = pilot.ship.health.apply(now, raw) else { return (Vec::new(), None) };
        let mut out: Vec<Out> = Self::health_told(session, owner, ship_id, now, &applied.attribute_changes())
            .into_iter()
            .collect();
        if applied.changed
            && let Some(subject) = self.subject(ship_id, now)
        {
            out.extend(self.announce_damage_state(&subject, now));
        }
        // a squadron that lost members is told its size (one with none left is destroyed, and told so)
        if !applied.destroyed {
            out.extend(self.tell_squadron_size(session, now));
        }
        (out, Some(applied))
    }

    /// `amount` points are given back to a layer of the ship `ship_id` at `now` (a repairer, a booster, a remote repairer): the pilot is told the attributes that
    /// changed and everyone who can see the ship, has it locked or flies it is told its damage state when it changed. Nothing is told when nothing was
    /// mended (the layer is whole, or the ship is not here).
    pub(crate) fn mend(&mut self, ship_id: i64, layer: Layer, amount: f64, now: i64) -> Vec<Out> {
        let Some(session) = self.session_of(ship_id) else { return Vec::new() };
        let pilot = self.pilots.get_mut(&session).expect("the pilot is in the scene");
        let owner = pilot.ship.spec.character_id;
        let Some(applied) = pilot.ship.health.repair(now, layer, amount) else { return Vec::new() };
        let mut out: Vec<Out> = Self::health_told(session, owner, ship_id, now, &applied.attribute_changes())
            .into_iter()
            .collect();
        if applied.changed
            && let Some(subject) = self.subject(ship_id, now)
        {
            out.extend(self.announce_damage_state(&subject, now));
        }
        out
    }

    /// The host asks for the ship to be mended (`Heal`, command 18): full shield, armor, hull and capacitor. The pilot is told the attributes that
    /// changed and the capacitor, those who can see the ship are told its damage state when it changed, and the command is answered last.
    pub fn heal(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        const CODE: u8 = 18;
        let Some(pilot) = self.pilots.get_mut(&session) else {
            return vec![Out::Refused { session, command: CODE, reason: "NO_SHIP_IN_SPACE" }];
        };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let mended = pilot.ship.health.restore(now);
        let (before, after) = pilot.modules.capacitor.as_mut().map_or((0.0, 0.0), |c| c.refill(now));
        let mut out: Vec<Out> = Self::health_told(session, owner, ship_id, now, &mended.attribute_changes())
            .into_iter()
            .collect();
        if after > 0.0 {
            out.push(Self::charge_told(session, owner, ship_id, now, after, before));
        }
        if mended.changed
            && let Some(subject) = self.subject(ship_id, now)
        {
            out.extend(self.announce_damage_state(&subject, now));
        }
        out.push(Out::Accepted { session, command: CODE, at: now });
        out
    }

    /// The host asks what the ship has taken (`GetCondition`, command 19).
    pub fn condition(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        const CODE: u8 = 19;
        match self.pilots.get(&session) {
            Some(pilot) => {
                vec![Out::Condition { session, command: CODE, condition: Self::condition_of(pilot, now) }]
            }
            None => vec![Out::Refused { session, command: CODE, reason: "NO_SHIP_IN_SPACE" }],
        }
    }

    /// The shield's charge is told: now, and a tenth of a second before; the next time is half a second on while the shield is below full.
    pub(crate) fn tell_shield(&mut self, session: SessionId, at: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let health = &mut pilot.ship.health;
        let told = health.shield_told(at);
        health.next_sample = if health.shield_full_at(at) { None } else { Some(at + SAMPLE_EVERY) };
        told.into_iter()
            .filter_map(|(new, old)| {
                Self::health_told(session, owner, ship_id, at, &[(SHIELD_CHARGE, new, old)])
            })
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SECOND: i64 = TICKS_PER_SECOND;

    /// An Ibis's points (220 shield, 137.5 armor, 201.25 hull; the shield takes 860 s to recharge) with resistances made up so that each kind of
    /// damage does something different to each layer (the real ones are the host's, worked out by dogma).
    fn frigate() -> Vitals {
        Vitals {
            shield_capacity: 220.0,
            armor_hp: 137.5,
            structure_hp: 201.25,
            resonances: Resonances {
                shield: [0.0, 0.8, 0.6, 0.5],
                armor: [0.5, 0.35, 0.25, 0.1],
                structure: [0.33, 0.33, 0.33, 0.33],
            },
            armor_pinned: false,
        }
    }

    fn health() -> Health {
        Health::new(frigate(), 860_000.0, Condition::FULL, 0)
    }

    #[test]
    fn a_whole_ship_is_told_whole() {
        let state = health().state_at(0);
        assert_eq!(state, DamageState { shield: 1.0, shield_tau_ms: 172_000.0, armor: 1.0, structure: 1.0 });
    }

    #[test]
    fn a_thing_with_no_points_is_told_whole_with_the_recharge_of_a_station() {
        let bare = Health::new(Vitals::default(), 860_000.0, Condition::FULL, 0);
        assert_eq!(bare.state_at(0), DamageState::whole(UNHURTABLE_TAU_MS));
        let mut bare = bare;
        assert!(bare.apply(0, [10.0; 4]).is_none());
    }

    #[test]
    fn a_hit_is_taken_by_the_shield_through_its_resonances() {
        let mut health = health();
        // 100 thermal, 100 kinetic: the shield lets through 0.8 and 0.6 of them
        let applied = health.apply(0, [0.0, 100.0, 100.0, 0.0]).unwrap();
        assert_eq!(applied.before.shield, 220.0);
        assert!((applied.after.shield - (220.0 - 140.0)).abs() < 1e-9);
        assert_eq!(applied.after.armor, 137.5);
        assert_eq!(applied.after.structure, 201.25);
        assert!(!applied.destroyed);
        assert_eq!(applied.taken.shield, 140.0);
        assert!((health.state_at(0).shield - 80.0 / 220.0).abs() < 1e-6);
    }

    #[test]
    fn what_the_shield_cannot_take_goes_on_to_the_armor_in_the_same_proportions() {
        let mut health = health();
        // 500 explosive: the shield holds 220 points and takes 0.5 of each point, so it soaks 440 of them; 60 explosive are left for the armor
        let applied = health.apply(0, [0.0, 0.0, 0.0, 500.0]).unwrap();
        assert_eq!(applied.after.shield, 0.0);
        assert!((applied.taken.shield - 220.0).abs() < 1e-9);
        assert!((applied.taken.armor - 60.0 * 0.1).abs() < 1e-9);
        assert!((applied.after.armor - (137.5 - 6.0)).abs() < 1e-9);
        assert_eq!(applied.after.structure, 201.25);
        assert_eq!(health.condition_at(0).shield_charge, 0.0);
    }

    #[test]
    fn a_mixed_hit_uses_up_the_shield_and_takes_the_rest_of_each_kind_from_the_armor() {
        let mut health = health();
        let applied = health.apply(0, [200.0, 200.0, 200.0, 200.0]).unwrap();
        // effective against the shield: 0 + 160 + 120 + 100 = 380 points, the shield has 220: 220/380 of the hit is used
        assert_eq!(applied.after.shield, 0.0);
        let left = 1.0 - 220.0 / 380.0;
        let expected: f64 = [0.5, 0.35, 0.25, 0.1].iter().map(|r| 200.0 * left * r).sum();
        assert!((applied.taken.armor - expected).abs() < 1e-6, "{} {}", applied.taken.armor, expected);
        assert!(applied.taken.structure == 0.0);
    }

    #[test]
    fn a_hit_that_goes_through_everything_destroys_the_ship() {
        let mut health = health();
        let applied = health.apply(0, [10_000.0; 4]).unwrap();
        assert!(applied.destroyed);
        assert_eq!(applied.after, Layers { shield: 0.0, armor: 0.0, structure: 0.0 });
        assert_eq!(health.state_at(0).structure, 0.0);
    }

    #[test]
    fn a_hit_that_does_nothing_does_not_change_the_ship() {
        let mut health = health();
        let applied = health.apply(0, [0.0; 4]).unwrap();
        assert!(!applied.changed && !applied.destroyed);
        assert!(applied.attribute_changes().is_empty());
        // damage below nothing is nothing
        let applied = health.apply(0, [-5.0, -1.0, -3.0, -9.0]).unwrap();
        assert!(!applied.changed);
    }

    #[test]
    fn a_kind_of_damage_the_shield_does_not_resist_at_all_goes_through_to_the_armor() {
        // the shield's em resonance is 0: nothing of it is taken there (a layer that would take nothing is skipped), the armor takes it at 0.5
        let mut health = health();
        let applied = health.apply(0, [50.0, 0.0, 0.0, 0.0]).unwrap();
        assert_eq!(applied.taken.shield, 0.0);
        assert_eq!(applied.taken.armor, 25.0);
        assert_eq!(applied.after.shield, 220.0);
    }

    #[test]
    fn what_is_kept_of_a_hit_is_rounded_to_six_places_and_what_is_told_of_it_is_not() {
        let mut health = health();
        let applied = health.apply(0, [0.0, 22.680704, 0.0, 0.0]).unwrap();
        // 22.680704 * 0.8 points are taken: 220 - 18.1445632 = 201.8554368; the fraction kept is that over 220, rounded: 0.917525
        assert!((applied.after.shield - 201.8554368).abs() < 1e-9);
        assert_eq!(health.condition_at(0).shield_charge, 0.917525);
        // the pilot is told 201.855437 was left and, a tick later, that it was 201.8555 (the stored fraction) before
        assert_eq!(applied.attribute_changes(), [(SHIELD_CHARGE, 201.855437, 220.0)]);
        assert_eq!(health.shield_told(crate::modules::TICK), Some((201.865_819, 201.8555)));
    }

    #[test]
    fn armor_and_hull_damage_are_told_in_points_as_it_grows() {
        let mut health = health();
        let applied = health.apply(0, [0.0, 0.0, 0.0, 1_000.0]).unwrap();
        // 440 explosive empty the shield; the 560 left do 56 points to the armor
        let changes = applied.attribute_changes();
        assert_eq!(changes[0], (SHIELD_CHARGE, 0.0, 220.0));
        assert_eq!(changes[1].0, ARMOR_DAMAGE);
        // what is told of the armor is the fraction kept (to six places) of its points: a few millionths off the 56 that were taken
        assert!((changes[1].1 - 56.0).abs() < 1e-4 && changes[1].2 == 0.0);
        let applied = health.apply(0, [0.0, 0.0, 0.0, 2_000.0]).unwrap();
        let changes = applied.attribute_changes();
        // the armor is gone (137.5 in all, 56 already) and the rest of the hit goes through to the hull at 0.33
        assert_eq!(changes.iter().map(|c| c.0).collect::<Vec<_>>(), [ARMOR_DAMAGE, HULL_DAMAGE]);
        assert!((changes[0].1 - 137.5).abs() < 1e-4 && (changes[0].2 - 56.0).abs() < 1e-4);
        assert!(changes[1].1 > 0.0 && changes[1].2 == 0.0);
    }

    #[test]
    fn the_shield_recharges_along_the_curve_the_reference_shows() {
        // the recording: 201.855437 left at the hit, 201.865819 a tenth of a second on (the stored fraction 0.917525 is what it goes on from)
        let mut health = health();
        health.apply(0, [0.0, 22.680704, 0.0, 0.0]);
        let at = |ticks: i64| 220.0 * health.shield_ratio_at(ticks * SECOND / 10);
        assert!((at(1) - 201.865_819).abs() < 2e-6, "{}", at(1));
        // and 0.05 a half second on: the recorded 201.917433 is five ticks later (the reference's ticks are not exactly a tenth of a second apart)
        assert!((at(6) - 201.917_433).abs() < 5e-4, "{}", at(6));
        assert!(at(50) > at(6) && at(600) > at(50));
    }

    #[test]
    fn an_empty_shield_recharges_from_one_point() {
        let mut health = health();
        health.apply(0, [0.0, 0.0, 0.0, 440.0]);
        assert_eq!(health.shield_ratio_at(0), 0.0);
        // a second on, from the point it is seeded with (a two hundred and twentieth of it): 1.166805 points, as a mended-at-once shield was told
        assert!(
            (220.0 * health.shield_ratio_at(SECOND) - 1.166_805).abs() < 1e-5,
            "{}",
            220.0 * health.shield_ratio_at(SECOND)
        );
    }

    #[test]
    fn a_shield_within_a_point_of_full_is_full() {
        let mut health = health();
        health.apply(0, [0.0, 10.0, 0.0, 0.0]);
        // it is a long time later: the curve has all but reached the top
        assert_eq!(health.shield_ratio_at(3_000 * SECOND), 1.0);
        assert!(health.shield_ratio_at(10 * SECOND) < 1.0);
    }

    #[test]
    fn the_pilot_is_told_the_shield_from_the_tick_after_a_hit_and_not_when_it_was_full_a_tick_before() {
        let mut health = health();
        assert_eq!(health.next_sample, None);
        health.apply(0, [0.0, 50.0, 0.0, 0.0]);
        assert_eq!(health.next_sample, Some(crate::modules::TICK));
        // once full nothing is told
        assert_eq!(health.shield_told(4_000 * SECOND), None);
    }

    #[test]
    fn a_mended_ship_is_whole_and_says_what_changed() {
        let mut health = health();
        health.apply(0, [0.0, 0.0, 0.0, 1_000.0]);
        health.apply(0, [0.0, 0.0, 0.0, 2_000.0]);
        let mended = health.restore(SECOND);
        assert!(mended.changed);
        assert_eq!(
            health.state_at(SECOND),
            DamageState { shield: 1.0, shield_tau_ms: 172_000.0, armor: 1.0, structure: 1.0 }
        );
        let changes = mended.attribute_changes();
        assert_eq!(
            changes.iter().map(|c| c.0).collect::<Vec<_>>(),
            [SHIELD_CHARGE, ARMOR_DAMAGE, HULL_DAMAGE]
        );
        // the empty shield had recharged for a second from one point
        assert!(changes[0].1 == 220.0 && changes[0].2 > 1.0 && changes[0].2 < 2.0, "{changes:?}");
        assert_eq!(changes[1].1, 0.0);
        assert_eq!(changes[2].1, 0.0);
        // mended again there is nothing to say
        let again = health.restore(2 * SECOND);
        assert!(!again.changed && again.attribute_changes().is_empty());
    }

    #[test]
    fn a_ship_that_comes_in_hurt_is_as_hurt_as_it_was_told() {
        let hurt = Condition { damage: 0.35, armor_damage: 0.6, shield_charge: 1.0, charge: 1.0 };
        let health = Health::new(frigate(), 860_000.0, hurt, 0);
        // the recording: armor 0.4 and hull 0.65 left; 82.5 points of armor and 70.4375 of hull gone
        let state = health.state_at(0);
        assert_eq!((state.shield, state.armor, state.structure), (1.0, 0.4, 0.65));
        let layers = health.layers_at(0);
        assert!(
            (137.5 - layers.armor - 82.5).abs() < 1e-9 && (201.25 - layers.structure - 70.4375).abs() < 1e-9
        );
    }

    #[test]
    fn new_numbers_keep_absolute_layer_damage_and_capacitor_fraction() {
        let mut health = health();
        health.apply(0, [0.0, 0.0, 0.0, 1_000.0]);
        let before = health.condition_at(SECOND);
        let old = health.vitals.layers();
        let hull_damage = old.structure * before.damage;
        let armor_damage = old.armor * before.armor_damage;
        let shield_damage = old.shield * (1.0 - before.shield_charge);
        let capacitor_fraction = before.charge;
        let mut bigger = health.vitals;
        bigger.shield_capacity *= 2.0;
        bigger.armor_hp *= 2.0;
        bigger.structure_hp *= 2.0;

        health.respec(bigger, 860_000.0, SECOND);

        let after = health.condition_at(SECOND);
        let new = health.vitals.layers();
        assert!((new.structure * after.damage - hull_damage).abs() < 1e-9);
        assert!((new.armor * after.armor_damage - armor_damage).abs() < 1e-9);
        assert!((new.shield * (1.0 - after.shield_charge) - shield_damage).abs() < 1e-9);
        assert_eq!(after.charge, capacitor_fraction);
    }


    #[test]
    fn negative_hull_after_respec_waits_until_the_next_hull_damage()
    {
        let condition = Condition {
            damage: 0.9,
            armor_damage: 1.0,
            shield_charge: 0.0,
            charge: 1.0,
        };
        let original = frigate();
        let mut health = Health::new(original, 860_000.0, condition, 0);
        let mut smaller = health.vitals;
        smaller.structure_hp *= 0.8;

        health.respec(smaller, 860_000.0, 0);

        assert!(health.condition_at(0).damage > 1.0);
        assert_eq!(health.layers_at(0).structure, 0.0);
        let hit = health.apply(SECOND, [1.0, 0.0, 0.0, 0.0]).expect("the next hull hit is processed");
        assert!(hit.destroyed);
    }

    #[test]
    fn a_shield_that_starts_below_full_is_told_from_the_start() {
        let health =
            Health::new(frigate(), 860_000.0, Condition { shield_charge: 0.7, ..Condition::FULL }, 100);
        assert_eq!(health.next_sample, Some(100));
        assert!((health.layers_at(100).shield - 154.0).abs() < 1e-9);
    }

    // ---- in a scene ----

    use std::sync::Arc;

    use crate::scene::tests::{NOON, STATION, spec, system};
    use crate::ship::ShipSpec;

    const A: SessionId = 7;
    const B: SessionId = 8;

    fn other_spec() -> ShipSpec {
        let mut other = spec();
        other.ship_id += 4;
        other.character_id += 1;
        other.owner_id += 1;
        other
    }

    /// Two ships that have bound the ballpark, forty seconds after they undocked (their protection is over).
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

    /// The lines of the `OnModuleAttributeChanges` sent to `session`: `(attribute, new, old)`, one list a notification.
    fn attribute_lists(out: &[Out], session: SessionId) -> Vec<Vec<(i64, Value, Value)>> {
        out.iter()
            .filter_map(|o| match o {
                Out::Notify { session: s, name: "OnModuleAttributeChanges", args, .. } if *s == session => {
                    let Some(Value::List(lines)) = args.first() else { panic!("{args:?}") };
                    Some(
                        lines
                            .iter()
                            .map(|line| {
                                let Value::Tuple(t) = line else { panic!() };
                                let Value::Int(attribute) = t[3] else { panic!("{:?}", t[3]) };
                                (attribute, t[5].clone(), t[6].clone())
                            })
                            .collect(),
                    )
                }
                _ => None,
            })
            .collect()
    }

    /// The damage states told in `DoDestinyUpdate`s, `(session, ship, shield, armor, structure)`.
    fn damage_states(out: &[Out]) -> Vec<(SessionId, Value, Value, Value, Value)> {
        let mut states = Vec::new();
        for o in out {
            let Out::Notify { session, name: "DoDestinyUpdate", args, .. } = o else { continue };
            let Some(Value::List(actions)) = args.first() else { continue };
            for action in actions {
                let Value::Tuple(pair) = action else { continue };
                let Value::Tuple(inner) = &pair[1] else { continue };
                if inner[0] != Value::Str("OnDamageStateChange".into()) {
                    continue;
                }
                let Value::Tuple(arguments) = &inner[1] else { continue };
                let Value::Tuple(state) = &arguments[1] else { continue };
                let Value::Tuple(shield) = &state[0] else { continue };
                states.push((
                    *session,
                    arguments[0].clone(),
                    shield[0].clone(),
                    state[1].clone(),
                    state[2].clone(),
                ));
            }
        }
        states
    }

    fn ship_of(spec: &ShipSpec) -> i64 {
        spec.ship_id
    }

    #[test]
    fn a_hit_tells_the_pilot_what_changed_and_everyone_who_can_see_the_ship_its_damage_state() {
        let (mut scene, t) = crowd();
        let (out, applied) = scene.damage(ship_of(&spec()), [0.0, 22.680704, 0.0, 0.0], t);
        assert!(applied.is_some());
        assert_eq!(
            attribute_lists(&out, A),
            [vec![(i64::from(SHIELD_CHARGE), Value::Real(201.855437), Value::Int(220))]],
            "the pilot is told the shield's charge, a whole number as an integer"
        );
        assert!(attribute_lists(&out, B).is_empty(), "the other pilot is not told the ship's attributes");
        let states = damage_states(&out);
        assert_eq!(
            states.iter().map(|s| s.0).collect::<Vec<_>>(),
            [A, B],
            "the ship's own pilot and the one that looks on"
        );
        let (_, ship, shield, armor, structure) = &states[0];
        assert_eq!(*ship, crate::wire::id(ship_of(&spec())));
        assert_eq!((armor, structure), (&Value::Real(1.0), &Value::Real(1.0)));
        assert_eq!(*shield, Value::Real(0.917525));
    }

    #[test]
    fn a_hit_the_ship_does_not_feel_says_nothing() {
        let (mut scene, t) = crowd();
        let (out, applied) = scene.damage(ship_of(&spec()), [0.0; 4], t);
        assert!(out.is_empty() && applied.is_some_and(|a| !a.changed));
        let (out, applied) = scene.damage(99, [10.0; 4], t);
        assert!(out.is_empty() && applied.is_none(), "no such ship");
    }

    #[test]
    fn a_hit_that_goes_through_the_armor_tells_the_armor_and_the_hull_in_points() {
        let (mut scene, t) = crowd();
        let (out, applied) = scene.damage(ship_of(&spec()), [0.0, 0.0, 0.0, 1_000.0], t);
        assert!(applied.is_some());
        let lists = attribute_lists(&out, A);
        assert_eq!(lists.len(), 1, "one notification for all the attributes");
        assert_eq!(lists[0].iter().map(|l| l.0).collect::<Vec<_>>(), [264, 266]);
        let states = damage_states(&out);
        let Value::Real(armor) = &states[0].3 else { panic!() };
        assert!(*armor < 1.0 && (*armor - (1.0 - 56.0 / 137.5)).abs() < 1e-5);
    }

    #[test]
    fn the_shield_is_told_a_tick_after_a_hit_and_then_every_half_second_while_it_recharges() {
        let (mut scene, t) = crowd();
        scene.damage(ship_of(&spec()), [0.0, 22.680704, 0.0, 0.0], t);
        // the poll at t + 0.25 s takes in the moments at 0.1 s and 0.6 s? no: 0.1 s only; the next is 0.6 s
        let (out, _) = poll_until(&mut scene, t, 3.0);
        let told = attribute_lists(&out, A);
        assert_eq!(told.len(), 6, "at 0.1, 0.6, 1.1, 1.6, 2.1 and 2.6 s: {told:?}");
        // the first: the shield a tenth of a second on and what the stored fraction was
        assert_eq!(told[0], [(264, Value::Real(201.865819), Value::Real(201.8555))]);
        assert!(told.iter().all(|l| l.len() == 1 && l[0].0 == 264));
        assert!(attribute_lists(&out, B).is_empty());
        // and no damage state is sent: the client works the recharge out itself
        assert!(damage_states(&out).is_empty());
    }

    #[test]
    fn the_stream_stops_when_the_shield_is_full() {
        let (mut scene, t) = crowd();
        scene.damage(ship_of(&spec()), [0.0, 5.0, 0.0, 0.0], t);
        // a long time on: the shield is back and nothing more is told
        let (_, t2) = poll_until(&mut scene, t, 1.0);
        let (out, t3) = poll_until(&mut scene, t2, 1_000.0);
        assert!(!attribute_lists(&out, A).is_empty());
        let (later, _) = poll_until(&mut scene, t3, 5.0);
        assert!(attribute_lists(&later, A).is_empty(), "{later:?}");
        assert_eq!(scene.pilots[&A].ship.health.state_at(t3).shield, 1.0);
        // and the ship is no longer looked at for a stream that has ended
        assert_eq!(scene.pilots[&A].ship.health.next_sample, None);
    }

    #[test]
    fn a_heal_tells_the_pilot_then_the_capacitor_then_everyone_the_damage_state_and_answers_last() {
        let (mut scene, t) = crowd();
        scene.damage(ship_of(&spec()), [0.0, 0.0, 0.0, 1_000.0], t);
        scene.damage(ship_of(&spec()), [0.0, 0.0, 0.0, 1_000.0], t + SECOND);
        let out = scene.heal(A, t + 2 * SECOND);
        let names: Vec<String> = out
            .iter()
            .map(|o| match o {
                Out::Notify { session, name, .. } => format!("{session}:{name}"),
                Out::Accepted { session, command, .. } => format!("{session}:accepted {command}"),
                other => format!("{other:?}"),
            })
            .collect();
        assert_eq!(
            names,
            [
                "7:OnModuleAttributeChanges",
                "7:OnModuleAttributeChanges",
                "7:DoDestinyUpdate",
                "8:DoDestinyUpdate",
                "7:accepted 18"
            ]
        );
        let lists = attribute_lists(&out, A);
        assert!(
            lists[0].iter().map(|l| l.0).eq([264, 266, 3])
                || lists[0].iter().map(|l| l.0).eq([266, 3])
                || lists[0].iter().map(|l| l.0).eq([264, 266])
        );
        // the capacitor: 149.5 and 149.5 as it was (it was not touched)
        assert_eq!(lists[1], [(18, Value::Real(149.5), Value::Real(149.5))]);
        assert!(
            damage_states(&out)
                .iter()
                .all(|s| s.2 == Value::Real(1.0) && s.3 == Value::Real(1.0) && s.4 == Value::Real(1.0))
        );
        assert_eq!(scene.pilots[&A].ship.health.state_at(t + 2 * SECOND).structure, 1.0);
    }

    #[test]
    fn a_heal_of_a_whole_ship_tells_only_the_capacitor_and_no_damage_state() {
        let (mut scene, t) = crowd();
        let out = scene.heal(A, t);
        assert_eq!(attribute_lists(&out, A).len(), 1);
        assert!(damage_states(&out).is_empty());
        assert!(matches!(out.last(), Some(Out::Accepted { command: 18, .. })));
    }

    #[test]
    fn a_heal_fills_a_capacitor_that_was_low_and_stops_its_stream() {
        let (mut scene, t) = crowd();
        scene.activate(A, crate::modules::tests::afterburner(), t);
        let out = scene.heal(A, t + SECOND / 2);
        let lists = attribute_lists(&out, A);
        let last = lists.last().unwrap();
        assert_eq!(last[0].0, 18);
        let Value::Real(new) = last[0].1 else { panic!() };
        assert_eq!(new, 149.5);
        let (later, _) = poll_until(&mut scene, t + SECOND, 3.0);
        assert!(
            attribute_lists(&later, A).iter().all(|l| l[0].0 != 18),
            "the capacitor is full: nothing more is told"
        );
    }

    #[test]
    fn a_heal_of_a_ship_that_is_not_here_is_refused() {
        let (mut scene, t) = crowd();
        assert_eq!(
            scene.heal(99, t),
            [Out::Refused { session: 99, command: 18, reason: "NO_SHIP_IN_SPACE" }]
        );
        assert_eq!(
            scene.condition(99, t),
            [Out::Refused { session: 99, command: 19, reason: "NO_SHIP_IN_SPACE" }]
        );
    }

    #[test]
    fn the_condition_is_what_the_ship_has_taken_and_how_full_its_capacitor_is() {
        let (mut scene, t) = crowd();
        scene.damage(ship_of(&spec()), [0.0, 22.680704, 0.0, 1_000.0], t);
        let Out::Condition { session, command, condition } = scene.condition(A, t).remove(0) else {
            panic!()
        };
        assert_eq!((session, command), (A, 19));
        assert_eq!(condition.charge, 1.0);
        assert_eq!(condition.shield_charge, 0.0, "the explosive damage emptied the shield");
        assert!(condition.armor_damage > 0.0 && condition.damage == 0.0);
    }

    #[test]
    fn a_ship_that_docks_hurt_hands_over_what_it_has_taken() {
        let mut scene = Scene::new(Arc::new((*system()).clone()));
        let mut near = spec();
        near.max_velocity = 0.0;
        scene.undock(A, near, STATION, NOON).unwrap();
        // 500 m off the surface of the station: in docking range
        scene.pilots.get_mut(&A).unwrap().ship.position =
            eve_destiny::Vec3::new(1_000_000.0 + 1_000.0 + 47.0 + 500.0, 0.0, 0.0);
        scene.bind(A, NOON);
        // hurt at the station's door, it is told to dock and is in the station four seconds on
        scene.damage(ship_of(&spec()), [0.0, 0.0, 0.0, 1_000.0], NOON);
        scene.command(A, crate::scene::Command::Dock { station_id: STATION }, NOON);
        let (out, _) = poll_until(&mut scene, NOON, 6.0);
        let docked = out
            .iter()
            .find_map(|o| match o {
                Out::Docked { session: A, condition, .. } => Some(*condition),
                _ => None,
            })
            .expect("the ship docked");
        assert!(docked.armor_damage > 0.4 && docked.shield_charge < 0.5, "{docked:?}");
        assert_eq!(docked.charge, 1.0);
    }

    #[test]
    fn new_numbers_for_a_ship_in_space_keep_absolute_damage_and_capacitor_fraction() {
        let (mut scene, t) = crowd();
        scene.damage(ship_of(&spec()), [0.0, 0.0, 0.0, 1_000.0], t);
        let before = scene.pilots[&A].ship.health.condition_at(t + SECOND);
        let old = scene.pilots[&A].ship.health.vitals.layers();
        let hull_damage = old.structure * before.damage;
        let armor_damage = old.armor * before.armor_damage;
        let shield_damage = old.shield * (1.0 - before.shield_charge);
        let capacitor_fraction = before.charge;
        let mut bigger = spec();
        bigger.vitals.shield_capacity *= 2.0;
        bigger.vitals.armor_hp *= 2.0;
        bigger.vitals.structure_hp *= 2.0;

        scene.respec(A, bigger, t + SECOND);

        let health = &scene.pilots[&A].ship.health;
        let after = health.condition_at(t + SECOND);
        let new = health.vitals.layers();
        assert!((new.structure * after.damage - hull_damage).abs() < 1e-9);
        assert!((new.armor * after.armor_damage - armor_damage).abs() < 1e-9);
        assert!((new.shield * (1.0 - after.shield_charge) - shield_damage).abs() < 1e-9);
        assert_eq!(after.charge, capacitor_fraction);
    }

    #[test]
    fn a_ship_that_comes_in_hurt_is_shown_hurt() {
        let mut hurt = spec();
        hurt.condition = Condition { damage: 0.35, armor_damage: 0.6, shield_charge: 1.0, charge: 0.65 };
        let mut scene = Scene::new(Arc::new((*system()).clone()));
        scene.undock(A, hurt, STATION, NOON).unwrap();
        let state = scene.pilots[&A].ship.health.state_at(NOON);
        assert_eq!((state.shield, state.armor, state.structure), (1.0, 0.4, 0.65));
        let Out::Condition { condition, .. } = scene.condition(A, NOON).remove(0) else { panic!() };
        assert_eq!(condition.charge, 0.65);
        // a capacitor that starts low is told as it recharges
        let (out, _) = poll_until(&mut scene, NOON, 1.0);
        assert!(attribute_lists(&out, A).iter().any(|l| l[0].0 == 18), "{out:?}");
    }
}

#[cfg(test)]
mod shift_tests {
    //! A reactive armor hardener's adaptive resonances on a ship's armor: the hits it counts, the cycle's move, the pin of a Polarized weapon, and a
    //! refit (the base changes, the profile stays).

    use super::*;

    /// An armor with no shield and plenty of hit points, so that a hit goes straight to the armor, whose base resonances are `base`.
    fn armor(base: Damage, pinned: bool) -> Health {
        let vitals = Vitals {
            shield_capacity: 0.0,
            armor_hp: 1.0e9,
            structure_hp: 1.0e9,
            resonances: Resonances { shield: [1.0; 4], armor: base, structure: [1.0; 4] },
            armor_pinned: pinned,
        };
        Health::new(vitals, 860_000.0, Condition::FULL, 0)
    }

    /// What a hit of `raw` (em, thermal, kinetic, explosive) takes from the armor at `now`.
    fn taken_armor(health: &mut Health, raw: Damage, now: i64) -> f64 {
        health.apply(now, raw).expect("the ship can be hurt").taken.armor
    }

    #[test]
    fn a_running_hardener_scales_the_ship_armor_resonances_by_its_profile() {
        let mut health = armor([0.5, 0.35, 0.25, 0.1], false);
        health.start_shift([0.85; 4], 0.06);
        let resonance = health.armor_resonance();
        assert!((resonance[0] - 0.425).abs() < 1e-12);
        assert!((resonance[1] - 0.2975).abs() < 1e-12);
        assert_eq!(health.shift_profile(), Some([0.85; 4]));
        // a hit of 1000 em takes 1000 x 0.425 from the armor
        assert!((taken_armor(&mut health, [1000.0, 0.0, 0.0, 0.0], 0) - 425.0).abs() < 1e-6);
    }

    #[test]
    fn a_hit_at_a_resonance_of_0_40_takes_400_and_at_0_85_takes_850() {
        let mut health = armor([1.0; 4], false);
        health.start_shift([0.85; 4], 0.06);
        assert!((taken_armor(&mut health, [1000.0, 0.0, 0.0, 0.0], 0) - 850.0).abs() < 1e-6);
        let mut moved = armor([1.0; 4], false);
        moved.start_shift([0.40, 1.0, 1.0, 1.0], 0.06);
        assert!((taken_armor(&mut moved, [1000.0, 0.0, 0.0, 0.0], 0) - 400.0).abs() < 1e-6);
    }

    #[test]
    fn the_cycle_counts_the_damage_the_armor_took_per_kind_and_moves_the_profile_by_it() {
        let mut health = armor([0.5, 0.35, 0.25, 0.1], false);
        health.start_shift([0.85; 4], 0.06);
        taken_armor(&mut health, [1000.0, 0.0, 0.0, 0.0], 0);
        // the tally is the damage taken per kind, weighted by the resonance it met: 1000 x 0.425 in em
        let tally = health.shift.expect("runs").tally;
        assert!((tally[0] - 425.0).abs() < 1e-6, "{tally:?}");
        assert!(tally[1..].iter().all(|t| *t == 0.0));
        let next = health.shift_cycle().expect("the profile moves");
        assert_eq!(next, [0.67, 0.91, 0.91, 0.91]);
        // the armor's resonance is the base times the new profile, and the tally starts again
        assert!((health.armor_resonance()[0] - 0.5 * 0.67).abs() < 1e-12);
        assert_eq!(health.shift.expect("runs").tally, [0.0; 4]);
        // a cycle with no damage moves nothing
        assert_eq!(health.shift_cycle(), None);
    }

    #[test]
    fn the_shield_takes_the_hit_before_the_armor_counts_it() {
        // a hit that the shield absorbs whole is not counted by the armor
        let vitals = Vitals {
            shield_capacity: 1.0e9,
            armor_hp: 1.0e9,
            structure_hp: 1.0e9,
            resonances: Resonances { shield: [1.0; 4], armor: [1.0; 4], structure: [1.0; 4] },
            armor_pinned: false,
        };
        let mut health = Health::new(vitals, 860_000.0, Condition::FULL, 0);
        health.start_shift([0.85; 4], 0.06);
        health.apply(0, [1000.0, 0.0, 0.0, 0.0]);
        assert_eq!(health.shift.expect("runs").tally, [0.0; 4]);
    }

    #[test]
    fn a_pinned_armor_is_one_and_takes_no_shift() {
        let mut health = armor([0.5, 0.35, 0.25, 0.1], true);
        health.start_shift([0.85; 4], 0.06);
        assert_eq!(health.armor_resonance(), [1.0; 4]);
        assert!((taken_armor(&mut health, [1000.0, 0.0, 0.0, 0.0], 0) - 1000.0).abs() < 1e-6);
        // nothing is counted while it is pinned, so the cycle does not move the profile
        assert_eq!(health.shift.expect("runs").tally, [0.0; 4]);
        assert_eq!(health.shift_cycle(), None);
        assert_eq!(health.shift_profile(), Some([0.85; 4]));
    }

    #[test]
    fn unpinned_the_profile_applies_again_from_the_next_hit() {
        let mut health = armor([0.5, 0.35, 0.25, 0.1], true);
        health.start_shift([0.85; 4], 0.06);
        taken_armor(&mut health, [1000.0, 0.0, 0.0, 0.0], 0);
        // the Polarized weapon is taken off: the pin goes, and the profile is the one the hardener kept
        let mut vitals = health.vitals;
        vitals.armor_pinned = false;
        health.respec(vitals, 860_000.0, 1);
        assert!((health.armor_resonance()[0] - 0.425).abs() < 1e-12);
        assert!((taken_armor(&mut health, [1000.0, 0.0, 0.0, 0.0], 2) - 425.0).abs() < 1e-6);
    }

    #[test]
    fn a_refit_changes_the_base_and_keeps_the_profile() {
        let mut health = armor([0.5, 0.35, 0.25, 0.1], false);
        health.start_shift([0.85; 4], 0.06);
        taken_armor(&mut health, [1000.0, 0.0, 0.0, 0.0], 0);
        health.shift_cycle();
        let profile = health.shift_profile();
        let mut bigger = health.vitals;
        bigger.resonances.armor = [0.4, 0.35, 0.25, 0.1];
        health.respec(bigger, 860_000.0, 1);
        assert_eq!(health.shift_profile(), profile);
        assert!((health.armor_resonance()[0] - 0.4 * 0.67).abs() < 1e-12);
    }

    #[test]
    fn stopping_leaves_the_base_and_a_restart_starts_from_the_seed() {
        let mut health = armor([0.5, 0.35, 0.25, 0.1], false);
        health.start_shift([0.85; 4], 0.06);
        taken_armor(&mut health, [1000.0, 0.0, 0.0, 0.0], 0);
        health.shift_cycle();
        health.stop_shift();
        assert_eq!(health.armor_resonance(), [0.5, 0.35, 0.25, 0.1]);
        assert_eq!(health.shift_profile(), None);
        assert_eq!(health.shift_cycle(), None);
        health.start_shift([0.85; 4], 0.06);
        assert_eq!(health.shift_profile(), Some([0.85; 4]));
        assert_eq!(health.shift.expect("runs").tally, [0.0; 4]);
    }

    #[test]
    fn a_hit_of_nothing_counts_nothing() {
        let mut health = armor([0.5; 4], false);
        health.start_shift([0.85; 4], 0.06);
        health.apply(0, [0.0; 4]);
        assert_eq!(health.shift.expect("runs").tally, [0.0; 4]);
    }
}
