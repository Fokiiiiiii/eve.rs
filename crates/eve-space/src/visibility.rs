//! Which ships can see which: the rule the scene applies between every two ships in it.
//!
//! The reference streams what is near a pilot in boxes of 7 864 320 m (its "public grid") and keeps a ship that warps in view until it is
//! 50 000 km away, taking it out at the first one-second sample beyond that (`destiny/visibility/constants.js`; the recorded watcher was told a
//! ship that had warped off was gone 7 seconds into its warp, `oracle/fixtures/scenarios/others.json`). The static bodies of a system are always
//! in view (every recorded `SetState` holds all of them), so this is about ships only.
use eve_destiny::Vec3;

/// The side of a box of the public grid, in metres.
pub const GRID_BOX: f64 = 7_864_320.0;
/// How far a ship in warp stays in view.
pub const WARP_ENVELOPE: f64 = 50_000_000.0;

/// The box a place is in: the whole number of boxes it is from the origin along each axis (rounded down).
pub fn grid_of(position: Vec3) -> (i64, i64, i64) {
    let index = |value: f64| (value / GRID_BOX).floor() as i64;
    (index(position.x), index(position.y), index(position.z))
}

fn distance(a: Vec3, b: Vec3) -> f64 {
    ((a.x - b.x).powi(2) + (a.y - b.y).powi(2) + (a.z - b.z).powi(2)).sqrt()
}

/// Where a ship is for the purpose of seeing: its place, and whether it is in warp (a ship that is aligning for warp is an ordinary one).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Presence {
    pub position: Vec3,
    pub warping: bool,
}

/// Cloak transition as it affects what another pilot can see and target.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CloakState {
    /// Fully visible and targetable.
    Uncloaked,
    /// The ship remains visible during the cloak animation but cannot lock or be locked.
    Cloaking,
    /// Fully cloaked, including while covert-warping.
    Cloaked,
    /// Visible and targetable as soon as decloaking begins.
    Decloaking,
}

/// Whether `observer` can see `subject`: they are in the same box of the grid, or one of them is in warp and they are within the warp envelope.
/// The rule is the same both ways round, so two ships see each other or neither does.
pub fn can_see(observer: Presence, subject: Presence) -> bool {
    if grid_of(observer.position) == grid_of(subject.position) {
        return true;
    }
    (observer.warping || subject.warping) && distance(observer.position, subject.position) <= WARP_ENVELOPE
}

/// Whether the observer sees the subject through the public-grid and warp rules,
/// after the subject's cloak transition is applied.
pub fn can_see_subject(observer: Presence, subject: Presence, subject_cloak: CloakState) -> bool {
    !matches!(subject_cloak, CloakState::Cloaked) && can_see(observer, subject)
}

