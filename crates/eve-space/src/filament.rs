//! Pochven's filaments (`docs/project/POCHVEN_DESIGN.md`): the travel items that carry a fleet into and out of Pochven. The numbers are the SDE's
//! (build 3396210, `typeDogma`, attributes `mjdShipJumpCap`, `filamentSpoolupTimeSeconds`, `mjfgRadius`, `lightYearDistanceMax`); the trace rules are the
//! EVE University wiki's (Filament, Pochven, read 2026-10-12). Where the wiki and the SDE differ the SDE is used (the wiki's "Pochven Inbound 60 s" does not
//! match the SDE's 30 s and 45 s for the Cladistic and Pochven-system filaments). The destinations are not in the client extract (the location lists 4510 to
//! 4569 are not in it), so this module gives the rules and not the jump's target: the jump is not built.

/// Where a filament leads.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Direction {
    /// From known space into Pochven: a Krai (Cladistic) filament to a system of its clade's constellation, or a Pochven-system filament to a Border,
    /// Internal or Home system.
    Inbound,
    /// Out of Pochven: a Devana (Glorification) filament to a Triglavian Minor Victory system, or an Extraction (Proximity) filament to a system within
    /// 2.5 light-years.
    Outbound,
}

/// One filament type, as the SDE gives it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Filament {
    pub type_id: u32,
    pub name: &'static str,
    pub direction: Direction,
    /// The most subcapital ships it carries (`mjdShipJumpCap`).
    pub ship_cap: u32,
    /// How long the owner spools the filament up before the jump, seconds (`filamentSpoolupTimeSeconds`).
    pub spool_up_s: u32,
    /// The distance from the trace within which fleet members link to it, metres (`mjfgRadius`).
    pub link_radius_m: f64,
    /// The furthest a system may be from the origin, light-years (`lightYearDistanceMax`), for an Extraction filament.
    pub max_light_years: Option<f64>,
}

/// The SDE's 17 Pochven filaments (group 4087 "Triglavian Space Filaments").
pub const FILAMENTS: [Filament; 17] = [
    Filament {
        type_id: 56065,
        name: "Glorification-1 'Devana'",
        direction: Direction::Outbound,
        ship_cap: 1,
        spool_up_s: 60,
        link_radius_m: 6000.0,
        max_light_years: None,
    },
    Filament {
        type_id: 56066,
        name: "Glorification-5 'Devana'",
        direction: Direction::Outbound,
        ship_cap: 5,
        spool_up_s: 120,
        link_radius_m: 6000.0,
        max_light_years: None,
    },
    Filament {
        type_id: 56067,
        name: "Glorification-15 'Devana'",
        direction: Direction::Outbound,
        ship_cap: 15,
        spool_up_s: 180,
        link_radius_m: 6000.0,
        max_light_years: None,
    },
    Filament {
        type_id: 56068,
        name: "Proximity-5 'Extraction'",
        direction: Direction::Outbound,
        ship_cap: 5,
        spool_up_s: 150,
        link_radius_m: 6000.0,
        max_light_years: Some(2.5),
    },
    Filament {
        type_id: 56069,
        name: "Proximity-15 'Extraction'",
        direction: Direction::Outbound,
        ship_cap: 15,
        spool_up_s: 210,
        link_radius_m: 6000.0,
        max_light_years: Some(2.5),
    },
    Filament {
        type_id: 56070,
        name: "Cladistic-5 'Krai Perun'",
        direction: Direction::Inbound,
        ship_cap: 5,
        spool_up_s: 30,
        link_radius_m: 6000.0,
        max_light_years: None,
    },
    Filament {
        type_id: 56071,
        name: "Cladistic-15 'Krai Perun'",
        direction: Direction::Inbound,
        ship_cap: 15,
        spool_up_s: 45,
        link_radius_m: 6000.0,
        max_light_years: None,
    },
    Filament {
        type_id: 56072,
        name: "Cladistic-5 'Krai Veles'",
        direction: Direction::Inbound,
        ship_cap: 5,
        spool_up_s: 30,
        link_radius_m: 6000.0,
        max_light_years: None,
    },
    Filament {
        type_id: 56073,
        name: "Cladistic-15 'Krai Veles'",
        direction: Direction::Inbound,
        ship_cap: 15,
        spool_up_s: 45,
        link_radius_m: 6000.0,
        max_light_years: None,
    },
    Filament {
        type_id: 56074,
        name: "Cladistic-5 'Krai Svarog'",
        direction: Direction::Inbound,
        ship_cap: 5,
        spool_up_s: 30,
        link_radius_m: 6000.0,
        max_light_years: None,
    },
    Filament {
        type_id: 56075,
        name: "Cladistic-15 'Krai Svarog'",
        direction: Direction::Inbound,
        ship_cap: 15,
        spool_up_s: 45,
        link_radius_m: 6000.0,
        max_light_years: None,
    },
    Filament {
        type_id: 56076,
        name: "Border-5 'Pochven'",
        direction: Direction::Inbound,
        ship_cap: 5,
        spool_up_s: 30,
        link_radius_m: 6000.0,
        max_light_years: None,
    },
    Filament {
        type_id: 56077,
        name: "Border-15 'Pochven'",
        direction: Direction::Inbound,
        ship_cap: 15,
        spool_up_s: 45,
        link_radius_m: 6000.0,
        max_light_years: None,
    },
    Filament {
        type_id: 56078,
        name: "Internal-5 'Pochven'",
        direction: Direction::Inbound,
        ship_cap: 5,
        spool_up_s: 30,
        link_radius_m: 6000.0,
        max_light_years: None,
    },
    Filament {
        type_id: 56079,
        name: "Internal-15 'Pochven'",
        direction: Direction::Inbound,
        ship_cap: 15,
        spool_up_s: 45,
        link_radius_m: 6000.0,
        max_light_years: None,
    },
    Filament {
        type_id: 56080,
        name: "Home-5 'Pochven'",
        direction: Direction::Inbound,
        ship_cap: 5,
        spool_up_s: 30,
        link_radius_m: 6000.0,
        max_light_years: None,
    },
    Filament {
        type_id: 56081,
        name: "Home-15 'Pochven'",
        direction: Direction::Inbound,
        ship_cap: 15,
        spool_up_s: 45,
        link_radius_m: 6000.0,
        max_light_years: None,
    },
];

