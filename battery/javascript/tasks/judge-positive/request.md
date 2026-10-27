Original request for this change:

Add `Stock.contains(sku)` to `src/stock.js`. It returns true when the SKU is known, even when its quantity is zero,
and throws `TypeError` when the SKU is not a string. Add a registered test for it.
