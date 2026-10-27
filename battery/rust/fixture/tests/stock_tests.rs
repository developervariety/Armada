//! Stock tests.

use inventory::{InventoryError, Item, Stock};

/// Adding the same SKU twice merges the quantities.
pub fn stock_add_same_sku_merges_quantity() {
    let mut stock = Stock::new();
    stock.add(Item::new("A-1", "Anchor", 100, 2).expect("valid item"));
    stock.add(Item::new("A-1", "Anchor", 100, 3).expect("valid item"));
    assert_eq!(1, stock.count());
    assert_eq!(5, stock.total_units());
}

/// Removing units lowers the quantity.
pub fn stock_remove_reduces_quantity() {
    let mut stock = Stock::new();
    stock.add(Item::new("A-1", "Anchor", 100, 5).expect("valid item"));
    stock.remove("A-1", 2).expect("remove");
    assert_eq!(3, stock.find("A-1").expect("known SKU").quantity);
}

/// Removing an unknown SKU returns UnknownSku.
pub fn stock_remove_unknown_sku_fails() {
    let mut stock = Stock::new();
    assert_eq!(Err(InventoryError::UnknownSku("missing".to_string())), stock.remove("missing", 1));
}
