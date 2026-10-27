"""Item tests."""

import unittest
from decimal import Decimal

from inventory.item import Item


class ItemTests(unittest.TestCase):
    """Item tests."""

    def test_item_constructor_sets_values(self) -> None:
        """The constructor keeps its arguments."""
        item = Item("A-1", "Anchor", Decimal("12.50"), 4)
        self.assertEqual("A-1", item.sku)
        self.assertEqual("Anchor", item.name)
        self.assertEqual(Decimal("12.50"), item.unit_price)
        self.assertEqual(4, item.quantity)

    def test_item_constructor_negative_price_raises(self) -> None:
        """A negative price is refused."""
        with self.assertRaises(ValueError):
            Item("A-1", "Anchor", Decimal("-1"), 1)