/// Whether the observer may lock the subject, applying cloak restrictions and visibility.
pub fn can_select_target(
    observer: Presence,
    subject: Presence,
    observer_cloak: CloakState,
    subject_cloak: CloakState,
) -> bool {
    matches!(observer_cloak, CloakState::Uncloaked | CloakState::Decloaking)
        && matches!(subject_cloak, CloakState::Uncloaked | CloakState::Decloaking)
        && can_see(observer, subject)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn at(x: f64, y: f64, z: f64, warping: bool) -> Presence {
        Presence { position: Vec3::new(x, y, z), warping }
    }

    #[test]
    fn the_sizes_are_the_reference_s() {
        // destiny/visibility/constants.js: PUBLIC_GRID_BOX_METERS and MICHELLE_TRANSIENT_WARP_VISIBILITY_RANGE_METERS
        assert_eq!(GRID_BOX, 7_864_320.0);
        assert_eq!(WARP_ENVELOPE, 50_000_000.0);
    }

    #[test]
    fn a_ship_in_warp_exactly_at_the_envelope_is_still_in_view_and_one_metre_further_is_not() {
        let watcher = at(0.0, 0.0, 0.0, false);
        assert!(can_see(watcher, at(WARP_ENVELOPE, 0.0, 0.0, true)));
        assert!(!can_see(watcher, at(WARP_ENVELOPE + 1.0, 0.0, 0.0, true)));
    }

    #[test]
    fn a_place_is_in_the_box_of_its_whole_number_of_boxes_rounded_down() {
        assert_eq!(grid_of(Vec3::new(0.0, 0.0, 0.0)), (0, 0, 0));
        assert_eq!(grid_of(Vec3::new(GRID_BOX - 1.0, 1.0, 0.0)), (0, 0, 0));
        assert_eq!(grid_of(Vec3::new(GRID_BOX, -1.0, 2.5 * GRID_BOX)), (1, -1, 2));
        assert_eq!(grid_of(Vec3::new(-GRID_BOX, -GRID_BOX - 1.0, 0.0)), (-1, -2, 0));
    }

    #[test]
    fn ships_in_one_box_see_each_other_however_they_move() {
        let (a, b) = (at(1_000.0, 0.0, 0.0, false), at(6_000_000.0, 7_000_000.0, 100.0, false));
        assert!(can_see(a, b) && can_see(b, a));
    }

    #[test]
    fn ships_in_boxes_that_are_not_the_same_do_not_see_each_other_unless_one_warps_within_the_envelope() {
        let near_the_edge = at(GRID_BOX - 1.0, 0.0, 0.0, false);
        let over_the_edge = at(GRID_BOX + 1.0, 0.0, 0.0, false);
        assert!(
            !can_see(near_the_edge, over_the_edge),
            "an ordinary ship the other side of an edge is out of view"
        );
        let warping = at(GRID_BOX + 1.0, 0.0, 0.0, true);
        assert!(can_see(near_the_edge, warping) && can_see(warping, near_the_edge));
        let far = at(WARP_ENVELOPE + 1_000.0, 0.0, 0.0, true);
        assert!(!can_see(at(0.0, 0.0, 0.0, false), far) && !can_see(far, at(0.0, 0.0, 0.0, false)));
        let inside = at(WARP_ENVELOPE - 1_000.0, 0.0, 0.0, true);
        assert!(can_see(at(0.0, 0.0, 0.0, false), inside));
    }

    #[test]
    fn two_ships_in_warp_see_each_other_when_they_are_within_the_envelope() {
        assert!(can_see(at(0.0, 0.0, 0.0, true), at(40_000_000.0, 0.0, 0.0, true)));
        assert!(!can_see(at(0.0, 0.0, 0.0, true), at(60_000_000.0, 0.0, 0.0, true)));
    }

    #[test]
    fn an_ordinary_ship_out_of_the_box_is_out_of_view_however_near() {
        assert!(!can_see(at(GRID_BOX - 1.0, 0.0, 0.0, false), at(GRID_BOX, 0.0, 0.0, false)));
    }

    #[test]
    fn a_cloaked_ship_is_hidden_from_visibility_and_target_selection_on_the_same_grid() {
        let observer = at(1_000.0, 0.0, 0.0, false);
        let cloaked_subject = at(2_000.0, 0.0, 0.0, false);

        assert!(!can_see_subject(observer, cloaked_subject, CloakState::Cloaked));
        assert!(!can_select_target(observer, cloaked_subject, CloakState::Uncloaked, CloakState::Cloaked));
    }

    #[test]
    fn a_cloaked_ship_flying_off_stays_hidden_inside_the_warp_envelope() {
        let observer = at(0.0, 0.0, 0.0, false);
        let cloaked_warping_subject = at(WARP_ENVELOPE / 2.0, 0.0, 0.0, true);

        assert!(can_see(observer, cloaked_warping_subject));
        assert!(!can_see_subject(observer, cloaked_warping_subject, CloakState::Cloaked));
        assert!(!can_select_target(
            observer,
            cloaked_warping_subject,
            CloakState::Uncloaked,
            CloakState::Cloaked
        ));
    }

    #[test]
    fn decloaking_makes_a_subject_visible_and_selectable_immediately() {
        let observer = at(1_000.0, 0.0, 0.0, false);
        let subject = at(2_000.0, 0.0, 0.0, false);

        assert!(can_see_subject(observer, subject, CloakState::Decloaking));
        assert!(can_select_target(observer, subject, CloakState::Uncloaked, CloakState::Decloaking));
    }

    #[test]
    fn a_cloaked_observer_cannot_select_an_uncloaked_visible_subject() {
        let observer = at(1_000.0, 0.0, 0.0, false);
        let subject = at(2_000.0, 0.0, 0.0, false);

        assert!(can_see_subject(observer, subject, CloakState::Decloaking));
        assert!(!can_select_target(observer, subject, CloakState::Cloaked, CloakState::Uncloaked));
    }

    #[test]
    fn the_cloaking_animation_is_visible_but_not_targetable() {
        let observer = at(1_000.0, 0.0, 0.0, false);
        let subject = at(2_000.0, 0.0, 0.0, false);

        assert!(can_see_subject(observer, subject, CloakState::Cloaking));
        assert!(!can_select_target(observer, subject, CloakState::Uncloaked, CloakState::Cloaking,));
    }
}
