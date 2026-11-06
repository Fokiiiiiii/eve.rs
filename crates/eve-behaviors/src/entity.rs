//! `behaviors/entity.py`: `BehaviorEntity`, the ball class (58) of an NPC that runs a behavior. It keeps what the NPC carries in addition to what
//! its type drops (`additionalLoot`, `additionalLootTables`), starts and ends the NPC's warps (`NPCWarpTo`, `NPCEndWarp`), and drops the wreck when
//! the ball explodes.
//!
//! The client's class is a subclass of the server's `EntityWrapper` and reaches the server's `ballpark`, `broker`, `inventory2`, `lootSvc` and
//! `evetypes` through it; none of those are part of the client, so what the entity asks of them is the `EntityHost` trait, in the order the client
//! asks it.

use std::collections::BTreeMap;

use crate::consts::DESTINY_MODE_WARP;
use crate::env::{InventoryItem, NpcWarpKind, WorldError, WorldResult};
use crate::events::message;
use crate::value::Value;

/// `__destiny_ball_class_id__`.
pub const DESTINY_BALL_CLASS_ID: i64 = 58;

/// `INVULNERABLE_LABEL`: the client's module declares it and nothing in it uses it.
pub const INVULNERABLE_LABEL: &str = "BehaviorWarp";

/// A thing that drops: `(typeId, quantity, customInfo)`.
pub type LootEntry = (i64, Value, Option<Value>);

/// What `lootSvc.GetLootForEntity` answers: for each type, the picks `(quantity, customInfo)`, in the order the server's dict has them.
pub type LootPicks = Vec<(i64, Vec<(Value, Option<Value>)>)>;

/// What a `BehaviorEntity` asks of the server: the ballpark the ball is in, the inventory, the loot service, the type data, and the base class
/// `EntityWrapper`. A method that can raise answers a `WorldResult`.
pub trait EntityHost {
    /// `hasattr(self, 'ballpark') and self.ballpark is not None`.
    fn has_ballpark(&self) -> bool {
        true
    }

    /// `self.mode`.
    fn mode(&self) -> i64 {
        0
    }

    /// `self.GetPosition()`.
    fn position(&self) -> [f64; 3] {
        [0.0; 3]
    }

    /// `ballpark.IsWarpScrambled(id)`.
    fn is_warp_scrambled(&self) -> bool {
        false
    }

    /// `ballpark.AdjustDstByJitter(destination, jitterRadius)`.
    fn adjust_dst_by_jitter(&mut self, destination: [f64; 3], _jitter_radius: f64) -> [f64; 3] {
        destination
    }

    /// `IsBallIntersectingAnyWarpDisruptionField(ballpark, id)`.
    fn is_ball_intersecting_any_warp_disruption_field(&self) -> bool {
        false
    }

    /// `ballpark.ApplyWarpDisruptors(id, position, destination, warpAtRange)`: a `UserError` where the path is too short.
    fn apply_warp_disruptors(
        &mut self,
        _position: [f64; 3],
        destination: [f64; 3],
        _warp_at_range: f64,
    ) -> WorldResult<[f64; 3]> {
        Ok(destination)
    }

    /// `ballpark.bubbleKeepAliveController.add_bubble_keep_alive_ball_at_coordinates(id, destination)`.
    fn add_bubble_keep_alive_ball_at_coordinates(&mut self, _destination: [f64; 3]) {}

    /// `ballpark.bubbleKeepAliveController.remove_bubble_keep_alive_ball(id)`.
    fn remove_bubble_keep_alive_ball(&mut self) {}

    /// `ballpark.EntityWarpStart(id, pos=, makeItReal=True, notWarpingException=NPCWarpError, minRange=, warpSpeed=)`.
    fn entity_warp_start(
        &mut self,
        _position: [f64; 3],
        _min_range: f64,
        _warp_speed: f64,
    ) -> WorldResult<()> {
        Ok(())
    }

    /// `self.SendMessage(self.id, message, *args)`.
    fn send_message(&mut self, _message: crate::events::MessageId, _args: Vec<Value>) {}

    /// `ballpark.inventory2.GetItem(id)`.
    fn item(&self) -> WorldResult<InventoryItem>;

    /// `HasCargoBayComponent(typeId)`.
    fn has_cargo_bay_component(&self, _type_id: i64) -> bool {
        false
    }

    fn kill_owner_id(&self) -> Option<i64> {
        None
    }

    /// `evetypes.GetWreckTypeIDOrNone(typeId)`.
    fn wreck_type_id(&self, _type_id: i64) -> Option<i64> {
        None
    }

    /// `ballpark.broker.ship.GetShipAccessEx(locationID, groupSolarSystem)` and `.Bind()`.
    fn bind_ship_access(&mut self, _location_id: i64) -> WorldResult<()> {
        Ok(())
    }

