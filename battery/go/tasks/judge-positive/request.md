Original request for this change:

Add `(*Stock).Contains(sku string) bool` to `stock.go`. It returns true when the SKU is known, even when its
quantity is zero. Add a registered test for it.
