//! `behaviors/conditions/entityspawning.py`.

enter_task!(HasSpawnPointsGreaterThan, |t| {
    let Some(spawn_pool_id) = t.value_at("spawnPoolIdAddress") else { return };
    let spawn_points = t.world().current_spawn_points(spawn_pool_id.as_int());
    let Some(minimum) = t.required_float("minSpawnPointAmount") else { return };
    t.set_status_to_success_if_true_else_to_failed(spawn_points > minimum);
});

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};
    use crate::value::Value;

    #[test]
    fn a_pool_has_more_points_than_the_minimum() {
        let attrs = Attrs::new()
            .with("spawnPoolIdAddress", Address::new(ScopeType::Item, "P").to_value())
            .with("minSpawnPointAmount", 10i64);
        for (points, wanted) in [(11.0, Status::Success), (10.0, Status::Failure)] {
            let mut rig = Rig::new();
            rig.world.spawn_points.insert(3, points);
            rig.post(ScopeType::Item, "P", Value::Int(3));
            assert_eq!(rig.run(Box::new(HasSpawnPointsGreaterThan), attrs.clone()), wanted);
        }
    }
}
