//! Item tests.

use inventory::{InventoryError, Item};

/// The constructor keeps its arguments.
pub fn item_constructor_sets_values() {
    let item = Item::new("A-1", "Anchor", 1250, 4).expect("valid item");
    assert_eq!("A-1", item.sku);
    assert_eq!("Anchor", item.name);
    assert_eq!(1250, item.unit_price_cents);
    assert_eq!(4, item.quantity);
}

/// A negative price is refused.
pub fn item_constructor_negative_price_fails() {
    let result = Item::new("A-1", "Anchor", -1, 1);
    assert!(matches!(result, Err(InventoryError::InvalidArgument(_))), "got {:?}", result);
}