    fn drop_wreck(
        &mut self,
        _offender_owner_id: Option<i64>,
        _type_id: i64,
        _position: [f64; 3],
        _wreck_type_id: i64,
        _items: &[LootEntry],
    ) {
    }

    /// `EntityWrapper.OnExplode`.
    fn on_explode_base(&mut self) {}

    /// `lootSvc.GetLootFromTable(lootTableId)`.
    fn loot_from_table(&self, _loot_table_id: i64) -> Vec<LootEntry> {
        Vec::new()
    }

    fn loot_for_entity(&self, _type_id: i64, _owner_id: i64) -> LootPicks {
        Vec::new()
    }

    /// `evetypes.GetAttributeForType(typeId, 'volume')`.
    fn type_volume(&self, _type_id: i64) -> f64 {
        0.0
    }

    /// `evetypes.GetAttributeForType(typeId, 'capacity')`.
    fn type_capacity(&self, _type_id: i64) -> f64 {
        0.0
    }
}

/// The NPC ball with a behavior. `id` is the item id; the rest is what the class keeps (`DoSetup`).
#[derive(Debug, Clone, Default, PartialEq)]
pub struct BehaviorEntity {
    pub id: i64,
    /// `additionalLoot`: a `defaultdict(int)` from type to amount. Python 2 lists a dict in the order of its hashes, which for the small numbers a type
    /// id is comes out ascending in the ordinary case; an ordered map is that. An amount is a number, an integer until `math.floor` (which answers
    /// a float in Python 2) gave it.
    additional_loot: BTreeMap<i64, Value>,
    /// `additionalLootTables`.
    additional_loot_tables: Vec<i64>,
}

fn type_error(what: &str) -> WorldError {
    WorldError::Runtime(format!("TypeError: {what}"))
}

/// A number as a float (`Value` numbers are what Python's arithmetic takes).
fn number(value: &Value) -> WorldResult<f64> {
    match value {
        Value::Bool(b) => Ok(f64::from(u8::from(*b))),
        Value::Int(i) => Ok(*i as f64),
        Value::Float(f) => Ok(*f),
        _ => Err(type_error("unsupported operand type(s) for *: amount")),
    }
}

impl BehaviorEntity {
    /// `DoSetup(entitySettings)`.
    pub fn new(id: i64) -> Self {
        Self { id, additional_loot: BTreeMap::new(), additional_loot_tables: Vec::new() }
    }

    /// `NPCWarpTo(destination, jitterRadius=20000, warpAtRange=0.0, warpSpeed=6)`.
    pub fn npc_warp_to(
        &self,
        host: &mut dyn EntityHost,
        destination: [f64; 3],
        jitter_radius: f64,
        warp_at_range: f64,
        warp_speed: f64,
    ) -> WorldResult<()> {
        if !host.has_ballpark() {
            return Err(WorldError::NpcWarp(NpcWarpKind::Other("No ballpark".to_string())));
        }
        if host.is_warp_scrambled() {
            return Err(WorldError::NpcWarp(NpcWarpKind::Scrambled));
        }
        if host.mode() == DESTINY_MODE_WARP {
            return Err(WorldError::NpcWarp(NpcWarpKind::Other("Already Warping".to_string())));
        }
        let destination = host.adjust_dst_by_jitter(destination, jitter_radius);
        if host.is_ball_intersecting_any_warp_disruption_field() {
            return Err(WorldError::NpcWarp(NpcWarpKind::Bubbled));
        }
        let position = host.position();
        let destination = match host.apply_warp_disruptors(position, destination, warp_at_range) {
            Ok(destination) => destination,
            Err(WorldError::User(_)) => return Err(WorldError::NpcWarp(NpcWarpKind::PathBubbled)),
            Err(other) => return Err(other),
        };
        host.add_bubble_keep_alive_ball_at_coordinates(destination);
        host.entity_warp_start(destination, warp_at_range, warp_speed)
    }

    /// `NPCEndWarp()`.
    pub fn npc_end_warp(&self, host: &mut dyn EntityHost) -> WorldResult<()> {
        if host.has_ballpark() {
            host.remove_bubble_keep_alive_ball();
            Ok(())
        } else {
            Err(WorldError::NpcWarp(NpcWarpKind::Other(String::new())))
        }
    }

    /// `OnExplode()`: the wreck, with what the type and the additional loot drop in it.
    pub fn on_explode(&self, host: &mut dyn EntityHost) -> WorldResult<()> {
        let item = host.item()?;
        if host.has_cargo_bay_component(item.type_id) {
            // The cargo bay component manages the dropped loot.
            return Ok(());
        }
        let offender_owner_id = host.kill_owner_id();
        let wreck_type_id = host.wreck_type_id(item.type_id);
        host.bind_ship_access(item.location_id)?;
        let items = self.loot_for_entity(host, item.type_id, item.owner_id);
        if let Some(wreck_type_id) = wreck_type_id {
            let position = host.position();
            host.drop_wreck(offender_owner_id, item.type_id, position, wreck_type_id, &items);
        }
        host.on_explode_base();
        Ok(())
    }

