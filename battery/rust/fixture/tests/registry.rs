//! Every test that runs. A test function that is not listed here never runs.

use crate::item_tests;
use crate::price_calculator_tests;
use crate::stock_tests;

/// A named test.
pub struct TestCase {
    /// Test name.
    pub name: &'static str,
    /// Test body; panics to fail.
    pub body: fn(),
}

/// The registered tests, in run order.
pub fn all() -> Vec<TestCase> {
    vec![
        TestCase { name: "item_constructor_sets_values", body: item_tests::item_constructor_sets_values },
        TestCase { name: "item_constructor_negative_price_fails", body: item_tests::item_constructor_negative_price_fails },
        TestCase { name: "stock_add_same_sku_merges_quantity", body: stock_tests::stock_add_same_sku_merges_quantity },
        TestCase { name: "stock_remove_reduces_quantity", body: stock_tests::stock_remove_reduces_quantity },
        TestCase { name: "stock_remove_unknown_sku_fails", body: stock_tests::stock_remove_unknown_sku_fails },
        TestCase { name: "price_calculator_line_total_multiplies", body: price_calculator_tests::price_calculator_line_total_multiplies },
    ]
}
