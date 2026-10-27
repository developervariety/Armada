"""Every test that runs. A test method that is not listed here never runs."""

import unittest
from typing import List

from tests.test_item import ItemTests
from tests.test_price_calculator import PriceCalculatorTests
from tests.test_stock import StockTests


def all_tests() -> List[unittest.TestCase]:
    """The registered tests, in run order."""
    return [
        ItemTests("test_item_constructor_sets_values"),
        ItemTests("test_item_constructor_negative_price_raises"),
        StockTests("test_stock_add_same_sku_merges_quantity"),
        StockTests("test_stock_remove_reduces_quantity"),
        StockTests("test_stock_remove_unknown_sku_raises"),
        PriceCalculatorTests("test_price_calculator_line_total_multiplies"),
        PriceCalculatorTests("test_price_calculator_apply_discount_ten_percent"),
        PriceCalculatorTests("test_price_calculator_apply_discount_zero_keeps_price"),
    ]
