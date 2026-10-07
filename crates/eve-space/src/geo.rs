//! Vector and angle helpers with the arithmetic the reference uses (`server/src/common/geo2.js`): single-precision steps,
//! a polynomial sine and cosine, a quaternion arc. Only what the world needs (a body's facing) is here.

use std::f64::consts::PI;

use eve_destiny::Vec3;

const TWO_PI: f64 = PI * 2.0;

/// `f32(value)`: round to single precision (a non-finite value is 0).
pub fn f32r(value: f64) -> f64 {
    if value.is_finite() { f64::from(value as f32) } else { 0.0 }
}

fn v32(v: Vec3) -> Vec3 {
    Vec3::new(f32r(v.x), f32r(v.y), f32r(v.z))
}

/// `Vec3Subtract`.
pub fn subtract(left: Vec3, right: Vec3) -> Vec3 {
    let (a, b) = (v32(left), v32(right));
    Vec3::new(f32r(a.x - b.x), f32r(a.y - b.y), f32r(a.z - b.z))
}

/// `Vec3Dot`.
pub fn dot(left: Vec3, right: Vec3) -> f64 {
    let (a, b) = (v32(left), v32(right));
    let x = f32r(a.x * b.x);
    let y = f32r(a.y * b.y);
    let z = f32r(a.z * b.z);
    f32r(f32r(x + y) + z)
}

/// `Vec3Cross`.
pub fn cross(left: Vec3, right: Vec3) -> Vec3 {
    let (a, b) = (v32(left), v32(right));
    Vec3::new(f32r(a.y * b.z - a.z * b.y), f32r(a.z * b.x - a.x * b.z), f32r(a.x * b.y - a.y * b.x))
}

/// `Vec3Normalize`: zero for a vector with no length.
pub fn normalize(value: Vec3) -> Vec3 {
    let v = v32(value);
    let length_squared = f32r(f32r(f32r(v.x * v.x) + f32r(v.y * v.y)) + f32r(v.z * v.z));
    if !length_squared.is_finite() || length_squared <= 0.0 {
        return Vec3::ZERO;
    }
    let length = f32r(length_squared.sqrt());
    if length <= 0.0 {
        return Vec3::ZERO;
    }
    Vec3::new(f32r(v.x / length), f32r(v.y / length), f32r(v.z / length))
}

const SIN_0: [f64; 4] = [-0.16666667, 0.008333331, -0.00019840874, 2.7525562e-06];
const SIN_1: f64 = -2.3889859e-08;
const COS_0: [f64; 4] = [-0.5, 0.041666638, -0.0013888378, 2.4760495e-05];
const COS_1: f64 = -2.6051615e-07;

fn madd(left: f64, right: f64, addend: f64) -> f64 {
    f32r(f32r(left * right) + addend)
}

/// `SinCosFloat32`: the game's polynomial `(cos, sin)` of an angle.
pub fn sin_cos(value: f64) -> (f64, f64) {
    let x = f32r(value);
    let x2 = f32r(x * x);
    let c = |k: f64| f32r(k);
    let mut sin = madd(c(SIN_1), x2, c(SIN_0[3]));
    sin = madd(sin, x2, c(SIN_0[2]));
    sin = madd(sin, x2, c(SIN_0[1]));
    sin = madd(sin, x2, c(SIN_0[0]));
    sin = madd(sin, x2, 1.0);
    sin = f32r(sin * x);

    let mut cos = madd(c(COS_1), x2, c(COS_0[3]));
    cos = madd(cos, x2, c(COS_0[2]));
    cos = madd(cos, x2, c(COS_0[1]));
    cos = madd(cos, x2, c(COS_0[0]));
    cos = madd(cos, x2, 1.0);
    (cos, sin)
}

#[derive(Clone, Copy)]
struct Quaternion {
    x: f64,
    y: f64,
    z: f64,
    w: f64,
}

const IDENTITY: Quaternion = Quaternion { x: 0.0, y: 0.0, z: 0.0, w: 1.0 };