    /// `OnUnspawn()`: whoever cares is told what the NPC carried off with it.
    pub fn on_unspawn(&self, host: &mut dyn EntityHost) {
        let additional_loot = self.additional_loot();
        if additional_loot.truthy() {
            host.send_message(message::ON_UNSPAWNED_WITH_ADDITIONAL_LOOT, vec![additional_loot]);
        }
    }

    /// `DoModeChange(oldMode, newMode)`.
    pub fn do_mode_change(&self, host: &mut dyn EntityHost, old_mode: i64, new_mode: i64) -> WorldResult<()> {
        host.send_message(message::ON_BALL_MODE_CHANGED, vec![Value::Int(old_mode), Value::Int(new_mode)]);
        if old_mode == DESTINY_MODE_WARP {
            self.npc_end_warp(host)?;
        }
        Ok(())
    }

    /// `_GetAdditionalLootForWreck()`: the loot tables' entries, then what was added by type.
    fn additional_loot_for_wreck(&self, host: &dyn EntityHost) -> Vec<LootEntry> {
        let mut additional_loot = Vec::new();
        for loot_table_id in &self.additional_loot_tables {
            additional_loot.extend(host.loot_from_table(*loot_table_id));
        }
        for (type_id, amount) in &self.additional_loot {
            additional_loot.push((*type_id, amount.clone(), None));
        }
        additional_loot
    }

    /// `AddTypeAndAmountToAdditionalLoot(typeId, amount=1, overload=True)`.
    pub fn add_type_and_amount_to_additional_loot(
        &mut self,
        host: &dyn EntityHost,
        type_id: i64,
        amount: Value,
        overload: bool,
    ) -> WorldResult<()> {
        let amount = if overload {
            amount
        } else {
            self.available_amount_for_type_in_additional_loot(host, type_id, &amount)?
        };
        let held = self.additional_loot.get(&type_id).cloned().unwrap_or(Value::Int(0));
        let total =
            held.py_add(&amount).ok_or_else(|| type_error("unsupported operand type(s) for +=: amount"))?;
        self.additional_loot.insert(type_id, total);
        Ok(())
    }

    /// `RemoveTypeFromAdditionalLoot(typeId)`.
    pub fn remove_type_from_additional_loot(&mut self, type_id: i64) {
        self.additional_loot.remove(&type_id);
    }

    /// `HasTypeInAdditionalLoot(typeId)`.
    pub fn has_type_in_additional_loot(&self, type_id: i64) -> bool {
        self.additional_loot.contains_key(&type_id)
    }

    /// and amounts the additional loot is.
    pub fn loot_for_entity(&self, host: &mut dyn EntityHost, type_id: i64, owner_id: i64) -> Vec<LootEntry> {
        let mut item_list = Vec::new();
        for (dropped_type_id, pick_list) in host.loot_for_entity(type_id, owner_id) {
            for (quantity, custom_info) in pick_list {
                item_list.push((dropped_type_id, quantity, custom_info));
            }
        }
        let additional_loot = self.additional_loot_for_wreck(host);
        if !additional_loot.is_empty() {
            let mut dropped = Value::set([]);
            for (dropped_type_id, amount, _) in &additional_loot {
                dropped.set_add(Value::tuple([Value::Int(*dropped_type_id), amount.clone()]));
            }
            host.send_message(message::ON_DROPPED_ADDITIONAL_LOOT, vec![dropped]);
        }
        item_list.extend(additional_loot);
        item_list
    }

    /// `AddLootTableToAdditionalLoot(lootTableId)`.
    pub fn add_loot_table_to_additional_loot(&mut self, loot_table_id: i64) {
        self.additional_loot_tables.push(loot_table_id);
    }

    /// `HasLootTableInAdditionalLoot(lootTableId)`.
    pub fn has_loot_table_in_additional_loot(&self, loot_table_id: i64) -> bool {
        self.additional_loot_tables.contains(&loot_table_id)
    }

    /// `HasEnoughAdditionalCargoCapacityForType(typeId, quantity=1)`.
    pub fn has_enough_additional_cargo_capacity_for_type(
        &self,
        host: &dyn EntityHost,
        type_id: i64,
        quantity: &Value,
    ) -> WorldResult<bool> {
        let required_cargo_capacity = host.type_volume(type_id) * number(quantity)?;
        Ok(self.available_additional_cargo_capacity(host)? - required_cargo_capacity >= 0.0)
    }

