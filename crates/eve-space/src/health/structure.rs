//! A structure's health: a ship's layers (`Health`) with two limits a ship does not have. A layer takes at most its per-second limit in one second
//! (SDE 2034-2036): a hit beyond it is cut to what that second has left, and the rest of the hit is lost. A second in which a layer takes its
//! repair-pause damage (SDE 3354-3356) is reported, so the host can pause that layer's repair.

use super::{Applied, Condition, Damage, DamageState, Health, Layer, Layers, Vitals};
use crate::scene::TICKS_PER_SECOND;

/// What each layer (shield, armor, hull) takes in one second at most, and the damage a second must reach on a layer before its repair pauses.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct StructureLimits {
    pub per_second: [f64; 3],
    pub repair_pause: [f64; 3],
}

/// What a hit did to a structure: the layer it emptied (the deepest, when it emptied more than one), and whether a layer has taken its repair-pause
/// damage in this second.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct StructureHit {
    pub depleted: Option<Layer>,
    pub over_threshold: bool,
}

#[derive(Debug, Clone, PartialEq)]
pub struct StructureHealth {
    health: Health,
    limits: StructureLimits,
    second: i64,
    taken: [f64; 3],
}

fn taken_in(layers: &Layers, layer: usize) -> f64 {
    [layers.shield, layers.armor, layers.structure][layer]
}

fn depleted(applied: &Applied) -> Option<Layer> {
    let mut emptied = None;
    if applied.before.shield > 0.0 && applied.after.shield <= 0.0 {
        emptied = Some(Layer::Shield);
    }
    if applied.before.armor > 0.0 && applied.after.armor <= 0.0 {
        emptied = Some(Layer::Armor);
    }
    if applied.before.structure > 0.0 && applied.after.structure <= 0.0 {
        emptied = Some(Layer::Structure);
    }
    emptied
}

impl StructureHealth {
    /// A structure that is whole: nothing taken yet.
    pub fn new(vitals: Vitals, shield_recharge_ms: f64, limits: StructureLimits, now: i64) -> Self {
        Self {
            health: Health::new(vitals, shield_recharge_ms, Condition::FULL, now),
            limits,
            second: now.div_euclid(TICKS_PER_SECOND),
            taken: [0.0; 3],
        }
    }

    /// What the client is told of the structure's health (as for a ship).
    pub fn state_at(&self, now: i64) -> DamageState {
        self.health.state_at(now)
    }

    /// The same authoritative layers, in the host's Command 19 condition format.
    pub fn condition_at(&self, now: i64) -> Condition {
        // shortcut: structure capacitor stays full until ST-8 adds its module runtime.
        Condition { charge: 1.0, ..self.health.condition_at(now) }
    }

    /// A hit of `raw` damage at `now`, cut to what the layer it lands in may still take this second. `None` when the structure has nothing to hurt.
    /// The layers are linear in the damage within one layer, so a cut hit is the whole hit scaled down; a hit that crosses into the next layer is
    /// scaled by its first layer's allowance (a stated approximation).
    pub fn apply(&mut self, now: i64, raw: Damage) -> Option<StructureHit> {
        let second = now.div_euclid(TICKS_PER_SECOND);
        if second != self.second {
            self.second = second;
            self.taken = [0.0; 3];
        }
        let layers = self.health.layers_at(now);
        if layers.structure <= 0.0 {
            // a destroyed structure takes nothing more (the host decides what its destruction means)
            return None;
        }
        let active = if layers.shield > 0.0 {
            0
        } else if layers.armor > 0.0 {
            1
        } else {
            2
        };
        let trial = self.health.clone().apply(now, raw)?;
        let allowance = (self.limits.per_second[active] - self.taken[active]).max(0.0);
        let used = taken_in(&trial.taken, active);
        let scale = if used > allowance && used > 0.0 { allowance / used } else { 1.0 };
        let applied = self.health.apply(now, raw.map(|d| d * scale))?;
        for (layer, total) in self.taken.iter_mut().enumerate() {
            *total += taken_in(&applied.taken, layer);
        }
        let over_threshold =
            self.taken.iter().zip(self.limits.repair_pause).any(|(taken, pause)| *taken >= pause);
        Some(StructureHit { depleted: depleted(&applied), over_threshold })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SECOND: i64 = TICKS_PER_SECOND;

    fn structure(now: i64) -> StructureHealth {
        let vitals = Vitals { shield_capacity: 1_000.0, armor_hp: 500.0, structure_hp: 300.0, ..Vitals::default() };
        let limits = StructureLimits { per_second: [400.0, 200.0, 100.0], repair_pause: [300.0, 150.0, 80.0] };
        StructureHealth::new(vitals, 1.0e15, limits, now)
    }

    #[test]
    fn a_layer_takes_no_more_than_its_limit_in_a_second_and_takes_the_next_second_fresh() {
        let t = 1_000 * SECOND;
        let mut s = structure(t);
        let hit = s.apply(t, [600.0, 0.0, 0.0, 0.0]).expect("a hit");
        assert_eq!(s.health.layers_at(t).shield, 600.0, "the hit is cut to the shield's 400 of this second");
        assert!(hit.over_threshold, "the shield took 400 this second, above its 300 pause");
        assert!(s.apply(t, [100.0, 0.0, 0.0, 0.0]).is_some_and(|h| h.depleted.is_none()));
        assert_eq!(s.health.layers_at(t).shield, 600.0, "nothing more of this second's allowance is left");
        s.apply(t + SECOND, [100.0, 0.0, 0.0, 0.0]).expect("the next second");
        assert_eq!(s.health.layers_at(t + SECOND).shield, 500.0, "the next second's allowance is whole again");
    }

    #[test]
    fn a_layer_emptied_is_reported_once() {
        let t = 1_000 * SECOND;
        let mut s = StructureHealth::new(
            Vitals { shield_capacity: 100.0, armor_hp: 500.0, structure_hp: 300.0, ..Vitals::default() },
            1.0e15,
            StructureLimits { per_second: [1_000.0, 200.0, 100.0], repair_pause: [300.0, 150.0, 80.0] },
            t,
        );
        assert_eq!(s.apply(t, [150.0, 0.0, 0.0, 0.0]).and_then(|h| h.depleted), Some(Layer::Shield));
        assert_eq!(s.apply(t, [10.0, 0.0, 0.0, 0.0]).and_then(|h| h.depleted), None, "the shield was empty already");
    }
}
