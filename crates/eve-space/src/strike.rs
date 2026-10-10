//! A doomsday's strike (wire tag 13, ADR 0013 epoch 25): an area of a fixed shape in front of the firing ship, hit a number of times after a warning.
//!
//! The geometry is the reference's (`superweaponRuntime.js`, `isEntityInsideCone` and `isEntityInsideCylinder`): the area starts at the hull's surface
//! along the aim, runs `length` metres, and a cone's radius at a distance is that distance (a 45 degree half angle). The numbers are the host's: it sends
//! the aim, the length, the damage per pulse and the windows (the SDE's values). The simulation only applies them.

use crate::health::Damage;

/// The share of the remote repair a ship takes while a disruptive lance has disrupted it (EveJS `DISRUPTIVE_LANCE_REMOTE_REPAIR_MULTIPLIER`, a CCP
/// description has no SDE attribute: "massively inhibit the effectiveness of external repair beams"; the 0.5 is EveJS's, see DEVIATIONS 139).
pub const DISRUPTED_REMOTE_REPAIR: f64 = 0.5;

/// The shape of a strike, by the static data's `doomsdayAOEShape` value: 1 a cylinder, 3 a cone, 4 a sphere (the projected sphere of the Gravitational
/// Transportation Field Oscillator, protocol 29: centred on the aim point, its radius the static data's `doomsdayAOERange`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StrikeShape {
    Cylinder,
    Cone,
    Sphere,
}

impl StrikeShape {
    /// The wire byte of the shape.
    pub const fn wire(self) -> u8 {
        match self {
            Self::Cylinder => 1,
            Self::Cone => 3,
            Self::Sphere => 4,
        }
    }

