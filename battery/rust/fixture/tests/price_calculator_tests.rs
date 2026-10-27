//! Price calculator tests.

use inventory::price_calculator::line_total;

/// A line total is the unit price times the quantity.
pub fn price_calculator_line_total_multiplies() {
    assert_eq!(Ok(750), line_total(250, 3));
}
