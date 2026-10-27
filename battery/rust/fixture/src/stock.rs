//! Items on hand, keyed by SKU.

use std::collections::HashMap;

use crate::error::InventoryError;
use crate::item::Item;

/// Items on hand, keyed by SKU.
#[derive(Debug, Default)]
pub struct Stock {
    items: HashMap<String, Item>,
}

impl Stock {
    /// Creates an empty stock.
    pub fn new() -> Stock {
        Stock { items: HashMap::new() }
    }

    /// Number of distinct items.
    pub fn count(&self) -> usize {
        self.items.len()
    }

    /// Adds an item, or adds its quantity to the item with the same SKU.
    pub fn add(&mut self, item: Item) {
        if let Some(existing) = self.items.get_mut(&item.sku) {
            existing.quantity += item.quantity;
            return;
        }
        self.items.insert(item.sku.clone(), item);
    }

    /// Removes units of an item. Returns `InvalidArgument` when `quantity` is 0, `UnknownSku` when the SKU is
    /// unknown and `InsufficientStock` when fewer units are on hand than requested.
    pub fn remove(&mut self, sku: &str, quantity: u32) -> Result<(), InventoryError> {
        if quantity == 0 {
            return Err(InventoryError::InvalidArgument("quantity must be positive".to_string()));
        }
        let existing = self
            .items
            .get_mut(sku)
            .ok_or_else(|| InventoryError::UnknownSku(sku.to_string()))?;
        if existing.quantity < quantity {
            return Err(InventoryError::InsufficientStock {
                sku: sku.to_string(),
                on_hand: existing.quantity,
            });
        }
        existing.quantity -= quantity;
        Ok(())
    }

    /// Finds an item; `None` when the SKU is unknown.
    pub fn find(&self, sku: &str) -> Option<&Item> {
        self.items.get(sku)
    }

    /// Units on hand across every item.
    pub fn total_units(&self) -> u64 {
        let mut total: u64 = 0;
        for item in self.items.values() {
            total += u64::from(item.quantity);
        }
        total
    }
}
