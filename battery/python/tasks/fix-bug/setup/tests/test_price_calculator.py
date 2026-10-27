"""PriceCalculator tests."""

import unittest
from decimal import Decimal

from inventory.price_calculator import apply_discount, line_total


class PriceCalculatorTests(unittest.TestCase):
    """PriceCalculator tests."""

    def test_price_calculator_line_total_multiplies(self) -> None:
        """A line total is the unit price times the quantity."""
        self.assertEqual(Decimal("7.50"), line_total(Decimal("2.50"), 3))

    def test_price_calculator_apply_discount_ten_percent(self) -> None:
        """Ten percent off 80.00 is 72.00."""
        self.assertEqual(Decimal("72.00"), apply_discount(Decimal("80.00"), Decimal("10")))

    def test_price_calculator_apply_discount_zero_keeps_price(self) -> None:
        """No discount keeps the price."""
        self.assertEqual(Decimal("19.99"), apply_discount(Decimal("19.99"), Decimal("0")))
