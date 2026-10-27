//! A stock-keeping item.

use crate::error::InventoryError;

/// A stock-keeping item.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Item {
    /// Stock-keeping unit code.
    pub sku: String,
    /// Display name.
    pub name: String,
    /// Price of one unit, in cents.
    pub unit_price_cents: i64,
    /// Units on hand.
    pub quantity: u32,
}

impl Item {
    /// Creates an item. Returns `InvalidArgument` when `sku` or `name` is empty or `unit_price_cents` is negative.
    pub fn new(sku: &str, name: &str, unit_price_cents: i64, quantity: u32) -> Result<Item, InventoryError> {
        if sku.is_empty() || name.is_empty() {
            return Err(InventoryError::InvalidArgument("sku and name are required".to_string()));
        }
        if unit_price_cents < 0 {
            return Err(InventoryError::InvalidArgument("unit_price_cents must not be negative".to_string()));
        }
        Ok(Item {
            sku: sku.to_string(),
            name: name.to_string(),
            unit_price_cents,
            quantity,
        })
    }
}
