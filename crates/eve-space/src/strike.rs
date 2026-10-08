//! A doomsday's strike (wire tag 13, ADR 0013 epoch 25): an area of a fixed shape in front of the firing ship, hit a number of times after a warning.
//!
//! The geometry is the reference's (`superweaponRuntime.js`, `isEntityInsideCone` and `isEntityInsideCylinder`): the area starts at the hull's surface
//! along the aim, runs `length` metres, and a cone's radius at a distance is that distance (a 45 degree half angle). The numbers are the host's: it sends
//! the aim, the length, the damage per pulse and the windows (the SDE's values). The simulation only applies them.

use crate::health::Damage;

/// The shape of a strike, by the static data's `doomsdayAOEShape` value: 1 a cylinder, 3 a cone.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StrikeShape {
    Cylinder,
    Cone,
}

impl StrikeShape {
    /// The wire byte of the shape.
    pub const fn wire(self) -> u8 {
        match self {
            Self::Cylinder => 1,
            Self::Cone => 3,
        }
    }

    /// The shape of a wire byte, or None for a shape the simulation does not run.
    pub const fn from_wire(byte: u8) -> Option<Self> {
        match byte {
            1 => Some(Self::Cylinder),
            3 => Some(Self::Cone),
            _ => None,
        }
    }
}

/// A strike as the host sends it. Times are in milliseconds, as the static data gives them; distances in metres.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Strike {
    pub shape: StrikeShape,
    /// The unit vector of the aim, from the firing ship's centre.
    pub direction: [f64; 3],
    pub length: f64,
    /// A cylinder's radius (0 for a cone).
    pub radius: f64,
    /// The time from the start of the cycle to the first pulse.
    pub warning_ms: f64,
    /// The time between two pulses.
    pub pulse_ms: f64,
    pub pulses: i32,
    /// The damage of one pulse to a target that is hit whole: em, thermal, kinetic, explosive.
    pub damage: Damage,
    /// The weapon's signature radius: a target smaller than it takes the damage scaled by target / weapon.
    pub signature_radius: f64,
    /// For this long from the start of the cycle the firing ship cannot move, warp or jump.
    pub immobile_ms: f64,
    /// For this long from the start of the cycle the firing ship cannot jump or cloak.
    pub no_jump_ms: f64,
    /// The capacitor every ship within `neut_radius` of the firing ship loses once, at the start of the cycle, scaled by its signature factor
    /// against `neut_signature_radius`.
    pub neut_amount: f64,
    pub neut_radius: f64,
    pub neut_signature_radius: f64,
}

impl Strike {
    /// Whether a ship is in the area, from `origin` (the hull's surface point along the aim) and with its centre and radius.
    pub fn covers(&self, origin: [f64; 3], centre: [f64; 3], target_radius: f64) -> bool {
        let offset = [centre[0] - origin[0], centre[1] - origin[1], centre[2] - origin[2]];
        let along = dot(offset, self.direction);
        let reach = target_radius.max(0.0);
        if along < -reach || along > self.length + reach {
            return false;
        }
        match self.shape {
            StrikeShape::Cone => {
                let radial = distance_from_axis(offset, self.direction, along);
                radial <= along.max(0.0) + reach
            }
            StrikeShape::Cylinder => {
                let clamped = along.clamp(0.0, self.length);
                let radial = distance_from_axis(offset, self.direction, clamped);
                radial <= self.radius.max(0.0) + reach
            }
        }
    }

    /// The moment, in simulation ticks, after the start of the cycle, of pulse `index` (0 is the first).
    pub fn pulse_offset_ms(&self, index: i32) -> f64 {
        self.warning_ms + f64::from(index) * self.pulse_ms
    }

    /// Whether the numbers are ones a strike can run (finite, positive where a divisor or a time needs it, a unit aim).
    pub fn is_valid(&self) -> bool {
        let finite = |values: &[f64]| values.iter().all(|v| v.is_finite());
        let norm = dot(self.direction, self.direction);
        finite(&self.direction)
            && (norm - 1.0).abs() < 1e-6
            && finite(&self.damage)
            && self.damage.iter().all(|d| *d >= 0.0)
            && self.length.is_finite()
            && self.length > 0.0
            && self.radius.is_finite()
            && self.radius >= 0.0
            && self.warning_ms.is_finite()
            && self.warning_ms >= 0.0
            && self.pulse_ms.is_finite()
            && self.pulse_ms > 0.0
            && self.pulses >= 1
            && self.signature_radius.is_finite()
            && self.signature_radius > 0.0
            && self.immobile_ms.is_finite()
            && self.immobile_ms >= 0.0
            && self.no_jump_ms.is_finite()
            && self.no_jump_ms >= 0.0
            && self.neut_amount.is_finite()
            && self.neut_amount >= 0.0
            && self.neut_radius.is_finite()
            && self.neut_radius >= 0.0
            && self.neut_signature_radius.is_finite()
            && self.neut_signature_radius >= 0.0
    }
}

