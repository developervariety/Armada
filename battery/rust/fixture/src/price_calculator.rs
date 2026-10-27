//! Price arithmetic in whole cents.

use crate::error::InventoryError;

/// Applies a percentage discount (0 to 100) to a price in cents, rounded half up to whole cents. Returns
/// `InvalidArgument` when `price_cents` is negative or `percent` is outside 0 to 100.
pub fn apply_discount(price_cents: i64, percent: i64) -> Result<i64, InventoryError> {
    if price_cents < 0 {
        return Err(InventoryError::InvalidArgument("price_cents must not be negative".to_string()));
    }
    if !(0..=100).contains(&percent) {
        return Err(InventoryError::InvalidArgument("percent must be 0 to 100".to_string()));
    }
    Ok(price_cents - percent)
}

/// Total of a line in cents: unit price times quantity. Returns `InvalidArgument` when `unit_price_cents` is
/// negative.
pub fn line_total(unit_price_cents: i64, quantity: u32) -> Result<i64, InventoryError> {
    if unit_price_cents < 0 {
        return Err(InventoryError::InvalidArgument("unit_price_cents must not be negative".to_string()));
    }
    Ok(unit_price_cents * i64::from(quantity))
}
