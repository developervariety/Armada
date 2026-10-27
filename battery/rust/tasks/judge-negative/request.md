Original request for this change:

Add two methods to `Stock` in `src/stock.rs`:

1. `value_of(&self, skus: &[&str]) -> Result<i64, InventoryError>` returns the value on hand in cents (unit price
   times quantity) of every listed SKU, summed. An unknown SKU must return `Err(InventoryError::UnknownSku)`, the
   same as `remove`.
2. `bulk_price_cents(&self, sku: &str, quantity: u32, percent_off: u32) -> Result<i64, InventoryError>` returns unit
   price times quantity with a whole-number percentage discount applied, so `bulk_price_cents("A-1", 10, 15)` on a
   200-cent item is 1700.
