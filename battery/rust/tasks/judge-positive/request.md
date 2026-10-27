Original request for this change:

Add `Stock::contains(&self, sku: &str) -> bool` to `src/stock.rs`. It returns true when the SKU is known, even when
its quantity is zero. Add a registered test for it.
