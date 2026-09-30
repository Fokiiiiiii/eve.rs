//! What the client's `evemath` package gives the tasks (`evemath/__init__.py`): a weighted choice, a stochastic round, the centre of a set of points
//! and the distance to the farthest.

use crate::env::World;

/// `weighted_choice`: one of the choices, with the chance its weight is of the total. The uniform number is drawn first, as the client draws it,
/// even when every weight is 0 (then the choice is any of them, drawn once more).
pub fn weighted_choice<T: Copy>(choices: &[(T, f64)], world: &mut dyn World) -> Option<T> {
    let total: f64 = choices.iter().map(|(_, weight)| *weight).sum();
    let r = world.random_uniform(0.0, total);
    if total == 0.0 {
        if choices.is_empty() {
            return None;
        }
        return Some(choices[world.random_index(choices.len())].0);
    }
    let mut accumulated = 0.0;
    for (choice, weight) in choices {
        if accumulated + weight >= r {
            return Some(*choice);
        }
        accumulated += weight;
    }
    None
}

/// `stochastic_round`: `x` rounded down, or up with the chance of its fraction.
pub fn stochastic_round(x: f64, world: &mut dyn World) -> i64 {
    (x + world.random()).floor() as i64
}

/// `get_center_point`: the mean of the points. `None` for no points (the client raises `ValueError`).
pub fn center_point(points: &[[f64; 3]]) -> Option<[f64; 3]> {
    match points {
        [] => None,
        [only] => Some(*only),
        many => {
            let mut sum = [0.0; 3];
            for p in many {
                sum = [sum[0] + p[0], sum[1] + p[1], sum[2] + p[2]];
            }
            let scale = 1.0 / many.len() as f64;
            Some([sum[0] * scale, sum[1] * scale, sum[2] * scale])
        }
    }
}

/// `geo2.Vec3DistanceSqD`.
pub fn distance_sq(a: [f64; 3], b: [f64; 3]) -> f64 {
    let (x, y, z) = (a[0] - b[0], a[1] - b[1], a[2] - b[2]);
    x * x + y * y + z * z
}

/// `get_farthest_distance_from_point`: 0 for no points (the client's `max` of nothing raises).
pub fn farthest_distance_from_point(reference: [f64; 3], points: &[[f64; 3]]) -> Option<f64> {
    points
        .iter()
        .map(|p| distance_sq(reference, *p))
        .fold(None, |best: Option<f64>, d| Some(best.map_or(d, |b| b.max(d))))
        .map(f64::sqrt)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::env::RecordingWorld;

    #[test]
    fn a_weighted_choice_picks_by_the_accumulated_weight() {
        let choices = [('a', 1.0), ('b', 3.0), ('c', 6.0)];
        // total 10: r = 10 * number
        assert_eq!(weighted_choice(&choices, &mut RecordingWorld::with_numbers(&[0.0])), Some('a'));
        assert_eq!(
            weighted_choice(&choices, &mut RecordingWorld::with_numbers(&[0.1])),
            Some('a'),
            "a weight reaches its own end"
        );
        assert_eq!(weighted_choice(&choices, &mut RecordingWorld::with_numbers(&[0.11])), Some('b'));
        assert_eq!(weighted_choice(&choices, &mut RecordingWorld::with_numbers(&[0.4])), Some('b'));
        assert_eq!(weighted_choice(&choices, &mut RecordingWorld::with_numbers(&[0.41])), Some('c'));
        assert_eq!(weighted_choice(&choices, &mut RecordingWorld::with_numbers(&[0.999])), Some('c'));
    }

    #[test]
    fn weights_of_nothing_choose_any_and_draw_two_numbers() {
        let choices = [('a', 0.0), ('b', 0.0), ('c', 0.0)];
        let mut world = RecordingWorld::with_numbers(&[0.5, 0.7]);
        assert_eq!(weighted_choice(&choices, &mut world), Some('c'), "the second number picks: 0.7 of three");
        assert_eq!(weighted_choice::<char>(&[], &mut RecordingWorld::default()), None);
    }

    #[test]
    fn a_stochastic_round_rounds_up_with_the_chance_of_the_fraction() {
        assert_eq!(stochastic_round(2.3, &mut RecordingWorld::with_numbers(&[0.6])), 2);
        assert_eq!(stochastic_round(2.3, &mut RecordingWorld::with_numbers(&[0.8])), 3);
    }

    #[test]
    fn the_centre_of_points_and_the_distance_to_the_farthest() {
        assert_eq!(center_point(&[]), None);
        assert_eq!(center_point(&[[1.0, 2.0, 3.0]]), Some([1.0, 2.0, 3.0]));
        assert_eq!(center_point(&[[0.0, 0.0, 0.0], [2.0, 4.0, 6.0]]), Some([1.0, 2.0, 3.0]));
        assert_eq!(farthest_distance_from_point([0.0; 3], &[[3.0, 4.0, 0.0], [1.0, 0.0, 0.0]]), Some(5.0));
        assert_eq!(farthest_distance_from_point([0.0; 3], &[]), None);
    }
}