    /// `GetAvailableAdditionalCargoCapacity()`.
    pub fn available_additional_cargo_capacity(&self, host: &dyn EntityHost) -> WorldResult<f64> {
        let self_type_id = host.item()?.type_id;
        let cargo_capacity = host.type_capacity(self_type_id);
        let used_cargo_capacity = self.used_additional_cargo_capacity(host)?;
        Ok(cargo_capacity - used_cargo_capacity)
    }

    /// `_GetUsedAdditionalCargoCapacity()`.
    fn used_additional_cargo_capacity(&self, host: &dyn EntityHost) -> WorldResult<f64> {
        let mut used_capacity = 0.0;
        for (type_id, amount) in &self.additional_loot {
            used_capacity += host.type_volume(*type_id) * number(amount)?;
        }
        Ok(used_capacity)
    }

    /// `_GetAvailableAmountForTypeInAdditionalLoot(typeId, amount)`: all of it if it fits, else what fits, rounded down (a float, as
    /// Python 2's `math.floor` answers).
    fn available_amount_for_type_in_additional_loot(
        &self,
        host: &dyn EntityHost,
        type_id: i64,
        amount: &Value,
    ) -> WorldResult<Value> {
        let available_cargo_capacity = self.available_additional_cargo_capacity(host)?;
        let total_volume_needed = host.type_volume(type_id) * number(amount)?;
        if available_cargo_capacity < total_volume_needed {
            if total_volume_needed == 0.0 {
                return Err(WorldError::ZeroDivision);
            }
            return Ok(Value::Float(
                (number(amount)? * (available_cargo_capacity / total_volume_needed)).floor(),
            ));
        }
        Ok(amount.clone())
    }

    /// `RemoveAdditionalLoot()`: takes what was added off the ball, tells whoever cares, and hands it back.
    pub fn remove_additional_loot(&mut self, host: &mut dyn EntityHost) -> BTreeMap<i64, Value> {
        let additional_loot = std::mem::take(&mut self.additional_loot);
        host.send_message(message::ON_ADDITIONAL_LOOT_REMOVED, Vec::new());
        additional_loot
    }

    /// `GetAdditionalLoot()`: a copy, as the dict of type to amount a message carries.
    pub fn additional_loot(&self) -> Value {
        Value::Dict(
            self.additional_loot
                .iter()
                .map(|(type_id, amount)| (Value::Int(*type_id), amount.clone()))
                .collect(),
        )
    }
}

#[cfg(test)]
mod tests {
    use std::collections::HashMap;

    use super::*;
    use crate::events::MessageId;

    /// A wreck that was dropped: the offender, the type, where, the wreck's type and what is in it.
    type Dropped = (Option<i64>, i64, [f64; 3], i64, Vec<LootEntry>);

    /// A server that logs what the entity asks of it, in order.
    struct Host {
        log: Vec<String>,
        messages: Vec<(MessageId, Vec<Value>)>,
        ballpark: bool,
        mode: i64,
        scrambled: bool,
        bubbled: bool,
        jitter: [f64; 3],
        disruptors: WorldResult<[f64; 3]>,
        warp_start: WorldResult<()>,
        item: WorldResult<InventoryItem>,
        cargo_bay_types: Vec<i64>,
        kill_owner: Option<i64>,
        wreck_type: Option<i64>,
        tables: HashMap<i64, Vec<LootEntry>>,
        picks: LootPicks,
        volumes: HashMap<i64, f64>,
        capacity: f64,
        dropped: Vec<Dropped>,
    }

    impl Host {
        fn new() -> Self {
            Self {
                log: Vec::new(),
                messages: Vec::new(),
                ballpark: true,
                mode: 0,
                scrambled: false,
                bubbled: false,
                jitter: [0.0; 3],
                disruptors: Ok([9.0, 9.0, 9.0]),
                warp_start: Ok(()),
                item: Ok(InventoryItem { item_id: 7, type_id: 100, owner_id: 500, location_id: 30_000_001 }),
                cargo_bay_types: Vec::new(),
                kill_owner: Some(90_000_001),
                wreck_type: Some(26_557),
                tables: HashMap::new(),
                picks: Vec::new(),
                volumes: HashMap::new(),
                capacity: 100.0,
                dropped: Vec::new(),
            }
        }
    }

