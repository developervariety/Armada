"""Stock tests."""

import unittest
from decimal import Decimal

from inventory.errors import UnknownSkuError
from inventory.item import Item
from inventory.stock import Stock


class StockTests(unittest.TestCase):
    """Stock tests."""

    def test_stock_add_same_sku_merges_quantity(self) -> None:
        """Adding the same SKU twice merges the quantities."""
        stock = Stock()
        stock.add(Item("A-1", "Anchor", Decimal("1"), 2))
        stock.add(Item("A-1", "Anchor", Decimal("1"), 3))
        self.assertEqual(1, stock.count)
        self.assertEqual(5, stock.total_units())

    def test_stock_remove_reduces_quantity(self) -> None:
        """Removing units lowers the quantity."""
        stock = Stock()
        stock.add(Item("A-1", "Anchor", Decimal("1"), 5))
        stock.remove("A-1", 2)
        self.assertEqual(3, stock.find("A-1").quantity)

    def test_stock_remove_unknown_sku_raises(self) -> None:
        """Removing an unknown SKU raises UnknownSkuError."""
        stock = Stock()
        with self.assertRaises(UnknownSkuError):
            stock.remove("missing", 1)
