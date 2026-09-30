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

/// `geo2.Vec3AddD`.
pub fn vec_add(a: [f64; 3], b: [f64; 3]) -> [f64; 3] {
    [a[0] + b[0], a[1] + b[1], a[2] + b[2]]
}

/// `geo2.Vec3SubtractD`.
pub fn vec_sub(a: [f64; 3], b: [f64; 3]) -> [f64; 3] {
    [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
}

/// `geo2.Vec3ScaleD`.
pub fn vec_scale(v: [f64; 3], factor: f64) -> [f64; 3] {
    [v[0] * factor, v[1] * factor, v[2] * factor]
}

/// `geo2.Vec3LengthD`.
pub fn vec_length(v: [f64; 3]) -> f64 {
    (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt()
}

/// `geo2.Vec3NormalizeD`: the vector of a length of 1 (the zero vector stays what it is).
pub fn vec_normalize(v: [f64; 3]) -> [f64; 3] {
    let length = vec_length(v);
    if length == 0.0 { [0.0; 3] } else { [v[0] / length, v[1] / length, v[2] / length] }
}

/// `geo2.Vec3DotD`.
pub fn vec_dot(a: [f64; 3], b: [f64; 3]) -> f64 {
    a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
}

/// `geo2.Vec3DirectionD(a, b)`: the direction of `a - b`.
pub fn vec_direction(a: [f64; 3], b: [f64; 3]) -> [f64; 3] {
    vec_normalize(vec_sub(a, b))
}

/// `mathCommon.RandomVector(radius, width)`: a point on a sphere of `radius`, or in a shell `width` thick below it. Draws two numbers, and a third
/// for a shell.
pub fn random_vector(radius: f64, width: f64, world: &mut dyn World) -> [f64; 3] {
    let t = world.random() * 2.0 * std::f64::consts::PI;
    let u = (world.random() - 0.5) * 2.0;
    let sq = (1.0 - u * u).sqrt();
    let mut radius = radius;
    if width > 0.0 {
        let width = width.min(radius);
        radius -= world.random() * width;
    }
    [radius * sq * t.cos(), radius * sq * t.sin(), radius * u]
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::env::TestWorld;

    #[test]
    fn a_weighted_choice_picks_by_the_accumulated_weight() {
        let choices = [('a', 1.0), ('b', 3.0), ('c', 6.0)];
        // total 10: r = 10 * number
        assert_eq!(weighted_choice(&choices, &mut TestWorld::with_numbers(&[0.0])), Some('a'));
        assert_eq!(
            weighted_choice(&choices, &mut TestWorld::with_numbers(&[0.1])),
            Some('a'),
            "a weight reaches its own end"
        );
        assert_eq!(weighted_choice(&choices, &mut TestWorld::with_numbers(&[0.11])), Some('b'));
        assert_eq!(weighted_choice(&choices, &mut TestWorld::with_numbers(&[0.4])), Some('b'));
        assert_eq!(weighted_choice(&choices, &mut TestWorld::with_numbers(&[0.41])), Some('c'));
        assert_eq!(weighted_choice(&choices, &mut TestWorld::with_numbers(&[0.999])), Some('c'));
    }

    #[test]
    fn weights_of_nothing_choose_any_and_draw_two_numbers() {
        let choices = [('a', 0.0), ('b', 0.0), ('c', 0.0)];
        let mut world = TestWorld::with_numbers(&[0.5, 0.7]);
        assert_eq!(weighted_choice(&choices, &mut world), Some('c'), "the second number picks: 0.7 of three");
        assert_eq!(weighted_choice::<char>(&[], &mut TestWorld::default()), None);
    }

    #[test]
    fn a_stochastic_round_rounds_up_with_the_chance_of_the_fraction() {
        assert_eq!(stochastic_round(2.3, &mut TestWorld::with_numbers(&[0.6])), 2);
        assert_eq!(stochastic_round(2.3, &mut TestWorld::with_numbers(&[0.8])), 3);
    }

    #[test]
    fn the_centre_of_points_and_the_distance_to_the_farthest() {
        assert_eq!(center_point(&[]), None);
        assert_eq!(center_point(&[[1.0, 2.0, 3.0]]), Some([1.0, 2.0, 3.0]));
        assert_eq!(center_point(&[[0.0, 0.0, 0.0], [2.0, 4.0, 6.0]]), Some([1.0, 2.0, 3.0]));
        assert_eq!(farthest_distance_from_point([0.0; 3], &[[3.0, 4.0, 0.0], [1.0, 0.0, 0.0]]), Some(5.0));
        assert_eq!(farthest_distance_from_point([0.0; 3], &[]), None);
    }

    #[test]
    fn a_random_vector_is_on_the_sphere_or_in_the_shell_and_draws_what_the_client_draws() {
        let mut w = TestWorld::with_numbers(&[0.25, 0.75, 0.5]);
        let v = random_vector(10.0, 0.0, &mut w);
        // t = pi / 2 * 1 = 0.5 * pi, u = 0.5, sq = sqrt(0.75)
        assert!(
            (v[0]).abs() < 1e-9 && (v[1] - 10.0 * 0.75f64.sqrt()).abs() < 1e-9 && (v[2] - 5.0).abs() < 1e-9
        );
        assert_eq!(w.next, 2, "two numbers");
        let mut w = TestWorld::with_numbers(&[0.0, 1.0, 0.5]);
        let v = random_vector(10.0, 4.0, &mut w);
        // u = 1: the pole; the radius is 10 - 0.5 * 4
        assert!((v[2] - 8.0).abs() < 1e-9 && v[0].abs() < 1e-9 && v[1].abs() < 1e-9);
        assert_eq!(w.next, 3);
        // a shell wider than the radius is the radius
        let mut w = TestWorld::with_numbers(&[0.0, 1.0, 1.0]);
        assert!((random_vector(10.0, 40.0, &mut w)[2]).abs() < 1e-9);
    }

    #[test]
    fn vectors_are_added_scaled_and_normalized_and_the_direction_goes_from_the_second_to_the_first() {
        assert_eq!(vec_add([1.0, 2.0, 3.0], [1.0, 1.0, 1.0]), [2.0, 3.0, 4.0]);
        assert_eq!(vec_sub([1.0, 2.0, 3.0], [1.0, 1.0, 1.0]), [0.0, 1.0, 2.0]);
        assert_eq!(vec_scale([1.0, 2.0, 3.0], 2.0), [2.0, 4.0, 6.0]);
        assert_eq!(vec_normalize([0.0, 3.0, 4.0]), [0.0, 0.6, 0.8]);
        assert_eq!(vec_normalize([0.0; 3]), [0.0; 3]);
        assert_eq!(vec_dot([1.0, 2.0, 3.0], [4.0, 5.0, 6.0]), 32.0);
        assert_eq!(vec_direction([10.0, 0.0, 0.0], [4.0, 0.0, 0.0]), [1.0, 0.0, 0.0]);
    }
}
