//! `behaviors/actions/map.py`.

enter_task!(
    /// The faction that holds the solar system, put on a channel; fails for a system with none.
    GetSolarSystemFactionID,
    |t| {
        let faction = t.ctx().solar_system_id.and_then(|system| t.world().solar_system_faction_id(system)).filter(|f| *f != 0);
        match faction {
            Some(faction_id) => {
                let Some(address) = t.required_address("messageAddress") else { return };
                t.send_blackboard_value(&address, crate::value::Value::Int(faction_id));
                t.set_status_to_success();
            }
            None => t.set_status_to_failed(),
        }
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};
    use crate::value::Value;

    #[test]
    fn the_systems_faction_is_put_on_the_channel_and_a_system_with_none_fails() {
        let attrs = Attrs::new().with("messageAddress", Address::new(ScopeType::Item, "F").to_value());
        let mut rig = Rig::new();
        rig.ctx.solar_system_id = Some(30000142);
        rig.world.system_factions.insert(30000142, 500001);
        assert_eq!(rig.run(Box::new(GetSolarSystemFactionID), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "F"), Value::Int(500001));
        let mut rig = Rig::new();
        rig.ctx.solar_system_id = Some(30000143);
        assert_eq!(rig.run(Box::new(GetSolarSystemFactionID), attrs), Status::Failure);
    }
}
