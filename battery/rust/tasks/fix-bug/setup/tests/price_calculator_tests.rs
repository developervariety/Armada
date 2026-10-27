//! Price calculator tests.

use inventory::price_calculator::{apply_discount, line_total};

/// A line total is the unit price times the quantity.
pub fn price_calculator_line_total_multiplies() {
    assert_eq!(Ok(750), line_total(250, 3));
}

/// Ten percent off 8000 cents is 7200 cents.
pub fn price_calculator_apply_discount_ten_percent() {
    assert_eq!(Ok(7200), apply_discount(8000, 10));
}

/// No discount keeps the price.
pub fn price_calculator_apply_discount_zero_keeps_price() {
    assert_eq!(Ok(1999), apply_discount(1999, 0));
}
