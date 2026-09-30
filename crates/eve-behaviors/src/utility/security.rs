//! `behaviors/utility/security.py`, and the security classes of `eveuniverse.security` it reads.

use crate::tree::TaskCtx;
use crate::value::Value;

pub const SECURITY_CLASS_ZERO_SEC: i64 = 0;
pub const SECURITY_CLASS_LOW_SEC: i64 = 1;
pub const SECURITY_CLASS_HIGH_SEC: i64 = 2;
pub const SECURITY_CLASS_SAFE_SEC: i64 = 3;

/// `SecurityClassFromLevel`.
pub fn security_class_from_level(level: f64) -> i64 {
    if level <= 0.0 {
        SECURITY_CLASS_ZERO_SEC
    } else if level < 0.45 {
        SECURITY_CLASS_LOW_SEC
    } else if level < 0.95 {
        SECURITY_CLASS_HIGH_SEC
    } else {
        SECURITY_CLASS_SAFE_SEC
    }
}

/// `get_security_level_for_system`: the security class of a solar system.
pub fn get_security_level_for_system(t: &mut TaskCtx<'_, '_>, solar_system_id: i64) -> i64 {
    security_class_from_level(t.world().security_status(solar_system_id))
}

/// `get_local_security_level`.
pub fn get_local_security_level(t: &mut TaskCtx<'_, '_>) -> Option<i64> {
    let system = t.ctx().solar_system_id;
    match system {
        Some(id) => Some(get_security_level_for_system(t, id)),
        None => {
            t.raise(crate::env::WorldError::Key("solarSystemId".to_string()).into());
            None
        }
    }
}

pub fn is_in_safe_sec(t: &mut TaskCtx<'_, '_>) -> bool {
    get_local_security_level(t) == Some(SECURITY_CLASS_SAFE_SEC)
}

pub fn is_in_high_sec(t: &mut TaskCtx<'_, '_>) -> bool {
    get_local_security_level(t) == Some(SECURITY_CLASS_HIGH_SEC)
}

pub fn is_in_low_sec(t: &mut TaskCtx<'_, '_>) -> bool {
    get_local_security_level(t) == Some(SECURITY_CLASS_LOW_SEC)
}

pub fn is_in_zero_sec(t: &mut TaskCtx<'_, '_>) -> bool {
    get_local_security_level(t) == Some(SECURITY_CLASS_ZERO_SEC)
}

/// `get_spawnlist_by_owner_map_by_security`: the task's map of owner to spawn list for the local security (high and safe share one).
pub fn get_spawnlist_by_owner_map_by_security(t: &mut TaskCtx<'_, '_>) -> Value {
    if is_in_high_sec(t) || is_in_safe_sec(t) {
        t.attribute("highSecOwnerToSpawnlistMapping")
    } else if is_in_low_sec(t) {
        t.attribute("lowSecOwnerToSpawnlistMapping")
    } else {
        t.attribute("zeroSecOwnerToSpawnlistMapping")
    }
}

/// `get_spawnlist_by_group_owner_and_security_level`: the spawn list of my group's owner in the local security (nothing for an owner the map lacks).
pub fn get_spawnlist_by_group_owner_and_security_level(t: &mut TaskCtx<'_, '_>) -> Value {
    let mapping = get_spawnlist_by_owner_map_by_security(t);
    let Some(owner) = crate::groups::mixin::get_group_owner_id(t) else { return Value::None };
    mapping.dict_get(&Value::Int(owner)).cloned().unwrap_or(Value::None)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tree::tests::with_task;

    #[test]
    fn the_security_class_goes_by_the_level() {
        let classes: Vec<i64> = [-0.5, 0.0, 0.1, 0.44, 0.45, 0.94, 0.95, 1.0]
            .into_iter()
            .map(security_class_from_level)
            .collect();
        assert_eq!(classes, [0, 0, 1, 1, 2, 2, 3, 3]);
    }

    #[test]
    fn the_local_security_is_the_systems_and_the_spawn_list_map_is_the_tasks_for_it() {
        let (_, answer) = with_task(
            |rig| {
                rig.ctx.solar_system_id = Some(30000142);
                rig.world.security.insert(30000142, 0.3);
            },
            |t| (is_in_low_sec(t), is_in_high_sec(t), get_local_security_level(t)),
        );
        assert_eq!(answer, Some((true, false, Some(SECURITY_CLASS_LOW_SEC))));
    }
}
