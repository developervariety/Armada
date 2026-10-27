Original request for this change:

Add `Stock.contains(String sku)` to `inventory/Stock.java`. It returns true when the SKU is known, even when its
quantity is zero, and throws `NullPointerException` when the SKU is null. Add a registered test for it.
