Original request for this change:

Add two methods to `Stock` in `src/Inventory/Stock.cs`:

1. `ValueOf(IList<string> skus)` returns the value on hand (unit price times quantity) of every listed SKU, summed.
   An unknown SKU must throw `KeyNotFoundException`, the same as `Remove`.
2. `BulkPrice(string sku, int quantity, int percentOff)` returns unit price times quantity with a whole-number
   percentage discount applied, so `BulkPrice("A-1", 10, 15)` on a 2.00 item is 17.00.