    impl EntityHost for Host {
        fn has_ballpark(&self) -> bool {
            self.ballpark
        }
        fn mode(&self) -> i64 {
            self.mode
        }
        fn position(&self) -> [f64; 3] {
            [1.0, 2.0, 3.0]
        }
        fn is_warp_scrambled(&self) -> bool {
            self.scrambled
        }
        fn adjust_dst_by_jitter(&mut self, destination: [f64; 3], jitter_radius: f64) -> [f64; 3] {
            self.log.push(format!("jitter({destination:?}, {jitter_radius})"));
            [
                destination[0] + self.jitter[0],
                destination[1] + self.jitter[1],
                destination[2] + self.jitter[2],
            ]
        }
        fn is_ball_intersecting_any_warp_disruption_field(&self) -> bool {
            self.bubbled
        }
        fn apply_warp_disruptors(
            &mut self,
            position: [f64; 3],
            destination: [f64; 3],
            warp_at_range: f64,
        ) -> WorldResult<[f64; 3]> {
            self.log.push(format!("disruptors({position:?}, {destination:?}, {warp_at_range})"));
            self.disruptors.clone()
        }
        fn add_bubble_keep_alive_ball_at_coordinates(&mut self, destination: [f64; 3]) {
            self.log.push(format!("keep_alive_add({destination:?})"));
        }
        fn remove_bubble_keep_alive_ball(&mut self) {
            self.log.push("keep_alive_remove".to_string());
        }
        fn entity_warp_start(
            &mut self,
            position: [f64; 3],
            min_range: f64,
            warp_speed: f64,
        ) -> WorldResult<()> {
            self.log.push(format!("warp_start({position:?}, {min_range}, {warp_speed})"));
            self.warp_start.clone()
        }
        fn send_message(&mut self, message: MessageId, args: Vec<Value>) {
            self.log.push(format!("message({message:?})"));
            self.messages.push((message, args));
        }
        fn item(&self) -> WorldResult<InventoryItem> {
            self.item.clone()
        }
        fn has_cargo_bay_component(&self, type_id: i64) -> bool {
            self.cargo_bay_types.contains(&type_id)
        }
        fn kill_owner_id(&self) -> Option<i64> {
            self.kill_owner
        }
        fn wreck_type_id(&self, _type_id: i64) -> Option<i64> {
            self.wreck_type
        }
        fn bind_ship_access(&mut self, location_id: i64) -> WorldResult<()> {
            self.log.push(format!("ship_access({location_id})"));
            Ok(())
        }
        fn drop_wreck(
            &mut self,
            offender: Option<i64>,
            type_id: i64,
            position: [f64; 3],
            wreck: i64,
            items: &[LootEntry],
        ) {
            self.log.push("drop_wreck".to_string());
            self.dropped.push((offender, type_id, position, wreck, items.to_vec()));
        }
        fn on_explode_base(&mut self) {
            self.log.push("explode_base".to_string());
        }
        fn loot_from_table(&self, loot_table_id: i64) -> Vec<LootEntry> {
            self.tables.get(&loot_table_id).cloned().unwrap_or_default()
        }
        fn loot_for_entity(&self, _type_id: i64, _owner_id: i64) -> LootPicks {
            self.picks.clone()
        }
        fn type_volume(&self, type_id: i64) -> f64 {
            self.volumes.get(&type_id).copied().unwrap_or(1.0)
        }
        fn type_capacity(&self, _type_id: i64) -> f64 {
            self.capacity
        }
    }

    fn int(value: i64) -> Value {
        Value::Int(value)
    }

    #[test]
    fn the_class_is_the_clients() {
        assert_eq!(DESTINY_BALL_CLASS_ID, 58);
        assert_eq!(INVULNERABLE_LABEL, "BehaviorWarp");
    }

    #[test]
    fn a_warp_starts_at_the_place_the_disruptors_left() {
        let mut host = Host::new();
        host.jitter = [10.0, 0.0, 0.0];
        let entity = BehaviorEntity::new(7);
        assert_eq!(entity.npc_warp_to(&mut host, [100.0, 0.0, 0.0], 20_000.0, 50.0, 6.0), Ok(()));
        assert_eq!(
            host.log,
            [
                "jitter([100.0, 0.0, 0.0], 20000)",
                "disruptors([1.0, 2.0, 3.0], [110.0, 0.0, 0.0], 50)",
                "keep_alive_add([9.0, 9.0, 9.0])",
                "warp_start([9.0, 9.0, 9.0], 50, 6)",
            ]
        );
    }