    /// The shape of a wire byte, or None for a shape the simulation does not run.
    pub const fn from_wire(byte: u8) -> Option<Self> {
        match byte {
            1 => Some(Self::Cylinder),
            3 => Some(Self::Cone),
            4 => Some(Self::Sphere),
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
    /// For this long from a pulse that covers a ship, the ship cannot warp, jump or dock and takes half the remote repair (the disruptive lance's
    /// `doomsdayAppliedDBuffDuration`, 60 s). 0 for none. Protocol 28.
    pub debuff_ms: f64,
    /// A sweeping strike (a reaper, `isPointTargeted` 2): the end of the cylinder's axis at the last pulse, as an offset from the firing ship's centre. The
    /// start is `direction * length`; each pulse runs the axis from the centre toward the point `start + (end - start) * progress`, where progress is the
    /// pulse's share of the window (EveJS `resolveSlashDamageVolume`). None for a fixed aim. Protocol 28.
    pub sweep_end: Option<[f64; 3]>,
    /// A lance's beacon (EVE.js `spawnLanceBeacon`, protocol 39): where the beam is anchored, as an offset from the firing ship's centre (the aim clamped to the
    /// strike's length). The simulation puts the beacon ball there when the strike starts. None for a strike with no beacon.
    pub beacon: Option<[f64; 3]>,
}

impl Strike {
    /// The strike as it is at pulse `pulse` (0 is the first). For a fixed aim that is the strike itself; for a sweep, its axis at that pulse, and None
    /// when the axis has no length there (no area to hit).
    pub fn at_pulse(&self, pulse: i32) -> Option<Strike> {
        let Some(end) = self.sweep_end else { return Some(*self) };
        let last = f64::from((self.pulses - 1).max(1));
        let progress = (f64::from(pulse) / last).clamp(0.0, 1.0);
        let start = [
            self.direction[0] * self.length,
            self.direction[1] * self.length,
            self.direction[2] * self.length,
        ];
        let offset = [0, 1, 2].map(|i| start[i] + (end[i] - start[i]) * progress);
        let length = dot(offset, offset).sqrt();
        if !(length.is_finite() && length > 0.0) {
            return None;
        }
        let direction = [offset[0] / length, offset[1] / length, offset[2] / length];
        Some(Strike { direction, length, ..*self })
    }

    /// Whether a ship is in the area, from `origin` and with its centre and radius. For a cone or a cylinder `origin` is the hull's surface point along the
    /// aim; for a sphere it is the sphere's centre (the aim point), and the sphere is `radius` around it.
    pub fn covers(&self, origin: [f64; 3], centre: [f64; 3], target_radius: f64) -> bool {
        let offset = [centre[0] - origin[0], centre[1] - origin[1], centre[2] - origin[2]];
        let along = dot(offset, self.direction);
        let reach = target_radius.max(0.0);
        if self.shape != StrikeShape::Sphere && (along < -reach || along > self.length + reach) {
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
            StrikeShape::Sphere => dot(offset, offset).sqrt() <= self.radius.max(0.0) + reach,
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
            // a strike with no damage (the transport field) has no signature to scale: its static data has none
            && (self.signature_radius > 0.0 || self.damage.iter().all(|d| *d == 0.0))
            && (self.shape != StrikeShape::Sphere || self.radius > 0.0)
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
            && self.debuff_ms.is_finite()
            && self.debuff_ms >= 0.0
            && self.sweep_end.is_none_or(|end| finite(&end) && dot(end, end) > 0.0)
            && self.beacon.is_none_or(|offset| finite(&offset))
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
            debuff_ms: 0.0,
            sweep_end: None,
            beacon: None,
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
    fn a_sweep_turns_its_axis_by_the_share_of_its_window_and_a_pulse_on_no_axis_hits_nothing() {
        let sweep = Strike {
            shape: StrikeShape::Cylinder,
            radius: 500.0,
            length: 2_000.0,
            pulses: 3,
            pulse_ms: 5_000.0,
            sweep_end: Some([0.0, 2_000.0, 0.0]),
            ..cone(2_000.0)
        };
        let first = sweep.at_pulse(0).unwrap();
        assert_eq!((first.direction, first.length), ([1.0, 0.0, 0.0], 2_000.0), "the start point");
        // halfway through the window the axis points between the two points, at the same length (a right angle sweep)
        let middle = sweep.at_pulse(1).unwrap();
        assert!(
            (middle.length - 2_000.0 * 0.5f64.sqrt()).abs() < 1e-6 * 2_000.0
                && (middle.direction[0] - middle.direction[1]).abs() < 1e-9
        );
        let last = sweep.at_pulse(2).unwrap();
        assert_eq!(last.direction, [0.0, 1.0, 0.0], "the end point");
        // a sweep whose axis passes through the origin has no length there and hits nothing
        let through = Strike { sweep_end: Some([-2_000.0, 0.0, 0.0]), pulses: 3, ..sweep };
        assert!(through.at_pulse(1).is_none(), "a zero axis at the middle pulse");
        assert!(
            cone(30_000.0).at_pulse(0).is_some_and(|fixed| fixed == cone(30_000.0)),
            "a fixed aim is the strike itself"
        );
        assert!(
            !Strike { sweep_end: Some([0.0; 3]), ..sweep }.is_valid(),
            "a sweep that ends on the ship is not valid"
        );
    }

    #[test]
    fn the_shape_bytes_are_the_static_data_values() {
        assert_eq!(StrikeShape::from_wire(3), Some(StrikeShape::Cone));
        assert_eq!(StrikeShape::from_wire(1), Some(StrikeShape::Cylinder));
        assert_eq!(StrikeShape::from_wire(2), None, "a slice is not run");
        assert_eq!(StrikeShape::Cone.wire(), 3);
    }

    fn sphere(radius: f64) -> Strike {
        Strike {
            shape: StrikeShape::Sphere,
            direction: [0.0, 0.0, 1.0],
            length: 80_000.0,
            radius,
            warning_ms: 15_000.0,
            pulse_ms: 1_000.0,
            pulses: 1,
            damage: [0.0; 4],
            signature_radius: 0.0,
            immobile_ms: 30_000.0,
            no_jump_ms: 300_000.0,
            neut_amount: 30_000.0,
            neut_radius: 10_000.0,
            neut_signature_radius: 20_000.0,
            debuff_ms: 0.0,
            sweep_end: None,
            beacon: None,
        }
    }

    #[test]
    fn a_sphere_covers_the_ships_within_its_radius_of_its_centre() {
        // the centre is the aim point; for a sphere the origin argument is that centre
        let strike = sphere(10_000.0);
        let centre = [0.0, 0.0, 80_000.0];
        assert!(strike.is_valid(), "a damage-free field with no signature is a valid strike");
        assert!(strike.covers(centre, centre, 0.0), "the centre itself");
        // (6000, 8000) is exactly 10 km from the centre, the radius
        assert!(strike.covers(centre, [6_000.0, 8_000.0, 80_000.0], 0.0), "on the radius");
        // (6000, 8001) is 10000.5 m away: outside with no reach, inside once the target's own radius (1 m) is counted
        assert!(!strike.covers(centre, [6_000.0, 8_001.0, 80_000.0], 0.0), "just outside");
        assert!(strike.covers(centre, [6_000.0, 8_001.0, 80_000.0], 1.0), "reach of the target's radius");
        // a ship behind the centre, along the aim, is inside a sphere that reaches it: the sphere does not clip by the axis
        assert!(strike.covers(centre, [0.0, 0.0, 75_000.0], 0.0));
        // the firing ship is 80 km back along the aim: out of the sphere
        assert!(!strike.covers(centre, [0.0, 0.0, 0.0], 0.0));
    }

    #[test]
    fn the_sphere_is_a_wire_shape_and_a_sphere_needs_a_radius() {
        assert_eq!(StrikeShape::Sphere.wire(), 4);
        assert_eq!(StrikeShape::from_wire(4), Some(StrikeShape::Sphere));
        assert!(!sphere(0.0).is_valid(), "a sphere with no radius covers nothing");
        let mut damaging = sphere(10_000.0);
        damaging.damage = [1.0, 0.0, 0.0, 0.0];
        assert!(!damaging.is_valid(), "a strike that damages needs its signature radius");
        damaging.signature_radius = 20_000.0;
        assert!(damaging.is_valid());
    }
}
