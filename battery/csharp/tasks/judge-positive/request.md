Original request for this change:

Add `Stock.Contains(string sku)` to `src/Inventory/Stock.cs`. It returns true when the SKU is known, even when its
quantity is zero, and throws `ArgumentNullException` for a null SKU. Add a registered test for it.
