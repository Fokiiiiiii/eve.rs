//! `behaviors/trees/incursions/const.py`.

use crate::consts::*;
use crate::value::Value;

use crate::trees::builder::int_set;

pub fn drifter_owners() -> Value {
    int_set(&[FACTION_DRIFTERS, OWNER_UNKNOWN])
}

pub fn amarr_navy_owners() -> Value {
    int_set(&[OWNER_AMARR_NAVY])
}

/// `VALID_HOSTILE_CATEGORIES`.
pub const VALID_HOSTILE_CATEGORIES: [i64; 4] =
    [CATEGORY_SHIP, CATEGORY_DRONE, CATEGORY_ENTITY, CATEGORY_FIGHTER];

/// `ROAMING_SITE_GROUP_IDS`.
pub const ROAMING_SITE_GROUP_IDS: [i64; 2] = [GROUP_STARGATE, GROUP_STATION];
