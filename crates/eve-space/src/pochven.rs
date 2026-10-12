//! The Pochven rules the simulation applies (`dotnet/src/Eve.Services.Space/Pochven/PochvenRules.cs` is the same rule for the host; keep both in step).
//! Pochven is SDE region 10000070: the Triglavian space of the Final Liminality systems. Its four NPC factions attack each other on sight (the wiki's
//! "Pochven" page). The factions are matched by the corporation the SDE names for each one (`factions.corporationID`), which is the owner id of an
//! NPC ship.

/// The Pochven region (SDE `mapRegions` 10000070).
pub const POCHVEN_REGION_ID: i32 = 10_000_070;

pub const DRIFTER_FACTION_ID: i64 = 500_024;
pub const ROGUE_DRONE_FACTION_ID: i64 = 500_025;
pub const TRIGLAVIAN_FACTION_ID: i64 = 500_026;
pub const EDENCOM_FACTION_ID: i64 = 500_027;

pub const DRIFTER_CORPORATION_ID: i64 = 1_000_274;
pub const ROGUE_DRONE_CORPORATION_ID: i64 = 1_000_287;
pub const TRIGLAVIAN_CORPORATION_ID: i64 = 1_000_298;
pub const EDENCOM_CORPORATION_ID: i64 = 1_000_297;

const _: () = assert!(HOSTILE_FACTION_STANDING < -10.0);

/// The standing an NPC of a Pochven faction has towards an NPC of another one. It is a faction rule, not a standing, so it is set far below every response
/// threshold (the standings scale runs from -10 to +10), which makes the other side hostile whatever the threshold of the tree.
pub const HOSTILE_FACTION_STANDING: f64 = -1_000.0;

/// The faction of an NPC corporation of Pochven, or `None` for any other corporation.
pub fn faction_of_corporation(corporation_id: i64) -> Option<i64> {
    match corporation_id {
        DRIFTER_CORPORATION_ID => Some(DRIFTER_FACTION_ID),
        ROGUE_DRONE_CORPORATION_ID => Some(ROGUE_DRONE_FACTION_ID),
        TRIGLAVIAN_CORPORATION_ID => Some(TRIGLAVIAN_FACTION_ID),
        EDENCOM_CORPORATION_ID => Some(EDENCOM_FACTION_ID),
        _ => None,
    }
}

/// An owner id no NPC has: the sensor accepts an owner only from its list, so a list of this id accepts nothing.
pub const NO_OWNER: i64 = 0;

/// The owners (corporations) a Pochven NPC of `corporation_id` attacks: the corporations of the other Pochven factions while the system is in Pochven,
/// and `[NO_OWNER]` (nothing) anywhere else, or for a corporation that is not one of the four.
pub fn hostile_corporations(corporation_id: i64, in_pochven: bool) -> Vec<i64> {
    let Some(mine) = faction_of_corporation(corporation_id).filter(|_| in_pochven) else {
        return vec![NO_OWNER];
    };
    [
        (DRIFTER_FACTION_ID, DRIFTER_CORPORATION_ID),
        (ROGUE_DRONE_FACTION_ID, ROGUE_DRONE_CORPORATION_ID),
        (TRIGLAVIAN_FACTION_ID, TRIGLAVIAN_CORPORATION_ID),
        (EDENCOM_FACTION_ID, EDENCOM_CORPORATION_ID),
    ]
    .into_iter()
    .filter(|(faction, _)| factions_are_hostile(mine, *faction))
    .map(|(_, corporation)| corporation)
    .collect()
}

/// Whether two Pochven NPC factions attack each other: distinct, and both among the four.
pub fn factions_are_hostile(a: i64, b: i64) -> bool {
    let four = |f: i64| {
        matches!(f, DRIFTER_FACTION_ID | ROGUE_DRONE_FACTION_ID | TRIGLAVIAN_FACTION_ID | EDENCOM_FACTION_ID)
    };
    a != b && four(a) && four(b)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn each_pochven_corporation_maps_to_its_faction_and_no_other_corporation_does() {
        assert_eq!(faction_of_corporation(1_000_298), Some(500_026));
        assert_eq!(faction_of_corporation(1_000_297), Some(500_027));
        assert_eq!(faction_of_corporation(1_000_287), Some(500_025));
        assert_eq!(faction_of_corporation(1_000_274), Some(500_024));
        assert_eq!(faction_of_corporation(1_000_125), None);
        assert_eq!(faction_of_corporation(0), None);
    }

    #[test]
    fn the_four_factions_are_hostile_to_each_other_and_to_no_other_faction() {
        let four = [TRIGLAVIAN_FACTION_ID, EDENCOM_FACTION_ID, ROGUE_DRONE_FACTION_ID, DRIFTER_FACTION_ID];
        for a in four {
            for b in four {
                assert_eq!(factions_are_hostile(a, b), a != b);
            }
        }
        assert!(!factions_are_hostile(TRIGLAVIAN_FACTION_ID, 500_006));
        assert!(!factions_are_hostile(EDENCOM_FACTION_ID, 500_020));
    }

    #[test]
    fn the_rule_constants_match_the_csharp_rules() {
        assert_eq!(POCHVEN_REGION_ID, 10_000_070);
    }
}
