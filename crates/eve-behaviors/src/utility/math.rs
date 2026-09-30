//! `behaviors/utility/math.py`: where a fleet stands between its friends and its enemies.

use std::collections::BTreeMap;

fn sub(a: [f64; 3], b: [f64; 3]) -> [f64; 3] {
    [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
}

fn normalize(v: [f64; 3]) -> [f64; 3] {
    let length = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt();
    if length == 0.0 { [0.0; 3] } else { [v[0] / length, v[1] / length, v[2] / length] }
}

/// `compute_forward_fleet_position`.
pub fn compute_forward_fleet_position(friendly: [f64; 3], hostile: [f64; 3], distance: f64) -> [f64; 3] {
    compute_fleet_position(friendly, hostile, distance, hostile)
}

/// `compute_rear_fleet_position`.
pub fn compute_rear_fleet_position(friendly: [f64; 3], hostile: [f64; 3], distance: f64) -> [f64; 3] {
    compute_fleet_position(friendly, hostile, distance, friendly)
}

/// `compute_fleet_position`: from the reference, `distance` along the way from the enemy to the friends.
pub fn compute_fleet_position(
    friendly: [f64; 3],
    hostile: [f64; 3],
    distance: f64,
    reference: [f64; 3],
) -> [f64; 3] {
    let rear = normalize(sub(friendly, hostile));
    [reference[0] + rear[0] * distance, reference[1] + rear[1] * distance, reference[2] + rear[2] * distance]
}

/// `get_normalized_dict`: every value over the largest (the dict as it is when the largest is 0). An empty dict has no largest, as in the client.
pub fn get_normalized_dict(target: &BTreeMap<i64, f64>) -> Option<BTreeMap<i64, f64>> {
    let max_value =
        target.values().copied().fold(None, |best: Option<f64>, v| Some(best.map_or(v, |b| b.max(v))))?;
    if max_value == 0.0 {
        return Some(target.clone());
    }
    Some(target.iter().map(|(k, v)| (*k, v / max_value)).collect())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_fleet_stands_along_the_line_from_its_enemy_to_its_friends() {
        let (friendly, hostile) = ([10.0, 0.0, 0.0], [0.0, 0.0, 0.0]);
        assert_eq!(compute_forward_fleet_position(friendly, hostile, 4.0), [4.0, 0.0, 0.0]);
        assert_eq!(compute_rear_fleet_position(friendly, hostile, 4.0), [14.0, 0.0, 0.0]);
    }

    #[test]
    fn a_dict_is_normalized_by_its_largest_value_unless_that_is_zero_and_an_empty_one_has_none() {
        let dict = BTreeMap::from([(1, 2.0), (2, 4.0)]);
        assert_eq!(get_normalized_dict(&dict), Some(BTreeMap::from([(1, 0.5), (2, 1.0)])));
        let zeros = BTreeMap::from([(1, 0.0)]);
        assert_eq!(get_normalized_dict(&zeros), Some(zeros.clone()));
        assert_eq!(get_normalized_dict(&BTreeMap::new()), None);
    }
}
