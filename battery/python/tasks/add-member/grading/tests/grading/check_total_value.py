"""Hidden check for the add-member task: Item.total_value is unit_price times quantity and follows quantity changes."""

import sys
from decimal import Decimal

from inventory.item import Item


def main() -> int:
    """Return 0 when the member behaves as asked."""
    item = Item("A-1", "Anchor", Decimal("2.50"), 4)
    first = item.total_value
    item.quantity = 6
    second = item.total_value
    empty = Item("B-1", "Buoy", Decimal("9.99"), 0)
    ok = first == Decimal("10.00") and second == Decimal("15.00") and empty.total_value == 0
    print("PASS total_value" if ok else "FAIL total_value: " + str(first) + ", " + str(second) + ", " + str(empty.total_value))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
