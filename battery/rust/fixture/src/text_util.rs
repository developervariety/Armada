//! Text helpers.

use crate::error::InventoryError;

/// Shortens `value` to at most `max_length` characters, ending in "..." when it was cut. Returns `EmptyValue` when
/// `value` is empty and `MaxLengthTooSmall` when `max_length` is less than 3.
pub fn truncate(value: &str, max_length: usize) -> Result<String, InventoryError> {
    if value.is_empty() {
        return Err(InventoryError::EmptyValue);
    }
    if max_length < 3 {
        return Err(InventoryError::MaxLengthTooSmall);
    }
    if value.chars().count() <= max_length {
        return Ok(value.to_string());
    }
    let kept: String = value.chars().take(max_length - 3).collect();
    Ok(kept + "...")
}
