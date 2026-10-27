Original request for this change:

Add two methods to `Stock` in `src/stock.js`:

1. `valueOfSkus(skus)` returns the value on hand (unit price times quantity) of every listed SKU, summed.
   An unknown SKU must throw `UnknownSkuError`, the same as `remove`.
2. `bulkPrice(sku, quantity, percentOff)` returns unit price times quantity with a whole-number percentage discount
   applied, so `bulkPrice('A-1', 10, 15)` on a 2.00 item is 17.00.
