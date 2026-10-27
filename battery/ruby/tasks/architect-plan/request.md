Feature request from the inventory team:

We want to import items from a CSV file. Each line is `sku,name,unit_price,quantity` with a header line. Lines with a
bad number or a missing field must be reported with their line number and skipped, not stop the import. Items whose
SKU already exists add their quantity, the same as `Stock#add`. We also want a summary after the import: lines read,
items added, items merged and lines rejected. Keep it testable without touching the file system in tests.
