Original request for this change:

Add two methods to `Stock` in `src/Stock.php`:

1. `valueOfSkus(array $skus): float` returns the value on hand (unit price times quantity) of every listed SKU,
   summed. An unknown SKU must throw `UnknownSkuException`, the same as `remove`.
2. `bulkPrice(string $sku, int $quantity, int $percentOff): float` returns unit price times quantity with a
   whole-number percentage discount applied, so `bulkPrice('A-1', 10, 15)` on a 2.00 item is 17.00.