    #[test]
    fn a_warp_that_cannot_start_says_why_and_stops_where_the_client_stops() {
        let entity = BehaviorEntity::new(7);
        let warp = |host: &mut Host| entity.npc_warp_to(host, [0.0; 3], 0.0, 0.0, 6.0);

        let mut host = Host::new();
        host.ballpark = false;
        assert_eq!(warp(&mut host), Err(WorldError::NpcWarp(NpcWarpKind::Other("No ballpark".into()))));
        assert!(host.log.is_empty());

        let mut host = Host::new();
        host.scrambled = true;
        host.mode = DESTINY_MODE_WARP;
        host.bubbled = true;
        assert_eq!(warp(&mut host), Err(WorldError::NpcWarp(NpcWarpKind::Scrambled)));
        assert!(host.log.is_empty());

        let mut host = Host::new();
        host.mode = DESTINY_MODE_WARP;
        host.bubbled = true;
        assert_eq!(warp(&mut host), Err(WorldError::NpcWarp(NpcWarpKind::Other("Already Warping".into()))));
        assert!(host.log.is_empty());

        let mut host = Host::new();
        host.mode = DESTINY_MODE_WARP - 1;
        host.bubbled = true;
        assert_eq!(warp(&mut host), Err(WorldError::NpcWarp(NpcWarpKind::Bubbled)));
        assert_eq!(host.log, ["jitter([0.0, 0.0, 0.0], 0)"]);

        let mut host = Host::new();
        host.disruptors = Err(WorldError::User("too short".into()));
        assert_eq!(warp(&mut host), Err(WorldError::NpcWarp(NpcWarpKind::PathBubbled)));
        assert_eq!(host.log.len(), 2);

        let mut host = Host::new();
        host.disruptors = Err(WorldError::Runtime("boom".into()));
        assert_eq!(warp(&mut host), Err(WorldError::Runtime("boom".into())));

        let mut host = Host::new();
        host.warp_start = Err(WorldError::NpcWarp(NpcWarpKind::Other("Not warping".into())));
        assert_eq!(warp(&mut host), Err(WorldError::NpcWarp(NpcWarpKind::Other("Not warping".into()))));
        assert_eq!(host.log.len(), 4);
    }

    #[test]
    fn ending_a_warp_lets_the_keep_alive_ball_go() {
        let entity = BehaviorEntity::new(7);
        let mut host = Host::new();
        assert_eq!(entity.npc_end_warp(&mut host), Ok(()));
        assert_eq!(host.log, ["keep_alive_remove"]);
        host.ballpark = false;
        assert_eq!(
            entity.npc_end_warp(&mut host),
            Err(WorldError::NpcWarp(NpcWarpKind::Other(String::new())))
        );
        assert_eq!(host.log.len(), 1);
    }

    #[test]
    fn a_mode_change_is_told_and_leaving_warp_ends_the_warp() {
        let entity = BehaviorEntity::new(7);
        let mut host = Host::new();
        assert_eq!(entity.do_mode_change(&mut host, 0, DESTINY_MODE_WARP), Ok(()));
        assert_eq!(host.log, ["message(Named(\"MESSAGE_ON_BALL_MODE_CHANGED\"))"]);
        assert_eq!(host.messages[0].1, [int(0), int(DESTINY_MODE_WARP)]);

        let mut host = Host::new();
        assert_eq!(entity.do_mode_change(&mut host, DESTINY_MODE_WARP, 0), Ok(()));
        assert_eq!(host.log, ["message(Named(\"MESSAGE_ON_BALL_MODE_CHANGED\"))", "keep_alive_remove"]);

        host.ballpark = false;
        assert_eq!(
            entity.do_mode_change(&mut host, DESTINY_MODE_WARP, 0),
            Err(WorldError::NpcWarp(NpcWarpKind::Other(String::new())))
        );
    }

    #[test]
    fn additional_loot_is_added_summed_and_removed_by_type() {
        let mut entity = BehaviorEntity::new(7);
        let host = Host::new();
        assert!(!entity.has_type_in_additional_loot(5));
        assert_eq!(entity.add_type_and_amount_to_additional_loot(&host, 5, int(2), true), Ok(()));
        assert_eq!(entity.add_type_and_amount_to_additional_loot(&host, 5, int(3), true), Ok(()));
        assert_eq!(entity.add_type_and_amount_to_additional_loot(&host, 3, int(1), true), Ok(()));
        assert!(entity.has_type_in_additional_loot(5));
        assert_eq!(entity.additional_loot(), Value::Dict(vec![(int(3), int(1)), (int(5), int(5))]));
        entity.remove_type_from_additional_loot(5);
        entity.remove_type_from_additional_loot(99);
        assert!(!entity.has_type_in_additional_loot(5));
        assert_eq!(entity.additional_loot(), Value::Dict(vec![(int(3), int(1))]));
    }

    #[test]
    fn an_amount_that_is_not_a_number_is_a_type_error() {
        let mut entity = BehaviorEntity::new(7);
        let host = Host::new();
        let result = entity.add_type_and_amount_to_additional_loot(&host, 5, Value::Str("many".into()), true);
        assert!(matches!(result, Err(WorldError::Runtime(ref what)) if what.starts_with("TypeError")));
        assert!(!entity.has_type_in_additional_loot(5));
    }

