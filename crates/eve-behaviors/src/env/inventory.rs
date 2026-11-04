//! The server's `inventory2` and `inventoryMgr` as the tasks see them: what an item is, what is in its cargo.

use super::WorldResult;

/// An item of the inventory.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct InventoryItem {
    pub item_id: i64,
    pub type_id: i64,
    pub owner_id: i64,
    pub location_id: i64,
}

/// What a cargo bay holds and can hold (`GetCapacity()`).
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct CargoCapacity {
    pub used: f64,
    pub capacity: f64,
}

/// What `AddFakeLocation` and `EveInsertLocation` are given for a space object.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct FakeLocation {
    pub type_id: i64,
    pub owner_id: Option<i64>,
    pub solar_system_id: i64,
    pub flag: i64,
    pub position: [f64; 3],
    pub object_name_id: Option<i64>,
    pub custom_info: Option<CustomInfo>,
}

/// The custom info of a dungeon's object (`dunRotation`, `dunObjectID`, `dunObjectNameID`).
#[derive(Debug, Clone, Default, PartialEq)]
pub struct CustomInfo {
    pub dun_rotation: Option<[f64; 3]>,
    pub dun_object_id: Option<i64>,
    pub dun_object_name_id: Option<i64>,
}

/// The inventory's answers and what it is asked to do.
pub trait Inventory {
    /// `inventory2.GetItem(itemId)`: `None` where the client raises (`RuntimeError`, `FakeItemNotHere`).
    fn inventory_item(&self, _item_id: i64) -> Option<InventoryItem> {
        None
    }

    /// `inventoryMgr.GetInventoryFromIdEx(itemId, -1).GetCapacity()`.
    fn cargo_capacity(&self, _item_id: i64) -> WorldResult<CargoCapacity> {
        Ok(CargoCapacity::default())
    }

    /// `inventory2.SelectItems(itemId)`: the ids of the items inside an item.
    fn select_items(&self, _item_id: i64) -> Vec<i64> {
        Vec::new()
    }

    /// `inventoryMgr.DestroyItem(itemId)`.
    fn destroy_item(&mut self, _item_id: i64) {}

    /// `inventoryMgr.ManufactureItemRaw(typeid=, portions=, locationid=, flag=)`.
    fn manufacture_item_raw(&mut self, _type_id: i64, _portions: i64, _location_id: i64, _flag: i64) {}

    /// `inventory2.AddFakeLocation(...)`: a space object that is not kept.
    fn add_fake_location(&mut self, _location: FakeLocation) {}

    /// `inventory2.EveInsertLocation(...)`: a space object that is kept in the database.
    fn insert_location(&mut self, _location: FakeLocation) {}
    /// `spacecomponents.common.helper.HasCargoBayComponent(typeId)`.
    fn has_cargo_bay_component(&self, _type_id: i64) -> bool {
        false
    }
}