fn rotation_axis(axis: Vec3, angle: f64) -> Quaternion {
    let n = normalize(axis);
    let half = f32r(f32r(angle) * 0.5);
    let (cos, sin) = sin_cos(half);
    Quaternion { x: f32r(n.x * sin), y: f32r(n.y * sin), z: f32r(n.z * sin), w: cos }
}

/// `QuaternionRotationArc(from, to)`.
fn rotation_arc(from: Vec3, to: Vec3) -> Quaternion {
    let d = dot(from, to).clamp(-1.0, 1.0);
    if d <= -1.0 || d >= 1.0 {
        return IDENTITY;
    }
    rotation_axis(cross(from, to), d.acos())
}

/// `QuaternionRotationGetYawPitchRoll`, as `(yaw, pitch)` in radians.
fn yaw_pitch(q: Quaternion) -> (f64, f64) {
    let (x, y, z, w) = (f32r(q.x), f32r(q.y), f32r(q.z), f32r(q.w));
    let (xx, yy) = (f32r(x * x), f32r(y * y));
    let (xz, yz) = (f32r(x * z), f32r(y * z));
    let (xw, yw) = (f32r(x * w), f32r(y * w));
    let m31 = f32r(2.0 * (xz + yw));
    let m32 = f32r(2.0 * (yz - xw));
    let m33 = f32r(1.0 - (2.0 * (xx + yy)));
    let mut yaw = f32r(m31.atan2(m33));
    if yaw < 0.0 {
        yaw = f32r(yaw + f32r(TWO_PI));
    }
    (yaw, f32r((-m32).clamp(-1.0, 1.0).asin()))
}

fn normalize_degrees(value: f64) -> f64 {
    if !value.is_finite() {
        return 0.0;
    }
    ((value % 360.0) + 360.0) % 360.0
}

/// `buildDunRotationFromNormalizedDirection`: the `(yaw, pitch, 0)` in degrees that turns the forward axis to a direction that is already a unit vector.
pub fn dun_rotation_of_unit(direction: Vec3) -> Option<[f64; 3]> {
    let forward = Vec3::new(0.0, 0.0, 1.0);
    let (yaw, pitch) = yaw_pitch(rotation_arc(direction, forward));
    let (yaw, pitch) = (normalize_degrees(yaw * (180.0 / PI)), pitch * (180.0 / PI));
    (yaw.is_finite() && pitch.is_finite()).then_some([yaw, pitch, 0.0])
}

/// `buildDunRotationFromDirection`: the same for any direction (normalized first, in single precision).
pub fn dun_rotation(direction: Vec3) -> Option<[f64; 3]> {
    dun_rotation_of_unit(normalize(direction))
}

/// A stargate faces along the line from its system to the one it leads to (`getStargateSystemForwardDirection`).
pub fn stargate_facing(source_system: Vec3, destination_system: Vec3) -> Option<[f64; 3]> {
    let direction = normalize(subtract(destination_system, source_system));
    (direction != Vec3::ZERO).then(|| dun_rotation_of_unit(direction)).flatten()
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum SpatialKind {
    Ship,
    PlayerShip,
    NpcShip,
    Drone,
    Missile,
    Bomb,
    Bubble,
    Wreck,
    Asteroid,
    SystemBody,
    Structure,
    Deployable,
    Probe,
}

#[derive(Clone, Copy, Debug)]
pub struct SpatialEntity<T> {
    pub id: i64,
    pub kind: SpatialKind,
    pub character_id: Option<i64>,
    pub owner_id: Option<i64>,
    pub corporation_id: Option<i32>,
    pub alliance_id: Option<i32>,
    pub type_id: Option<i32>,
    pub group_id: Option<i32>,
    pub category_id: Option<i32>,
    pub position: [f64; 3],
    pub radius: f64,
    pub warping: bool,
    pub item: T,
}

#[derive(Clone, Copy, Debug, Default)]
pub struct SpatialFilter<'a> {
    pub kinds: Option<&'a [SpatialKind]>,
    pub owner_id: Option<i64>,
    pub corporation_id: Option<i32>,
    pub alliance_id: Option<i32>,
    /// Character IDs from the caller's current fleet read model.
    pub fleet_members: Option<&'a std::collections::HashSet<i64>>,
    pub type_id: Option<i32>,
    pub group_id: Option<i32>,
    pub category_id: Option<i32>,
    pub exclude_id: Option<i64>,
    pub exclude_warping: bool,
}

