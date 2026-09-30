//! `behaviors/conditions/dungeons.py`.

use crate::utility::dungeons::does_spawn_exist;

enter_task!(DungeonIsMyDungeonSpawnAlive, |t| {
    t.set_status_to_failed();
    if t.ctx().my_dungeon_spawn_id.is_some() && does_spawn_exist(t) {
        t.set_status_to_success();
    }
});

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    #[test]
    fn my_dungeon_spawn_is_alive_when_i_have_one_and_the_keeper_knows_it() {
        let mut rig = Rig::new();
        rig.ctx.my_dungeon_spawn_id = Some(5);
        rig.world.dungeon_spawns.insert(5);
        assert_eq!(rig.run(Box::new(DungeonIsMyDungeonSpawnAlive), Attrs::new()), Status::Success);
        let mut rig = Rig::new();
        rig.ctx.my_dungeon_spawn_id = Some(5);
        assert_eq!(rig.run(Box::new(DungeonIsMyDungeonSpawnAlive), Attrs::new()), Status::Failure);
        assert_eq!(Rig::new().run(Box::new(DungeonIsMyDungeonSpawnAlive), Attrs::new()), Status::Failure);
    }
}