/// The unit vector of an aim, or None when the aim is zero or not finite.
pub fn unit(vector: [f64; 3]) -> Option<[f64; 3]> {
    let length = dot(vector, vector).sqrt();
    (length.is_finite() && length > 0.0).then(|| [vector[0] / length, vector[1] / length, vector[2] / length])
}

fn dot(a: [f64; 3], b: [f64; 3]) -> f64 {
    a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
}

fn distance_from_axis(offset: [f64; 3], axis: [f64; 3], along: f64) -> f64 {
    let d = [offset[0] - axis[0] * along, offset[1] - axis[1] * along, offset[2] - axis[2] * along];
    dot(d, d).sqrt()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn cone(length: f64) -> Strike {
        Strike {
            shape: StrikeShape::Cone,
            direction: [1.0, 0.0, 0.0],
            length,
            radius: 0.0,
            warning_ms: 10_000.0,
            pulse_ms: 1_000.0,
            pulses: 20,
            damage: [10_000.0; 4],
            signature_radius: 10_000.0,
            immobile_ms: 30_000.0,
            no_jump_ms: 300_000.0,
            neut_amount: 30_000.0,
            neut_radius: 10_000.0,
            neut_signature_radius: 20_000.0,
        }
    }

    #[test]
    fn a_cone_widens_to_45_degrees_along_its_length() {
        let strike = cone(30_000.0);
        let origin = [0.0, 0.0, 0.0];
        // straight ahead, at the far end and just inside the widening cone (radial reach equals distance)
        assert!(strike.covers(origin, [20_000.0, 0.0, 0.0], 0.0));
        assert!(strike.covers(origin, [10_000.0, 9_900.0, 0.0], 0.0), "inside the 45 degree flank");
        assert!(!strike.covers(origin, [10_000.0, 10_100.0, 0.0], 0.0), "outside the flank");
        // behind the origin, and past the length
        assert!(!strike.covers(origin, [-5_000.0, 0.0, 0.0], 0.0));
        assert!(!strike.covers(origin, [31_000.0, 0.0, 0.0], 0.0));
        // a target's radius counts (a ship of 100 m at 30 100 m is reached by the end of the cone)
        assert!(strike.covers(origin, [30_050.0, 0.0, 0.0], 100.0));
    }

    #[test]
    fn a_cylinder_is_a_radius_along_a_length() {
        let strike = Strike { shape: StrikeShape::Cylinder, radius: 500.0, ..cone(10_000.0) };
        assert!(strike.covers([0.0; 3], [5_000.0, 400.0, 0.0], 0.0));
        assert!(!strike.covers([0.0; 3], [5_000.0, 600.0, 0.0], 0.0));
        assert!(!strike.covers([0.0; 3], [10_600.0, 0.0, 0.0], 0.0));
    }

    #[test]
    fn a_strike_needs_a_unit_aim_and_positive_cadence() {
        assert!(cone(30_000.0).is_valid());
        assert!(!Strike { direction: [2.0, 0.0, 0.0], ..cone(30_000.0) }.is_valid());
        assert!(!Strike { pulse_ms: 0.0, ..cone(30_000.0) }.is_valid());
        assert!(!Strike { pulses: 0, ..cone(30_000.0) }.is_valid());
        assert!(!Strike { damage: [1.0, -1.0, 0.0, 0.0], ..cone(30_000.0) }.is_valid());
        assert_eq!(unit([0.0, 3.0, 4.0]).map(|u| u[1]), Some(0.6));
        assert_eq!(unit([0.0; 3]), None);
    }

    #[test]
    fn the_shape_bytes_are_the_static_data_values() {
        assert_eq!(StrikeShape::from_wire(3), Some(StrikeShape::Cone));
        assert_eq!(StrikeShape::from_wire(1), Some(StrikeShape::Cylinder));
        assert_eq!(StrikeShape::from_wire(2), None, "a slice is not run");
        assert_eq!(StrikeShape::Cone.wire(), 3);
    }
}
