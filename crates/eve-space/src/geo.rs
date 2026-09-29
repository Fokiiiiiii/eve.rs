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
}
