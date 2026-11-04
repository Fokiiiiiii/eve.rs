//! `behaviors/monitors/entities.py`.

use crate::events::EventKey;
use crate::events::message::{ON_ADDITIONAL_LOOT_REMOVED, ON_INVENTORY_CHANGE};
use crate::monitors::Monitor;
use crate::tree::TaskCtx;

/// Starts the tree over when an entity's additional loot is removed.
#[derive(Debug, Default)]
pub struct AdditionalInventoryRemovalMonitor;

impl Monitor for AdditionalInventoryRemovalMonitor {
    fn type_name(&self) -> &'static str {
        "AdditionalInventoryRemovalMonitor"
    }

    fn message(&self, _t: &mut TaskCtx<'_, '_>) -> Option<EventKey> {
        Some(ON_ADDITIONAL_LOOT_REMOVED.into())
    }
}

/// Starts the tree over when what is in the inventory changes.
#[derive(Debug, Default)]
pub struct InventoryChangeMonitor;

impl Monitor for InventoryChangeMonitor {
    fn type_name(&self) -> &'static str {
        "InventoryChangeMonitor"
    }

    fn message(&self, _t: &mut TaskCtx<'_, '_>) -> Option<EventKey> {
        Some(ON_INVENTORY_CHANGE.into())
    }
}