    #[test]
    fn without_overload_only_what_fits_is_added_rounded_down() {
        let mut entity = BehaviorEntity::new(7);
        let mut host = Host::new();
        host.capacity = 10.0;
        host.volumes.insert(5, 2.0);
        // 10 m3 free, 7 units of 2 m3 need 14: 10/14 of 7 = 5 units (floor of 5.0).
        assert_eq!(entity.add_type_and_amount_to_additional_loot(&host, 5, int(7), false), Ok(()));
        assert_eq!(entity.additional_loot(), Value::Dict(vec![(int(5), Value::Float(5.0))]));
        // The bay is full now: nothing more fits, and the entry stays a float.
        assert_eq!(entity.add_type_and_amount_to_additional_loot(&host, 5, int(1), false), Ok(()));
        assert_eq!(entity.additional_loot(), Value::Dict(vec![(int(5), Value::Float(5.0))]));
        // A 3 m3 item with 5 m3 free: 5/6 of 2 = 1.66.. -> 1.
        host.capacity = 15.0;
        host.volumes.insert(6, 3.0);
        assert_eq!(entity.add_type_and_amount_to_additional_loot(&host, 6, int(2), false), Ok(()));
        assert_eq!(entity.additional_loot().dict_get(&int(6)), Some(&Value::Float(1.0)));
        // What fits whole is added whole and stays an integer.
        let mut roomy = BehaviorEntity::new(8);
        assert_eq!(roomy.add_type_and_amount_to_additional_loot(&host, 6, int(2), false), Ok(()));
        assert_eq!(roomy.additional_loot().dict_get(&int(6)), Some(&int(2)));
    }

    #[test]
    fn what_exactly_fits_is_not_cut() {
        let mut entity = BehaviorEntity::new(7);
        let mut host = Host::new();
        host.capacity = 10.0;
        host.volumes.insert(5, 2.0);
        assert_eq!(entity.add_type_and_amount_to_additional_loot(&host, 5, int(5), false), Ok(()));
        assert_eq!(entity.additional_loot().dict_get(&int(5)), Some(&int(5)));
    }

    #[test]
    fn a_type_without_volume_that_does_not_fit_divides_by_zero() {
        let mut entity = BehaviorEntity::new(7);
        let mut host = Host::new();
        host.capacity = -1.0;
        host.volumes.insert(5, 0.0);
        assert_eq!(
            entity.add_type_and_amount_to_additional_loot(&host, 5, int(3), false),
            Err(WorldError::ZeroDivision)
        );
        assert!(!entity.has_type_in_additional_loot(5));
        host.capacity = 0.0;
        assert_eq!(entity.add_type_and_amount_to_additional_loot(&host, 5, int(3), false), Ok(()));
    }

    #[test]
    fn the_cargo_capacity_left_is_the_type_capacity_less_what_the_additional_loot_takes() {
        let mut entity = BehaviorEntity::new(7);
        let mut host = Host::new();
        host.capacity = 100.0;
        host.volumes.insert(5, 2.5);
        host.volumes.insert(6, 10.0);
        assert_eq!(entity.available_additional_cargo_capacity(&host), Ok(100.0));
        entity.add_type_and_amount_to_additional_loot(&host, 5, int(4), true).unwrap();
        entity.add_type_and_amount_to_additional_loot(&host, 6, int(2), true).unwrap();
        assert_eq!(entity.available_additional_cargo_capacity(&host), Ok(70.0));
        assert_eq!(entity.has_enough_additional_cargo_capacity_for_type(&host, 6, &int(7)), Ok(true));
        assert_eq!(entity.has_enough_additional_cargo_capacity_for_type(&host, 6, &int(8)), Ok(false));
        assert_eq!(
            entity.has_enough_additional_cargo_capacity_for_type(&host, 6, &Value::Float(7.5)),
            Ok(false)
        );
        host.item = Err(WorldError::Runtime("no item".into()));
        assert_eq!(
            entity.available_additional_cargo_capacity(&host),
            Err(WorldError::Runtime("no item".into()))
        );
    }

    #[test]
    fn the_loot_of_a_death_is_the_types_then_the_tables_then_what_was_added() {
        let mut entity = BehaviorEntity::new(7);
        let mut host = Host::new();
        host.picks = vec![
            (11, vec![(int(2), None), (int(1), Some(Value::Str("info".into())))]),
            (12, vec![(int(4), None)]),
        ];
        host.tables.insert(70, vec![(21, int(1), None)]);
        host.tables.insert(71, vec![(22, int(9), None)]);
        entity.add_loot_table_to_additional_loot(71);
        entity.add_loot_table_to_additional_loot(70);
        entity.add_type_and_amount_to_additional_loot(&host, 31, int(3), true).unwrap();
        assert!(entity.has_loot_table_in_additional_loot(70));
        assert!(!entity.has_loot_table_in_additional_loot(72));
        let items = entity.loot_for_entity(&mut host, 100, 500);
        assert_eq!(
            items,
            [
                (11, int(2), None),
                (11, int(1), Some(Value::Str("info".into()))),
                (12, int(4), None),
                (22, int(9), None),
                (21, int(1), None),
                (31, int(3), None),
            ]
        );
        let (message, args) = host.messages.pop().unwrap();
        assert_eq!(message, message::ON_DROPPED_ADDITIONAL_LOOT);
        assert_eq!(
            args,
            [Value::set([
                Value::tuple([int(22), int(9)]),
                Value::tuple([int(21), int(1)]),
                Value::tuple([int(31), int(3)]),
            ])]
        );
    }