impl SpatialFilter<'_> {
    fn accepts<T>(&self, entity: &SpatialEntity<T>) -> bool {
        self.exclude_id != Some(entity.id)
            && (!self.exclude_warping || !entity.warping)
            && self.kinds.is_none_or(|kinds| kinds.contains(&entity.kind))
            && self.owner_id.is_none_or(|id| entity.owner_id == Some(id))
            && self.corporation_id.is_none_or(|id| entity.corporation_id == Some(id))
            && self.alliance_id.is_none_or(|id| entity.alliance_id == Some(id))
            && self
                .fleet_members
                .is_none_or(|members| entity.character_id.is_some_and(|id| members.contains(&id)))
            && self.type_id.is_none_or(|id| entity.type_id == Some(id))
            && self.group_id.is_none_or(|id| entity.group_id == Some(id))
            && self.category_id.is_none_or(|id| entity.category_id == Some(id))
    }
}

#[derive(Clone, Copy, Debug)]
pub struct SpatialHit<T> {
    pub id: i64,
    pub distance: f64,
    pub along_path: f64,
    pub item: T,
}

#[derive(Clone, Copy, Debug)]
enum SpatialShape {
    Radius { center: [f64; 3], center_radius: f64, range: f64, surfaces: bool, tolerance: f64 },
    Path { start: [f64; 3], end: [f64; 3], width: f64, tolerance: f64 },
}

pub struct SpatialQuery<'a> {
    shape: SpatialShape,
    filter: SpatialFilter<'a>,
}

impl<'a> SpatialQuery<'a> {
    pub fn radius(center: [f64; 3], range: f64, filter: SpatialFilter<'a>) -> Self {
        Self {
            shape: SpatialShape::Radius {
                center,
                center_radius: 0.0,
                range,
                surfaces: false,
                tolerance: 0.0,
            },
            filter,
        }
    }

    pub fn surface_radius(
        center: [f64; 3],
        center_radius: f64,
        range: f64,
        tolerance: f64,
        filter: SpatialFilter<'a>,
    ) -> Self {
        Self {
            shape: SpatialShape::Radius { center, center_radius, range, surfaces: true, tolerance },
            filter,
        }
    }

    pub fn path(
        start: [f64; 3],
        end: [f64; 3],
        width: f64,
        tolerance: f64,
        filter: SpatialFilter<'a>,
    ) -> Self {
        Self { shape: SpatialShape::Path { start, end, width, tolerance }, filter }
    }

    pub fn execute<T>(self, entities: impl IntoIterator<Item = SpatialEntity<T>>) -> Vec<SpatialHit<T>> {
        let mut hits = Vec::new();
        for entity in entities {
            if !self.filter.accepts(&entity) {
                continue;
            }
            let Some((distance, along_path)) = self.distance(&entity) else {
                continue;
            };
            hits.push(SpatialHit { id: entity.id, distance, along_path, item: entity.item });
        }
        match self.shape {
            SpatialShape::Path { .. } => {
                hits.sort_by(|a, b| a.along_path.total_cmp(&b.along_path).then(a.id.cmp(&b.id)))
            }
            SpatialShape::Radius { .. } => {
                hits.sort_by(|a, b| a.distance.total_cmp(&b.distance).then(a.id.cmp(&b.id)))
            }
        }
        hits
    }

    pub fn nearest<T>(self, entities: impl IntoIterator<Item = SpatialEntity<T>>) -> Option<SpatialHit<T>> {
        self.execute(entities).into_iter().next()
    }

