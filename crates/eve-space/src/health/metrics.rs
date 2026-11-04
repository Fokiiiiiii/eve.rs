//! Current remaining effective hit points, using the V24.01 fitting convention of
//! averaging each layer's four damage resonances. This is a local health metric;
//! it does not claim to reproduce the Tournament Fancy service's private aggregate.
use super::{Health, Layers, Resonances};

/// A layer whose current hit points and damage resonances contribute to remaining EHP.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HpLayer {
    Shield,
    Armor,
    Structure,
}

/// Why a current EHP value cannot be calculated faithfully.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EffectiveHpUnknown {
    NoShip,
    InvalidMaximumPoints { layer: HpLayer },
    InvalidCurrentPoints { layer: HpLayer },
    InvalidResonance { layer: HpLayer, damage_kind: usize },
    InvalidResistanceMultiplier,
    NonFiniteResult,
}

/// A current EHP value. Infinite means a layer with remaining hit points has
/// zero resonance for all four damage kinds; Unknown preserves invalid/missing data.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum RemainingEffectiveHp {
    Finite(f64),
    Infinite,
    Unknown(EffectiveHpUnknown),
}

/// Calculate effective hit points from current layer points and effective damage resonances.
///
/// Resonances are damage multipliers, not resistance percentages: a value greater
/// than one represents vulnerability and is accepted. The mean is arithmetic and
/// equally weights EM, thermal, kinetic, and explosive damage as the fitting client does.
pub fn remaining_effective_hp(layers: Layers, resonances: Resonances) -> RemainingEffectiveHp {
    let inputs = [
        (HpLayer::Shield, layers.shield, resonances.shield),
        (HpLayer::Armor, layers.armor, resonances.armor),
        (HpLayer::Structure, layers.structure, resonances.structure),
    ];
    let mut total = 0.0;
    for (layer, points, damage_resonances) in inputs {
        if !points.is_finite() || points < 0.0 {
            return RemainingEffectiveHp::Unknown(EffectiveHpUnknown::InvalidCurrentPoints { layer });
        }
        if points == 0.0 {
            continue;
        }

        let mut mean = 0.0;
        for (damage_kind, resonance) in damage_resonances.into_iter().enumerate() {
            if !resonance.is_finite() || resonance < 0.0 {
                return RemainingEffectiveHp::Unknown(EffectiveHpUnknown::InvalidResonance {
                    layer,
                    damage_kind,
                });
            }
            mean += resonance / 4.0;
        }
        if !mean.is_finite() {
            return RemainingEffectiveHp::Unknown(EffectiveHpUnknown::NonFiniteResult);
        }
        if mean == 0.0 {
            return RemainingEffectiveHp::Infinite;
        }

        let layer_ehp = points / mean;
        if !layer_ehp.is_finite() {
            return RemainingEffectiveHp::Unknown(EffectiveHpUnknown::NonFiniteResult);
        }
        total += layer_ehp;
        if !total.is_finite() {
            return RemainingEffectiveHp::Unknown(EffectiveHpUnknown::NonFiniteResult);
        }
    }
    RemainingEffectiveHp::Finite(total)
}

