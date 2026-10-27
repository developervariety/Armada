Original request for this change:

Add two methods to `Stock` in `stock.go`:

1. `ValueOf(skus []string) (int64, error)` returns the value on hand in cents (unit price times quantity) of every
   listed SKU, summed. An unknown SKU must return an error wrapping `ErrUnknownSku`, the same as `Remove`.
2. `BulkPriceCents(sku string, quantity int, percentOff int) (int64, error)` returns unit price times quantity with a
   whole-number percentage discount applied, so `BulkPriceCents("A-1", 10, 15)` on a 200-cent item is 1700.