    fn distance<T>(&self, entity: &SpatialEntity<T>) -> Option<(f64, f64)> {
        if !entity.radius.is_finite()
            || entity.radius < 0.0
            || entity.position.iter().any(|coordinate| !coordinate.is_finite())
        {
            return None;
        }
        match self.shape {
            SpatialShape::Radius { center, center_radius, range, surfaces, tolerance } => {
                if !valid_range(range, tolerance)
                    || center.iter().any(|coordinate| !coordinate.is_finite())
                    || !center_radius.is_finite()
                    || center_radius < 0.0
                {
                    return None;
                }
                let centers = distance3(center, entity.position)?;
                let gap = if surfaces {
                    surface_gap(center, center_radius, entity.position, entity.radius)?
                } else {
                    centers
                };
                (gap <= range + tolerance).then_some((gap, 0.0))
            }
            SpatialShape::Path { start, end, width, tolerance } => {
                if !valid_range(width, tolerance)
                    || start.iter().chain(&end).any(|coordinate| !coordinate.is_finite())
                {
                    return None;
                }
                let (separation, along) = segment_distance(start, end, entity.position)?;
                let gap = (separation - entity.radius).max(0.0);
                (gap <= width + tolerance).then_some((gap, along))
            }
        }
    }
}

fn valid_range(range: f64, tolerance: f64) -> bool {
    range.is_finite() && range >= 0.0 && tolerance.is_finite() && tolerance >= 0.0
}

fn distance3(a: [f64; 3], b: [f64; 3]) -> Option<f64> {
    let distance = (a[0] - b[0]).hypot(a[1] - b[1]).hypot(a[2] - b[2]);
    distance.is_finite().then_some(distance)
}

pub fn surface_gap(a: [f64; 3], a_radius: f64, b: [f64; 3], b_radius: f64) -> Option<f64> {
    if !a_radius.is_finite() || !b_radius.is_finite() {
        return None;
    }
    Some((distance3(a, b)? - a_radius.max(0.0) - b_radius.max(0.0)).max(0.0))
}