impl Health {
    /// Calculate current EHP after shield recharge, damage, adaptive armor
    /// resonance and polarized-armor pinning. The multiplier is an external
    /// scene resistance modifier (for example, the active CRAB link).
    pub fn remaining_effective_hp_at(
        &self,
        now: i64,
        external_resistance_multiplier: f64,
    ) -> RemainingEffectiveHp {
        if !external_resistance_multiplier.is_finite() || external_resistance_multiplier < 0.0 {
            return RemainingEffectiveHp::Unknown(EffectiveHpUnknown::InvalidResistanceMultiplier);
        }

        let vitals = self.vitals();
        for (layer, points) in [
            (HpLayer::Shield, vitals.shield_capacity),
            (HpLayer::Armor, vitals.armor_hp),
            (HpLayer::Structure, vitals.structure_hp),
        ] {
            if !points.is_finite() || points < 0.0 {
                return RemainingEffectiveHp::Unknown(EffectiveHpUnknown::InvalidMaximumPoints { layer });
            }
        }

        let mut effective_resonances = vitals.resonances;
        effective_resonances.armor = self.armor_resonance();
        effective_resonances.shield = effective_resonances.shield.map(|r| r * external_resistance_multiplier);
        effective_resonances.armor = effective_resonances.armor.map(|r| r * external_resistance_multiplier);
        effective_resonances.structure =
            effective_resonances.structure.map(|r| r * external_resistance_multiplier);

        remaining_effective_hp(self.layers_at(now), effective_resonances)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::health::{Condition, Vitals};

    fn vitals(resonances: Resonances) -> Vitals {
        Vitals {
            shield_capacity: 100.0,
            armor_hp: 100.0,
            structure_hp: 100.0,
            resonances,
            armor_pinned: false,
        }
    }

    fn uniform(value: f64) -> Resonances {
        Resonances { shield: [value; 4], armor: [value; 4], structure: [value; 4] }
    }

    #[test]
    fn uses_current_layer_points_and_equal_mean_of_four_damage_resonances() {
        let layers = Layers { shield: 80.0, armor: 40.0, structure: 20.0 };
        let resonances = Resonances { shield: [0.2, 0.4, 0.6, 0.8], armor: [1.0; 4], structure: [2.0; 4] };
        assert_eq!(remaining_effective_hp(layers, resonances), RemainingEffectiveHp::Finite(210.0));
    }

    #[test]
    fn health_wrapper_uses_current_damage_shift_polarized_pin_and_external_resistance() {
        let condition = Condition { shield_charge: 0.5, armor_damage: 0.25, damage: 0.75, charge: 1.0 };
        let mut health = Health::new(vitals(uniform(1.0)), 860_000.0, condition, 0);
        health.start_shift([0.5; 4], 0.1);
        assert_eq!(health.remaining_effective_hp_at(0, 1.0), RemainingEffectiveHp::Finite(225.0));
        assert_eq!(health.remaining_effective_hp_at(0, 0.9), RemainingEffectiveHp::Finite(250.0));

        let pinned = Vitals { resonances: uniform(0.2), armor_pinned: true, ..vitals(uniform(1.0)) };
        let mut pinned_health = Health::new(pinned, 860_000.0, condition, 0);
        pinned_health.start_shift([0.1; 4], 0.1);
        assert_eq!(pinned_health.armor_resonance(), [1.0; 4]);
        assert_eq!(pinned_health.remaining_effective_hp_at(0, 1.0), RemainingEffectiveHp::Finite(450.0));
    }

    #[test]
    fn zero_resonance_is_infinite_only_while_that_layer_has_points_left() {
        let immune = Resonances { shield: [0.0; 4], armor: [1.0; 4], structure: [1.0; 4] };
        assert_eq!(
            remaining_effective_hp(Layers { shield: 1.0, armor: 0.0, structure: 0.0 }, immune),
            RemainingEffectiveHp::Infinite
        );
        assert_eq!(
            remaining_effective_hp(Layers { shield: 0.0, armor: 25.0, structure: 0.0 }, immune),
            RemainingEffectiveHp::Finite(25.0)
        );
    }

    #[test]
    fn accepts_vulnerability_and_reports_invalid_or_unrepresentable_inputs_as_unknown() {
        assert_eq!(
            remaining_effective_hp(Layers { shield: 20.0, armor: 0.0, structure: 0.0 }, uniform(2.0)),
            RemainingEffectiveHp::Finite(10.0)
        );
        assert!(matches!(
            remaining_effective_hp(
                Layers { shield: 20.0, armor: 0.0, structure: 0.0 },
                Resonances { shield: [-0.1, 1.0, 1.0, 1.0], armor: [1.0; 4], structure: [1.0; 4] }
            ),
            RemainingEffectiveHp::Unknown(EffectiveHpUnknown::InvalidResonance {
                layer: HpLayer::Shield,
                damage_kind: 0
            })
        ));
        assert!(matches!(
            remaining_effective_hp(Layers { shield: f64::NAN, armor: 0.0, structure: 0.0 }, uniform(1.0)),
            RemainingEffectiveHp::Unknown(EffectiveHpUnknown::InvalidCurrentPoints {
                layer: HpLayer::Shield
            })
        ));
        assert!(matches!(
            remaining_effective_hp(
                Layers { shield: f64::MAX, armor: 0.0, structure: 0.0 },
                Resonances { shield: [f64::MIN_POSITIVE; 4], armor: [1.0; 4], structure: [1.0; 4] }
            ),
            RemainingEffectiveHp::Unknown(EffectiveHpUnknown::NonFiniteResult)
        ));
    }

    #[test]
    fn health_wrapper_does_not_turn_invalid_capacity_into_zero_ehp() {
        let invalid = Vitals { shield_capacity: f64::NAN, ..vitals(uniform(1.0)) };
        let health = Health::new(invalid, 860_000.0, Condition::FULL, 0);
        assert_eq!(
            health.remaining_effective_hp_at(0, 1.0),
            RemainingEffectiveHp::Unknown(EffectiveHpUnknown::InvalidMaximumPoints {
                layer: HpLayer::Shield
            })
        );
        assert_eq!(
            health.remaining_effective_hp_at(0, f64::INFINITY),
            RemainingEffectiveHp::Unknown(EffectiveHpUnknown::InvalidResistanceMultiplier)
        );
    }
}
