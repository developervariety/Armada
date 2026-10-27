"""PriceCalculator tests."""

import unittest
from decimal import Decimal

from inventory.price_calculator import line_total


class PriceCalculatorTests(unittest.TestCase):
    """PriceCalculator tests."""

    def test_price_calculator_line_total_multiplies(self) -> None:
        """A line total is the unit price times the quantity."""
        self.assertEqual(Decimal("7.50"), line_total(Decimal("2.50"), 3))
