//! `behaviors/utility/threat.py`: what a target did and had done to it lately.

use crate::tree::TaskCtx;

/// `get_damage_and_healing`: the damage a ship took from NPCs and the healing it did over the last `sample_interval_seconds`.
pub fn get_damage_and_healing(t: &mut TaskCtx<'_, '_>, target_id: i64, sample_interval_seconds: f64) -> f64 {
    let since = t.now() - crate::env::seconds_to_ticks(sample_interval_seconds);
    let tracker = t.world().damage_tracker_totals(target_id, since);
    tracker.damage_to_npcs + tracker.healing_done
}

/// `evaluate_target` of a target for a damage tracker: a ship that was abandoned counts a tenth.
pub fn evaluate_target(t: &mut TaskCtx<'_, '_>, target_id: i64, sample_interval_seconds: f64) -> f64 {
    let mut threat_value = get_damage_and_healing(t, target_id, sample_interval_seconds);
    if t.world().is_ship_abandoned(target_id) {
        threat_value *= 0.1;
    }
    threat_value
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::env::DamageTotals;
    use crate::tree::tests::with_task;

    #[test]
    fn threat_is_the_damage_and_the_healing_since_the_interval_began_and_a_tenth_for_an_abandoned_ship() {
        let (_, answer) = with_task(
            |rig| {
                rig.world.damage.insert(9, DamageTotals { damage_to_npcs: 30.0, healing_done: 10.0 });
                rig.world.abandoned.insert(9);
            },
            |t| {
                (get_damage_and_healing(t, 9, 10.0), evaluate_target(t, 9, 10.0), evaluate_target(t, 8, 10.0))
            },
        );
        let (total, evaluated, none) = answer.unwrap();
        assert_eq!(total, 40.0);
        assert!((evaluated - 4.0).abs() < 1e-12);
        assert_eq!(none, 0.0);
    }
}