fn segment_distance(start: [f64; 3], end: [f64; 3], point: [f64; 3]) -> Option<(f64, f64)> {
    let delta = [end[0] - start[0], end[1] - start[1], end[2] - start[2]];
    let length = distance3(start, end)?;
    let length_squared = delta.iter().map(|value| value * value).sum::<f64>();
    if !length_squared.is_finite() {
        return None;
    }
    let fraction = if length_squared == 0.0 {
        0.0
    } else {
        let from_start = [point[0] - start[0], point[1] - start[1], point[2] - start[2]];
        ((from_start[0] * delta[0] + from_start[1] * delta[1] + from_start[2] * delta[2]) / length_squared)
            .clamp(0.0, 1.0)
    };
    let closest =
        [start[0] + fraction * delta[0], start[1] + fraction * delta[1], start[2] + fraction * delta[2]];
    Some((distance3(closest, point)?, length * fraction))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn single_precision_rounding_drops_bits() {
        assert_eq!(f32r(0.1), f64::from(0.1_f32));
        assert_eq!(f32r(f64::NAN), 0.0);
        assert_eq!(f32r(1.0), 1.0);
    }

    #[test]
    fn a_vector_along_the_forward_axis_has_no_rotation() {
        assert_eq!(dun_rotation(Vec3::new(0.0, 0.0, 5.0)), Some([0.0, 0.0, 0.0]));
    }

    #[test]
    fn a_vector_to_one_side_turns_the_yaw() {
        // The arc runs from the direction to the forward axis, so a vector along +x is a quarter turn the other way round.
        let [yaw, pitch, roll] = dun_rotation(Vec3::new(1.0, 0.0, 0.0)).unwrap();
        assert!((yaw - 270.0).abs() < 0.01, "yaw {yaw}");
        assert!(pitch.abs() < 0.01 && roll == 0.0);
        let [yaw, ..] = dun_rotation(Vec3::new(-1.0, 0.0, 0.0)).unwrap();
        assert!((yaw - 90.0).abs() < 0.01, "yaw {yaw}");
    }

    #[test]
    fn the_polynomial_sine_and_cosine_are_close_to_the_true_ones() {
        for angle in [0.0, 0.1, 0.5, 1.0, -0.7] {
            let (cos, sin) = sin_cos(angle);
            assert!((sin - f64::sin(angle)).abs() < 1e-5, "sin {angle}");
            assert!((cos - f64::cos(angle)).abs() < 1e-5, "cos {angle}");
        }
    }

    fn spatial_entity(
        id: i64,
        kind: SpatialKind,
        position: [f64; 3],
        radius: f64,
        character_id: Option<i64>,
        owner_id: Option<i64>,
        corporation_id: Option<i32>,
    ) -> SpatialEntity<i64> {
        SpatialEntity {
            id,
            kind,
            character_id,
            owner_id,
            corporation_id,
            alliance_id: None,
            type_id: Some(id as i32),
            group_id: Some(1),
            category_id: Some(6),
            position,
            radius,
            warping: false,
            item: id,
        }
    }

    #[test]
    fn spatial_radius_query_applies_kind_owner_corporation_fleet_and_surface_distance() {
        let kinds = [SpatialKind::Drone];
        let fleet = std::collections::HashSet::from([42]);
        let filter = SpatialFilter {
            kinds: Some(&kinds),
            owner_id: Some(99),
            corporation_id: Some(7),
            fleet_members: Some(&fleet),
            ..SpatialFilter::default()
        };
        let entities = [
            spatial_entity(2, SpatialKind::Drone, [4.0, 0.0, 0.0], 1.0, Some(42), Some(99), Some(7)),
            spatial_entity(1, SpatialKind::Drone, [3.0, 0.0, 0.0], 1.0, Some(42), Some(99), Some(7)),
            spatial_entity(3, SpatialKind::NpcShip, [2.0, 0.0, 0.0], 1.0, Some(42), Some(99), Some(7)),
            spatial_entity(4, SpatialKind::Drone, [1.0, 0.0, 0.0], 1.0, Some(43), Some(99), Some(7)),
        ];

        let hits = SpatialQuery::surface_radius([0.0; 3], 1.0, 2.0, 0.0, filter).execute(entities);
        assert_eq!(hits.iter().map(|hit| hit.id).collect::<Vec<_>>(), [1, 2]);
        assert_eq!(hits[0].distance, 1.0);
        assert_eq!(
            SpatialQuery::surface_radius([0.0; 3], 1.0, 2.0, 0.0, filter).nearest(entities).map(|hit| hit.id),
            Some(1)
        );
    }

    #[test]
    fn spatial_path_query_returns_intersections_in_travel_order_and_rejects_bad_ranges() {
        let entities = [
            spatial_entity(2, SpatialKind::Missile, [8.0, 0.0, 0.0], 0.5, None, Some(99), None),
            spatial_entity(1, SpatialKind::Missile, [5.0, 1.5, 0.0], 0.5, None, Some(99), None),
            spatial_entity(3, SpatialKind::Missile, [5.0, 1.501, 0.0], 0.5, None, Some(99), None),
        ];
        let filter = SpatialFilter { owner_id: Some(99), ..SpatialFilter::default() };
        let hits = SpatialQuery::path([0.0; 3], [10.0, 0.0, 0.0], 1.0, 0.0, filter).execute(entities);
        assert_eq!(hits.iter().map(|hit| hit.id).collect::<Vec<_>>(), [1, 2]);
        assert_eq!(hits[0].along_path, 5.0);
        assert!(
            SpatialQuery::radius([0.0; 3], f64::NAN, SpatialFilter::default())
                .execute([spatial_entity(4, SpatialKind::PlayerShip, [0.0; 3], 1.0, None, None, None)])
                .is_empty()
        );
    }

    #[test]
    fn spatial_query_filters_warping_entities_and_sde_group_categories() {
        let valid = spatial_entity(1, SpatialKind::Ship, [1.0, 0.0, 0.0], 0.0, None, None, None);
        let wrong_group = SpatialEntity { id: 2, group_id: Some(2), ..valid };
        let warping = SpatialEntity { id: 3, warping: true, ..valid };
        let filter = SpatialFilter {
            group_id: Some(1),
            category_id: Some(6),
            exclude_warping: true,
            ..SpatialFilter::default()
        };

        let hits = SpatialQuery::radius([0.0; 3], 10.0, filter).execute([valid, wrong_group, warping]);
        assert_eq!(hits.iter().map(|hit| hit.id).collect::<Vec<_>>(), [1]);
    }
}
