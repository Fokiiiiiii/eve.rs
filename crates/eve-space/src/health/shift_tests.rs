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
