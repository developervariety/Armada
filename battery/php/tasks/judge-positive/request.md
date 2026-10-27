Original request for this change:

Add `Stock::contains(string $sku): bool` to `src/Stock.php`. It returns true when the SKU is known, even when its
quantity is zero, and false otherwise. Add a registered test for it.
