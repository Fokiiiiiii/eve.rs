//! What the tasks ask of the rest of the server: the types of items (`evetypes`), the map, the corporations and the standings.

/// What the damage tracker holds of a ship since a time: `GetDamageForShipSince(shipId, since, toNpcOnly=True)` and
/// `GetHealingDoneByShipSince(shipId, since)`.
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct DamageTotals {
    pub damage_to_npcs: f64,
    pub healing_done: f64,
}

/// The static data and the standings the tasks read.
pub trait Universe {
    /// `damageTracker.GetDamageForShipSince(...)` and `damageTracker.GetHealingDoneByShipSince(...)` of a ship, since a time of the clock.
    fn damage_tracker_totals(&self, _ship_id: i64, _since: i64) -> DamageTotals {
        DamageTotals::default()
    }

    /// `structureProfiles.GetSettingValueForCharacter(corporationId, structureId, SETTING_HOUSING_CAN_DOCK, corporationId, allianceId, None)` is not
    /// `None`.
    fn structure_housing_can_dock(
        &self,
        _corporation_id: i64,
        _structure_id: i64,
        _character_corporation_id: i64,
        _alliance_id: Option<i64>,
    ) -> bool {
        false
    }

    /// `evetypes.GetTypeIDsByListID(typeListId)`.
    fn type_ids_in_list(&self, _type_list_id: i64) -> Vec<i64> {
        Vec::new()
    }

    /// `evetypes.GetAttributeForType(typeId, name)` (`volume`, `capacity`, ...).
    fn type_attribute_by_name(&self, _type_id: i64, _name: &str) -> f64 {
        0.0
    }

    /// `evetypes.GetCategoryID(typeId)`.
    fn type_category_id(&self, _type_id: i64) -> Option<i64> {
        None
    }

    /// `GetShipIDForCharID(charId)`.
    fn ship_id_for_char_id(&self, _char_id: i64) -> Option<i64> {
        None
    }

    /// `cfg.mapSystemCache[solarSystemId].securityStatus`.
    fn security_status(&self, _solar_system_id: i64) -> f64 {
        0.0
    }

    /// `get_corporation_faction_id(corporationId)`.
    fn corporation_faction_id(&self, _corporation_id: i64) -> Option<i64> {
        None
    }

    /// `get_standings_from_npc_to_item(standingMgr, npcOwnerId, toId)`.
    fn standings_from_npc_to_item(&self, _npc_owner_id: i64, _to_id: i64) -> f64 {
        0.0
    }

    /// `GetAveragePrice(typeId)`.
    fn average_price(&self, _type_id: i64) -> Option<f64> {
        None
    }

    /// `cfg.mapSystemCache.get(solarSystemId).factionID`.
    fn solar_system_faction_id(&self, _solar_system_id: i64) -> Option<i64> {
        None
    }
    /// `sm.GetService('machoNet').SinglecastBySolarSystemID(solarSystemId, notification, *args)`.
    fn singlecast_by_solar_system_id(
        &mut self,
        _solar_system_id: Option<i64>,
        _notification: &str,
        _args: &[crate::value::Value],
    ) {
    }
    /// What `IsEvePlayerCharacter` asks the server for an owner of the ids that are not clearly a player's: `cfg.eveowners.Get(id).IsCharacter()`
    /// (and not a player corporation).
    fn is_pre_inv64_player_character(&self, _owner_id: i64) -> bool {
        false
    }
    /// `sm.GetService('machoNet').SinglecastByCharID(charId, notification, *args)`.
    fn singlecast_by_char_id(
        &mut self,
        _char_id: Option<i64>,
        _notification: &str,
        _args: &[crate::value::Value],
    ) {
    }
}
