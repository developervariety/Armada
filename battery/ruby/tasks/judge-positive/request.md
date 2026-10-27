Original request for this change:

Add `Inventory::Stock#contains?(sku)` to `lib/inventory/stock.rb`. It returns true when the SKU is known, even when
its quantity is zero, and raises `TypeError` when the SKU is not a String. Add a registered test for it.
