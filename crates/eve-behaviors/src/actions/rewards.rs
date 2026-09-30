//! `behaviors/actions/rewards.py`.

enter_task!(
    /// Has the reward paid, and the loot rights of the wreck given, when I explode.
    RegisterPaymentOnExplosion,
    |t| {
        t.set_status_to_success();
        let Some(me) = t.my_item_id() else { return };
        let Some(mine) = crate::utility::ballparks::get_my_ball(t) else { return };
        let Some(reward_id) = t.required("rewardId") else { return };
        let tale_id = t.ctx().my_tale_id.unwrap_or(0);
        let dungeon_id = t.ctx().my_dungeon_id.unwrap_or(0);
        t.world().register_payment_on_explosion(me, mine.new_bubble_id, reward_id.as_int(), tale_id, dungeon_id);
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tree::Attrs;
    use crate::tree::tests::Rig;

    #[test]
    fn the_payment_and_the_loot_rights_are_registered_for_my_bubble_and_the_reward() {
        let mut rig = Rig::new();
        rig.world.add_ball(1, [0.0; 3], 2).new_bubble_id = 3;
        rig.ctx.my_tale_id = Some(9);
        rig.run(Box::new(RegisterPaymentOnExplosion), Attrs::new().with("rewardId", 44i64));
        assert_eq!(rig.world.take_calls(), ["register_payment_on_explosion(1, 3, Some(44), 9, 0)"]);
    }
}
