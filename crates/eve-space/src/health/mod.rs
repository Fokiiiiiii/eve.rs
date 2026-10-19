//! What a ship can take and what it has taken: shield, armor and hull, the damage that goes through them and the shield's recharge.
//!
//! The host says what a ship can hold and how it resists (`Vitals`, worked out by dogma from the ship, its fitting and its pilot's skills) and what
//! it has taken when it comes into space (`Condition`, fractions, as the reference keeps them). The scene keeps the rest: damage goes through the
//! shield, then the armor, then the hull, each layer's resonances taken off the four kinds of damage; whatever a hit does not use in one layer goes
//! on to the next. The shield recharges along CCP's curve while it is below full. The reference rounds what it keeps of a hit to six places (the
//! stored fractions, not the points it tells), and this does the same, so the numbers a client is told are the same to the last digit.
mod scene_api;
mod shift;
mod structure;

pub use self::structure::{StructureHealth, StructureHit, StructureLimits};

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
    pub(crate) fn layers(&self) -> Layers {
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
        self.apply_through(now, raw, true)
    }

    /// A hit of `raw` damage that the layers take as it is (a damage-over-time tick: the resistances do not apply to it).
    pub fn apply_unresisted(&mut self, now: i64, raw: Damage) -> Option<Applied> {
        self.apply_through(now, raw, false)
    }

    /// Applies an already-resolved damage amount across shield, armor, and structure without inventing a damage type.
    pub fn apply_resolved_amount(&mut self, now: i64, amount: f64) -> Option<Applied> {
        if !self.vitals.hurtable() || !amount.is_finite() || amount <= 0.0 {
            return None;
        }

        let max = self.vitals.layers();
        let before = self.layers_at(now);
        let armor_before = self.armor_gone();
        let mut after = before;
        let mut taken = Layers { shield: 0.0, armor: 0.0, structure: 0.0 };
        let mut remaining = amount;

        for layer in 0..3 {
            let (most, hp) = match layer {
                0 => (max.shield, after.shield),
                1 => (max.armor, after.armor),
                _ => (max.structure, after.structure),
            };
            if most <= 0.0 || hp <= 0.0 || remaining <= 0.0 {
                continue;
            }

            let used = hp.min(remaining);
            remaining = (remaining - used).max(0.0);
            match layer {
                0 => {
                    after.shield = (hp - used).max(0.0);
                    taken.shield = used;
                }
                1 => {
                    after.armor = (hp - used).max(0.0);
                    taken.armor = used;
                }
                _ => {
                    after.structure = (hp - used).max(0.0);
                    taken.structure = used;
                }
            }
        }

        let max_structure = max.structure;
        let armor_after = armor_before + taken.armor;
        let unit = |left: f64, most: f64| (left / most).clamp(0.0, 1.0);
        self.condition = Condition {
            damage: if max_structure > 0.0 {
                round6(1.0 - unit(after.structure, max_structure))
            } else {
                1.0
            },
            armor_damage: if max.armor > 0.0 { round6(armor_after / max.armor) } else { 0.0 },
            shield_charge: if max.shield > 0.0 { round6(unit(after.shield, max.shield)) } else { 0.0 },
            charge: self.condition.charge,
        };
        self.at = now;
        if self.condition.shield_charge < 1.0 && max.shield > 0.0 {
            self.next_sample.get_or_insert(now + crate::modules::TICK);
        }

        let destroyed = max_structure > 0.0 && after.structure <= 1e-9;
        let changed = before != after;
        Some(Applied {
            before,
            after,
            taken,
            armor_damage: (armor_before, self.armor_gone()),
            max_structure,
            destroyed,
            changed,
        })
    }

    /// `apply`, with the resonances of the layers when `resisted`, and with none (every kind goes through whole) when not.
    fn apply_through(&mut self, now: i64, raw: Damage, resisted: bool) -> Option<Applied> {
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
        let resonances = if resisted {
            Resonances { armor: self.armor_resonance(), ..self.vitals.resonances }
        } else {
            Resonances::default()
        };
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

#[cfg(test)]
mod tests;

#[cfg(test)]
mod shift_tests;
