Original request for this change:

Add `Stock.contains(sku)` to `inventory/stock.py`. It returns True when the SKU is known, even when its quantity is
zero, and raises `TypeError` when the SKU is not a string. Add a registered test for it.
