Original request for this change:

Add two methods to `Inventory::Stock` in `lib/inventory/stock.rb`:

1. `value_of_skus(skus)` returns the value on hand (unit price times quantity) of every listed SKU, summed.
   An unknown SKU must raise `UnknownSkuError`, the same as `remove`.
2. `bulk_price(sku, quantity, percent_off)` returns unit price times quantity with a whole-number percentage
   discount applied, so `bulk_price('A-1', 10, 15)` on a 2.00 item is 17.00.