    #[test]
    fn nothing_is_told_when_there_is_no_additional_loot() {
        let entity = BehaviorEntity::new(7);
        let mut host = Host::new();
        host.picks = vec![(11, vec![(int(2), None)])];
        assert_eq!(entity.loot_for_entity(&mut host, 100, 500), [(11, int(2), None)]);
        assert!(host.messages.is_empty());
    }

    #[test]
    fn the_dropped_message_holds_each_type_and_amount_once() {
        let entity = BehaviorEntity::new(7);
        let mut host = Host::new();
        host.tables.insert(70, vec![(21, int(1), None), (21, int(1), Some(Value::Str("other".into())))]);
        let mut entity = entity;
        entity.add_loot_table_to_additional_loot(70);
        assert_eq!(entity.loot_for_entity(&mut host, 100, 500).len(), 2);
        assert_eq!(host.messages[0].1, [Value::set([Value::tuple([int(21), int(1)])])]);
    }

    #[test]
    fn a_ball_that_explodes_drops_a_wreck_with_the_loot() {
        let mut entity = BehaviorEntity::new(7);
        let mut host = Host::new();
        host.picks = vec![(11, vec![(int(2), None)])];
        entity.add_type_and_amount_to_additional_loot(&host, 31, int(3), true).unwrap();
        assert_eq!(entity.on_explode(&mut host), Ok(()));
        assert_eq!(
            host.log,
            [
                "ship_access(30000001)",
                "message(Named(\"MESSAGE_ON_DROPPED_ADDITIONAL_LOOT\"))",
                "drop_wreck",
                "explode_base"
            ]
        );
        assert_eq!(
            host.dropped,
            [(Some(90_000_001), 100, [1.0, 2.0, 3.0], 26_557, vec![(11, int(2), None), (31, int(3), None)])]
        );
    }

    #[test]
    fn a_type_without_a_wreck_still_asks_for_its_loot_and_explodes() {
        let entity = BehaviorEntity::new(7);
        let mut host = Host::new();
        host.wreck_type = None;
        host.tables.insert(70, vec![(21, int(1), None)]);
        let mut entity = entity;
        entity.add_loot_table_to_additional_loot(70);
        assert_eq!(entity.on_explode(&mut host), Ok(()));
        assert_eq!(
            host.log,
            [
                "ship_access(30000001)",
                "message(Named(\"MESSAGE_ON_DROPPED_ADDITIONAL_LOOT\"))",
                "explode_base"
            ]
        );
        assert!(host.dropped.is_empty());
    }

    #[test]
    fn a_cargo_bay_component_manages_the_loot_and_the_entity_does_nothing() {
        let entity = BehaviorEntity::new(7);
        let mut host = Host::new();
        host.cargo_bay_types.push(100);
        assert_eq!(entity.on_explode(&mut host), Ok(()));
        assert!(host.log.is_empty());
    }

    #[test]
    fn an_item_that_is_not_there_stops_the_explosion() {
        let entity = BehaviorEntity::new(7);
        let mut host = Host::new();
        host.item = Err(WorldError::FakeItemNotHere);
        assert_eq!(entity.on_explode(&mut host), Err(WorldError::FakeItemNotHere));
        assert!(host.log.is_empty());
    }

    #[test]
    fn unspawning_tells_only_of_additional_loot() {
        let mut entity = BehaviorEntity::new(7);
        let mut host = Host::new();
        entity.on_unspawn(&mut host);
        assert!(host.messages.is_empty());
        entity.add_type_and_amount_to_additional_loot(&host, 31, int(3), true).unwrap();
        entity.on_unspawn(&mut host);
        assert_eq!(
            host.messages,
            [(message::ON_UNSPAWNED_WITH_ADDITIONAL_LOOT, vec![Value::Dict(vec![(int(31), int(3))])])]
        );
    }

    #[test]
    fn removing_the_additional_loot_hands_it_back_and_tells_whoever_cares() {
        let mut entity = BehaviorEntity::new(7);
        let mut host = Host::new();
        entity.add_type_and_amount_to_additional_loot(&host, 31, int(3), true).unwrap();
        entity.add_loot_table_to_additional_loot(70);
        let removed = entity.remove_additional_loot(&mut host);
        assert_eq!(removed, BTreeMap::from([(31, int(3))]));
        assert_eq!(host.messages, [(message::ON_ADDITIONAL_LOOT_REMOVED, Vec::new())]);
        assert!(!entity.has_type_in_additional_loot(31));
        // The tables are not part of it.
        assert!(entity.has_loot_table_in_additional_loot(70));
        assert!(entity.remove_additional_loot(&mut host).is_empty());
        assert_eq!(host.messages.len(), 2);
    }
}
