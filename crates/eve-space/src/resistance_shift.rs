//! The adaptive resistances of a reactive armor hardener: each cycle, the four resistances of the armor move towards the kinds of damage the armor took
//! in that cycle, and what one gives the others takes (the total is kept).
//!
//! The state is the module's resonance multiplier for each kind of damage (`profile`, seeded from the module's own values, 0.85 each for a 4403; the
//! ship's armor resonance is its base times this), in the order a damage vector is written: em, thermal, kinetic, explosive. The tally is the damage
//! the armor took in the cycle per kind, weighted by the resonance it had. Each cycle the kinds the armor took least are the donors (at least two): each
//! resistance moves from the kinds the armor was hit by to the kinds it was not. A tie is settled in the order the reference declares the kinds (em,
//! explosive, kinetic, thermal). When the armor took nothing in any kind there is no change.
//!
/// The four kinds of damage, in the order a damage vector is written: em, thermal, kinetic, explosive.
pub type Kinds = [f64; 4];

/// The order the kinds are settled in when they are tied: em (0), explosive (3), kinetic (2), thermal (1), as their attributes are declared.
const DECLARED_ORDER: [usize; 4] = [0, 3, 2, 1];

/// Rounds a resonance to six places, as the stored values are.
fn round6(value: f64) -> f64 {
    (value * 1_000_000.0).round() / 1_000_000.0
}

/// The resonances after one cycle, given the damage the armor took per kind in it (`tally`, already weighted by the resonance it had) and the most
///
/// the same to the millionth): the armor is then told nothing.
pub fn next_profile(profile: Kinds, tally: Kinds, step: f64) -> Option<Kinds> {
    if !step.is_finite() || step <= 0.0 {
        return None;
    }
    let profile = profile.map(|r| r.clamp(0.0, 1.0));
    let taken_nothing = tally.iter().filter(|t| **t <= 0.0).count();
    let donors = taken_nothing.max(2);
    if donors >= 4 {
        return None;
    }
    let recipients = 4 - donors;
    // the least taken first, ties in the declared order
    let mut order = [0usize, 1, 2, 3];
    order.sort_by(|&a, &b| {
        tally[a]
            .partial_cmp(&tally[b])
            .unwrap_or(std::cmp::Ordering::Equal)
            .then_with(|| rank(a).cmp(&rank(b)))
    });
    let mut next = profile;
    let mut given = 0.0;
    for &kind in &order[..donors] {
        let donation = (1.0 - profile[kind]).max(0.0).min(step);
        next[kind] = round6(profile[kind] + donation);
        given += donation;
    }
    if given <= 0.0 {
        return None;
    }
    for &kind in &order[donors..] {
        next[kind] = round6((profile[kind] - given / recipients as f64).clamp(0.0, 1.0));
    }
    let moved = (0..4).map(|i| (next[i] - profile[i]).abs()).fold(0.0_f64, f64::max);
    (moved > 1e-9).then_some(next)
}

/// Where a kind sits in the declared order (`DECLARED_ORDER`): em first, thermal last.
fn rank(kind: usize) -> usize {
    DECLARED_ORDER.iter().position(|&k| k == kind).unwrap_or(kind)
}

#[cfg(test)]
mod tests {
    use super::*;

    const SEED: Kinds = [0.85; 4];

    /// The resonances after a cycle in which the armor took `raw` (em, thermal, kinetic, explosive) at the resonances `profile` (the tally is
    /// the raw damage weighted by them).
    fn cycle(profile: Kinds, raw: Kinds) -> Option<Kinds> {
        let tally = std::array::from_fn(|i| raw[i] * profile[i]);
        next_profile(profile, tally, 0.06)
    }

    fn assert_kinds(actual: Kinds, expected: Kinds) {
        for i in 0..4 {
            assert!((actual[i] - expected[i]).abs() < 1e-9, "kind {i}: {actual:?} != {expected:?}");
        }
    }

    fn total(profile: Kinds) -> f64 {
        profile.iter().sum()
    }

    #[test]
    fn the_reference_sequence_for_a_hardener_that_is_hit_in_one_kind_moves_as_the_model_says() {
        // em (0), thermal (1), kinetic (2), explosive (3): the reactive armor hardener of the reference, seeded at 0.85 each (pool 0.6)
        let mut profile = SEED;
        let steps: [(Kinds, Kinds); 10] = [
            ([1000.0, 0.0, 0.0, 0.0], [0.67, 0.91, 0.91, 0.91]),
            ([1000.0, 0.0, 0.0, 0.0], [0.49, 0.97, 0.97, 0.97]),
            ([1000.0, 0.0, 0.0, 0.0], [0.40, 1.0, 1.0, 1.0]),
            ([1000.0, 0.0, 0.0, 0.0], [0.40, 1.0, 1.0, 1.0]),
            ([1000.0, 0.0, 0.0, 0.0], [0.40, 1.0, 1.0, 1.0]),
            ([1000.0, 0.0, 0.0, 0.0], [0.40, 1.0, 1.0, 1.0]),
            ([0.0, 0.0, 1000.0, 0.0], [0.46, 1.0, 0.94, 1.0]),
            ([0.0, 0.0, 1000.0, 0.0], [0.52, 1.0, 0.88, 1.0]),
            ([500.0, 0.0, 500.0, 0.0], [0.52, 1.0, 0.88, 1.0]),
            ([0.0, 0.0, 0.0, 0.0], [0.52, 1.0, 0.88, 1.0]),
        ];
        for (index, (raw, expected)) in steps.iter().enumerate() {
            let after = cycle(profile, *raw).unwrap_or(profile);
            // the table is written to the hundredth
            for i in 0..4 {
                assert!(
                    (after[i] - expected[i]).abs() < 0.006,
                    "cycle {} kind {i}: {after:?} vs {expected:?}",
                    index + 1
                );
            }
            profile = after;
        }
        assert_kinds(profile, [0.52, 1.0, 0.88, 1.0]);
    }

