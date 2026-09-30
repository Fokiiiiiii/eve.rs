//! `behaviors/utility/dungeons.py`.

use crate::tree::TaskCtx;

/// `does_spawn_exist`: the keeper knows my dungeon's spawn.
pub fn does_spawn_exist(t: &mut TaskCtx<'_, '_>) -> bool {
    match t.ctx().my_dungeon_spawn_id {
        Some(id) => t.world().does_spawn_exist(id),
        None => false,
    }
}