/// A filament's trace cannot be jumped for this long after its first use (wiki, Pochven: "up to 3.5 minutes after initial use"), seconds.
pub const OUTBOUND_TRACE_COOLDOWN_S: u32 = 210;
/// A trace closes this long after it first appears (wiki, Filament), seconds.
pub const TRACE_LIFETIME_S: u32 = 900;
/// A linked character that goes beyond this distance from the trace is unlinked, and a broken link cannot be made again (wiki, Filament), metres.
pub const UNLINK_DISTANCE_M: f64 = 30_000.0;
/// One light-year, metres.
pub const LIGHT_YEAR_M: f64 = 9.460_730_472_580_8e15;

/// The filament of a type, or `None` for a type that is not a Pochven filament.
pub fn filament(type_id: u32) -> Option<&'static Filament> {
    FILAMENTS.iter().find(|filament| filament.type_id == type_id)
}

/// Whether a fleet of this many subcapital ships may use the filament: at most its cap, and at least one ship.
pub fn fleet_fits(filament: &Filament, subcapitals: u32) -> Result<(), &'static str> {
    match subcapitals {
        0 => Err("NO_SHIPS"),
        n if n > filament.ship_cap => Err("FLEET_TOO_LARGE"),
        _ => Ok(()),
    }
}

/// Whether a system at this distance from the filament's origin is within an Extraction filament's reach (2.5 light-years).
pub fn within_reach(filament: &Filament, distance_m: f64) -> bool {
    filament.max_light_years.is_some_and(|max| distance_m <= max * LIGHT_YEAR_M)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_table_has_the_17_sde_filaments_with_their_caps_and_spool_ups() {
        assert_eq!(FILAMENTS.len(), 17);
        let type_ids: Vec<u32> = FILAMENTS.iter().map(|f| f.type_id).collect();
        assert_eq!(type_ids, (56065..=56081).collect::<Vec<u32>>());
        assert_eq!(filament(56066).map(|f| (f.ship_cap, f.spool_up_s)), Some((5, 120)));
        assert_eq!(filament(56069).map(|f| (f.ship_cap, f.spool_up_s)), Some((15, 210)));
        assert_eq!(filament(56077).map(|f| (f.ship_cap, f.spool_up_s)), Some((15, 45)));
        assert!(filament(56064).is_none() && filament(56082).is_none());
    }

    #[test]
    fn the_inbound_and_outbound_families_are_the_wiki_ones() {
        let outbound: Vec<&str> =
            FILAMENTS.iter().filter(|f| f.direction == Direction::Outbound).map(|f| f.name).collect();
        assert_eq!(outbound.len(), 5, "three Devana and two Extraction");
        assert!(
            FILAMENTS
                .iter()
                .filter(|f| f.name.starts_with("Border") || f.name.starts_with("Home"))
                .all(|f| f.direction == Direction::Inbound)
        );
    }

    #[test]
    fn a_fleet_over_the_cap_or_empty_is_refused() {
        let devana_one = filament(56065).unwrap();
        assert_eq!(fleet_fits(devana_one, 1), Ok(()));
        assert_eq!(fleet_fits(devana_one, 2), Err("FLEET_TOO_LARGE"));
        assert_eq!(fleet_fits(devana_one, 0), Err("NO_SHIPS"));
        assert_eq!(fleet_fits(filament(56081).unwrap(), 15), Ok(()));
        assert_eq!(fleet_fits(filament(56081).unwrap(), 16), Err("FLEET_TOO_LARGE"));
    }

    #[test]
    fn an_extraction_reaches_2_5_light_years_and_no_other_filament_has_a_reach() {
        let extraction = filament(56068).unwrap();
        assert!(within_reach(extraction, 2.5 * LIGHT_YEAR_M));
        assert!(!within_reach(extraction, 2.5 * LIGHT_YEAR_M + 1_000.0));
        assert!(!within_reach(filament(56065).unwrap(), 1.0));
    }

    #[test]
    fn the_trace_rules_are_the_wiki_numbers() {
        assert_eq!(OUTBOUND_TRACE_COOLDOWN_S, 3 * 60 + 30);
        assert_eq!(TRACE_LIFETIME_S, 15 * 60);
        assert_eq!(UNLINK_DISTANCE_M, 30_000.0);
    }
}