    #[test]
    fn a_cycle_of_damage_in_one_kind_takes_six_percent_from_each_of_the_three_others_for_the_one_it_hits() {
        let next = cycle(SEED, [1000.0, 0.0, 0.0, 0.0]).expect("the armor took damage");
        assert_kinds(next, [0.67, 0.91, 0.91, 0.91]);
    }

    #[test]
    fn the_total_resonance_is_kept_in_every_cycle() {
        let before = total(SEED);
        let mut profile = SEED;
        for raw in [
            [1000.0, 0.0, 0.0, 0.0],
            [0.0, 0.0, 1000.0, 0.0],
            [300.0, 800.0, 0.0, 50.0],
            [0.0, 0.0, 0.0, 999.0],
        ] {
            for _ in 0..6 {
                if let Some(next) = cycle(profile, raw) {
                    profile = next;
                }
                assert!((total(profile) - before).abs() < 1e-4, "{profile:?}");
            }
        }
    }

    #[test]
    fn every_resonance_stays_within_zero_and_one() {
        let mut profile = SEED;
        for _ in 0..40 {
            if let Some(next) = cycle(profile, [1000.0, 0.0, 0.0, 0.0]) {
                profile = next;
            }
            assert!(profile.iter().all(|r| (0.0..=1.0).contains(r)), "{profile:?}");
        }
        // a kind that is hit forever ends at the resonance the pool allows: 0.4 (the other three gave all they had)
        assert!((profile[0] - 0.40).abs() < 1e-9, "{profile:?}");
    }

    #[test]
    fn a_donor_already_at_one_gives_nothing_and_the_others_do_not_move() {
        // the three others are at one: the kinds that took no damage give nothing, so the hit kind does not move either
        let profile = [0.40, 1.0, 1.0, 1.0];
        assert_eq!(cycle(profile, [1000.0, 0.0, 0.0, 0.0]), None);
    }

    #[test]
    fn a_cycle_with_no_damage_is_no_change() {
        assert_eq!(cycle(SEED, [0.0; 4]), None);
    }

    #[test]
    fn a_cycle_that_took_nothing_in_any_kind_is_no_change() {
        assert_eq!(next_profile(SEED, [0.0, 0.0, 0.0, 0.0], 0.06), None);
    }

    #[test]
    fn two_kinds_hit_equally_are_both_held_and_the_two_others_donate() {
        // em (sim index 0) and kinetic (2) are hit: thermal (1) and explosive (3) took nothing and donate
        let next = cycle(SEED, [1000.0, 0.0, 1000.0, 0.0]).expect("the armor took damage");
        assert_kinds(next, [0.79, 0.91, 0.79, 0.91]);
    }

    #[test]
    fn with_four_kinds_hit_the_two_taken_least_donate() {
        // the reference's case: em 400, explosive 300, kinetic 200, thermal 100 (tallies 340, 255, 170, 85); thermal and kinetic are taken least
        let next = cycle(SEED, [400.0, 100.0, 200.0, 300.0]).expect("the armor took damage");
        assert_kinds(next, [0.79, 0.91, 0.91, 0.79]);
    }

    #[test]
    fn a_hit_equal_in_every_kind_is_settled_by_the_declared_order() {
        // all four tally 850: the two first declared (em, explosive) are the donors, their resonances rise to 0.91 and thermal and kinetic fall to 0.79
        let next = cycle(SEED, [1000.0; 4]).expect("the armor took damage");
        assert_kinds(next, [0.91, 0.79, 0.79, 0.91]);
    }

    #[test]
    fn the_donation_is_capped_by_what_the_donor_has_left() {
        let profile = [0.49, 0.97, 0.97, 0.97];
        let next = next_profile(profile, [1000.0, 0.0, 0.0, 0.0].map(|d: f64| d * 0.49), 0.06).unwrap();
        assert!((next[0] - 0.40).abs() < 1e-9, "{next:?}");
        assert!(
            (next[1] - 1.0).abs() < 1e-9 && (next[2] - 1.0).abs() < 1e-9 && (next[3] - 1.0).abs() < 1e-9,
            "{next:?}"
        );
    }

    #[test]
    fn a_non_positive_or_unknown_step_changes_nothing() {
        assert_eq!(next_profile(SEED, [1000.0, 0.0, 0.0, 0.0], 0.0), None);
        assert_eq!(next_profile(SEED, [1000.0, 0.0, 0.0, 0.0], f64::NAN), None);
    }

    #[test]
    fn a_second_profile_of_the_xarasier_pool_moves_the_same_way() {
        // pool 0.64 (seed 0.84): the kind hit three cycles in a row goes 0.66 -> 0.48 -> 0.36, the others to one
        let seed = [0.84; 4];
        let first = cycle(seed, [1000.0, 0.0, 0.0, 0.0]).unwrap();
        assert!((first[0] - 0.66).abs() < 1e-9, "{first:?}");
        let second = cycle(first, [1000.0, 0.0, 0.0, 0.0]).unwrap();
        assert!((second[0] - 0.48).abs() < 1e-9, "{second:?}");
        let third = cycle(second, [1000.0, 0.0, 0.0, 0.0]).unwrap();
        assert!((third[0] - 0.36).abs() < 1e-9, "{third:?}");
        assert!((total(third) - 3.36).abs() < 1e-6);
    }
}
